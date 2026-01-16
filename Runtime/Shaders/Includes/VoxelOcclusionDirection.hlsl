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

// ---------------------------------------------------------------------------
// Precomputed global cheat-sheet texture
// We pack: R,G,B,A = 0..63 indices
TEXTURE2D(_FibIndexTexture);
SAMPLER(sampler_FibIndexTexture);

// Debug mode global (0 = off)
int _VoxelDebugMode;
// Inverse of voxel size in world units (set from C#)
float3 _InverseVoxelSize;

// -----------------------------------------------------------------------------
// 0. HELPERS
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

// -----------------------------------------------------------------------------
// 1. DATA STRUCTURES
// -----------------------------------------------------------------------------

// The "Dictionary": 64 Directions stored in fast Constant Memory.
// C# is the source of truth; SdfShaderGlobals uploads `_FibonacciDirections` as a global Vector4 array.

// -----------------------------------------------------------------------------
// 2. HELPER: OCTAHEDRAL MAPPING
// -----------------------------------------------------------------------------
// Maps a 3D direction to the 2D texture UVs
float2 PackOctahedral(float3 dir) {
    dir = normalize(dir);
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
// -----------------------------------------------------------------------------
// 4. CORE: WEIGHT CALCULATION
// -----------------------------------------------------------------------------
// Calculates how much influence each of the 4 neighbors has based on distance.
float4 _FibonacciDirections[64];

float4 CalculateWeights(float3 lightDir, uint4 indices) {
    lightDir = normalize(lightDir);
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

/**
 * Calculates a spatially smoothed shadow value by sampling 4 neighboring voxels.
 * @param voxTex        The 3D Texture (uint2) containing the 64-bit occlusion masks.
 * @param localPos      The current pixel's position in Voxel Space (e.g., worldPos / voxelScale).
 * @param selectedIndex The single Fibonacci direction index (0-63) chosen for this pixel.
 * @return              A smooth float value: 0.0 (fully shadowed) to 1.0 (fully lit).
 */
float GetSpatialOcclusion(float3 localPos, uint selectedIndex) {
    // 1. Calculate Grid Coordinates
    int3 baseIdx = int3(floor(localPos));
    float3 f = frac(localPos);

    // 2. Setup Bit-Selection Logic
    // We do this once to avoid redundant math in the 4 taps
    uint selectMask = selectedIndex >> 5; 
    uint shiftAmount = selectedIndex & 31;

    // 3. Helper to fetch and extract a single bit
    // Returns 1.0 if occluded, 0.0 if clear
    #define FETCH_BIT(offset) \
        (float)((((selectMask == 1) ? GetBitmaskAtVoxel(baseIdx + offset).y : \
                                      GetBitmaskAtVoxel(baseIdx + offset).x) >> shiftAmount) & 1)

    // 4. Identify the 3 most relevant neighbors
    // If f.x > 0.5, we want the neighbor at +1, otherwise -1
    int3 s = int3(sign(f - 0.5)); 
    
    // Fetch the 4 corners of the tetrahedron
    float v000 = FETCH_BIT(int3(0, 0, 0)); // The base voxel
    float v100 = FETCH_BIT(int3(s.x, 0, 0)); // Neighbor along X
    float v010 = FETCH_BIT(int3(0, s.y, 0)); // Neighbor along Y
    float v001 = FETCH_BIT(int3(0, 0, s.z)); // Neighbor along Z

    // 5. Calculate Interpolation Weights
    // We measure how far we are from the center (0.5) of the base voxel
    float3 weights = abs(f - 0.5);

    // 6. Perform the Blend
    // We start with the base voxel value and lerp toward neighbors 
    // based on how far we've crossed into their "territory"
    float occ = v000;
    occ = lerp(occ, v100, weights.x);
    occ = lerp(occ, v010, weights.y); // Note: Simplified 3rd axis lerp
    occ = lerp(occ, v001, weights.z);

    // 7. Final Visibility Flip
    // 0 = Full Shadow, 1 = Full Light
    return saturate(1.0 - occ);
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
// -----------------------------------------------------------------------------
// MAIN: The 4-Tap Spatial + Angular Blend
// Returns 0.0 (Shadow) to 1.0 (Lit)
// -----------------------------------------------------------------------------
float GetFinalShadow(float3 worldPos, float3 lightDir) {
    // 1. Calculate Spatial Coordinates
    // Add offset towards light
    float3 offsetPos = worldPos + normalize(lightDir) * (_SdfBoundsSize.x / _VoxelResolution.x) * 0.5;
    // float3 offsetPos = worldPos;
    float3 localPos = (offsetPos - _SdfBoundsMin) * _InverseVoxelSize;
    // float3 localPos = (worldPos - _SdfBoundsMin) * _InverseVoxelSize;
    int3 baseIdx = int3(floor(localPos)); // Which voxel we're in (integer, xyz)
    float3 f = frac(localPos); // Where in the voxel we are (0..1)

    // 2. Map light direction to octahedral UVs
    float2 uv = PackOctahedral(lightDir);
    // Get 4 closest Fibonacci direction indices to lightDir
    half4 rawIndices = _FibIndexTexture.SampleLevel(sampler_FibIndexTexture, uv, 0);
    // Saved as 0-1 in texture, so map to 0-255
    uint4 indices = (uint4)(rawIndices * 255.5);


    // TEST
    float selectedIndex = indices.x; // Just use the first index for testing
    uint2 voxelMask = GetBitmaskAtVoxel(baseIdx);
    return 1 - GetBit64(voxelMask, selectedIndex);

    
    return GetSpatialOcclusion(localPos, selectedIndex);
}
float GetFinalShadow2(float3 worldPos, float3 lightDir, float3 normal) {
    // Add half a voxel offset along normal to reduce self-occlusion
    float3 offsetPos = worldPos + normal * (1/_InverseVoxelSize) * 0.5f;
    // float3 offsetPos = worldPos;
    return GetFinalShadow(offsetPos, lightDir);
}
#endif
