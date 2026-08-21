#ifndef LOTEC_VOLUME_INCLUDED
#define LOTEC_VOLUME_INCLUDED

// ENGINE-AGNOSTIC, like every header in this folder: HLSL intrinsics and our own headers only. No
// URP includes, no vertex/fragment semantics, no Core.hlsl texture macros. That means any of these
// can be included from a fragment shader, a compute shader or the voxelize raster alike.
//
// The engine boundary is the .shader / .compute ENTRY POINTS. VoxelLit.shader includes URP's
// Core.hlsl and Lighting.hlsl and calls GetMainLight(), then hands this library plain values.
// Guarded by Shaders/Compute/BufferGiCommonCanary.compute, which includes every header here and fails
// moment one acquires an engine dependency - do not "fix" that by adding an include to the canary.


// Universal volume space: the active volume's world-space AABB and the world->[0,1] UVW
// mapping that every feature (shadows, occlusion, GI) and both stages (fragment + compute)
// share. Published by the active VoxelVolume. No textures or stage-specific uniforms here -
// those live in the per-feature headers (e.g. VoxelSdfShadows.hlsl for the hi-res SDF).
float3 _VoxelVolumeBoundsMin;
float3 _VoxelVolumeBoundsSize;

// Normalise to local [0,1] for texture sampling. Assumes the volume is axis-aligned.
// TODO: Maybe support non-axis-aligned volumes with _VolumeRotation or _VolumeMatrix?
float3 WorldToVoxelUV(float3 worldPos)
{
    return (worldPos - _VoxelVolumeBoundsMin) / max(_VoxelVolumeBoundsSize, 1e-6);
}

// True when the UVW lies inside the volume's [0,1] box.
bool InsideVolumeUVW(float3 uvw)
{
    return all(uvw >= 0.0) && all(uvw <= 1.0);
}

#endif // LOTEC_VOLUME_INCLUDED
