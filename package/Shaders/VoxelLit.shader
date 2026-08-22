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
        // Alpha cut (foliage). URP's own property names on purpose: the buffer-GI voxelizer reads
        // _AlphaClip / _Cutoff off the scene material (BufferGiUpdater.GetMaterialVoxelProps), so a
        // cutout material leaves its voxels EMPTY - leaves don't occupy or block GI rays - with no
        // extra plumbing. _Cull defaults to Back; foliage cards want Off (two-sided).
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha Clip", Float) = 0.0
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" "RenderType" = "Opaque" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]

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
            // Per-material: alpha cut. A keyword, never a uniform branch - clip() takes the WHOLE
            // kernel off early-Z (the depth write moves behind the fragment), and this shader is
            // occupancy/early-Z bound, so opaque materials must not compile the discard in at all.
            #pragma shader_feature_local_fragment _ALPHATEST_ON

            // URP
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Surface lighting: direct + selectable shadow source + SDF AO (pulls its own
            // shadow/AO/volume headers). BufferGi is the runtime GI read (guarded to its variant).
            // Each is self-contained, so these two are all the lit pass needs.
            #include "Packages/com.lotecsoftware.voxel-lighting/ShaderLibrary/VoxelDirectLighting.hlsl"
            #include "Packages/com.lotecsoftware.voxel-lighting/ShaderLibrary/BufferGiRead.hlsl"
            // Display-transform tonemap operators (Reinhard / AgX / ACES), selected by the TONEMAP_* keyword.
            #include "Packages/com.lotecsoftware.voxel-lighting/ShaderLibrary/Tonemap.hlsl"

            // GI_OFF (default): direct lighting only. GI_VOXEL_BUFFER: the buffer GI read filter
            // (BufferGiUpdater). GI_UNITY: Unity's built-in indirect (SampleSH) - a component-less A/B
            // baseline for measuring the voxel GI's runtime cost against the engine's baked path.
            // Mutually exclusive - the buffer GI updater enables its keyword; GiMethodSelector drives
            // the component-less GI_UNITY / GI_OFF.
            #pragma multi_compile GI_OFF GI_VOXEL_BUFFER GI_UNITY
            // Display transform, COMPILE-TIME (not a uniform branch). A fragment kernel's register
            // allocation covers every path it contains, so keeping all four options in one kernel sized
            // it for AgX's worst case (two 3x3 matrices, a degree-6 polynomial, log2/pow). That capped
            // occupancy on a shader whose cost is GI-tap memory latency, and measured ~1ms/frame even
            // while the CHEAP operator ran - Reinhard alone in the kernel costs 0.3ms, all three 1.3ms.
            // TONEMAP_OFF (the default) compiles the whole block out: linear HDR for a post stack.
            #pragma multi_compile TONEMAP_OFF TONEMAP_REINHARD TONEMAP_AGX TONEMAP_ACES
            // SINGLE-mode irradiance tap filter (BufferGiUpdater.SingleTapFilter). Bare default = the
            // Fast one-tap read, compiled byte for byte as before; the keyword selects the axis-snapped
            // n^2-weighted taps (see BgiSampleFieldTexture). Compile-time for the same reason as
            // TONEMAP_* - a fragment kernel is sized for every path it contains, and the Fast variant's
            // whole purpose is to be the cheapest read available, so it must not pay the other's
            // register pressure. _fragment: the tap is fragment-only, so the vertex variants don't double.
            #pragma multi_compile_fragment __ BGI_TAP_AXIS_SNAPPED

            // ANALYSIS views (BufferGiUpdater.DebugView). Fragment-only and OFF by default, so a
            // shipping build never compiles the solid-weight walk or its occupancy reads in - see
            // LightingKeywords.BgiDebug for why this is a keyword and not a branch on the uniform.
            #pragma multi_compile_fragment __ BGI_DEBUG_VIEWS

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
                half _Cutoff;
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

            // The interpolated VERTEX normal, never the normal map. Everything that steps into the
            // voxel grid must use this: the grid has no idea a normal map exists, so perturbing the
            // lookup by one makes the offset - and the face-plane axis pick - jump per texel. With a
            // point-sampled bitmask that lands the sample in a different voxel and punches unshadowed
            // holes along shadow edges; the old 8-tap only blurred the same error into a gradient.
            half3 GetGeometricNormal(v2f input) {
                #ifdef _NORMALMAP
                    return normalize(input.normal);
                #else
                    return normalize(input.normalWS);
                #endif
            }

            half4 frag(v2f IN) : SV_Target
            {
                // Base map first, so the alpha cut can kill the fragment before any lighting,
                // normal-map or GI work is done for it.
                half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                #if defined(_ALPHATEST_ON)
                    // Foliage cutout. Same alpha as URP Lit (base color x base map), so a material
                    // authored for URP's cutout surface type cuts identically here.
                    clip(baseTex.a * _BaseColor.a - _Cutoff);
                #endif

                Light light = GetMainLight();
                half3 N = GetNormal(IN);
                // Vertex normal, kept alongside the shading normal: the voxel lookups need it (the grid
                // knows nothing about normal maps) and so does the geometric gate on direct light, which
                // stops a normal map from lighting texels on a surface that geometrically faces away
                // from the light (see GetGeometricGate).
                half3 geoN = GetGeometricNormal(IN);

                half3 albedo = _BaseColor.rgb * baseTex.rgb;

                // Main light. Under the buffer GI, BgiSampleFaceAoShadow is the SOLE authority for the
                // main-light sun shadow (Off = no shadow, Baked = baked value, Sdf = SDF raymarch) and
                // also resolves the baked AO from the same face read - so the shadow feeds straight into
                // the main light and never falls through to GetShadow (SDF/bitmask/occlusion). Other GI
                // modes resolve the main-light shadow inside GetShadow as before.
                #if defined(GI_VOXEL_BUFFER)
                    half bgiAo, bgiShadow;
                    // Geometric normal, not N: this is a voxel-grid lookup, not a shading term.
                    BgiSampleFaceAoShadow(IN.positionWS, geoN, light.direction, bgiAo, bgiShadow);
                    half3 lit = GetMainDirectLightingShadow(light.direction, light.color, IN.positionWS, N, geoN, albedo, bgiShadow);
                #else
                    half3 lit = GetMainDirectLighting(light.direction, light.color, IN.positionWS, N, geoN, albedo);
                #endif
                lit += GetPointLightDirect(IN.positionWS, N, geoN, albedo);
                lit += GetSpotLightDirect(IN.positionWS, N, geoN, albedo);

                // `lit` is exactly the summed DIRECT term at this point - indirect and emission are
                // added below - so the analysis mute applies here and nowhere else. One multiply,
                // no branch, no variant; unbound it reads 0 and this is the identity. See
                // _VoxelDirectMute in VoxelDirectLighting.hlsl for why it is a mute and not a scale.
                lit *= VoxelDirectGain();

                #if defined(GI_VOXEL_BUFFER)
                    // Indirect lit (buffer GI) modulated by the buffer's OWN baked AO (bgiAo, resolved
                    // above together with the sun shadow). No SDF AO here - the buffer GI carries its
                    // own openness, so this path no longer samples the SDF texture at all.
                    // Geometric normal, not N, for the same reason as the face read above: it only picks
                    // the voxel layer to read, and the grid knows nothing about normal maps. Feeding it
                    // the per-texel N made the sampled layer jump within a single flat face, which
                    // pushed the tap back into the dark solid cell in blotches.
                    half3 bgiDirect   = lit; // the summed, muted DIRECT term - kept for the analysis views
                    half3 bgiIndirect = albedo * BgiGatherIndirect(IN.positionWS, geoN) * bgiAo;
                    lit += bgiIndirect;

                    // ANALYSIS views (BufferGiUpdater.DebugView). Isolating a term here rather than in
                    // the voxel-cube viewer is the point: this is the value AFTER the fragment's own
                    // tap, so comparing it against the cubes (which show the same quantity per VOXEL)
                    // tells you whether an artifact came out of the bake or out of the read.
                    #if defined(BGI_DEBUG_VIEWS)
                    uint bgiDbg = (uint)_BgiDebugView;
                    if (bgiDbg != 0u) {
                        // Contamination map: how much of THIS pixel's GI footprint sat on solid cells.
                        // Raw, like the other scalars - it is a weight, not a radiance.
                        if (bgiDbg == 5u) return half4(((half)BgiTapSolidWeight(IN.positionWS, geoN)).xxx, 1.0h);
                        // Scalars are returned RAW - no exposure, no tonemap - so they read as literal
                        // 0..1 greyscale and can be eyedropped against the cube colours directly.
                        // Tonemapping them would remap the very numbers being compared.
                        if (bgiDbg == 2u) return half4(bgiShadow.xxx, 1.0h); // sun visibility
                        if (bgiDbg == 3u) return half4(bgiAo.xxx,     1.0h); // baked AO
                        // The HDR terms keep the display transform below, so they read like the normal
                        // image with the other term removed rather than like a different scene.
                        if (bgiDbg == 1u) lit = bgiIndirect; // GI only
                        if (bgiDbg == 4u) lit = bgiDirect;   // direct only
                    }
                    #endif // BGI_DEBUG_VIEWS
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

                // Opaque pass (no Blend, RenderType=Opaque): the output alpha is never used for
                // blending, it just lands in the target's alpha channel - which the XR compositor
                // and alpha-reading post-FX do look at. So write 1, never _BaseColor.a: under
                // _ALPHATEST_ON that value is a live cutoff input the author tunes, and leaking it
                // into the eye buffer would make a cut adjustment silently change compositing.
                return half4(lit, 1.0h);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
