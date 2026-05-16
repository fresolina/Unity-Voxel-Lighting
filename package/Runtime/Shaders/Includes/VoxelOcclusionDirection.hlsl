#ifndef LOTEC_VOXEL_OCCLUSION_DIRECTION_INCLUDED
#define LOTEC_VOXEL_OCCLUSION_DIRECTION_INCLUDED

// Directional occlusion bitmask helpers.
//
// Expects the includer to provide:
//   float3 _SdfBoundsMin;
//   float3 _SdfBoundsSize;
//   float3 _VoxelResolution;
//

#include "Fibonacci.hlsl"

// -----------------------------------------------------------------------------
// DATA STRUCTURES
// -----------------------------------------------------------------------------

// 64-bit occlusion direction bitmask field, stored as RGBA16_UNorm:
//   R,G = low/high 16 bits of uint bitmask.x
//   B,A = low/high 16 bits of uint bitmask.y
TEXTURE3D(_BitmaskTex);
// Precomputed 64 directions evenly distributed on a sphere with Fibonacci.
// Mapped onto 2D texture using octahedral mapping.
// We pack: R,G,B,A = 0..63 indices
// Usage: Map a direction to octahedral UVs, sample this texture to get the 4 nearest direction indices.
TEXTURE2D(_FibIndexTexture);
SAMPLER(sampler_FibIndexTexture);

// Inverse of voxel size in world units (set from C#)
float3 _InverseVoxelSize;

// -----------------------------------------------------------------------------
// HELPERS
// -----------------------------------------------------------------------------

// TODO: Upload as constant instead.
inline float3 GetVoxelSizeWorld()
{
    return 1.0 / max(_InverseVoxelSize, 1e-6);
}

// Convert UNorm float (0..1) to uint16 (0..65535)
// Assumes input is already clamped to [0,1]
inline uint U16FromUNorm(float v)
{
    return (uint)floor(v * 65535.0 + 0.5);
}
// Fetch the 64bit bitmask value for a given voxel index
inline uint2 GetBitmaskAtVoxel(int3 voxelIdx) {
    voxelIdx = clamp(voxelIdx, int3(0,0,0), int3(_VoxelResolution) - int3(1,1,1));
    float4 raw = _BitmaskTex.Load(int4(voxelIdx, 0));

    uint xLo = U16FromUNorm(raw.r);
    uint xHi = U16FromUNorm(raw.g);
    uint yLo = U16FromUNorm(raw.b);
    uint yHi = U16FromUNorm(raw.a);

    return uint2(xLo | (xHi << 16), yLo | (yHi << 16));
}

// -----------------------------------------------------------------------------
// SHADOW FETCHING FUNCTIONS
// -----------------------------------------------------------------------------

/*
* Performs a trilinear interpolation of the occlusion bit for the chosen direction
* across the 8 neighboring voxels.
* @param localPos Local voxel-space position of the pixel to shade
* @param chosenIndex The single fibonacci direction index to sample
* Returns 0.0 (Shadow) to 1.0 (Lit)
*/
inline float GetShadowBitTrilinear8Tap(float3 localPos, uint chosenIndex)
{
    int3 baseIdx = int3(floor(localPos));
    float3 f = frac(localPos);

    // Fetch the 8 corners (occlusion bits)
    uint2 m000 = GetBitmaskAtVoxel(baseIdx + int3(0, 0, 0));
    uint2 m100 = GetBitmaskAtVoxel(baseIdx + int3(1, 0, 0));
    uint2 m010 = GetBitmaskAtVoxel(baseIdx + int3(0, 1, 0));
    uint2 m110 = GetBitmaskAtVoxel(baseIdx + int3(1, 1, 0));
    uint2 m001 = GetBitmaskAtVoxel(baseIdx + int3(0, 0, 1));
    uint2 m101 = GetBitmaskAtVoxel(baseIdx + int3(1, 0, 1));
    uint2 m011 = GetBitmaskAtVoxel(baseIdx + int3(0, 1, 1));
    uint2 m111 = GetBitmaskAtVoxel(baseIdx + int3(1, 1, 1));

    float o000 = (float)GetBit64(m000, chosenIndex);
    float o100 = (float)GetBit64(m100, chosenIndex);
    float o010 = (float)GetBit64(m010, chosenIndex);
    float o110 = (float)GetBit64(m110, chosenIndex);
    float o001 = (float)GetBit64(m001, chosenIndex);
    float o101 = (float)GetBit64(m101, chosenIndex);
    float o011 = (float)GetBit64(m011, chosenIndex);
    float o111 = (float)GetBit64(m111, chosenIndex);

    // Trilinear interpolation of occlusion
    float oX00 = lerp(o000, o100, f.x);
    float oX10 = lerp(o010, o110, f.x);
    float oX01 = lerp(o001, o101, f.x);
    float oX11 = lerp(o011, o111, f.x);

    float oXY0 = lerp(oX00, oX10, f.y);
    float oXY1 = lerp(oX01, oX11, f.y);

    float occ = lerp(oXY0, oXY1, f.z);
    return saturate(1.0 - occ);
}

/*
* Main entry point: Fetch final shadow value from voxel occlusion bitmask
* using the selected method.
* @param worldPos World position of the pixel to shade
* @param lightDir Normalized light direction
* Returns 0.0 (Shadow) to 1.0 (Lit)
*/
float GetFinalShadow(float3 worldPos, float3 lightDir) {
    // 1) Convert to voxel space.
    float3 localPos = (worldPos - _SdfBoundsMin) * _InverseVoxelSize;

    // BITMASK_POINT: simplest possible shadow test (single voxel, single bit)
    // 1 = lit, 0 = shadow.
    #if defined(BITMASK_POINT)
        int3 baseIdx = int3(floor(localPos));
        uint chosenIndex = FibonacciIndexTex(_FibIndexTexture, sampler_FibIndexTexture, lightDir);
        uint2 mask = GetBitmaskAtVoxel(baseIdx);
        return saturate(1.0 - (float)GetBit64(mask, chosenIndex));
    #endif

    // BITMASK_8TAP: 2x2x2 trilinear blend of the selected-direction occlusion bit.
    #if defined(BITMASK_8TAP)
        uint chosenIndex = FibonacciIndexTex(_FibIndexTexture, sampler_FibIndexTexture, lightDir);
        return GetShadowBitTrilinear8Tap(localPos, chosenIndex);
    #endif

    // Default to 8-tap if no mode is selected.
    return GetShadowBitTrilinear8Tap(localPos, FibonacciIndexTex(_FibIndexTexture, sampler_FibIndexTexture, lightDir));
}

/*
* Variant of GetFinalShadow that adds a normal-based offset to reduce self-occlusion.
* @param worldPos World position of the pixel to shade
* @param lightDir Normalized light direction
* @param normal   Normal at the world position
* Returns 0.0 (Shadow) to 1.0 (Lit)
*/
float GetFinalShadow2(float3 worldPos, float3 lightDir, float3 normal) {
    // Offset sampling position along normal to reduce self-occlusion
    float3 offsetPos = worldPos + normal * GetVoxelSizeWorld() * 1.2;
    // float3 offsetPos = worldPos;
    return GetFinalShadow(offsetPos, lightDir);
}
#endif
