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

/**
 * Extracts 4 occlusion bits from the 64-bit value (uint2).
 * NOTE: This texture stores *occlusion* bits (1 = occluded, 0 = unoccluded).
 * @param bitmask The uint2 64bit occlusion bitmask for the voxel
 * @param indices   The 4 fibonacci direction indices to extract bits for
 */
float4 GetOcclusionBit4(uint2 bitmask, uint4 indices) {
    float4 occlusion;

    // Select the correct 32-bit word per index, then extract the bit.
    uint word32x = bitmask[indices.x >> 5];
    uint word32y = bitmask[indices.y >> 5];
    uint word32z = bitmask[indices.z >> 5];
    uint word32w = bitmask[indices.w >> 5];

    occlusion.x = (float)GetBit32(indices.x, word32x);
    occlusion.y = (float)GetBit32(indices.y, word32y);
    occlusion.z = (float)GetBit32(indices.z, word32z);
    occlusion.w = (float)GetBit32(indices.w, word32w);
    return occlusion;
}

// -----------------------------------------------------------------------------
// ANGULAR LOOKUP HELPERS
// -----------------------------------------------------------------------------

/*
* Calculates how much influence each of the 4 neighbors has based on distance.
* Returns unnormalized weights (sum may be > 1.0).
* @param lightDir Normalized light direction
* @param indices  The 4 fibonacci direction indices to calculate weights for
*/
float4 CalculateWeights(float3 lightDir, uint4 indices) {
    // 1. Reconstruct the exact 3D vectors for our 4 neighbors
    float3 d0 = _FibonacciDirections[indices.x].xyz;
    float3 d1 = _FibonacciDirections[indices.y].xyz;
    float3 d2 = _FibonacciDirections[indices.z].xyz;
    float3 d3 = _FibonacciDirections[indices.w].xyz;

    // 2. Calculate Angular Alignment (Dot Product)
    // Result is close to 1.0 if perfectly aligned, < 1.0 if further away.
    float4 dots;
    dots.x = max(0, dot(lightDir, d0));
    dots.y = max(0, dot(lightDir, d1));
    dots.z = max(0, dot(lightDir, d2));
    dots.w = max(0, dot(lightDir, d3));

    // 3. Sharpen the Weights
    // Using a power function (pow) makes the closest neighbor much stronger.
    // _ShadowSoftness: High (e.g., 128) = Sharp Shadow. Low (e.g., 32) = Soft/Blurry.
    // TODO: Add _ShadowSoftness parameter maybe, but probably better to base it on distance.
    float sharpness = 2.0; 
    float4 weights = pow(dots, sharpness);

    // 4. Tiny epsilon to prevent divide-by-zero if all weights are 0 (rare)
    return weights + 1e-5;
}

// -----------------------------------------------------------------------------
// SHADOW FETCHING HELPERS
// -----------------------------------------------------------------------------

/*
* Fetches shadow value (0.0 = shadow, 1.0 = lit) from the voxel occlusion bitmask
* using 4-tap spatial + angular filtering.
* @param worldPos World position of the pixel to shade
* @param lightDir Normalized light direction
* @param indices  The 4 fibonacci direction indices to sample
* @param weights  The corresponding weights for each index
* Returns 0.0 (Shadow) to 1.0 (Lit)
*/
float GetShadowFromSingleVoxel(uint2 mask, float3 lightDir, uint4 indices, float4 weights) {
    float4 occlusion = GetOcclusionBit4(mask, indices);
    float totalWeight = dot(weights, float4(1,1,1,1));
    float weightedOcclusion = dot(occlusion, weights) / totalWeight;
    return saturate(1.0 - weightedOcclusion);
}

/*
* Fetches shadow value (0.0 = shadow, 1.0 = lit) from a single voxel
* using the selected direction index.
* @param voxelIdx Voxel index to sample
* @param lightDir Normalized light direction
* @param indices  The 4 fibonacci direction indices to sample
* @param weights  The corresponding weights for each index
* Returns 0.0 (Shadow) to 1.0 (Lit)
*/
inline float GetShadowAngularAtVoxel(int3 voxelIdx, float3 lightDir, uint4 indices, float4 weights)
{
    uint2 mask = GetBitmaskAtVoxel(voxelIdx);
    return GetShadowFromSingleVoxel(mask, lightDir, indices, weights);
}

// -----------------------------------------------------------------------------
// SHADOW FETCHING FUNCTIONS
// -----------------------------------------------------------------------------

/*
* Performs a 3-step ray traversal along the light direction, sampling
* the occlusion bit at each step and combining them with weights to produce
* a final shadow value.
* @param worldPos World position of the pixel to shade
* @param lightDir Normalized light direction
* @param chosenIndex The single fibonacci direction index to sample
* Returns 0.0 (Shadow) to 1.0 (Lit)
*/
inline float GetShadowRay3Traversal(float3 worldPos, float3 lightDir, uint chosenIndex)
{
    float3 voxelSize = GetVoxelSizeWorld();
    // Start bias to reduce self-occlusion.
    // distance from center to voxel boundary along light lightDirection (approx)
    float startBias = dot(abs(lightDir), voxelSize) * 0.5;
    float3 startPos = worldPos + lightDir * startBias;

    // We want the 3 taps to actually span multiple voxels (otherwise Ray3 ~= Point).
    // Approximate "voxel length" along this lightDirection, then take ~0.75 of it per step.
    float voxelLenAlongDir = dot(abs(lightDir), voxelSize);

    float baseStepDist = max(voxelLenAlongDir * 0.75, 1e-4);

    // Only fade very near the horizon to avoid wide lateral smearing.
    // (At ~0 deg, abs(lightDir.y)=0 -> Ray3 collapses to the first tap)
    float stepScale = saturate(abs(lightDir.y) / 0.2);
    float stepDist = max(baseStepDist * stepScale, 1e-5);

    float occ = 0.0;
    float wSum = 0.0;

    [unroll]
    for (int i = 0; i < 3; i++)
    {
        // Slightly front-load weights to reduce "over-darkening" from far taps.
        float w = (i == 0) ? 0.5 : ((i == 1) ? 0.3 : 0.2);

        float3 samplePos = startPos + lightDir * (stepDist * (float)i);
        float3 localPos = (samplePos - _SdfBoundsMin) * _InverseVoxelSize;

        // Use floor() (cell selection) rather than round() to avoid hopping into nearby solids.
        int3 voxelIdx = int3(floor(localPos));

        uint2 mask = GetBitmaskAtVoxel(voxelIdx);
        occ += w * (float)GetBit64(mask, chosenIndex);
        wSum += w;
    }

    occ = occ / max(wSum, 1e-6);
    return saturate(1.0 - occ);
}

/*
* Performs a 4-tap spatial blend of the occlusion values at neighboring voxels,
* using angular filtering for each tap. Does not look good.
* @param localPos Local voxel-space position of the pixel to shade
* @param lightDir Normalized light direction
* @param indices  The 4 fibonacci direction indices to sample
* @param weights  The corresponding weights for each index
* Returns 0.0 (Shadow) to 1.0 (Lit)
*/
inline float GetShadowAngularSpatial4Tap(float3 localPos, float3 lightDir, uint4 indices, float4 weights)
{
    int3 baseIdx = int3(floor(localPos));
    float3 f = frac(localPos);

    // Choose the neighbor direction for each axis (either -1 or +1).
    // sign(0) returns 0, which is fine (degenerate edge case).
    int3 s = int3(sign(f - 0.5));

    float sh000 = GetShadowAngularAtVoxel(baseIdx + int3(0, 0, 0), lightDir, indices, weights);
    float sh100 = GetShadowAngularAtVoxel(baseIdx + int3(s.x, 0, 0), lightDir, indices, weights);
    float sh010 = GetShadowAngularAtVoxel(baseIdx + int3(0, s.y, 0), lightDir, indices, weights);
    float sh001 = GetShadowAngularAtVoxel(baseIdx + int3(0, 0, s.z), lightDir, indices, weights);

    // How far we've crossed toward each chosen neighbor.
    float3 w = saturate(abs(f - 0.5) * 2.0);

    float sh = sh000;
    sh = lerp(sh, sh100, w.x);
    sh = lerp(sh, sh010, w.y);
    sh = lerp(sh, sh001, w.z);
    return saturate(sh);
}

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

    // BITMASK_RAY3: 3 taps along the light ray for a small penumbra
    #if defined(BITMASK_RAY3)
        uint chosenIndex = FibonacciIndexTex(_FibIndexTexture, sampler_FibIndexTexture, lightDir);
        return GetShadowRay3Traversal(worldPos, lightDir, chosenIndex);
    #endif

    // BITMASK_8TAP: 2x2x2 trilinear blend of the selected-direction occlusion bit.
    #if defined(BITMASK_8TAP)
        uint chosenIndex = FibonacciIndexTex(_FibIndexTexture, sampler_FibIndexTexture, lightDir);
        return GetShadowBitTrilinear8Tap(localPos, chosenIndex);
    #endif

    // 3) 4-tap spatial filtering
    #if defined(BITMASK_4TAP)
        uint4 indices = FibonacciIndicesTex(_FibIndexTexture, sampler_FibIndexTexture, lightDir);
        float4 weights = CalculateWeights(lightDir, indices);
        return GetShadowAngularSpatial4Tap(localPos, lightDir, indices, weights);
    #else
        // Default to 4-tap if no mode is selected.
        uint4 indices = FibonacciIndicesTex(_FibIndexTexture, sampler_FibIndexTexture, lightDir);
        float4 weights = CalculateWeights(lightDir, indices);
        return GetShadowAngularSpatial4Tap(localPos, lightDir, indices, weights);
    #endif
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
