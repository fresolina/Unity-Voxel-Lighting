#ifndef LOTEC_RAYMARCH_INCLUDED
#define LOTEC_RAYMARCH_INCLUDED

#include "Math.hlsl"

struct RayMarchResult {
    float lit;
    float3 hitPos;
};

// General-purpose SDF raymarch used by both runtime and baker.
// Returns 0..1: total occlusion hit to no occlusion.
RayMarchResult RayMarchTex3DSdf(
    Texture3D<float> sdfTex,
    SamplerState sampler_SdfTex,
    float3 worldPos,
    float3 dir,
    float3 boundsMin,
    float3 boundsSize,
    float startOffset,
    float maxDistance,
    float epsilon,
    float minStep,
    int maxSteps,
    half softness
) {
    RayMarchResult result = (RayMarchResult)0;
    result.lit = 1.0;

    float3 size = max(boundsSize, 1e-6);
    float3 invSize = rcp(size);
    float3 rayOrigin = (worldPos - boundsMin) * invSize;
    float3 dirUvw = dir * invSize;

    AabbHit aabb = RayIntersectUnitAabb(rayOrigin, dirUvw);
    maxDistance = aabb.hit ? min(maxDistance, aabb.tExit) : 0.0;
    float t = aabb.hit ? max(startOffset, aabb.tEnter) : 1.0;

    [loop]
    for (int stepIndex = 0; stepIndex < maxSteps; stepIndex++) {
        if (t > maxDistance)
            break;

        float3 uvw = rayOrigin + dirUvw * t;
        float d = sdfTex.SampleLevel(sampler_SdfTex, uvw, 0).r;

        if (d <= epsilon) {
            result.hitPos = worldPos + dir * t;
            result.lit = 0.0;
            break;
        }

        result.lit = min(result.lit, softness * d * rcp(max(t, 1e-6)));
        if (result.lit < 0.01) {
            result.hitPos = worldPos + dir * t;
            result.lit = 0.0;
            break;
        }

        t += max(d, minStep);
    }

    if (result.lit > 0.0) {
        result.hitPos = worldPos + dir * min(t, maxDistance);
        result.lit = saturate(result.lit);
    }
    return result;
}

// Convenience overloads matching the old signatures.
float RayMarchTex3D(
    Texture3D<float> sdfTex,
    SamplerState sampler_SdfTex,
    float3 worldPos,
    float3 dir,
    float3 boundsMin,
    float3 boundsSize,
    float startOffset,
    float maxDistance,
    float epsilon,
    float minStep,
    int maxSteps,
    half softness,
    out float3 hitPos
) {
    RayMarchResult r = RayMarchTex3DSdf(sdfTex, sampler_SdfTex, worldPos, dir, boundsMin, boundsSize, startOffset, maxDistance, epsilon, minStep, maxSteps, softness);
    hitPos = r.hitPos;
    return r.lit;
}

float RayMarchTex3D(
    Texture3D<float> sdfTex,
    SamplerState sampler_SdfTex,
    float3 worldPos,
    float3 dir,
    float3 boundsMin,
    float3 boundsSize,
    float startOffset,
    float maxDistance,
    float epsilon,
    float minStep,
    int maxSteps,
    half softness
) {
    RayMarchResult r = RayMarchTex3DSdf(sdfTex, sampler_SdfTex, worldPos, dir, boundsMin, boundsSize, startOffset, maxDistance, epsilon, minStep, maxSteps, softness);
    return r.lit;
}
#endif
