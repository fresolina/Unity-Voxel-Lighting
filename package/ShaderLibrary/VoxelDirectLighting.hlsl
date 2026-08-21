
#ifndef LOTECSOFTWARE_VOXEL_DIRECT_LIGHTING_INCLUDED
#define LOTECSOFTWARE_VOXEL_DIRECT_LIGHTING_INCLUDED

// ENGINE-AGNOSTIC, like every header in this folder: HLSL intrinsics and our own headers only. No
// URP includes, no vertex/fragment semantics, no Core.hlsl texture macros. That means any of these
// can be included from a fragment shader, a compute shader or the voxelize raster alike.
//
// The engine boundary is the .shader / .compute ENTRY POINTS. VoxelLit.shader includes URP's
// Core.hlsl and Lighting.hlsl and calls GetMainLight(), then hands this library plain values.
// Guarded by Shaders/Compute/BufferGiCommonCanary.compute, which includes every header here and fails
// moment one acquires an engine dependency - do not "fix" that by adding an include to the canary.


// Surface lighting module: direct lighting (sun + local point/spot lights) and the selectable
// shadow source (SDF / bitmask / occlusion field). Self-contained - it includes every shadow
// header it dispatches to, so the lit shader only needs this.
// Needs no URP header of its own: the caller resolves the light (GetMainLight in a URP shader) and
// passes its direction and colour in.

#include "VoxelSdfShadows.hlsl" // GetShadowFromSdf (the shadow source for this path)
#include "VoxelOcclusion.hlsl"  // GetBitmaskShadow / GetOccFieldShadow - buffer-GI per-field shadow
#include "Math.hlsl"           // GetLightRangeAttenuation, shared with the GI solve
                                // sources; also keeps their globals declared in every variant (WebGPU-safe)

#ifndef MAX_POINT_LIGHTS
#define MAX_POINT_LIGHTS 4
#endif
#ifndef MAX_SPOT_LIGHTS
#define MAX_SPOT_LIGHTS 4
#endif

// Local light arrays, published as globals by LightingManager (independent of GI).
uint _PointLightCount;
float4 _PointLightPositionRange[MAX_POINT_LIGHTS];
float4 _PointLightColor[MAX_POINT_LIGHTS];
uint _SpotLightCount;
float4 _SpotLightPositionRange[MAX_SPOT_LIGHTS];
float4 _SpotLightDirectionAngleScale[MAX_SPOT_LIGHTS];
float4 _SpotLightColorAngleOffset[MAX_SPOT_LIGHTS];

// Analysis toggle: fade out ALL direct lighting (main + point + spot) at the fragment, leaving the
// indirect term - and emission - to be viewed on their own. The point is A/B'ing the GI itself: with
// direct in the frame, a change worth a few percent of indirect is buried under a term an order of
// magnitude larger, and a leak through a thin wall is impossible to attribute to the bounce or the
// sun. Applied to the summed direct term in VoxelLit, so it costs one multiply and NO extra shader
// variant (unlike BGI_TAP_AXIS_SNAPPED, which needs a keyword because it is a whole second tap
// implementation with its own register footprint - a scalar mute has no such cost).
//
// MUTE, not scale, so that the UNBOUND value is the safe one: an undeclared global reads 0 in HLSL,
// and a "_DirectScale" would then multiply all direct lighting to BLACK in any project that never
// publishes it. 0 = normal lighting, 1 = indirect only.
//
// The SOLVE is untouched - this is purely the fragment's own direct term. The GI still receives and
// bounces the sun exactly as before, which is the whole point: what remains on screen is the bounce.
// Auto-exposure is likewise unaffected, since it measures the GI field's air voxels (CSAverageLuminance)
// and not the framebuffer - so an A/B pair taken with this on stays exposure-matched.
float _VoxelDirectMute;

// 1 = full direct lighting, 0 = fully muted. Clamped so an out-of-range publish cannot amplify.
inline half VoxelDirectGain() {
    return (half)saturate(1.0 - _VoxelDirectMute);
}

// Resolve the shadow term for a surface point: always the SDF raymarch. (The baked bitmask /
// occlusion-field sources are buffer-GI-only now - selected per field by BgiSampleFaceAoShadow, not
// here.) Under the buffer GI the main-light sun-shadow is resolved entirely by BgiSampleFaceAoShadow
// (Off = none, Baked, Sdf, OcclusionField, Bitmask) and fed to GetMainDirectLightingShadow, so the main
// light NEVER routes through here; this serves the non-buffer GI modes' main light plus all local lights.
// `lightDir` must be unit length (it always is at every call site), so no normalize here.
inline half GetShadow(float3 worldPos, float3 lightDir, float3 normal) {
    return GetShadowFromSdf(lightDir, worldPos, 1.0e+10f);
}

// Finite-distance shadow for local (point/spot) lights: always SDF, so a blocker behind
// the light does not shadow the surface (the bitmask field stores occlusion to infinity).
// Per-material opt-out: _RECEIVE_LOCAL_SHADOWS_OFF compiles out the per-light SDF march.
inline half GetShadow(float3 worldPos, float3 lightDir, float3 normal, float maxDistance) {
    #if defined(_RECEIVE_LOCAL_SHADOWS_OFF)
        return 1.0h;
    #else
        return GetShadowFromSdf(lightDir, worldPos, maxDistance);
    #endif
}

// GEOMETRIC GATE. N.L uses the normal-MAPPED normal, as every renderer does - but on a surface whose
// real (vertex) normal points away from the light, a normal map can still tilt individual texels back
// toward it and light them at full strength. In a shadow-mapped pipeline that never shows, because
// such a surface is occluded by itself and its shadow term is 0. Nothing here provides that: the voxel
// sun shadow is POSITIONAL (it answers "does sunlight reach this point", and next to a back-facing
// wall it does), the SDF source fails open when no march budget is published, and VoxelLit never
// samples URP's shadow map. So gate direct light by the geometric N.L as well.
//
// It matters most exactly where it looks worst: with a near-overhead sun every vertical wall sits at
// |N.L| ~ 0.09, a hair off the terminator, so a brick normal map swings whole mortar lines to lit and
// a black wall grows white sparkles.
//
// Ramped rather than a hard step: a step facets the terminator on low-poly curved geometry, since the
// vertex normal is only piecewise-linear across a triangle. 8 = full light once the geometric normal
// is ~7 degrees past the terminator, which is under the tilt any real normal map applies.
#define VOXEL_GEOMETRIC_GATE_SHARPNESS 8.0h

inline half GetGeometricGate(half3 geoNormal, float3 lightDir) {
    return saturate(dot(geoNormal, (half3)lightDir) * VOXEL_GEOMETRIC_GATE_SHARPNESS);
}

// `lightDir` is unit length in every caller; kept float3 because it is also what the SDF march
// steps along, where a half direction's ~1e-3 relative error would drift the ray by centimetres
// over a room-scale distance. The N.L term itself runs in fp16.
// `geoNormal` is the interpolated VERTEX normal (never the normal map) - see GetGeometricGate.
inline half3 GetDirectLighting(float3 worldPos, half3 normal, half3 geoNormal, half3 albedo, float3 lightDir, half3 lightColor, half attenuation) {
    half ndotl = saturate(dot(normal, (half3)lightDir));
    if (ndotl <= 0.0h)
        return 0.0h;
    half gate = GetGeometricGate(geoNormal, lightDir);
    if (gate <= 0.0h)
        return 0.0h; // fully back-facing: skip the shadow march too

    half shadow = GetShadow(worldPos, lightDir, normal);
    return albedo * lightColor * (ndotl * gate * shadow * attenuation);
}

inline half3 GetDirectLighting(float3 worldPos, half3 normal, half3 geoNormal, half3 albedo, float3 lightDir, half3 lightColor, half attenuation, float shadowDistance) {
    half ndotl = saturate(dot(normal, (half3)lightDir));
    if (ndotl <= 0.0h)
        return 0.0h;
    half gate = GetGeometricGate(geoNormal, lightDir);
    if (gate <= 0.0h)
        return 0.0h;

    half shadow = GetShadow(worldPos, lightDir, normal, shadowDistance);
    return albedo * lightColor * (ndotl * gate * shadow * attenuation);
}

// Takes the light DIRECTION and COLOR rather than URP's Light struct. Those two fields were the only
// thing this header ever read from it, and taking them plainly is what keeps the whole shader library
// engine-agnostic: only the .shader entry points call GetMainLight() and know URP exists.
inline half3 GetMainDirectLighting(half3 lightDir, half3 lightColor, float3 worldPos, half3 normal, half3 geoNormal, half3 albedo) {
    return GetDirectLighting(worldPos, normal, geoNormal, albedo, lightDir, lightColor, 1.0h);
}

// Main directional light with an externally-resolved shadow term - used by the buffer-GI path, which
// computes the baked sun visibility together with the baked AO in a single face read
// (BgiSampleFaceAoShadow) and passes it in here, so the shadow is not resolved again via GetShadow.
inline half3 GetMainDirectLightingShadow(half3 lightDir, half3 lightColor, float3 worldPos, half3 normal, half3 geoNormal, half3 albedo, half shadow) {
    half ndotl = saturate(dot(normal, lightDir)); // callers hand back a unit direction
    if (ndotl <= 0.0h)
        return 0.0h;
    return albedo * lightColor * (ndotl * GetGeometricGate(geoNormal, lightDir) * shadow);
}

inline half3 GetPointLightDirect(float3 worldPos, half3 normal, half3 geoNormal, half3 albedo) {
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
        float3 lightDir = toLight * invDistance; // already unit - GetDirectLighting does not re-normalize
        half attenuation = GetLightRangeAttenuation(surfaceDistSq, rangeSq);
        totalLight += GetDirectLighting(worldPos, normal, geoNormal, albedo, lightDir, (half3)_PointLightColor[lightIndex].rgb, attenuation, distanceToLight);
    }

    return totalLight;
}

inline half3 GetSpotLightDirect(float3 worldPos, half3 normal, half3 geoNormal, half3 albedo) {
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
        float3 lightDir = toLight * invDistance; // already unit - GetDirectLighting does not re-normalize
        float4 directionAngleScale = _SpotLightDirectionAngleScale[lightIndex];
        float4 colorAngleOffset = _SpotLightColorAngleOffset[lightIndex];
        // Cone falloff is a unitless 0..1 term: fp16 throughout. The angle scale can be large for a
        // narrow cone, so the dot/scale/offset chain is formed in fp32 and narrowed once.
        half coneAttenuation = (half)saturate(dot(-lightDir, directionAngleScale.xyz) * directionAngleScale.w + colorAngleOffset.a);
        if (coneAttenuation <= 0.0h)
            continue;

        half attenuation = GetLightRangeAttenuation(surfaceDistSq, rangeSq) * (coneAttenuation * coneAttenuation);
        totalLight += GetDirectLighting(worldPos, normal, geoNormal, albedo, lightDir, (half3)colorAngleOffset.rgb, attenuation, distanceToLight);
    }

    return totalLight;
}

#endif
