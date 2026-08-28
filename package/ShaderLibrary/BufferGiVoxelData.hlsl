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

// Made an explicit include rather than an assumption about include order. Every symbol below reaches
// into it (BgiOccWord, BgiOccBitMask, _OccFieldWordOffset, _BgiGridSize), and since S2 of
// docs/direct-shadow-extraction.md this header is included by a compute file that has no reason to
// know it must include BufferGiField.hlsl first. The guard makes it a no-op everywhere else.
#include "BufferGiField.hlsl"


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


// --- HI-RES TRAVERSAL ------------------------------------------------------------------------
// Moved here from BufferGiSolve.compute by S2 of docs/direct-shadow-extraction.md. It is pure
// occupancy traversal - no solve buffer, no lighting concept - and it now has two callers in two
// different compute files: the solve's own occlusion queries, and CSSunVisibility over in
// VoxelSunShadow.compute. It was the ONLY thing keeping the sun-visibility kernel inside the solve.
//
// A fragment-stage caller is the point of putting it in a header at all (S5's per-pixel raymarch),
// but it cannot become one yet: _OccupancyHi is a StructuredBuffer, and the shipping fragment
// deliberately declares none. See the resource problem in that document.
// Worst-case step count on the OCCUPANCY grid. Keys off _BgiOccGrid, not _BgiGrid: once the two
// differ, a bound derived from the lighting grid truncates the ray long before it has crossed the
// volume, and a truncated shadow ray reads as LIT - a silently missing shadow, not an obvious one.
#define BGI_MAX_OCC_RAY_STEPS (BGI_OCC_GRID * 3u)

// The same Amanatides-Woo walk over the HI-RES occupancy. `hiGridPos` is in OCCUPANCY-grid units
// (cell index + fraction) and the direction is world-space, so the returned t is in world units like
// the coarse march. Steps off the origin cell first, so a cell never self-occludes - which is what
// lets a SOLID shell texel answer "is my own face lit" rather than always answering 0.
//
// No hitCell out-param: the only caller wants a visibility bit, and a hi-res hit index has no
// consumer yet.
int MarchOccupancyHiFrom(float3 hiGridPos, float3 worldDir, float maxDist)
{
    int3 cell = (int3)floor(hiGridPos);
    float3 inCell = hiGridPos - (float3)cell;
    // Per-axis hi-res voxel size: the occupancy grid spans the SAME world box as the field, so this
    // is the field extent over the occupancy resolution. Stepping in grid units without it would be
    // anisotropic on a non-cubic volume, exactly as it would be on the coarse march.
    float3 hiVox = _BgiGridSize / (float)BGI_OCC_GRID;
    float3 d = worldDir / max(hiVox, 1e-6);
    int3 step = int3(d.x >= 0 ? 1 : -1, d.y >= 0 ? 1 : -1, d.z >= 0 ? 1 : -1);
    float3 inv = 1.0 / max(abs(d), 1e-6);
    float3 toBoundary = float3(d.x >= 0 ? 1.0 - inCell.x : inCell.x,
                               d.y >= 0 ? 1.0 - inCell.y : inCell.y,
                               d.z >= 0 ? 1.0 - inCell.z : inCell.z);
    float3 tMax = inv * toBoundary;
    float3 tDelta = inv;

    [loop]
    for (uint s = 0u; s < BGI_MAX_OCC_RAY_STEPS; s++) {
        float tCross;
        if (tMax.x < tMax.y) {
            if (tMax.x < tMax.z) { tCross = tMax.x; cell.x += step.x; tMax.x += tDelta.x; }
            else                 { tCross = tMax.z; cell.z += step.z; tMax.z += tDelta.z; }
        } else {
            if (tMax.y < tMax.z) { tCross = tMax.y; cell.y += step.y; tMax.y += tDelta.y; }
            else                 { tCross = tMax.z; cell.z += step.z; tMax.z += tDelta.z; }
        }
        if (tCross > maxDist) return 0;
        if (!BgiOccInBounds(cell)) return 0;   // escaped the grid -> sky
        if (BgiHiSolidBit((uint3)cell)) return 1;
    }
    return 0;
}

#endif // LOTEC_BUFFER_GI_VOXEL_DATA_INCLUDED
