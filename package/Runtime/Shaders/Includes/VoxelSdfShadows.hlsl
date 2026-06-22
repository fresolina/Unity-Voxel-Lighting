#ifndef LOTECSOFTWARE_VOXEL_SDF_SHADOWS_INCLUDED
#define LOTECSOFTWARE_VOXEL_SDF_SHADOWS_INCLUDED

// SDF raymarch shadow with 64-bit bitmask occlusion optimization.
// Expects an SDF stored in a 3D texture in world units (signed distance).
// Optionally uses a bitmask texture for more accurate geometry detection.

// RayMarchTex3D
#include "Raymarch.hlsl"

Texture3D<float> _SdfTex;
SamplerState sampler_SdfTex;

float3 _SdfBoundsMin;
float3 _SdfBoundsSize;
float3 _VoxelResolution; // resolution of the bitmask voxel grid (set from C# as float vector)
float3 _InverseVoxelSize; // inverse of voxel size in world units (set from C#)

float _RaymarchEpsilon;
float _RaymarchMinStep;
float _RaymarchStartOffset; // world units; skips the ray forward along the light direction
int _RaymarchMaxSteps;
float _RaymarchSoftness;

inline bool SdfWorldToUVW(float3 worldPos, out float3 uvw)
{
    float3 size = max(_SdfBoundsSize, 1e-6);
    uvw = (worldPos - _SdfBoundsMin) / size;
    return all(uvw >= 0.0) && all(uvw <= 1.0);
}

// Returns 0..1: fully shadowed to fully lit. The start offset skips the ray forward
// along the light direction past the near-surface band before sampling begins, which
// is what prevents the surface from self-shadowing.
inline float GetShadowFromSdf(float3 dir, float3 worldPos, float maxDistance)
{
    float startOffset = min(_RaymarchStartOffset, maxDistance * 0.5);
    return RayMarchTex3D(_SdfTex, sampler_SdfTex, worldPos, dir, _SdfBoundsMin, _SdfBoundsSize, startOffset, maxDistance, _RaymarchEpsilon, _RaymarchMinStep, _RaymarchMaxSteps, _RaymarchSoftness);
}

#endif
