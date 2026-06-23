Shader "Lotec/Voxel Lighting/SDF Shadow Test"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _BumpMap ("Normal Map", 2D) = "bump" {}
        _Roughness ("Roughness", Range(0,1)) = 1.0
        [Toggle] _Emission ("Emission", Float) = 0.0
        _EmissionMap ("Emission Map", 2D) = "white" {}
        [HDR] _EmissionColor ("Emission Color", Color) = (1,1,1,1)
        [ToggleOff(_RECEIVE_LOCAL_SHADOWS_OFF)] _ReceiveLocalShadows ("Receive Local Shadows", Float) = 1.0
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
            // Per-material: skip the per-light SDF march for local (point/spot) shadows.
            #pragma shader_feature_local _RECEIVE_LOCAL_SHADOWS_OFF

            // URP
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Lotec Voxel Lighting SDF ray marching shadows
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelSdfShadows.hlsl"
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelSdfAo.hlsl"
            // Lotec Voxel Lighting occlusion direction bitmask shadows
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelOcclusionDirection.hlsl"
            // Lotec Voxel Lighting per-direction occlusion field (hardware-interpolated)
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelOcclusionField.hlsl"
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelGi.hlsl"
            // Direct lighting (sun + local lights) + shadow dispatch. Included last so its
            // keyword-gated GetShadow can see the bitmask / occlusion-field helpers above.
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelDirectLighting.hlsl"

            // Choose shadow implementation at compile-time only.
            // Keywords: SDF_ONLY, BITMASK_POINT (single bit), BITMASK_8TAP (trilinear 2x2x2), OCC_FIELD (per-direction field)
            #pragma multi_compile __ SDF_ONLY BITMASK_POINT BITMASK_8TAP OCC_FIELD
            #pragma multi_compile SDF_AO_OFF SDF_AO_LQ SDF_AO_HQ
            // GI_ON: sample the runtime irradiance field + AO. GI_OFF: direct lighting only.
            #pragma multi_compile GI_ON GI_OFF

            CBUFFER_START(UnityPerMaterial)
                TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
                TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
                float4 _BaseColor;
                float _Roughness;
                float _Emission;
                TEXTURE2D(_EmissionMap); SAMPLER(sampler_EmissionMap);
                float4 _EmissionColor;
                float _ReceiveLocalShadows;
            CBUFFER_END

            // Published by GiFieldUpdater (auto-exposure) when GI is active.
            float _Exposure;

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
                    // Convert tangent space normal from normal map to world space.
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

                // Albedo: texture modulated by base color
                half3 texAlbedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).rgb;
                half3 albedo = _BaseColor.rgb * texAlbedo;

                half3 directLighting = GetMainDirectLighting(light, IN.positionWS, N, albedo);
                directLighting += GetPointLightDirect(IN.positionWS, N, albedo);
                directLighting += GetSpotLightDirect(IN.positionWS, N, albedo);

                half3 lit = directLighting;
                #if defined(GI_ON)
                    // Indirect lit (Voxel GI field) modulated by SDF ambient occlusion.
                    float3 gi = SampleVoxelGI(IN.positionWS, N);
                    float ao = GetAmbientOcclusionFromSdf(IN.positionWS, N);
                    lit += albedo * gi * ao;
                #endif

                // Exposure (EV stops) + Reinhard tonemapping
                lit *= exp2(_Exposure);
                lit = lit / (1.0h + lit);

                return half4(lit, _BaseColor.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
