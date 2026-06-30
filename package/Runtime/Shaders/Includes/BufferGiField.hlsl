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

// Cascade placement in world space (set by BufferGiUpdater). Voxels are per-axis (the grid
// stretches to fill non-cubic volume bounds), so _BgiVoxelSize is a float3, not a scalar.
float3 _BgiGridOrigin; // world-space min corner of the cascade grid
float3 _BgiGridSize;   // world-space extent of the cascade grid
float3 _BgiVoxelSize;  // per-axis voxel size (= _BgiGridSize / BGI_GRID)

uint BgiIndex(uint3 c) {
    return c.x | (c.y << BGI_GRID_LOG2) | (c.z << (BGI_GRID_LOG2 * 2u));
}

uint3 BgiCoord(uint i) {
    return uint3(i & BGI_GRID_MASK,
                 (i >> BGI_GRID_LOG2) & BGI_GRID_MASK,
                 (i >> (BGI_GRID_LOG2 * 2u)) & BGI_GRID_MASK);
}

bool BgiInBounds(int3 c) {
    return all(c >= 0) && all(c < (int)BGI_GRID);
}

// World-space center of a voxel.
float3 BgiVoxelCenter(uint3 c) {
    return _BgiGridOrigin + (float3(c) + 0.5) * _BgiVoxelSize;
}

// Continuous grid coordinates (voxel units) of a world position. floor() gives the base cell.
float3 BgiWorldToGrid(float3 worldPos) {
    return (worldPos - _BgiGridOrigin) / max(_BgiVoxelSize, 1e-6);
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

#endif // LOTEC_BUFFER_GI_FIELD_INCLUDED
