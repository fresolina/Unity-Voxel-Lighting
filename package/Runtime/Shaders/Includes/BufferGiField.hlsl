#ifndef LOTEC_BUFFER_GI_FIELD_INCLUDED
#define LOTEC_BUFFER_GI_FIELD_INCLUDED

// Shared layout for the buffer-based GI (the textureless, cache-resident GI). One fixed 32^3 grid
// per cascade so a voxel index fits in 16 bits and the whole field stays inside the GPU L2. All
// fields are flat StructuredBuffers indexed by BgiIndex; this header holds the index math, the
// world<->grid mapping (its own _Bgi* grid, independent of the SDF/volume UVW space), and the
// pack/unpack helpers shared by the compute solve, the fragment read, and the debug visualizer.
// Buffers themselves are declared per-shader (RW in compute, read-only elsewhere).

// 32^3 = 32768 voxels (< 65536, so a 16-bit index suffices). GRID is a power of two so the
// linear<->3D index math reduces to shifts/masks.
static const uint BGI_GRID = 32u;
static const uint BGI_GRID_LOG2 = 5u;
static const uint BGI_GRID_MASK = BGI_GRID - 1u;
static const uint BGI_COUNT = BGI_GRID * BGI_GRID * BGI_GRID;

// Field slice offsets in the concatenated buffers (must match BufferGiUpdater CoarseField/FineField):
// coarse = slot 0, fine = slot 1, so any future fine fields stay contiguous (slots 1..N-1).
static const uint BGI_COARSE_OFFSET = 0u;
static const uint BGI_FINE_OFFSET = BGI_COUNT;

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

float3 BgiAlbedo(uint m) {
    return float3(m & 0xffu, (m >> 8) & 0xffu, (m >> 16) & 0xffu) * (1.0 / 255.0);
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

// --- Baked surface normal: octahedral-encoded unit vector packed into one uint (16 bits/axis) ---
// A per-voxel alternative to the runtime occupancy-gradient normal, for the BGI_BAKED_NORMALS path.
// Voxel normals only need coarse fidelity, but 16-bit oct is free here (one uint either way) and
// exact enough. Cost: 4 bytes/voxel.
float2 BgiOctWrap(float2 v) {
    return (1.0 - abs(v.yx)) * float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
}

uint BgiPackNormal(float3 n) {
    n /= max(abs(n.x) + abs(n.y) + abs(n.z), 1e-8);
    float2 e = (n.z >= 0.0) ? n.xy : BgiOctWrap(n.xy);
    e = e * 0.5 + 0.5; // [-1,1] -> [0,1]
    uint2 q = (uint2)(saturate(e) * 65535.0 + 0.5);
    return q.x | (q.y << 16);
}

float3 BgiUnpackNormal(uint p) {
    float2 e = float2(p & 0xffffu, (p >> 16) & 0xffffu) * (1.0 / 65535.0);
    e = e * 2.0 - 1.0; // [0,1] -> [-1,1]
    float3 n = float3(e.x, e.y, 1.0 - abs(e.x) - abs(e.y));
    float t = saturate(-n.z);
    n.xy += float2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
    return normalize(n);
}

#endif // LOTEC_BUFFER_GI_FIELD_INCLUDED
