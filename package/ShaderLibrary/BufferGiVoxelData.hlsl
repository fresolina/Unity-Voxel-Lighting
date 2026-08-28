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
// triangle only where the gradient cancels) and adds the flags and the sun-ray origin. Read
// once per hit by the solve. RW so CSBuildSurface can write it; the solve kernels only read it.
RWStructuredBuffer<uint>  _Surface;
// HI-RES occupancy, at _BgiOccGrid rather than _BgiGrid, in 4x4x4 blocks of two contiguous uints
// (see BufferGiField.hlsl). Written by the voxelizer's bit-only pass (or uploaded from the bake
// asset), never by anything per-frame. This is the geometry the DDA is meant to march.
//
// NOTE the two independent axes. "Hi-res vs lighting grid" is a RESOLUTION inside one world box;
// "fine vs coarse FIELD" is a different world box. They multiply: each field carries its own hi-res
// slice at the same _BgiOccGrid, addressed by _OccFieldWordOffset exactly as _FieldOffset addresses
// the lighting buffers. Since the two fields' boxes differ in size, the same _BgiOccGrid buys
// different physical detail in each - see the architecture doc.
RWStructuredBuffer<uint>  _OccupancyHi;
// OR-DOWNSAMPLE of _OccupancyHi onto the _BgiGrid, in the same plain 1-bit/voxel layout as
// _Occupancy: the always-hot upper LEVEL of a two-level march (a resolution level - nothing to do
// with the coarse FIELD). Kept SEPARATE from _Occupancy, which stays the storage field for the blur
// gate, the air distance, the surface build and the far-air skip - those all describe the LIGHTING
// grid and must not silently gain the hi-res raster's extra cells. Derived (never rasterized
// independently), because a traversal level has to be CONSERVATIVE: an empty cell skips its children
// untested, so under-estimating is a silent missed-occluder bug, and OR is the only operator that
// cannot under-estimate.
RWStructuredBuffer<uint>  _OccupancyTraversal;
// GROWN occupancy on the LIGHTING grid, 1 bit per voxel: every rasterized voxel plus the one behind
// each FRAGMENT along its triangle normal (written by BufferGiVoxelize's pass 0). Read by exactly one
// consumer - CSBlur's shell-dilation neighbour test - and by nothing that traces a ray.
//
// It is the BGI_THICKEN geometry without the BGI_THICKEN costs. Thickening was measured to fix the
// concave-corner dilation outright (corner cell 0.057 -> 0.284, the seam gone) but it does so by
// changing _Material itself, so the DDA, the gather and the hi-res march all see +59% solid cells,
// gaps close, and the air field comes out 8% brighter. Splitting the grown set into its own bitfield
// keeps the fix and drops all of that: the raster stays honest and only the dilation's question -
// "which air is genuinely on my side" - is answered against the grown geometry.
//
// NOT the same as _OccupancyThick, which grows one cell per VOXEL along that voxel's single stored
// normal. That was tried on this defect and moved it 0.0570 -> 0.0545, because a corner cell holds
// two surfaces and the voxelizer's normal is last-write-wins between them.
//
// READ-ONLY, unlike every other buffer in this file, and deliberately so. The others are RW because
// BufferGiBake.compute WRITES them and includes this same header. This one has no compute writer at
// all: the VOXELIZER fills it through its own `_GrownWrite : register(u3)` declaration, and
// BufferGiVoxelize.shader does not include this file. Declaring it RW would put it on the UAV budget
// of every kernel that touches it - CSBlur already binds 7 resources against a D3D11 compute limit
// of 8 - to buy write access nothing wants.
StructuredBuffer<uint>    _OccupancyGrown;

// Solidity of a HI-RES cell (field word offset already applied by BgiOccWord).
bool BgiHiSolidBit(uint3 c)
{
    return (_OccupancyHi[BgiOccWord(c)] & BgiOccBitMask(c)) != 0u;
}

// WHOLE-BLOCK test: is there any solid, and any air, in the 4x4x4 block containing `c`? Two word
// loads answer for 64 cells at once, which is the point of the block layout - a "is there geometry
// near me" query that would otherwise be 125 bit tests becomes a handful of loads.
// Out of range counts as AIR and no solid: outside the grid is open space.
void BgiHiBlockContents(int3 block, out bool anySolid, out bool anyAir)
{
    uint blocksPerAxis = 1u << BgiOccBlockGridLog2();
    if (any(block < 0) || any(block >= (int)blocksPerAxis)) { anySolid = false; anyAir = true; return; }
    uint s = BgiOccBlockGridLog2();
    uint w = _OccFieldWordOffset
           + ((uint)block.x | ((uint)block.y << s) | ((uint)block.z << (s * 2u))) * 2u;
    uint w0 = _OccupancyHi[w], w1 = _OccupancyHi[w + 1u];
    anySolid = (w0 | w1) != 0u;
    anyAir   = (w0 & w1) != 0xffffffffu;
}

// Solidity test for a concatenated-buffer slot (field offset already applied - slots are multiples
// of BGI_COUNT per field, and BGI_COUNT is divisible by 32, so field slices stay word-aligned).
bool BgiSolidBit(uint slot)
{
    return (_Occupancy[slot >> 5] >> (slot & 31u)) & 1u;
}

// Same test against the GROWN level. `true` means "a surface would have grown into this cell", i.e.
// it is air the dilation must not pull light from. Same layout and indexing as BgiSolidBit.
bool BgiGrownSolidBit(uint slot)
{
    return (_OccupancyGrown[slot >> 5] >> (slot & 31u)) & 1u;
}

// Same test against the TRAVERSAL level. Identical layout and indexing to BgiSolidBit, deliberately
// a separate function rather than a parameter: the two answer different questions (does this cell
// hold LIGHTING vs does this cell hold ANY geometry) and a two-level march must never mix them up.
// Conservative by construction (OR-downsample), so `false` is a safe skip and `true` only ever costs
// a descent that finds nothing.
bool BgiTraversalSolidBit(uint slot)
{
    return (_OccupancyTraversal[slot >> 5] >> (slot & 31u)) & 1u;
}

// The 4x4x4 block's two words, loaded once, for an inner march that runs entirely in registers.
// `block` must be in range - the caller of a descent already knows it is, and the bounds test is the
// expensive part of BgiHiBlockContents.
void BgiHiBlockWords(int3 block, out uint w0, out uint w1)
{
    uint s = BgiOccBlockGridLog2();
    uint w = _OccFieldWordOffset
           + ((uint)block.x | ((uint)block.y << s) | ((uint)block.z << (s * 2u))) * 2u;
    w0 = _OccupancyHi[w];
    w1 = _OccupancyHi[w + 1u];
}

// Solidity of a cell inside an already-loaded block. `c` is the full hi-res coordinate; only its low
// two bits per axis are used, so the caller does not have to strip them.
bool BgiHiSolidInBlock(uint w0, uint w1, int3 c)
{
    uint bit = ((uint)c.x & 3u) | (((uint)c.y & 3u) << 2u) | (((uint)c.z & 3u) << 4u);
    uint w = (bit < 32u) ? w0 : w1;
    return ((w >> (bit & 31u)) & 1u) != 0u;
}


#endif // LOTEC_BUFFER_GI_VOXEL_DATA_INCLUDED
