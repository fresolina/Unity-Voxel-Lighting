#ifndef LOTEC_RAYMARCH_INCLUDED
#define LOTEC_RAYMARCH_INCLUDED

// RayIntersectUnitAabb
#include "Math.hlsl"

// General-purpose SDF raymarch used by both runtime and baker.
// Returns 0..1: total occlusion hit to no occlusion.
float RayMarchTex3D(
    Texture3D<float> sdfTex,
    SamplerState sampler_SdfTex,
    float3 worldPos,
    float3 dir,
    float3 boundsMin,
    float3 boundsSize,
    float startOffset,
    float maxDistance, // Use to not travel past the flashlight
    float epsilon,
    float minStep,
    int maxSteps,
    half softness // Lower = softer shadows
) {
    float3 size = max(boundsSize, 1e-6);
    float3 invSize = rcp(size);
    // Transform ray into SDF UVW space once.
    float3 rayOrigin = (worldPos - boundsMin) * invSize;
    float3 dirUvw = dir * invSize;

    // Ensure we are inside the SDF bounds
    float tAabbEnter, tAabbExit;
    if (!RayIntersectUnitAabb(rayOrigin, dirUvw, tAabbEnter, tAabbExit)) {
        return 1.0;
    }
    maxDistance = min(maxDistance, tAabbExit);
    float t = max(startOffset, tAabbEnter);
    float lit = 1.0;

    [loop]
    for (int stepIndex = 0; stepIndex < maxSteps; stepIndex++) {
        // Traveled max distance
        if (t > maxDistance) {
            return lit;
        }

        float3 uvw = rayOrigin + dirUvw * t;

        float d = sdfTex.SampleLevel(sampler_SdfTex, uvw, 0).r;

        // Inside surface -> full shadow
        if (d <= epsilon) {
            return 0.0;
        }

        // Rays that did not hit, gets partial shadow, based on how close they got to a surface.
        // Use reciprocal instead of division (cheaper on many GPUs)
        lit = min(lit, softness * d * rcp(max(t, 1e-6)));
        // Early exit if we are effectively in total darkness
        if (lit < 0.01) {
            return 0.0;
        }
        
        t += max(d, minStep);
    }

    // return 0.0;
    return saturate(lit);
}

#endif
