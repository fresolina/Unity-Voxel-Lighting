#ifndef LOTEC_BUFFER_GI_VOXEL_DATA_INCLUDED
#define LOTEC_BUFFER_GI_VOXEL_DATA_INCLUDED

// ENGINE-AGNOSTIC, like every header in this folder: HLSL intrinsics and our own headers only. No
// URP includes, no vertex/fragment semantics, no Core.hlsl texture macros. That means any of these
// can be included from a fragment shader, a compute shader or the voxelize raster alike.
//
// The engine boundary is the .shader / .compute ENTRY POINTS. VoxelLit.shader includes URP's
// Core.hlsl and Lighting.hlsl and calls GetMainLight(), then hands this library plain values.
// Guarded by Shaders/Compute/BufferGiCommonCanary.compute, which includes every header here and fails
// moment one acquires an engine dependency - do not "fix" that by adding an include to the canary.


// LAYER 1 (Resources): the STATIC voxel fields, shared by both compute stages.
// Depends only on BufferGiField.hlsl - no URP headers, no vertex/fragment semantics.
//
// These three buffers are the contract between the two compute shaders:
//   BufferGiBake.compute  WRITES them (rasterized _Material -> occupancy, surface word).
//   BufferGiSolve.compute READS them every solve frame and never modifies them.
// The DYNAMIC fields (_Radiance, _Irradiance, _IrradianceBlur) are solve-owned and are
// deliberately NOT here - if a buffer appears in this file, the bake decides its contents.

StructuredBuffer<uint>    _Material;      // albedo/emission (voxelized by BufferGiVoxelize.shader); inject-only cold data
// Occupancy as a 1-bit-per-voxel bitfield (uint packs 32 voxels): 4 KB per field, so the WHOLE
// occupancy of both fields is L1-resident and one cache line covers 512 voxels. This is what every
// solidity test reads - most importantly the DDA inner loop, which previously fetched 4 bytes of
// _Material per stepped cell for a 1-bit answer. Derived from _Material by CSBuildOccupancy at bake.
RWStructuredBuffer<uint>  _Occupancy;
// Per-voxel surface word (normal in low 16 bits; see BufferGiField.hlsl). The voxelizer seeds it with
// the triangle normal; CSBuildSurface then rewrites it with the occupancy gradient (keeping the
// triangle only where the gradient cancels) and adds openness, flags and the sun-ray origin. Read
// once per hit by the solve. RW so CSBuildSurface can write it; the solve kernels only read it.
RWStructuredBuffer<uint>  _Surface;

// Solidity test for a concatenated-buffer slot (field offset already applied - slots are multiples
// of BGI_COUNT per field, and BGI_COUNT is divisible by 32, so field slices stay word-aligned).
bool BgiSolidBit(uint slot)
{
    return (_Occupancy[slot >> 5] >> (slot & 31u)) & 1u;
}


#endif // LOTEC_BUFFER_GI_VOXEL_DATA_INCLUDED
