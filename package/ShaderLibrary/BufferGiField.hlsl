#ifndef LOTEC_BUFFER_GI_FIELD_INCLUDED
#define LOTEC_BUFFER_GI_FIELD_INCLUDED

// ENGINE-AGNOSTIC, like every header in this folder: HLSL intrinsics and our own headers only. No
// URP includes, no vertex/fragment semantics, no Core.hlsl texture macros. That means any of these
// can be included from a fragment shader, a compute shader or the voxelize raster alike.
//
// The engine boundary is the .shader / .compute ENTRY POINTS. VoxelLit.shader includes URP's
// Core.hlsl and Lighting.hlsl and calls GetMainLight(), then hands this library plain values.
// Guarded by Shaders/Compute/BufferGiCommonCanary.compute, which includes every header here and fails
// moment one acquires an engine dependency - do not "fix" that by adding an include to the canary.


// Shared layout for the buffer-based GI (the textureless voxel GI). One cubic grid per cascade,
// sized at runtime from VoxelVolume._maxResolution (snapped to a power of two - see BufferGiUpdater).
// All fields are flat StructuredBuffers indexed by BgiIndex; this header holds the index math, the
// world<->grid mapping (its own _Bgi* grid, independent of the SDF/volume UVW space), and the
// pack/unpack helpers shared by the compute solve, the fragment read, and the debug visualizer.
// Buffers themselves are declared per-shader (RW in compute, read-only elsewhere).

// Grid resolution, published per active volume by BufferGiUpdater. It is always a power of two so the
// linear<->3D index math reduces to shifts/masks: BGI_GRID_LOG2 = log2(BGI_GRID), and BGI_COUNT =
// BGI_GRID^3 is a multiple of 32 (grid >= 4), so the occupancy bitfield stays word-aligned per field.
uint _BgiGrid;      // cubic grid resolution (power of two)
uint _BgiGridLog2;  // log2(_BgiGrid)
uint _BgiCount;     // _BgiGrid^3 - voxels per field
#define BGI_GRID _BgiGrid
#define BGI_GRID_LOG2 _BgiGridLog2
#define BGI_GRID_MASK (_BgiGrid - 1u)
#define BGI_COUNT _BgiCount

// Field slice offsets in the concatenated buffers (must match BufferGiUpdater CoarseField/FineField):
// coarse = slot 0, fine = slot 1, so any future fine fields stay contiguous (slots 1..N-1).
static const uint BGI_COARSE_OFFSET = 0u;
#define BGI_FINE_OFFSET _BgiCount

// Cascade placement in world space (set by BufferGiUpdater). Voxels are per-axis (the grid
// stretches to fill non-cubic volume bounds), so _BgiVoxelSize is a float3, not a scalar.
// These are the CURRENT field's bounds, set per compute/voxelize dispatch.
float3 _BgiGridOrigin; // world-space min corner of the cascade grid
float3 _BgiGridSize;   // world-space extent of the cascade grid
float3 _BgiVoxelSize;  // per-axis voxel size (= _BgiGridSize / BGI_GRID)

// Base index of the current field's slice in the concatenated buffers (= field * BGI_COUNT). All
// fields share one buffer; field f occupies [f*BGI_COUNT, (f+1)*BGI_COUNT). Set per dispatch.
uint _FieldOffset;

uint BgiIndex(uint3 c) {
    return c.x | (c.y << BGI_GRID_LOG2) | (c.z << (BGI_GRID_LOG2 * 2u));
}

// Buffer slot for a voxel in the current field (compute/voxelize): field offset + local index.
uint BgiSlot(uint3 c) {
    return _FieldOffset + BgiIndex(c);
}

uint3 BgiCoord(uint i) {
    return uint3(i & BGI_GRID_MASK,
                 (i >> BGI_GRID_LOG2) & BGI_GRID_MASK,
                 (i >> (BGI_GRID_LOG2 * 2u)) & BGI_GRID_MASK);
}

bool BgiInBounds(int3 c) {
    return all(c >= 0) && all(c < (int)BGI_GRID);
}

// World-space center of a voxel, for an explicit field (used by the fragment read, which samples
// multiple fields) and for the current dispatch's field (compute/voxelize).
float3 BgiVoxelCenterAt(uint3 c, float3 origin, float3 voxelSize) {
    return origin + (float3(c) + 0.5) * voxelSize;
}
float3 BgiVoxelCenter(uint3 c) {
    return BgiVoxelCenterAt(c, _BgiGridOrigin, _BgiVoxelSize);
}

// Continuous grid coordinates (voxel units) of a world position. floor() gives the base cell.
float3 BgiWorldToGridAt(float3 worldPos, float3 origin, float3 voxelSize) {
    return (worldPos - origin) / max(voxelSize, 1e-6);
}
float3 BgiWorldToGrid(float3 worldPos) {
    return BgiWorldToGridAt(worldPos, _BgiGridOrigin, _BgiVoxelSize);
}

// --- Material / occupancy packing: R8G8B8 albedo + A8 log-encoded emission intensity ---
// The albedo doubles as the occupancy flag: rgb == 0 means empty space. The bake forces every
// solid voxel to a nonzero ("dark gray") albedo so emptiness is the only all-zero state.
uint BgiPackMaterial(float3 albedo, float emission8) {
    uint3 a = (uint3)(saturate(albedo) * 255.0 + 0.5);
    uint  e = (uint)(saturate(emission8) * 255.0 + 0.5);
    return a.x | (a.y << 8) | (a.z << 16) | (e << 24);
}

// 8-bit channels -> fp16 is lossless (11-bit mantissa), and albedo is a colour: half.
half3 BgiAlbedo(uint m) {
    return (half3)(float3(m & 0xffu, (m >> 8) & 0xffu, (m >> 16) & 0xffu) * (1.0 / 255.0));
}

// Raw 8-bit log-encoded emission; decode with DecodeEmissionIntensityFrom8Bit (Math.hlsl).
float BgiEmission8(uint m) {
    return ((m >> 24) & 0xffu) * (1.0 / 255.0);
}

bool BgiIsSolid(uint m) {
    return (m & 0x00ffffffu) != 0u;
}

// --- Radiance / irradiance packing: fp16 x3 (+ a 4th half) into a uint2 ---
// Avoids StructuredBuffer<half> (patchy on WebGPU/GLES). The 4th half carries the temporal sample
// count for irradiance (unused/1.0 for radiance).
uint2 BgiPackRgb(float3 c, float w) {
    return uint2(f32tof16(c.x) | (f32tof16(c.y) << 16),
                 f32tof16(c.z) | (f32tof16(w) << 16));
}

void BgiUnpackRgb(uint2 p, out float3 c, out float w) {
    c = float3(f16tof32(p.x & 0xffffu), f16tof32(p.x >> 16), f16tof32(p.y & 0xffffu));
    w = f16tof32(p.y >> 16);
}

// fp16 flavour of the unpack. The stored values ARE fp16 by construction, so narrowing back is
// exact - this just keeps the result in fp16 registers instead of round-tripping through fp32.
// Separate name (not an overload) so a call can never resolve ambiguously on its out params.
void BgiUnpackRgbH(uint2 p, out half3 c, out half w) {
    c = half3(f16tof32(p.x & 0xffffu), f16tof32(p.x >> 16), f16tof32(p.y & 0xffffu));
    w = (half)f16tof32(p.y >> 16);
}

// --- Per-voxel SURFACE word (32 bits/voxel), baked at voxelize/build time, read once per hit ---
// bits  0-15 : octahedral surface normal, 8 bits/axis (~1-2 deg - plenty for a voxel GI normal)
// bits 16-23 : SOLID -> openness / static AO;  AIR -> distance to the nearest solid (voxels, capped)
// bits 24-31 : flags - bit 24 EMISSIVE, bit 25 TWO-SIDED (see below); 26-31 still reserved
// The two bit-16..23 meanings never collide - a voxel is either solid or air - so readers pick by the
// occupancy bit. Split from occupancy (the hot 1-bit/voxel bitfield) so this cold 4 B word is touched
// only per ray-hit / per voxel, never in the DDA loop.

// City-block distance (voxels) from an AIR voxel to the nearest solid, saturating at this cap. Baked
// by CSBuildAirDistance and used to skip gathering air that no surface ever reads (far-air gather
// skip). Cap is small - readers only care about the "near vs far" boundary, not the exact far value.
// The sole consumer is the gather's `> BGI_GATHER_MAX_AIR_DIST` (=4) cutoff, so this only needs to be
// one past that (5) to classify the boundary voxels exactly; the wider 0-8 range only mattered for the
// (deferred) DDA empty-space jump. Bump back up if that lands.
static const uint BGI_MAX_AIR_DIST = 5u;
float2 BgiOctWrap(float2 v) {
    return (1.0 - abs(v.yx)) * float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
}

// Pack a unit normal into the low 16 bits (leaves bits 16-31 for the caller to OR in later).
uint BgiPackSurfaceNormal(float3 n) {
    n /= max(abs(n.x) + abs(n.y) + abs(n.z), 1e-8);
    float2 e = (n.z >= 0.0) ? n.xy : BgiOctWrap(n.xy);
    e = e * 0.5 + 0.5; // [-1,1] -> [0,1]
    uint2 q = (uint2)(saturate(e) * 255.0 + 0.5);
    return q.x | (q.y << 8);
}

float3 BgiSurfaceNormal(uint word) {
    float2 e = float2(word & 0xffu, (word >> 8) & 0xffu) * (1.0 / 255.0);
    e = e * 2.0 - 1.0; // [0,1] -> [-1,1]
    float3 n = float3(e.x, e.y, 1.0 - abs(e.x) - abs(e.y));
    float t = saturate(-n.z);
    n.xy += float2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
    return normalize(n);
}

// Static openness / ambient occlusion in bits 16-23 (solid voxels): 1 = open (flat/convex surface),
// < 1 = the front hemisphere is partly blocked by nearby geometry (concave corner, contact gap).
// Baked by CSBuildSurface; keep it OUT of the normal's low 16 bits when composing the word.
uint BgiPackOpenness(float openness) {
    return (uint)(saturate(openness) * 255.0 + 0.5) << 16;
}

half BgiSurfaceOpenness(uint word) {
    return (half)(((word >> 16) & 0xffu) * (1.0 / 255.0));
}

// Air-distance in bits 16-23 (AIR voxels): integer city-block distance to the nearest solid, capped
// at BGI_MAX_AIR_DIST. Shares the bits with openness - valid only where the occupancy bit is 0.
uint BgiPackAirDist(uint dist) {
    return (min(dist, BGI_MAX_AIR_DIST) & 0xffu) << 16;
}

uint BgiSurfaceAirDist(uint word) {
    return (word >> 16) & 0xffu;
}

// Flags in bits 24-31 (the reserved block). EMISSIVE marks a voxel that is a LIGHT rather than a
// surface: a Unity Light set to Baked inside the field is voxelized into one emissive voxel (see
// LightEmissionBake). It radiates in EVERY direction - a point light has no face - so the gather
// skips the front-face test a real surface takes. Anything that rewrites the surface word (i.e.
// CSBuildSurface) must carry these bits across.
#define BGI_SURFACE_FLAGS_MASK 0xff000000u
#define BGI_SURFACE_FLAG_EMISSIVE 0x01000000u
// TWO-SIDED: this voxel received triangles facing OPPOSITE ways - a wall, floor slab or railing
// thinner than a voxel, so both of its faces landed in this one cell. The stored normal is whichever
// side the voxelizer wrote last; the other side's normal is its negation, which is why marking the
// condition costs one bit and no second normal. Set by BufferGiVoxelize (InterlockedOr - the two
// faces' fragments race for the cell), carried across the derive passes by BGI_SURFACE_FLAGS_MASK,
// and baked, since it is a property of the geometry rather than of the runtime mode.
#define BGI_SURFACE_FLAG_TWOSIDED 0x02000000u

bool BgiSurfaceIsEmissive(uint word) {
    return (word & BGI_SURFACE_FLAG_EMISSIVE) != 0u;
}

bool BgiSurfaceIsTwoSided(uint word) {
    return (word & BGI_SURFACE_FLAG_TWOSIDED) != 0u;
}

// SUN-RAY ORIGIN, bits 26-30: which of the 27 cells in this voxel's 3x3x3 neighbourhood CSInject
// starts its shadow march from, as k = (dz+1)*9 + (dy+1)*3 + (dx+1). k == 13 is the voxel itself and
// the range 0..26 fits in 5 bits. Chosen by BgiPickShadowOrigin, in CSBuildSurface - the same
// bake-time pass that derives the normal, and the right place for it: picking the origin needs the
// completed occupancy and a 26-neighbour search, both of which that pass already has in hand.
//
// It was previously chosen inside CSInject, which runs EVERY SOLVE FRAME, and the cost there is not
// per-voxel but per-WAVE: a wave whose 64 threads contain one corner voxel executes the whole scan in
// lockstep. Measured in Sponza, 6.1% of threads needed it but 37% of the working waves contained one.
// Deciding once at bake makes the solve a table lookup.
//
// Nothing about the choice may depend on the LIGHT. A sun change only restarts the solve
// (HasSunChanged resets the sample counter); the derive passes do not re-run, so a sun-scored origin
// would be frozen at whatever the sun was during the bake and would strand corners on an occluded
// start at other angles. Every other field in this word is geometry-only for the same reason.
//
// NOTE a zeroed word decodes to (-1,-1,-1), not self, so callers keep the "landed in solid -> use the
// cell" guard. They need it anyway: the BACK face of a two-sided voxel negates the step.
#define BGI_SURFACE_ORIGIN_SHIFT 26u
#define BGI_SURFACE_ORIGIN_MASK 0x7c000000u

uint BgiPackShadowOrigin(int3 step) {
    uint k = (uint)((step.z + 1) * 9 + (step.y + 1) * 3 + (step.x + 1));
    return k << BGI_SURFACE_ORIGIN_SHIFT;
}

int3 BgiShadowOriginStep(uint word) {
    uint k = (word >> BGI_SURFACE_ORIGIN_SHIFT) & 31u;
    return int3((int)(k % 3u) - 1, (int)((k / 3u) % 3u) - 1, (int)(k / 9u) - 1);
}

// --- Directional radiance slots (see RadianceDirections in BufferGiUpdater) ---
// How many directions of outgoing radiance each voxel stores: 1 (Single) or 2 (Cube - see below for
// why Cube's SIX directions still only need two outgoing slots).
// Published as a plain uniform by both BindGridConstantsToCompute and SetGlobals.
int _BgiRadianceDirs;

// Clamped accessor. An unbound uniform reads 0, and a stride of 0 would collapse every voxel onto
// slot 0 - silent, total corruption of the field rather than an obvious failure. 1 degrades to the
// original single-slot layout instead, which is always a valid interpretation of the buffer.
uint BgiRadianceDirs() {
    return max((uint)_BgiRadianceDirs, 1u);
}

// Index of a voxel's radiance for one of its FACES. THE ONLY PLACE THAT KNOWS THE MODE - every
// reader and writer of _Radiance goes through it, so the modes can't drift apart across call sites.
//
// A slot is always identified by the OUTWARD NORMAL of the face it serves. That one convention keeps
// every call site sign-safe:
//   inject   : writing the face with outward normal f            -> faceN = f
//   gather   : a ray traveling `dir` crossed the face opposing it -> faceN = -dir
//   fragment : the shading surface's own outward normal           -> faceN = N
// `faceN` need not be normalized. `n` is the voxel's stored surface normal (unused when Single).
//
// The stride is 1 or 2 and NEVER 6, in any mode - including Cube. Outgoing radiance is a property of
// real geometry, and the voxelizer stores one normal per cell; the only second face we can describe
// is its negation, when the cell is a sub-voxel wall. Six outgoing faces would need a per-face
// coverage mask from the rasterizer, which does not exist. Cube's six directions are on the INCIDENT
// irradiance instead (below), where a hemisphere is well defined for every voxel.
//
// Single -> the one slot.
// Cube   -> front/back in the voxel's OWN normal basis: a face agreeing with the stored normal is
//           front (slot 0), the opposing face is back (slot 1). A thin wall's back normal is just
//           -n, which is why this needs a flag and not a second stored normal.
// `surfaceWord` is the voxel's _Surface entry. It is required, not optional: only a TWO-SIDED voxel
// actually HAS a second face, and CSInject only writes the back slot under that flag. A one-sided
// voxel's back slot is left at the CSClear zero, which decodes as black radiance AND zero sun
// visibility - so handing it out would answer "no light, fully shadowed" for a face that physically
// exists. That bites in two places: a shading normal that happens to oppose the stored normal (dark
// blotches on detailed geometry), and a ray reaching an emissive lamp voxel from behind, which would
// make a baked light emit into one hemisphere only. Both collapse to the front slot here instead.
uint BgiRadianceSlot(uint voxelSlot, float3 faceN, float3 n, uint surfaceWord) {
    uint dirs = BgiRadianceDirs();
    uint base_ = voxelSlot * dirs;
    if (dirs == 1u) return base_;
    if (!BgiSurfaceIsTwoSided(surfaceWord)) return base_; // one real face - always the front slot
    return base_ + (dot(faceN, n) >= 0.0 ? 0u : 1u);
}

// First slot of a voxel's radiance run. The run is contiguous, so clears walk base..base+dirs.
uint BgiRadianceBase(uint voxelSlot) {
    return voxelSlot * BgiRadianceDirs();
}

// --- Directional INCIDENT irradiance (the ambient cube) ---
// A SECOND, independent stride: 1 normally, 6 in Cube mode. Unlike outgoing radiance - which is a
// property of real geometry and so is capped at the one normal the voxelizer stored plus its
// negation - the irradiance arriving from a hemisphere is well defined for EVERY voxel, air or
// solid. That is what Cube makes directional, and why the two strides are not the same number.
// Buckets are cosine lobes about the six world axes: bucket k holds the average radiance arriving
// over the hemisphere around axis k, weighted by cos. A surface with normal n reconstructs its
// irradiance as sum(n_k^2 * bucket_k) over the 3 axes in n's hemisphere - the standard ambient-cube
// evaluation, whose weights sum to exactly 1.
int _BgiIrradianceDirs;

uint BgiIrradianceDirs() {
    return max((uint)_BgiIrradianceDirs, 1u);
}

uint BgiIrradianceBase(uint voxelSlot) {
    return voxelSlot * BgiIrradianceDirs();
}

// Texel of a bucket in the mirror texture. The six buckets are STACKED ALONG Z (the texture is
// Grid x Grid x Grid*dirs), so a bucket is just a Z offset - see CreateIrradianceTexture for why one
// stacked texture rather than six bindings, and why no border padding is needed.
// stacked texture rather than six bindings, and why no border padding is needed. Compute-side only:
// the fragment samples with normalized uvw, so it builds the slab offset in that space instead.
uint3 BgiTexCoord(uint3 c, uint bucket) {
    return uint3(c.x, c.y, c.z + bucket * BGI_GRID);
}

#endif // LOTEC_BUFFER_GI_FIELD_INCLUDED
