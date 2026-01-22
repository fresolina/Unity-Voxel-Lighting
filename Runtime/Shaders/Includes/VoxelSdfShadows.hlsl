#ifndef LOTECSOFTWARE_VOXEL_SDF_SHADOWS_INCLUDED
#define LOTECSOFTWARE_VOXEL_SDF_SHADOWS_INCLUDED

// SDF raymarch shadow with 64-bit bitmask occlusion optimization.
// Expects an SDF stored in a 3D texture in world units (signed distance).
// Optionally uses a bitmask texture for more accurate geometry detection.

#include "Math.hlsl"

// Support being included from both .shader (where TEXTURE3D/SAMPLER macros exist)
// and .compute (where the macros are not defined). When TEXTURE3D is not defined
// assume the including file declares the Texture3D and SamplerState itself.
#ifndef TEXTURE3D
    // compute shader path: ensure a sample macro exists
    #ifndef SAMPLE_TEXTURE3D_LOD
        #define SAMPLE_TEXTURE3D_LOD(tex, samp, uvw, lod) (tex.SampleLevel(samp, uvw, lod))
    #endif
#else
    TEXTURE3D(_SdfTex);
    SAMPLER(sampler_SdfTex);
#endif

float3 _SdfBoundsMin;
float3 _SdfBoundsSize;
float3 _VoxelResolution; // resolution of the bitmask voxel grid (set from C# as float vector)

float _SdfShadowMaxDistance;
float _SdfShadowEpsilon;
float _SdfShadowMinStep;
float _SdfShadowStartOffset;
int _SdfShadowMaxSteps;
half _SdfShadowSoftness;

// Intersect ray (ro + rd * t) with unit AABB [0,1]^3.
// ro/rd are in UVW space; t is still in world-distance units (because rd already includes invSize).
inline bool RayIntersectUnitAabb(float3 ro, float3 rd, out float tEnter, out float tExit)
{
    const float kHuge = 1e20;
    bool3 parallel = abs(rd) < 1e-8;

    // If we're parallel to a slab and outside it, no hit.
    if (parallel.x && (ro.x < 0.0 || ro.x > 1.0)) { tEnter = 0.0; tExit = 0.0; return false; }
    if (parallel.y && (ro.y < 0.0 || ro.y > 1.0)) { tEnter = 0.0; tExit = 0.0; return false; }
    if (parallel.z && (ro.z < 0.0 || ro.z > 1.0)) { tEnter = 0.0; tExit = 0.0; return false; }

    float3 invRd = rcp(rd);
    float3 t0 = (0.0 - ro) * invRd;
    float3 t1 = (1.0 - ro) * invRd;

    // Ignore parallel axes by making their interval unbounded.
    t0 = float3(parallel.x ? -kHuge : t0.x, parallel.y ? -kHuge : t0.y, parallel.z ? -kHuge : t0.z);
    t1 = float3(parallel.x ?  kHuge : t1.x, parallel.y ?  kHuge : t1.y, parallel.z ?  kHuge : t1.z);

    float3 tMin3 = min(t0, t1);
    float3 tMax3 = max(t0, t1);

    tEnter = max(max(tMin3.x, tMin3.y), tMin3.z);
    tExit  = min(min(tMax3.x, tMax3.y), tMax3.z);

    return tExit >= tEnter;
}

// General-purpose SDF raymarch used by both runtime and baker.
// Returns 0..1: total occlusion hit to no occlusion.
float RayMarch(
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

        float d = SAMPLE_TEXTURE3D_LOD(_SdfTex, sampler_SdfTex, uvw, 0).r;

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

inline bool SdfWorldToUVW(float3 worldPos, out float3 uvw)
{
    float3 size = max(_SdfBoundsSize, 1e-6);
    uvw = (worldPos - _SdfBoundsMin) / size;
    return all(uvw >= 0.0) && all(uvw <= 1.0);
}

// Returns 0..1: fully shadowed to fully lit..
inline float GetShadowFromSdf(float3 dir, float3 worldPos)
{
    half lit = RayMarch(worldPos, dir, _SdfBoundsMin, _SdfBoundsSize, _SdfShadowStartOffset, _SdfShadowMaxDistance, _SdfShadowEpsilon, _SdfShadowMinStep, _SdfShadowMaxSteps, _SdfShadowSoftness);
    return lit;
}

#endif
