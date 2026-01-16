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

// General-purpose SDF raymarch used by both runtime and baker.
// Returns true if an occluder (surface within 'epsilon') was found within maxDistance.
inline bool RayMarchOccluded(
    float3 worldPos,
    float3 dir,
    float3 boundsMin,
    float3 boundsSize,
    float startOffset,
    float maxDistance,
    float epsilon,
    float minStep,
    int maxSteps,
    out float traveled
) {
    float3 size = max(boundsSize, 1e-6);
    float t = max(startOffset, 0.0);
    traveled = 0.0;

    [loop]
    for (int stepIndex = 0; stepIndex < maxSteps; stepIndex++) {
        if (t > maxDistance) {
            traveled = t;
            return false;
        }

        float3 p = worldPos + dir * t;
        float3 uvw = (p - boundsMin) / size;
        if (!all(uvw >= 0.0) || !all(uvw <= 1.0)) {
            traveled = t;
            return false;
        }

        float d = SAMPLE_TEXTURE3D_LOD(_SdfTex, sampler_SdfTex, uvw, 0).r;
        if (d <= epsilon) {
            traveled = t;
            return true;
        }

        t += max(d, minStep);
    }

    traveled = t;
    return false;
}

inline bool SdfWorldToUVW(float3 worldPos, out float3 uvw)
{
    float3 size = max(_SdfBoundsSize, 1e-6);
    uvw = (worldPos - _SdfBoundsMin) / size;
    return all(uvw >= 0.0) && all(uvw <= 1.0);
}

// Returns 1 for lit, 0 for fully shadowed.
#ifdef TEXTURE3D
inline float GetShadowFromSdf(Light light, float3 worldPos)
{
    float3 dir = normalize(light.direction);

    float traveled = 0.0;
    bool occluded = RayMarchOccluded(worldPos, dir, _SdfBoundsMin, _SdfBoundsSize, _SdfShadowStartOffset, _SdfShadowMaxDistance, _SdfShadowEpsilon, _SdfShadowMinStep, _SdfShadowMaxSteps, traveled);
    return occluded ? 0.0 : 1.0;
}
#else
// Compute path: provide a function that accepts a direction vector instead of Unity's Light type.
inline float GetShadowFromSdfDir(float3 dir, float3 worldPos)
{
    dir = normalize(dir);
    float traveled = 0.0;
    bool occluded = RayMarchOccluded(worldPos, dir, _SdfBoundsMin, _SdfBoundsSize, _SdfShadowStartOffset, _SdfShadowMaxDistance, _SdfShadowEpsilon, _SdfShadowMinStep, _SdfShadowMaxSteps, traveled);
    return occluded ? 0.0 : 1.0;
}
#endif

#endif
