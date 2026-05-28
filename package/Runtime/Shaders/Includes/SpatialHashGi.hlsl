// SpatialHashGi.hlsl
// Surface-Based Spatial Hash Voxel GI
// Stores indirect light exclusively at geometric surfaces using a compact
// StructuredBuffer + collision-free spatial hash lookup (no Texture3D).

#ifndef LOTEC_SPATIAL_HASH_GI_INCLUDED
#define LOTEC_SPATIAL_HASH_GI_INCLUDED

#include "Volume.hlsl"

#define SPATIAL_HASH_GRID_SIZE 64
#define SPATIAL_HASH_GRID_TOTAL (SPATIAL_HASH_GRID_SIZE * SPATIAL_HASH_GRID_SIZE * SPATIAL_HASH_GRID_SIZE)

// Size: 8 bytes (64 bits) — dwordx2 single-cycle read on Adreno 740
struct VoxelGI
{
    uint voxelPackedPos;  // X (10 bits), Y (10 bits), Z (10 bits), Padding (2 bits)
    uint packedLighting;  // R(5) G(6) B(5) M(2) axisX(4) axisY(4) axisZ(4) pad(2)
};

// --- Packing / Unpacking Utilities ---

inline uint PackPosition10(uint3 pos)
{
    return (pos.x & 0x3FF) | ((pos.y & 0x3FF) << 10) | ((pos.z & 0x3FF) << 20);
}

inline uint3 UnpackPosition10(uint packed)
{
    return uint3(packed & 0x3FF, (packed >> 10) & 0x3FF, (packed >> 20) & 0x3FF);
}

// Pack HDR color (RGB565 + 2-bit RGBM multiplier) and 3 axis directional modifiers
// into a single 32-bit uint.
//   hdrColor: linear HDR irradiance (range 0..8)
//   axisModifiers: signed directional weights in [-1, +1]
inline uint PackLighting(float3 hdrColor, float3 axisModifiers)
{
    // Determine RGBM multiplier: 1x, 2x, 4x, or 8x
    float maxChannel = max(max(hdrColor.r, hdrColor.g), hdrColor.b);
    uint m;
    float multiplier;
    if      (maxChannel > 4.0) { m = 3; multiplier = 8.0; }
    else if (maxChannel > 2.0) { m = 2; multiplier = 4.0; }
    else if (maxChannel > 1.0) { m = 1; multiplier = 2.0; }
    else                       { m = 0; multiplier = 1.0; }

    float3 baseColor = saturate(hdrColor / multiplier);

    uint r = (uint)(baseColor.r * 31.0 + 0.5);
    uint g = (uint)(baseColor.g * 63.0 + 0.5);
    uint b = (uint)(baseColor.b * 31.0 + 0.5);

    // Axis modifiers: remap [-1,1] to [0,15] unorm
    uint ax = (uint)(saturate(axisModifiers.x * 0.5 + 0.5) * 15.0 + 0.5);
    uint ay = (uint)(saturate(axisModifiers.y * 0.5 + 0.5) * 15.0 + 0.5);
    uint az = (uint)(saturate(axisModifiers.z * 0.5 + 0.5) * 15.0 + 0.5);

    return (r & 0x1F) | ((g & 0x3F) << 5) | ((b & 0x1F) << 11)
         | ((m & 0x3) << 16)
         | ((ax & 0xF) << 18) | ((ay & 0xF) << 22) | ((az & 0xF) << 26);
}

// Unpack the 32-bit packedLighting field into HDR color and axis modifiers.
inline void UnpackLighting(uint pL, out float3 hdrColor, out float3 axisModifiers)
{
    float3 baseColor = float3(
        (pL & 0x1F) / 31.0,
        ((pL >> 5) & 0x3F) / 63.0,
        ((pL >> 11) & 0x1F) / 31.0
    );
    float multiplier = (float)(1u << ((pL >> 16) & 0x3)); // 1, 2, 4, or 8
    hdrColor = baseColor * multiplier;

    axisModifiers.x = (((pL >> 18) & 0xF) / 15.0) * 2.0 - 1.0;
    axisModifiers.y = (((pL >> 22) & 0xF) / 15.0) * 2.0 - 1.0;
    axisModifiers.z = (((pL >> 26) & 0xF) / 15.0) * 2.0 - 1.0;
}

inline int SpatialHashLinearIndex(uint3 voxelPos)
{
    return (int)(voxelPos.x + (voxelPos.y * SPATIAL_HASH_GRID_SIZE) + (voxelPos.z * SPATIAL_HASH_GRID_SIZE * SPATIAL_HASH_GRID_SIZE));
}

// --- Read-only buffer declarations for surface shaders ---

StructuredBuffer<VoxelGI> _SpatialHashVoxelData;
StructuredBuffer<int> _SpatialHashGrid;
float _SpatialHashVoxelSize;
float _SpatialHashOneOverVoxelSize;

// --- Sampling ---

// Main entry point for sampling spatial hash GI from surface shaders.
// Equivalent to SampleVoxelGI() but uses the flat hash grid instead of Texture3D.
float3 SampleSpatialHashGi(float3 worldPos, float3 worldNormal)
{
    // Normal-bias trick: push sample position along the surface normal
    // by half a voxel. This forces the lookup to the correct side of
    // walls, preventing interior/exterior light bleed.
    worldPos += worldNormal * (_SpatialHashVoxelSize * 0.5);

    // Convert world position to hash grid coordinates
    float3 localPos = (worldPos - _VolumePosition) * _SpatialHashOneOverVoxelSize;
    int3 voxelPos = (int3)floor(localPos);

    // Bounds check against the 64^3 grid
    if (any(voxelPos < 0) || any(voxelPos >= (int)SPATIAL_HASH_GRID_SIZE))
        return float3(0, 0, 0);

    uint3 uVoxelPos = (uint3)voxelPos;
    int linearIndex = SpatialHashLinearIndex(uVoxelPos);
    int dataIndex = _SpatialHashGrid[linearIndex];

    float3 finalIrradiance = float3(0, 0, 0);

    if (dataIndex >= 0)
    {
        VoxelGI data = _SpatialHashVoxelData[dataIndex];

        float3 hdrColor;
        float3 axisModifiers;
        UnpackLighting(data.packedLighting, hdrColor, axisModifiers);

        // Anisotropic reconstruction via vector projection
        float directionalWeight = dot(worldNormal, axisModifiers);
        finalIrradiance = max(0.0, hdrColor + (hdrColor * directionalWeight));
    }

    return finalIrradiance;
}

#endif // LOTEC_SPATIAL_HASH_GI_INCLUDED
