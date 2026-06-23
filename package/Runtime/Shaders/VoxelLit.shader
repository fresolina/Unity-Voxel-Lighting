Shader "Lotec/Voxel Lighting/Voxel Lit"
{
    // Direct lighting + SDF ray-marched shadows only. No GI, no AO - so it needs only the
    // SDF bake (VoxelSdfBaker) and a LightingManager to publish lights and shadow params.
    // Exposure is a per-material property here (no GI auto-exposure), so it can be cranked
    // up to match boosted light intensities.
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _BumpMap ("Normal Map", 2D) = "bump" {}
        _Roughness ("Roughness", Range(0,1)) = 1.0
        [Toggle] _Emission ("Emission", Float) = 0.0
        _EmissionMap ("Emission Map", 2D) = "white" {}
        [HDR] _EmissionColor ("Emission Color", Color) = (1,1,1,1)
        _Exposure ("Exposure (EV stops)", Float) = 0.0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" "RenderType" = "Opaque" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature _NORMALMAP

            // URP
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Direct lighting (sun + local lights) + SDF ray-marched shadows. This pulls in
            // VoxelSdfShadows.hlsl; no other voxel includes are needed for direct-only.
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelDirectLighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
                TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
                float4 _BaseColor;
                float _Roughness;
                float _Emission;
                TEXTURE2D(_EmissionMap); SAMPLER(sampler_EmissionMap);
                float4 _EmissionColor;
                float _Exposure;
            CBUFFER_END

            struct v {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                #ifdef _NORMALMAP
                    float4 tangent : TANGENT;
                #endif
            };

            struct v2f {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                #ifdef _NORMALMAP
                    half3 normal  : TEXCOORD2;
                    half3 tangent : TEXCOORD3;
                    half3 bitangent: TEXCOORD4;
                #else
                    half3 normalWS: TEXCOORD2;
                #endif
            };

            v2f vert(v IN) {
                v2f OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                #ifdef _NORMALMAP
                    OUT.normal = TransformObjectToWorldNormal(IN.normalOS);
                    OUT.tangent = normalize(TransformObjectToWorldDir(IN.tangent.xyz));
                    float tangentSign = IN.tangent.w * GetOddNegativeScale();
                    OUT.bitangent = cross(OUT.normal, OUT.tangent) * tangentSign;
                #else
                    OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                #endif
                OUT.uv = IN.uv;
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);

                return OUT;
            }

            // Get pixel normal, either from normal map or interpolated vertex normal if no normal map is used.
            half3 GetNormal(v2f input) {
                #ifdef _NORMALMAP
                    half4 normSample = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv);
                    half3 normTex = UnpackNormal(normSample);
                    half3x3 TBN = half3x3(normalize(input.tangent),
                                          normalize(input.bitangent),
                                          normalize(input.normal));
                    return normalize(TransformTangentToWorld(normTex, TBN));
                #else
                    return normalize(input.normalWS);
                #endif
            }

            half4 frag(v2f IN) : SV_Target
            {
                Light light = GetMainLight();
                half3 N = GetNormal(IN);

                half3 texAlbedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).rgb;
                half3 albedo = _BaseColor.rgb * texAlbedo;

                half3 lit = GetMainDirectLighting(light, IN.positionWS, N, albedo);
                lit += GetPointLightDirect(IN.positionWS, N, albedo);
                lit += GetSpotLightDirect(IN.positionWS, N, albedo);

                // Manual exposure (material property) + Reinhard tonemapping.
                lit *= exp2(_Exposure);
                lit = lit / (1.0h + lit);

                return half4(lit, _BaseColor.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
