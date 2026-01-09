#ifndef LOTECSOFTWARE_VOXEL_OCCLUSION_DIRECTION_INCLUDED
#define LOTECSOFTWARE_VOXEL_OCCLUSION_DIRECTION_INCLUDED

// Directional occlusion bitmask helpers.
//
// Expects the including file to provide:
//   float3 _SdfBoundsMin;
//   float3 _SdfBoundsSize;
//   float3 _VoxelResolution;
//
// And Math.hlsl (for NearestFibonacciDirectionIndex64), which we include here.

#include "Math.hlsl"

// 64-bit occlusion direction bitmask field, stored as RGBA16_UNorm:
//   R,G = low/high 16 bits of uint bitmask.x
//   B,A = low/high 16 bits of uint bitmask.y
TEXTURE3D(_BitmaskTex);
SAMPLER(sampler_BitmaskTex);

inline uint2 DecodeOcclusionBitmask64_RGBA16_UNorm(float4 packed)
{
    uint xLo = (uint)round(saturate(packed.r) * 65535.0);
    uint xHi = (uint)round(saturate(packed.g) * 65535.0);
    uint yLo = (uint)round(saturate(packed.b) * 65535.0);
    uint yHi = (uint)round(saturate(packed.a) * 65535.0);

    uint x = xLo | (xHi << 16);
    uint y = yLo | (yHi << 16);
    return uint2(x, y);
}

// Sample bitmask texture and check if a light direction is occluded at this position.
// Returns true if the light direction is marked as occluded in the directional bitmask.
inline bool CheckBitmaskOcclusion(float3 worldPos, float3 lightDir)
{
    float3 localPos = (worldPos - _SdfBoundsMin) / _SdfBoundsSize;

    // Early exit if outside bounds
    if (any(localPos < 0.0) || any(localPos > 1.0))
        return false;

    // Map to voxel coordinates
    uint3 voxelResU = max((uint3)_VoxelResolution, uint3(1u, 1u, 1u));
    uint3 voxelIdx = (uint3)(localPos * (float3)voxelResU);
    voxelIdx = min(voxelIdx, voxelResU - uint3(1u, 1u, 1u));

    // Sample packed RGBA16_UNorm bitmask (point filtered)
    float3 uvw = (float3(voxelIdx) + 0.5) / (float3)voxelResU;
    float4 bitmaskPacked = SAMPLE_TEXTURE3D(_BitmaskTex, sampler_BitmaskTex, uvw).rgba;
    uint2 bitmask = DecodeOcclusionBitmask64_RGBA16_UNorm(bitmaskPacked);

    // Normalize light direction and find closest Fibonacci direction via dot products.
    lightDir = normalize(lightDir);
    uint closestDir = NearestFibonacciDirectionIndex64(lightDir);

    // Check if this direction is occluded
    if (closestDir < 32u)
        return (bitmask.x & (1u << closestDir)) != 0u;

    return (bitmask.y & (1u << (closestDir - 32u))) != 0u;
}

// Returns 1 for lit, 0 for fully shadowed.
// Fast directional bitmask-only occlusion test (instant, no raymarching).
inline float GetShadowFromBitmask(Light light, float3 worldPos)
{
    if (CheckBitmaskOcclusion(worldPos, light.direction))
        return 0.0;

    return 1.0;
}

#endif
