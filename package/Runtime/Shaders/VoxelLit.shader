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
            // 4.5 (SM5.0) required: the buffer GI reads StructuredBuffers in the fragment stage
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
            // shadow/AO/volume headers). BufferGi is the runtime GI read (guarded to its variant).
            // Each is self-contained, so these two are all the lit pass needs.
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/VoxelDirectLighting.hlsl"
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/BufferGi.hlsl"
            // Display-transform tonemap operators (Reinhard / AgX / ACES), selected by the TONEMAP_* keyword.
            #include "Packages/com.lotecsoftware.voxel-lighting/Runtime/Shaders/Includes/Tonemap.hlsl"

            // GI_OFF (default): direct lighting only. GI_VOXEL_BUFFER: the buffer GI read filter
            // (BufferGiUpdater). GI_UNITY: Unity's built-in indirect (SampleSH) - a component-less A/B
            // baseline for measuring the voxel GI's runtime cost against the engine's baked path.
            // Mutually exclusive - the buffer GI updater enables its keyword; GiMethodSelector drives
            // the component-less GI_UNITY / GI_OFF.
            #pragma multi_compile GI_OFF GI_VOXEL_BUFFER GI_UNITY
            // Buffer-GI fragment read source. DEFAULT (no keyword): one hardware-trilinear tap of the
            // mirrored irradiance Texture3D - the fast path on Adreno/Quest (the GPU does the
            // interpolation the SSBO gather recomputes in software). BGI_SSBO_READ flips back to the
            // original 9-tap StructuredBuffer gather, kept as an on-device A/B baseline. Driven by BufferGiUpdater.
            #pragma multi_compile __ BGI_SSBO_READ
            // Display transform, COMPILE-TIME (not a uniform branch). A fragment kernel's register
            // allocation covers every path it contains, so keeping all four options in one kernel sized
            // it for AgX's worst case (two 3x3 matrices, a degree-6 polynomial, log2/pow). That capped
            // occupancy on a shader whose cost is GI-tap memory latency, and measured ~1ms/frame even
            // while the CHEAP operator ran - Reinhard alone in the kernel costs 0.3ms, all three 1.3ms.
            // TONEMAP_OFF (the default) compiles the whole block out: linear HDR for a post stack.
            #pragma multi_compile TONEMAP_OFF TONEMAP_REINHARD TONEMAP_AGX TONEMAP_ACES

            // Colours and factors are declared `half` (matching URP's own UnityPerMaterial layout in
            // LitInput.hlsl) so the constants arrive in fp16 registers and the shading chain never
            // gets promoted back to fp32 by a stray float operand.
            CBUFFER_START(UnityPerMaterial)
                TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
                TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
                half4 _BaseColor;
                half _Roughness;
                half _Emission;
                TEXTURE2D(_EmissionMap); SAMPLER(sampler_EmissionMap);
                half4 _EmissionColor;
                half _ReceiveLocalShadows;
            CBUFFER_END

            // Scene-wide exposure as a LINEAR multiplier - exp2(EV) precomputed on the CPU by
            // AutoExposure, so the fragment doesn't spend a transcendental per pixel on a uniform.
            float _ExposureLinear;

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
                    // No normalize here: interpolation denormalizes anyway, and GetNormal
                    // re-normalizes all three basis vectors in the fragment.
                    OUT.normal = (half3)TransformObjectToWorldNormal(IN.normalOS);
                    OUT.tangent = (half3)TransformObjectToWorldDir(IN.tangent.xyz);
                    half tangentSign = (half)(IN.tangent.w * GetOddNegativeScale());
                    OUT.bitangent = cross(OUT.normal, OUT.tangent) * tangentSign;
                #else
                    OUT.normalWS = (half3)TransformObjectToWorldNormal(IN.normalOS);
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

                // Main light. Under the buffer GI, BgiSampleFaceAoShadow is the SOLE authority for the
                // main-light sun shadow (Off = no shadow, Baked = baked value, Sdf = SDF raymarch) and
                // also resolves the baked AO from the same face read - so the shadow feeds straight into
                // the main light and never falls through to GetShadow (SDF/bitmask/occlusion). Other GI
                // modes resolve the main-light shadow inside GetShadow as before.
                #if defined(GI_VOXEL_BUFFER)
                    half bgiAo, bgiShadow;
                    BgiSampleFaceAoShadow(IN.positionWS, N, light.direction, bgiAo, bgiShadow);
                    half3 lit = GetMainDirectLightingShadow(light, IN.positionWS, N, albedo, bgiShadow);
                #else
                    half3 lit = GetMainDirectLighting(light, IN.positionWS, N, albedo);
                #endif
                lit += GetPointLightDirect(IN.positionWS, N, albedo);
                lit += GetSpotLightDirect(IN.positionWS, N, albedo);

                #if defined(GI_VOXEL_BUFFER)
                    // Indirect lit (buffer GI) modulated by the buffer's OWN baked AO (bgiAo, resolved
                    // above together with the sun shadow). No SDF AO here - the buffer GI carries its
                    // own openness, so this path no longer samples the SDF texture at all.
                    lit += albedo * BgiGatherIndirect(IN.positionWS, N) * bgiAo;
                #elif defined(GI_UNITY)
                    // (SampleSH is fp32 in URP; narrowed once so the add stays in fp16.)
                    // A/B baseline: Unity's built-in indirect diffuse (ambient / light probes via
                    // SampleSH). No voxel fields are read or bound in this variant (honest perf +
                    // WebGPU-safe), and no voxel-GI exposure is applied below - so this measures the
                    // engine's baked indirect against the voxel GI with direct lighting held constant.
                    lit += albedo * (half3)SampleSH(N);
                #endif

                // Self-emission, added before the display transform so it is exposed/tonemapped
                // with the rest of the HDR scene.
                #if defined(_EMISSION_ON)
                    lit += _EmissionColor.rgb * SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, IN.uv).rgb;
                #endif

                // In-shader display transform (exposure + tonemap operator + dither). Compiled out
                // entirely under TONEMAP_OFF, which outputs linear HDR for a post-processing stack; only
                // ONE operator is ever present in a variant, which is what keeps the register allocation
                // (and therefore occupancy) proportional to the operator actually in use.
                #if !defined(TONEMAP_OFF)
                    #if defined(GI_VOXEL_BUFFER)
                        lit *= (half)_ExposureLinear;   // exposure only meaningful when GI drives it
                    #endif
                    #if defined(TONEMAP_AGX)
                        lit = AgxTonemap(lit);
                    #elif defined(TONEMAP_ACES)
                        lit = AcesTonemap(lit);
                    #else
                        lit = ReinhardTonemap(lit);
                    #endif
                    // Interleaved-gradient-noise dither (~1/255) so the smooth GI gradients don't
                    // band when written to an 8-bit target. Skipped when Off (HDR output).
                    // This is the one chain that has to stay fp32 (three scalar ops, no register
                    // pressure): the frac() cascade needs the low bits of a screen-pixel-scale
                    // product. At a 1080p x coordinate the intermediate is ~140, where fp16's ulp is
                    // 0.125 - which collapses the noise to ~32 levels and reintroduces the very
                    // banding this exists to hide. Only the final +-1/255 offset is fp16.
                    float ign = frac(52.9829189 * frac(dot(IN.positionHCS.xy, float2(0.06711056, 0.00583715))));
                    lit += (half)((ign - 0.5) * (1.0 / 255.0));
                #endif

                return half4(lit, _BaseColor.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
