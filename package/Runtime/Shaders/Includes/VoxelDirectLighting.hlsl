#ifndef LOTECSOFTWARE_VOXEL_DIRECT_LIGHTING_INCLUDED
#define LOTECSOFTWARE_VOXEL_DIRECT_LIGHTING_INCLUDED

// Surface lighting module: direct lighting (sun + local point/spot lights) and the selectable
// shadow source (SDF / bitmask / occlusion field). Self-contained - it includes every shadow
// header it dispatches to, so the lit shader only needs this.
// Assumes the including shader has already included URP Lighting.hlsl (for the Light type).

#include "VoxelSdfShadows.hlsl" // GetShadowFromSdf (the shadow source for this path)
#include "VoxelOcclusion.hlsl"  // GetBitmaskShadow / GetOccFieldShadow - buffer-GI per-field shadow
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

// Resolve the shadow term for a surface point: always the SDF raymarch. (The baked bitmask /
// occlusion-field sources are buffer-GI-only now - selected per field by BgiSampleFaceAoShadow, not
// here.) Under the buffer GI the main-light sun-shadow is resolved entirely by BgiSampleFaceAoShadow
// (Off = none, Baked, Sdf, OcclusionField, Bitmask) and fed to GetMainDirectLightingShadow, so the main
// light NEVER routes through here; this serves the non-buffer GI modes' main light plus all local lights.
inline half GetShadow(float3 worldPos, float3 lightDir, float3 normal) {
    return GetShadowFromSdf(normalize(lightDir), worldPos, 1.0e+10f);
}

// Finite-distance shadow for local (point/spot) lights: always SDF, so a blocker behind
// the light does not shadow the surface (the bitmask field stores occlusion to infinity).
// Per-material opt-out: _RECEIVE_LOCAL_SHADOWS_OFF compiles out the per-light SDF march.
inline half GetShadow(float3 worldPos, float3 lightDir, float3 normal, float maxDistance) {
    #if defined(_RECEIVE_LOCAL_SHADOWS_OFF)
        return 1.0h;
    #else
        return GetShadowFromSdf(normalize(lightDir), worldPos, maxDistance);
    #endif
}

inline float GetLocalLightRangeAttenuation(float distSq, float rangeSq) {
    // Inverse-square distance falloff (physical light intensity) combined with a smooth
    // range window that fades to zero at max range.
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

// Main directional light with an externally-resolved shadow term - used by the buffer-GI path, which
// computes the baked sun visibility together with the baked AO in a single face read
// (BgiSampleFaceAoShadow) and passes it in here, so the shadow is not resolved again via GetShadow.
inline half3 GetMainDirectLightingShadow(Light light, float3 worldPos, half3 normal, half3 albedo, half shadow) {
    half3 normalizedLightDir = normalize(light.direction);
    half ndotl = saturate(dot(normal, normalizedLightDir));
    if (ndotl <= 0.0h)
        return 0.0h;
    return albedo * light.color * (ndotl * shadow);
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

#endif
