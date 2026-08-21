
#ifndef LOTECSOFTWARE_VOXEL_SDF_SHADOWS_INCLUDED
#define LOTECSOFTWARE_VOXEL_SDF_SHADOWS_INCLUDED

// ENGINE-AGNOSTIC, like every header in this folder: HLSL intrinsics and our own headers only. No
// URP includes, no vertex/fragment semantics, no Core.hlsl texture macros. That means any of these
// can be included from a fragment shader, a compute shader or the voxelize raster alike.
//
// The engine boundary is the .shader / .compute ENTRY POINTS. VoxelLit.shader includes URP's
// Core.hlsl and Lighting.hlsl and calls GetMainLight(), then hands this library plain values.
// Guarded by Compute/BufferGiCommonCanary.compute, which includes every header here and fails the
// moment one acquires an engine dependency - do not "fix" that by adding an include to the canary.


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
// `dir` must already be normalized - every caller has a unit direction in hand (URP's
// light.direction, or toLight * rsqrt(distSq)), so re-normalizing here was pure waste.
inline half GetShadowFromSdf(float3 dir, float3 worldPos, float maxDistance)
{
    // No step budget means nobody published the tuning (the SdfShadow component owns it, scaled by the
    // active volume's voxel size). Fail OPEN - unlit is not a safe default here: the march's
    // out-of-steps fallback is a hard 0, so a scene without that component silently lost every local
    // light instead of just their shadows.
    if (_RaymarchMaxSteps <= 0)
        return 1.0h;

    float startOffset = min(_RaymarchStartOffset, maxDistance * 0.5);
    return RayMarchTex3D(_SdfHires, sampler_SdfHires, worldPos, dir, _VoxelVolumeBoundsMin, _VoxelVolumeBoundsSize, startOffset, maxDistance, _RaymarchEpsilon, _RaymarchMinStep, _RaymarchMaxSteps, _RaymarchSoftness);
}

#endif
