Shader "Lotec/Voxel Lighting/Voxel Lit"
{
    // Per-feature lit shader: direct lighting + selectable shadow source (SDF / bitmask /
    // occlusion field) + optional GI and AO. Exposure is a scene-wide global (set by the
    // GiFieldUpdater - auto or manual).
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
            // 4.5 (SM5.0) required: SampleBufferGI reads StructuredBuffers in the fragment stage
            // (GI_VOXEL_BUFFER). The package already targets compute-capable hardware, so this is safe.
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature _NORMALMAP
            // Per-material: skip the per-light SDF march for local (point/spot) shadows.
            #pragma shader_feature_local _RECEIVE_LOCAL_SHADOWS_OFF
            // Per-material: emissive contribution (the [Toggle] _Emission property).
            #pragma shader_feature_local _EMISSION_ON

            // URP
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Surface lighting: direct + selectable shadow source + SDF AO (pulls its own
            // shadow/AO/volume headers). Runtime GI (irradiance field) pulls the volume header.
            // Each is self-contained, so these two are all the lit pass needs.
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelDirectLighting.hlsl"
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelGi.hlsl"
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/BufferGi.hlsl"
            // Display-transform tonemap operators (Reinhard / AgX / ACES), selected by _Tonemap.
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/Tonemap.hlsl"

            // Shadow source (default = SDF): directional bitmask (point / 8-tap) or occlusion field.
            #pragma multi_compile __ BITMASK_POINT BITMASK_8TAP OCC_FIELD
            // Ambient occlusion quality: bare default = off (no keyword), low, or high; modulates
            // the GI term.
            #pragma multi_compile __ SDF_AO_LQ SDF_AO_HQ
            // GI_OFF (default): direct lighting only. GI_VOXEL_TEXTURE: texture irradiance field +
            // AO (GiFieldUpdater). GI_VOXEL_BUFFER: the buffer GI read filter (BufferGiUpdater).
            // GI_UNITY: Unity's built-in indirect (SampleSH) - a component-less A/B baseline for
            // measuring the voxel GI's runtime cost against the engine's baked path.
            // Mutually exclusive - the active updater enables its keyword; GiMethodSelector drives the
            // component-less GI_UNITY / GI_OFF.
            #pragma multi_compile GI_OFF GI_VOXEL_TEXTURE GI_VOXEL_BUFFER GI_UNITY

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

            // Scene-wide exposure (EV stops), published as a global by the GiFieldUpdater (auto or manual).
            float _Exposure;
            // In-shader display transform, published as a global by the GI updater. Matches
            // AutoExposure.TonemapMode: 0 = Off (linear HDR out), 1 = Reinhard, 2 = AgX, 3 = ACES.
            int _Tonemap;

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

                // Main light: its shadow source is resolved inside GetShadow (VoxelDirectLighting) -
                // under the buffer GI, the per-field baked voxel sun-shadow is one of the selectable
                // sources there, so no shadow multiply is applied here.
                half3 lit = GetMainDirectLighting(light, IN.positionWS, N, albedo);
                lit += GetPointLightDirect(IN.positionWS, N, albedo);
                lit += GetSpotLightDirect(IN.positionWS, N, albedo);

                #if defined(GI_VOXEL_TEXTURE)
                    // Indirect lit (texture Voxel GI field) modulated by SDF ambient occlusion.
                    float3 gi = SampleVoxelGI(IN.positionWS, N);
                    float ao = GetAmbientOcclusionFromSdf(IN.positionWS, N);
                    lit += albedo * gi * ao;
                #elif defined(GI_VOXEL_BUFFER)
                    // Indirect lit (buffer GI) modulated by SDF ambient occlusion.
                    float3 gi = SampleBufferGI(IN.positionWS, N);
                    float ao = GetAmbientOcclusionFromSdf(IN.positionWS, N);
                    lit += albedo * gi * ao;
                #elif defined(GI_UNITY)
                    // A/B baseline: Unity's built-in indirect diffuse (ambient / light probes via
                    // SampleSH). No voxel fields are read or bound in this variant (honest perf +
                    // WebGPU-safe), and no voxel-GI exposure is applied below - so this measures the
                    // engine's baked indirect against the voxel GI with direct lighting held constant.
                    lit += albedo * SampleSH(N);
                #endif

                // Self-emission, added before the display transform so it is exposed/tonemapped
                // with the rest of the HDR scene.
                #if defined(_EMISSION_ON)
                    lit += _EmissionColor.rgb * SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, IN.uv).rgb;
                #endif

                // In-shader display transform (exposure + selected tonemap operator). _Tonemap == 0
                // (Off) outputs linear HDR for a post-processing stack instead. The branch is on a
                // global, so it's uniform across the frame - no warp divergence, effectively free.
                if (_Tonemap != 0) {
                    #if defined(GI_VOXEL_TEXTURE) || defined(GI_VOXEL_BUFFER)
                        lit *= exp2(_Exposure);   // exposure only meaningful when GI drives it
                    #endif
                    if (_Tonemap == 2) lit = AgxTonemap(lit);
                    else if (_Tonemap == 3) lit = AcesTonemap(lit);
                    else lit = ReinhardTonemap(lit);
                    // Interleaved-gradient-noise dither (~1/255) so the smooth GI gradients don't
                    // band when written to an 8-bit target. Skipped when Off (HDR output).
                    half ign = frac(52.9829189h * frac(dot(IN.positionHCS.xy, half2(0.06711056h, 0.00583715h))));
                    lit += (ign - 0.5h) * (1.0h / 255.0h);
                }

                return half4(lit, _BaseColor.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
