Shader "Lotec/Voxel Lighting/SDF Shadow Test"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _BaseMap ("Base Map", 2D) = "white" {}
        _Roughness ("Roughness", Range(0,1)) = 1.0
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
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelSdfAo.hlsl"
            // Lotec Voxel Lighting occlusion direction bitmask shadows
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelOcclusionDirection.hlsl"
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelGi.hlsl"

            // Choose shadow implementation at compile-time only.
            // Keywords: SDF_ONLY, BITMASK_POINT (single bit), BITMASK_4TAP (spatial 4-tap), BITMASK_RAY3 (3-step traversal), BITMASK_8TAP (trilinear 2x2x2)
            #pragma multi_compile __ SDF_ONLY BITMASK_POINT BITMASK_4TAP BITMASK_RAY3 BITMASK_8TAP
            #pragma multi_compile SDF_AO_OFF SDF_AO_LQ SDF_AO_HQ

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                TEXTURE2D(_BaseMap);
                SAMPLER(sampler_BaseMap);
                float _Roughness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                float2 uv          : TEXCOORD3;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv = IN.uv;
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);

                return OUT;
            }

            // Default to SDF if no keyword is set
            inline half GetShadow(Light light, float3 worldPos, float3 normal)
            {
                #if defined(BITMASK_POINT) || defined(BITMASK_4TAP) || defined(BITMASK_RAY3) || defined(BITMASK_8TAP)
                    return GetFinalShadow2(worldPos, normalize(light.direction), normal);
                    // return GetFinalShadow(worldPos, normalize(light.direction));
                #else
                    return GetShadowFromSdf(normalize(light.direction), worldPos);
                #endif
            }

            half4 frag(Varyings IN) : SV_Target
            {
                Light light = GetMainLight();
                float3 N = normalize(IN.normalWS);
                float3 L = normalize(light.direction);

                // Self shadowing factor
                float ndotl = saturate(dot(N, L));
                // Direct light shadowing, only if facing the light
                float shadow = 1.0; // No shadow.
                if (ndotl > 0)
                    shadow = GetShadow(light, IN.positionWS, N);

                // Albedo: texture modulated by base color
                float3 texAlbedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).rgb;
                float3 albedo = _BaseColor.rgb * texAlbedo;

                // Simple Blinn-Phong specular modulated by roughness (1 = very rough -> no specular)
                // float3 V = normalize(_WorldSpaceCameraPos - IN.positionWS);
                // float3 H = normalize(L + V);
                // float specPower = 16.0;
                // float spec = pow(saturate(dot(N, H)), specPower) * (1.0 - saturate(_Roughness));
                
                // Global Illumination from Voxel GI field
                float3 gi = SampleVoxelGI(IN.positionWS, N);
                float ao = GetAmbientOcclusionFromSdf(IN.positionWS, N);

                // float3 lit = albedo * gi; // DEBUG: Indirect lit only for testing
                float3 lit =
                    albedo * light.color * ndotl * shadow // Direct lit
                    + albedo * gi * ao // Indirect lit (ambient occlusion from SDF)
                    // + light.color * spec * shadow // Specular lit
                    ;

                return half4(lit, _BaseColor.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
