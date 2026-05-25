// SpatialHashGi.hlsl
// Surface-Based Spatial Hash Voxel GI
// Stores indirect light exclusively at geometric surfaces using a compact
// StructuredBuffer + collision-free spatial hash lookup (no Texture3D).

#ifndef LOTEC_SPATIAL_HASH_GI_INCLUDED
#define LOTEC_SPATIAL_HASH_GI_INCLUDED

#include "Volume.hlsl"

#define SPATIAL_HASH_GRID_SIZE 64
#define SPATIAL_HASH_GRID_TOTAL (SPATIAL_HASH_GRID_SIZE * SPATIAL_HASH_GRID_SIZE * SPATIAL_HASH_GRID_SIZE)

// Size: 12 bytes (Perfect alignment for GPU cache lines)
struct VoxelGI
{
    uint voxelPackedPos; // X, Y, Z coordinates packed (10 bits each: 0-1023)
    uint colorAmbient;   // RGB base light (Ambient) packed as R8G8B8A8
    uint colorModifiers; // L1 SH coefficients (X, Y, Z) packed as R8G8B8A8
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

inline uint PackR8G8B8A8(float4 color)
{
    uint r = (uint)(saturate(color.r) * 255.0);
    uint g = (uint)(saturate(color.g) * 255.0);
    uint b = (uint)(saturate(color.b) * 255.0);
    uint a = (uint)(saturate(color.a) * 255.0);
    return (r << 24) | (g << 16) | (b << 8) | a;
}

inline float4 UnpackR8G8B8A8(uint packed)
{
    float r = ((packed >> 24) & 0xFF) / 255.0;
    float g = ((packed >> 16) & 0xFF) / 255.0;
    float b = ((packed >> 8) & 0xFF) / 255.0;
    float a = (packed & 0xFF) / 255.0;
    return float4(r, g, b, a);
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
        float3 ambient = UnpackR8G8B8A8(data.colorAmbient).rgb;
        float3 sh = UnpackR8G8B8A8(data.colorModifiers).rgb;

        // Decode SH from [0,1] bias encoding back to [-1,1]
        sh = sh * 2.0 - 1.0;

        // Anisotropic L1 reconstruction: protects house interiors
        // based on the surface normal direction.
        finalIrradiance = ambient + (sh.x * worldNormal.x + sh.y * worldNormal.y + sh.z * worldNormal.z);
    }

    return max(finalIrradiance, 0);
}

#endif // LOTEC_SPATIAL_HASH_GI_INCLUDED
