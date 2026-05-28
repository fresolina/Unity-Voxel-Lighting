// Spatial Hash GI - Full-screen resolve (fragment shader path)
// Optimized for tile-based deferred renderers (Adreno 740 / Quest 3).
// Uses framebuffer fetch (Vulkan subpass input) for depth when available,
// keeping G-buffer data in GMEM instead of round-tripping through DRAM.
Shader "Hidden/Lotec/SpatialHashGiResolve"
{
    Properties
    {
        [HideInInspector] _MainTex ("", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "SpatialHashGI_Resolve"
            ZTest Always ZWrite Off Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            // Enable framebuffer fetch on Vulkan mobile (keeps depth in GMEM)
            #pragma multi_compile __ _GMEM_FRAMEBUFFER_FETCH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // ---- Spatial Hash types (must match SpatialHashGi.hlsl) ----

            #define SPATIAL_HASH_GRID_SIZE 64

            struct VoxelGI
            {
                uint voxelPackedPos;  // X (10 bits), Y (10 bits), Z (10 bits), Padding (2 bits)
                uint packedLighting;  // R(5) G(6) B(5) M(2) axisX(4) axisY(4) axisZ(4) pad(2)
            };

            inline void UnpackLighting(uint pL, out half3 hdrColor, out half3 axisModifiers)
            {
                half3 baseColor = half3(
                    (pL & 0x1F) / 31.0h,
                    ((pL >> 5) & 0x3F) / 63.0h,
                    ((pL >> 11) & 0x1F) / 31.0h
                );
                half multiplier = (half)(1u << ((pL >> 16) & 0x3));
                hdrColor = baseColor * multiplier;

                axisModifiers.x = (((pL >> 18) & 0xF) / 15.0h) * 2.0h - 1.0h;
                axisModifiers.y = (((pL >> 22) & 0xF) / 15.0h) * 2.0h - 1.0h;
                axisModifiers.z = (((pL >> 26) & 0xF) / 15.0h) * 2.0h - 1.0h;
            }

            inline int SpatialHashLinearIndex(uint3 voxelPos)
            {
                return (int)(voxelPos.x + (voxelPos.y * SPATIAL_HASH_GRID_SIZE) + (voxelPos.z * SPATIAL_HASH_GRID_SIZE * SPATIAL_HASH_GRID_SIZE));
            }

            // ---- Buffers & uniforms ----

            StructuredBuffer<VoxelGI> _SpatialHashVoxelData;
            StructuredBuffer<int> _SpatialHashGrid;
            float _SpatialHashVoxelSize;
            float _SpatialHashOneOverVoxelSize;
            float3 _VolumePosition;

            float4x4 _InverseViewProjection;

            // Framebuffer fetch path: depth lives in GMEM as a Vulkan subpass input.
            // Avoids a full-screen DRAM read on Adreno TBDRs.
            #if defined(_GMEM_FRAMEBUFFER_FETCH) && defined(UNITY_FRAMEBUFFER_FETCH_AVAILABLE)
                UNITY_DECLARE_FRAMEBUFFER_INPUT_FLOAT(0);
                #define SAMPLE_SCENE_DEPTH(uv) UNITY_READ_FRAMEBUFFER_INPUT(0, uv).r
            #else
                TEXTURE2D_X(_CameraDepthTexture);
                SAMPLER(sampler_CameraDepthTexture);
                #define SAMPLE_SCENE_DEPTH(uv) SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, uv).r
            #endif

            TEXTURE2D_X(_CameraNormalsTexture);
            SAMPLER(sampler_CameraNormalsTexture);

            // ---- Vertex ----

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                // Full-screen triangle trick (3 vertices, no vertex buffer needed)
                output.uv = float2((input.vertexID << 1) & 2, input.vertexID & 2);
                output.positionCS = float4(output.uv * 2.0 - 1.0, 0.0, 1.0);
                #if UNITY_UV_STARTS_AT_TOP
                    output.uv.y = 1.0 - output.uv.y;
                #endif
                return output;
            }

            // ---- Fragment ----

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float depth = SAMPLE_SCENE_DEPTH(uv);

                // Skip sky pixels
                #if UNITY_REVERSED_Z
                    if (depth <= 0.0) return half4(0, 0, 0, 0);
                #else
                    if (depth >= 1.0) return half4(0, 0, 0, 0);
                #endif

                // Reconstruct world position from depth
                float4 clipPos = float4(uv * 2.0 - 1.0, depth, 1.0);
                #if UNITY_UV_STARTS_AT_TOP
                    clipPos.y = -clipPos.y;
                #endif
                float4 worldPos4 = mul(_InverseViewProjection, clipPos);
                float3 worldPos = worldPos4.xyz / worldPos4.w;

                // Read normal
                float4 normalSample = SAMPLE_TEXTURE2D_X(_CameraNormalsTexture, sampler_CameraNormalsTexture, uv);
                float3 worldNormal = normalize(normalSample.xyz * 2.0 - 1.0);

                // Normal-bias trick
                worldPos += worldNormal * (_SpatialHashVoxelSize * 0.5);

                // Spatial hash lookup
                float3 localPos = (worldPos - _VolumePosition) * _SpatialHashOneOverVoxelSize;
                int3 voxelPos = (int3)floor(localPos);

                half3 gi = half3(0, 0, 0);

                if (all(voxelPos >= 0) && all(voxelPos < (int)SPATIAL_HASH_GRID_SIZE))
                {
                    uint3 uVoxelPos = (uint3)voxelPos;
                    int linearIndex = SpatialHashLinearIndex(uVoxelPos);
                    int dataIndex = _SpatialHashGrid[linearIndex];

                    if (dataIndex >= 0)
                    {
                        VoxelGI data = _SpatialHashVoxelData[dataIndex];
                        half3 hdrColor;
                        half3 axisModifiers;
                        UnpackLighting(data.packedLighting, hdrColor, axisModifiers);

                        half directionalWeight = dot((half3)worldNormal, axisModifiers);
                        gi = max(half3(0, 0, 0), hdrColor + (hdrColor * directionalWeight));
                    }
                }

                return half4(gi, 1.0h);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
