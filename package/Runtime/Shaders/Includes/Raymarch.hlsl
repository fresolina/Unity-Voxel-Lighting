#ifndef LOTEC_RAYMARCH_INCLUDED
#define LOTEC_RAYMARCH_INCLUDED

// LAYER: FRAGMENT-SIDE - only the lit shader path uses it. Engine-free as it stands, so it would
// compile in a compute shader, but nothing guarantees that: it is free to take a URP dependency.
// Anything compute must be able to include belongs in the COMMON set instead (see BufferGiField.hlsl).

#include "Math.hlsl"

// `lit` is a 0..1 visibility factor - fp16 is ample for it and it is what every consumer
// (shadow terms, occlusion bakes) wants. Positions/distances stay fp32: `t` accumulates over
// hundreds of steps and `hitPos`/`uvw` are world/volume coordinates, where fp16's ~3 decimal
// digits would quantize the march into visible stair-stepping.
struct RayMarchResult {
    half lit;
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
    result.lit = 1.0h;

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
            result.lit = 0.0h;
            break;
        }

        // Penumbra estimate. The ratio is formed in fp32 (d and t are world distances) and
        // saturated before narrowing: with a large `softness` the product can exceed fp16's
        // 65504 and become Inf, and saturate() is a no-op on the result either way - `lit`
        // starts at 1, so min() with anything >= 1 never changes it.
        result.lit = min(result.lit, (half)saturate(softness * d * rcp(max(t, 1e-6))));
        if (result.lit < 0.01h) {
            result.hitPos = worldPos + dir * t;
            result.lit = 0.0h;
            break;
        }

        t += max(d, minStep);
    }

    if (result.lit > 0.0h) {
        if (t <= maxDistance) {
            // Ran out of steps before the ray reached maxDistance. The path
            // has not been verified clear, so conservatively treat it as
            // occluded to avoid light leaking through complex geometry.
            result.hitPos = worldPos + dir * t;
            result.lit = 0.0h;
        } else {
            result.hitPos = worldPos + dir * min(t, maxDistance);
            result.lit = saturate(result.lit);
        }
    }
    return result;
}

// Convenience overloads matching the old signatures.
half RayMarchTex3D(
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

half RayMarchTex3D(
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
