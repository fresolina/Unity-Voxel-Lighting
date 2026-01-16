Shader "Lotec/Voxel Lighting/SDF Shadow Test"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" "RenderType" = "Opaque" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            // URP
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Lotec Voxel Lighting SDF ray marching shadows
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelSdfShadows.hlsl"
            // Lotec Voxel Lighting occlusion direction bitmask shadows
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelOcclusionDirection.hlsl"

            // Choose shadow implementation at compile-time only.
            // Keywords: SDF_ONLY, BITMASK_POINT (single bit), BITMASK_4TAP (spatial 4-tap), BITMASK_RAY3 (3-step traversal), BITMASK_8TAP (trilinear 2x2x2)
            #pragma multi_compile __ SDF_ONLY BITMASK_POINT BITMASK_4TAP BITMASK_RAY3 BITMASK_8TAP

            // Optional debug visualization toggle: when set, the shader outputs debug colors
            // from the bitmask debug helper.
            // Keyword: VOXEL_OCCLUSION_DEBUG_COLORS
            #pragma multi_compile __ VOXEL_OCCLUSION_DEBUG_COLORS

            // Compile-time selection only: keywords control the shadow path

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);

                return OUT;
            }

            // Default to SDF if no keyword is set
            inline half GetShadow(Light light, float3 worldPos, float3 normal)
            {
                #if defined(SDF_ONLY)
                    return GetShadowFromSdf(light, worldPos);
                #elif defined(BITMASK_POINT) || defined(BITMASK_4TAP) || defined(BITMASK_RAY3) || defined(BITMASK_8TAP)
                    // return GetShadowFromBitmaskFiltered(light, worldPos);
                    return GetFinalShadow2(worldPos, normalize(light.direction), normal);
                    // return GetFinalShadow(worldPos, normalize(light.direction));
                #else
                    return GetShadowFromSdf(light, worldPos);
                #endif
            }

            half4 frag(Varyings IN) : SV_Target
            {
                Light light = GetMainLight();
                float3 N = normalize(IN.normalWS);
                float3 L = normalize(light.direction);

                // Self shadowing factor
                float ndotl = saturate(dot(N, L));
                // Direct light shadowing
                float shadow = GetShadow(light, IN.positionWS, N);

                // Ambient light
                if (shadow < 0.02) shadow = 0.02;

                #if defined(VOXEL_OCCLUSION_DEBUG_COLORS)
                    return GetShadowDebugColorFromBitmaskFiltered(light, IN.positionWS);
                #else
                    float3 lit = _BaseColor.rgb * light.color * ndotl * shadow;
                    return half4(lit, _BaseColor.a);
                #endif
            }
            ENDHLSL
        }
    }

    Fallback Off
}
