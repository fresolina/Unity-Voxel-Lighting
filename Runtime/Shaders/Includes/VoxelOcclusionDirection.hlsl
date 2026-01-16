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

// -----------------------------------------------------------------------------
// DATA STRUCTURES
// -----------------------------------------------------------------------------

// 64-bit occlusion direction bitmask field, stored as RGBA16_UNorm:
//   R,G = low/high 16 bits of uint bitmask.x
//   B,A = low/high 16 bits of uint bitmask.y
TEXTURE3D(_BitmaskTex);

// -----------------------------------------------------------------------------
// Precomputed global cheat-sheet texture
// We pack: R,G,B,A = 0..63 indices
TEXTURE2D(_FibIndexTexture);
SAMPLER(sampler_FibIndexTexture);

// Debug mode global (0 = off)
int _VoxelDebugMode;
// Inverse of voxel size in world units (set from C#)
float3 _InverseVoxelSize;
// Precomputed Fibonacci directions (set from C#)
float4 _FibonacciDirections[64];

// -----------------------------------------------------------------------------
// HELPERS
// -----------------------------------------------------------------------------

// Convert UNorm float (0..1) to uint16 (0..65535)
inline uint U16FromUNorm(float v)
{
    // saturate optional if you guarantee the texture is UNorm and no filtering/mips are used
    return (uint)floor(saturate(v) * 65535.0 + 0.5);
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

// Maps a 3D direction to the 2D texture UVs
float2 PackOctahedral(float3 dir) {
    dir /= (abs(dir.x) + abs(dir.y) + abs(dir.z) + 1e-8);
    float2 p = dir.xz;
    if (dir.y < 0.0) {
        p = (1.0 - abs(p.yx)) * (p >= 0.0 ? 1.0 : -1.0);
    }
    return p * 0.5 + 0.5;
}

/**
 * Extracts a single bit from the 64-bit mask (uint2).
 * Since bitmask is not one 64bit integer, we have to create a 2x32 bit shift helper.
 * float value = (float)((bitmask >> bit) & 1);
 * HIGH-PERFORMANCE VERSION (Use inside loops)
 * Expects the 32-bit bucket to be pre-selected.
 * @param bit    The original 0-63 bit (we only use the lower 5 bits here).
 * @param word32 Either bitmask.x or bitmask.y.
 */
inline uint GetBit32(uint bit, uint word32) {
    // Standard bitwise ops are the safest and most portable.
    // The compiler optimizes this to a single 'BFE' instruction automatically.
    return (word32 >> (bit & 31u)) & 1u;
}
/**
 * Extracts a single bit from the 64-bit mask (uint2).
 * Since bitmask is not one 64bit integer, we have to create a 2x32 bit shift helper.
 * float bit = (float)((bitmask >> bit) & 1);
 * CONVENIENCE VERSION (Use for single lookups)
 * Handles bucket selection automatically.
 * @param bitmask  The full uint2 (64-bit) mask.
 * @param bit    The 0-63 fibonacci direction bi.
 */
inline uint GetBit64(uint2 bitmask, uint bit) {
    // bit >> 5 results in 0 for (0-31) and 1 for (32-63)
    uint word32 = bitmask[bit >> 5];
    return GetBit32(bit, word32);
}
// -----------------------------------------------------------------------------
// 3. CORE: VOXEL OCCLUSION CHECK
// -----------------------------------------------------------------------------
// Extracts 4 occlusion bits from the 64-bit mask (uint2).
// NOTE: This texture stores *occlusion* bits (1 = occluded, 0 = unoccluded).
// voxelMask: The uint2 64bit occlusion bitmask for the voxel
float4 GetOcclusionBit4(uint2 voxelMask, uint4 indices) {
    float4 occlusion;

    // Select the correct 32-bit word per index, then extract the bit.
    uint word32x = voxelMask[indices.x >> 5];
    uint word32y = voxelMask[indices.y >> 5];
    uint word32z = voxelMask[indices.z >> 5];
    uint word32w = voxelMask[indices.w >> 5];

    occlusion.x = (float)GetBit32(indices.x, word32x);
    occlusion.y = (float)GetBit32(indices.y, word32y);
    occlusion.z = (float)GetBit32(indices.z, word32z);
    occlusion.w = (float)GetBit32(indices.w, word32w);
    return occlusion;
}

inline uint4 DecodeFibIndicesFromTexel(half4 raw)
{
    // Texture stores indices as UNorm8: index/255.
    // Decode with rounding and clamp to [0,63].
    return (uint4)clamp((int4)round(raw * 255.0), 0, 63);
}

// -----------------------------------------------------------------------------
// ANGULAR LOOKUP HELPERS
// -----------------------------------------------------------------------------
// -----------------------------------------------------------------------------
// 4. CORE: WEIGHT CALCULATION
// -----------------------------------------------------------------------------
// Calculates how much influence each of the 4 neighbors has based on distance.

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
    // We use a safe default of 64 if you don't have a uniform set up.
    float sharpness = 2.0; 
    float4 weights = pow(dots, sharpness);

    // 4. Tiny epsilon to prevent divide-by-zero if all weights are 0 (rare)
    return weights + 1e-5;
}

inline uint4 GetFibIndices(float3 lightDir)
{
    float2 uv = PackOctahedral(lightDir);
    half4 raw = _FibIndexTexture.SampleLevel(sampler_FibIndexTexture, uv, 0);
    return DecodeFibIndicesFromTexel(raw);
}

inline float4 GetFibWeights(float3 lightDir, uint4 indices)
{
    // CalculateWeights adds a small epsilon to avoid div-by-zero.
    return CalculateWeights(lightDir, indices);
}

// -----------------------------------------------------------------------------
// 4b. STOCHASTIC INDEX PICKING (from 4 nearest)
// -----------------------------------------------------------------------------
inline float3 GetVoxelSizeWorld()
{
    return 1.0 / max(_InverseVoxelSize, 1e-6);
}

inline float ShadowStartBiasAlongDir(float3 dir)
{
    float3 voxelSize = GetVoxelSizeWorld();
    // distance from center to voxel boundary along this direction (approx)
    return dot(abs(dir), voxelSize) * 0.5;
}

// Assumes normalized direction input
inline uint NearestOcclusionDirectionIndex(float3 nDir)
{
    // Map light direction to octahedral UVs
    float2 uv = PackOctahedral(nDir);

    // Get 4 closest Fibonacci direction indices to lightDir
    half4 rawIndices = _FibIndexTexture.SampleLevel(sampler_FibIndexTexture, uv, 0);
    uint4 indices = DecodeFibIndicesFromTexel(rawIndices);

    // 4 dot products: cheapest reliable way to pick the closest.
    float d0 = dot(nDir, _FibonacciDirections[indices.x].xyz);
    float d1 = dot(nDir, _FibonacciDirections[indices.y].xyz);
    float d2 = dot(nDir, _FibonacciDirections[indices.z].xyz);
    float d3 = dot(nDir, _FibonacciDirections[indices.w].xyz);

    uint bestIndex = indices.x;
    float bestDot = d0;

    if (d1 > bestDot) { bestDot = d1; bestIndex = indices.y; }
    if (d2 > bestDot) { bestDot = d2; bestIndex = indices.z; }
    if (d3 > bestDot) { bestDot = d3; bestIndex = indices.w; }

    return bestIndex;
}

// -----------------------------------------------------------------------------
// 5. MAIN: GET FINAL SHADOW
// -----------------------------------------------------------------------------
float GetShadowFromSingleVoxel(uint2 mask, float3 lightDir, uint4 indices, float4 weights) {
    float4 occlusion = GetOcclusionBit4(mask, indices);
    float totalWeight = dot(weights, float4(1,1,1,1));
    float weightedOcclusion = dot(occlusion, weights) / totalWeight;
    return saturate(1.0 - weightedOcclusion);
}

inline float GetShadowAngularAtVoxel(int3 voxelIdx, float3 lightDir, uint4 indices, float4 weights)
{
    uint2 mask = GetBitmaskAtVoxel(voxelIdx);
    return GetShadowFromSingleVoxel(mask, lightDir, indices, weights);
}

// -----------------------------------------------------------------------------
// 5d. 3-STEP RAY TRAVERSAL FILTER (along light direction)
// -----------------------------------------------------------------------------

inline float GetShadowRay3Traversal(float3 worldPos, float3 lightDir, uint chosenIndex)
{
    float3 dir = lightDir;

    // Start bias to reduce self-occlusion.
    float startBias = ShadowStartBiasAlongDir(dir);
    float3 startPos = worldPos + dir * startBias;

    // We want the 3 taps to actually span multiple voxels (otherwise Ray3 ~= Point).
    // Approximate "voxel length" along this direction, then take ~0.75 of it per step.
    float3 voxelSize = GetVoxelSizeWorld();
    float voxelLenAlongDir = dot(abs(dir), voxelSize);

    float baseStepDist = max(voxelLenAlongDir * 0.75, 1e-4);

    // Only fade very near the horizon to avoid wide lateral smearing.
    // (At ~0 deg, abs(dir.y)=0 -> Ray3 collapses to the first tap)
    float stepScale = saturate(abs(dir.y) / 0.2);
    float stepDist = max(baseStepDist * stepScale, 1e-5);

    float occ = 0.0;
    float wSum = 0.0;

    [unroll]
    for (int i = 0; i < 3; i++)
    {
        // Slightly front-load weights to reduce "over-darkening" from far taps.
        float w = (i == 0) ? 0.5 : ((i == 1) ? 0.3 : 0.2);

        float3 samplePos = startPos + dir * (stepDist * (float)i);
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

// -----------------------------------------------------------------------------
// 5b. 4-TAP SPATIAL BLEND (tetra-like)
// -----------------------------------------------------------------------------
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

// -----------------------------------------------------------------------------
// MAIN: The 4-Tap Spatial + Angular Blend
// worldPos: World position of pixel to shade
// lightDir: Normalized light direction
// Returns 0.0 (Shadow) to 1.0 (Lit)
// -----------------------------------------------------------------------------
float GetFinalShadow(float3 worldPos, float3 lightDir) {
    // 1) Convert to voxel space. Offset start slightly along the light direction to reduce self-occlusion.
    float3 offsetPos = worldPos + lightDir * ShadowStartBiasAlongDir(lightDir);
    float3 localPos = (offsetPos - _SdfBoundsMin) * _InverseVoxelSize;

    // BITMASK_POINT: simplest possible shadow test (single voxel, single bit)
    // 1 = lit, 0 = shadow.
    #if defined(BITMASK_POINT)
        int3 baseIdx = int3(floor(localPos));
        uint chosenIndex = NearestOcclusionDirectionIndex(lightDir);
        uint2 mask = GetBitmaskAtVoxel(baseIdx);
        return saturate(1.0 - (float)GetBit64(mask, chosenIndex));
    #endif

    // BITMASK_RAY3: 3 taps along the light ray for a small penumbra
    #if defined(BITMASK_RAY3)
        uint chosenIndex = NearestOcclusionDirectionIndex(lightDir);
        return GetShadowRay3Traversal(worldPos, lightDir, chosenIndex);
    #endif

    // 3) 4-tap spatial filtering (previous BITMASK_POINT behavior)
    // BITMASK_4TAP is the new name. BITMASK_FILTERED is treated as an alias.
    #if defined(BITMASK_4TAP) || defined(BITMASK_FILTERED)
        uint4 indices = GetFibIndices(lightDir);
        float4 weights = GetFibWeights(lightDir, indices);
        return GetShadowAngularSpatial4Tap(localPos, lightDir, indices, weights);
    #else
        // Default to 4-tap if no mode is selected.
        uint4 indices = GetFibIndices(lightDir);
        float4 weights = GetFibWeights(lightDir, indices);
        return GetShadowAngularSpatial4Tap(localPos, lightDir, indices, weights);
    #endif
}
float GetFinalShadow2(float3 worldPos, float3 lightDir, float3 normal) {
    // Add half a voxel offset along normal to reduce self-occlusion
    float3 offsetPos = worldPos + normalize(normal) * (GetVoxelSizeWorld() * 0.5);
    return GetFinalShadow(offsetPos, lightDir);
}
#endif
