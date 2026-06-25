#ifndef LOTECSOFTWARE_VOXEL_SDF_SHADOWS_INCLUDED
#define LOTECSOFTWARE_VOXEL_SDF_SHADOWS_INCLUDED

// SDF raymarch shadow with 64-bit bitmask occlusion optimization.
// Expects an SDF stored in a 3D texture in world units (signed distance).
// Optionally uses a bitmask texture for more accurate geometry detection.

// RayMarchTex3D, plus the volume bounds + WorldToVoxelUV.
#include "Raymarch.hlsl"
#include "Volume.hlsl"

Texture3D<float> _SdfHires;
SamplerState sampler_SdfHires;

float _RaymarchEpsilon;
float _RaymarchMinStep;
float _RaymarchStartOffset; // world units; skips the ray forward along the light direction
int _RaymarchMaxSteps;
float _RaymarchSoftness;

// Returns 0..1: fully shadowed to fully lit. The start offset skips the ray forward
// along the light direction past the near-surface band before sampling begins, which
// is what prevents the surface from self-shadowing.
inline float GetShadowFromSdf(float3 dir, float3 worldPos, float maxDistance)
{
    float startOffset = min(_RaymarchStartOffset, maxDistance * 0.5);
    return RayMarchTex3D(_SdfHires, sampler_SdfHires, worldPos, dir, _VoxelVolumeBoundsMin, _VoxelVolumeBoundsSize, startOffset, maxDistance, _RaymarchEpsilon, _RaymarchMinStep, _RaymarchMaxSteps, _RaymarchSoftness);
}

#endif
