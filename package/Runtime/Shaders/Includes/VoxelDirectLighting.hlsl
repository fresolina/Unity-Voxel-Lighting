#ifndef LOTECSOFTWARE_VOXEL_DIRECT_LIGHTING_INCLUDED
#define LOTECSOFTWARE_VOXEL_DIRECT_LIGHTING_INCLUDED

// Direct lighting (sun + local point/spot lights) with SDF ray-marched shadows.
//
// Baseline dependency: VoxelSdfShadows.hlsl (included below) for GetShadowFromSdf.
// Assumes the including shader has already included URP Lighting.hlsl (for the Light
// type). The bitmask / occlusion-field shadow modes are optional: their GetShadow
// branches only compile when the matching keyword is set, in which case the includer
// must also have included VoxelOcclusionDirection.hlsl / VoxelOcclusionField.hlsl
// before this header. The minimal VoxelLit shader defines none of those keywords, so it
// compiles the SDF path with just this module.

#include "VoxelSdfShadows.hlsl"

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

// Resolve the shadow term for a surface point. Defaults to SDF; the bitmask /
// occlusion-field modes are selected by their compile-time keywords.
inline half GetShadow(float3 worldPos, float3 lightDir, float3 normal) {
    #if defined(OCC_FIELD)
        return GetOccFieldShadow(worldPos, normal);
    #elif defined(BITMASK_POINT) || defined(BITMASK_8TAP)
        return GetBitmaskShadow(worldPos, normal);
    #else
        return GetShadowFromSdf(normalize(lightDir), worldPos, 1.0e+10f);
    #endif
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
