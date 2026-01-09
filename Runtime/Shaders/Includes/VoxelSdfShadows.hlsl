#ifndef LOTECSOFTWARE_VOXEL_SDF_SHADOWS_INCLUDED
#define LOTECSOFTWARE_VOXEL_SDF_SHADOWS_INCLUDED

// SDF raymarch shadow with 64-bit bitmask occlusion optimization.
// Expects an SDF stored in a 3D texture in world units (signed distance).
// Optionally uses a bitmask texture for more accurate geometry detection.

#include "Math.hlsl"

TEXTURE3D(_SdfTex);
SAMPLER(sampler_SdfTex);

float3 _SdfBoundsMin;
float3 _SdfBoundsSize;
float3 _VoxelResolution; // resolution of the bitmask voxel grid (set from C# as float vector)

float _SdfShadowMaxDistance;
float _SdfShadowEpsilon;
float _SdfShadowMinStep;
float _SdfShadowStartOffset;
int _SdfShadowMaxSteps;

// Directional occlusion bitmask helpers (depends on the globals above)
#include "VoxelOcclusionDirection.hlsl"

inline bool SdfWorldToUVW(float3 worldPos, out float3 uvw)
{
    float3 size = max(_SdfBoundsSize, 1e-6);
    uvw = (worldPos - _SdfBoundsMin) / size;
    return all(uvw >= 0.0) && all(uvw <= 1.0);
}

// Returns 1 for lit, 0 for fully shadowed.
// Uses occlusion bitmask for fast binary geometry detection, falls back to SDF raymarching.
inline float GetShadow(Light light, float3 worldPos)
{
    float3 dir = normalize(light.direction);

    // Start slightly along the ray to avoid immediate self-hit.
    float t = max(_SdfShadowStartOffset, 0.0);

    [loop]
    for (int stepIndex = 0; stepIndex < _SdfShadowMaxSteps; stepIndex++)
    {
        if (t > _SdfShadowMaxDistance)
            return 1.0;

        float3 p = worldPos + dir * t;

        // Leaving the SDF volume => no occluder within the field.
        float3 uvw;
        if (!SdfWorldToUVW(p, uvw))
            return 1.0;

        // Directional bitmask pass: check if this light direction is occluded (fast)
        if (CheckBitmaskOcclusion(p, dir))
            return 0.0;

        // Fallback: SDF-based distance field (handles smooth blending)
        float d = SAMPLE_TEXTURE3D_LOD(_SdfTex, sampler_SdfTex, uvw, 0).r;
        if (d <= _SdfShadowEpsilon)
            return 0.0;

        t += max(d, _SdfShadowMinStep);
    }

    // Max steps hit; treat as lit to avoid overly dark artifacts.
    return 1.0;
}

#endif
