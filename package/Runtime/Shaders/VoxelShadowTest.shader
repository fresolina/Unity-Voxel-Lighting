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

            // Lotec Voxel Lighting SDF ray marching shadows
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelSdfShadows.hlsl"
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelSdfAo.hlsl"
            // Lotec Voxel Lighting occlusion direction bitmask shadows
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelOcclusionDirection.hlsl"
            // Lotec Voxel Lighting per-direction occlusion field (hardware-interpolated)
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelOcclusionField.hlsl"
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelGi.hlsl"

            // Choose shadow implementation at compile-time only.
            // Keywords: SDF_ONLY, BITMASK_POINT (single bit), BITMASK_8TAP (trilinear 2x2x2), OCC_FIELD (per-direction field)
            #pragma multi_compile __ SDF_ONLY BITMASK_POINT BITMASK_8TAP OCC_FIELD
            #pragma multi_compile SDF_AO_OFF SDF_AO_LQ SDF_AO_HQ

            #define MAX_POINT_LIGHTS 4
            #define MAX_SPOT_LIGHTS 4

            CBUFFER_START(UnityPerMaterial)
                TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
                TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
                float4 _BaseColor;
                float _Roughness;
                float _Emission;
                TEXTURE2D(_EmissionMap); SAMPLER(sampler_EmissionMap);
                float4 _EmissionColor;
            CBUFFER_END

            uint _PointLightCount;
            float4 _PointLightPositionRange[MAX_POINT_LIGHTS];
            float4 _PointLightColor[MAX_POINT_LIGHTS];
            uint _SpotLightCount;
            float4 _SpotLightPositionRange[MAX_SPOT_LIGHTS];
            float4 _SpotLightDirectionAngleScale[MAX_SPOT_LIGHTS];
            float4 _SpotLightColorAngleOffset[MAX_SPOT_LIGHTS];
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

            // Default to SDF if no keyword is set.
            inline half GetShadow(float3 worldPos, float3 lightDir, float3 normal) {
                #if defined(OCC_FIELD)
                    return GetOccFieldShadow(worldPos, normal);
                #elif defined(BITMASK_POINT) || defined(BITMASK_8TAP)
                    return GetFinalShadow(worldPos, normal);
                #else
                    return GetShadowFromSdf(normalize(lightDir), worldPos, 1.0e+10f);
                #endif
            }

            inline half GetShadow(float3 worldPos, float3 lightDir, float3 normal, float maxDistance) {
                float3 normalizedLightDir = normalize(lightDir);

                // The bitmask field stores directional occlusion to infinity, so it
                // cannot stop at a nearby point/spot light. Use finite-distance SDF
                // shadows for local lights so blockers behind the light do not shadow it.
                return GetShadowFromSdf(normalizedLightDir, worldPos, maxDistance);
            }

            inline float GetLocalLightRangeAttenuation(float distSq, float rangeSq) {
                // Inverse-square distance falloff (physical light intensity) combined with
                // a smooth range window that fades to zero at max range.
                float distanceAtten = rcp(max(distSq, 0.01));
                float rangeFade = saturate(1.0 - (distSq / max(rangeSq, 1e-6)));
                return distanceAtten * rangeFade * rangeFade;
            }

            inline half3 GetDirectLighting(float3 worldPos, half3 normal, half3 albedo, float3 lightDir, half3 lightColor, float attenuation) {
                half3 normalizedLightDir = normalize(lightDir);
                half ndotl = saturate(dot(normal, normalizedLightDir));
                if (ndotl <= 0.0h)
                    return 0.0h;

                half shadow = GetShadow(worldPos, normalizedLightDir, normal);
                return albedo * lightColor * (ndotl * shadow * attenuation);
            }

            inline half3 GetDirectLighting(float3 worldPos, half3 normal, half3 albedo, float3 lightDir, half3 lightColor, float attenuation, float shadowDistance) {
                half3 normalizedLightDir = normalize(lightDir);
                half ndotl = saturate(dot(normal, normalizedLightDir));
                if (ndotl <= 0.0h)
                    return 0.0h;

                half shadow = GetShadow(worldPos, normalizedLightDir, normal, shadowDistance);
                return albedo * lightColor * (ndotl * shadow * attenuation);
            }

            inline half3 GetMainDirectLighting(Light light, float3 worldPos, half3 normal, half3 albedo) {
                return GetDirectLighting(worldPos, normal, albedo, light.direction, light.color, 1.0);
            }

            inline half3 GetPointLightDirect(float3 worldPos, half3 normal, half3 albedo) {
                half3 totalLight = 0.0h;
                uint pointLightCount = min(_PointLightCount, (uint)MAX_POINT_LIGHTS);

                [loop]
                for (uint lightIndex = 0u; lightIndex < pointLightCount; lightIndex++)
                {
                    float4 positionRange = _PointLightPositionRange[lightIndex];
                    float3 toLight = positionRange.xyz - worldPos;
                    float surfaceDistSq = dot(toLight, toLight);
                    if (surfaceDistSq <= 1e-6)
                        continue;
                    float rangeSq = positionRange.w * positionRange.w;
                    if (surfaceDistSq >= rangeSq)
                        continue;

                    float invDistance = rsqrt(surfaceDistSq);
                    float distanceToLight = surfaceDistSq * invDistance;
                    float3 lightDir = toLight * invDistance;
                    float attenuation = GetLocalLightRangeAttenuation(surfaceDistSq, rangeSq);
                    totalLight += GetDirectLighting(worldPos, normal, albedo, lightDir, _PointLightColor[lightIndex].rgb, attenuation, distanceToLight);
                }

                return totalLight;
            }

            inline half3 GetSpotLightDirect(float3 worldPos, half3 normal, half3 albedo) {
                half3 totalLight = 0.0h;
                uint spotLightCount = min(_SpotLightCount, (uint)MAX_SPOT_LIGHTS);

                [loop]
                for (uint lightIndex = 0u; lightIndex < spotLightCount; lightIndex++)
                {
                    float4 positionRange = _SpotLightPositionRange[lightIndex];
                    float3 toLight = positionRange.xyz - worldPos;
                    float surfaceDistSq = dot(toLight, toLight);
                    if (surfaceDistSq <= 1e-6)
                        continue;
                    float rangeSq = positionRange.w * positionRange.w;
                    if (surfaceDistSq >= rangeSq)
                        continue;

                    float invDistance = rsqrt(surfaceDistSq);
                    float distanceToLight = surfaceDistSq * invDistance;
                    float3 lightDir = toLight * invDistance;
                    float4 directionAngleScale = _SpotLightDirectionAngleScale[lightIndex];
                    float4 colorAngleOffset = _SpotLightColorAngleOffset[lightIndex];
                    float coneAttenuation = saturate(dot(-lightDir, directionAngleScale.xyz) * directionAngleScale.w + colorAngleOffset.a);
                    if (coneAttenuation <= 0.0)
                        continue;

                    float attenuation = GetLocalLightRangeAttenuation(surfaceDistSq, rangeSq) * (coneAttenuation * coneAttenuation);
                    totalLight += GetDirectLighting(worldPos, normal, albedo, lightDir, colorAngleOffset.rgb, attenuation, distanceToLight);
                }

                return totalLight;
            }

            half4 frag(v2f IN) : SV_Target
            {
                Light light = GetMainLight();
                half3 N = GetNormal(IN);

                // Albedo: texture modulated by base color
                half3 texAlbedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).rgb;
                half3 albedo = _BaseColor.rgb * texAlbedo;

                // Simple Blinn-Phong specular modulated by roughness (1 = very rough -> no specular)
                // float3 V = normalize(_WorldSpaceCameraPos - IN.positionWS);
                // float3 H = normalize(L + V);
                // float specPower = 16.0;
                // float spec = pow(saturate(dot(N, H)), specPower) * (1.0 - saturate(_Roughness));
                
                // Global Illumination from Voxel GI field
                float3 gi = SampleVoxelGI(IN.positionWS, N);
                float ao = GetAmbientOcclusionFromSdf(IN.positionWS, N);
                half3 directLighting = GetMainDirectLighting(light, IN.positionWS, N, albedo);
                directLighting += GetPointLightDirect(IN.positionWS, N, albedo);
                directLighting += GetSpotLightDirect(IN.positionWS, N, albedo);

                // float3 lit = albedo * gi; // DEBUG: Indirect lit only for testing
                half3 lit =
                    directLighting // Direct lit
                    + albedo * gi * ao // Indirect lit (ambient occlusion from SDF)
                    // + light.color * spec * shadow // Specular lit
                    ;

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
