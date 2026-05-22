#ifndef LOTEC_VOXEL_OCCLUSION_DIRECTION_INCLUDED
#define LOTEC_VOXEL_OCCLUSION_DIRECTION_INCLUDED

// Directional occlusion bitmask shadows.
//
// Expects the includer to provide:
//   float3 _SdfBoundsMin;
//   float3 _InverseVoxelSize;
//   float3 _VoxelResolution;

// Bitmask texture. Format depends on direction count:
//   8 dirs  -> R8_UNorm      (8 bits in R channel)
//   32 dirs -> RG16_UNorm    (32 bits: R,G = low/high 16 bits)
//   64 dirs -> RGBA16_UNorm  (64 bits: R,G = uint.x low/high, B,A = uint.y low/high)
TEXTURE3D(_BitmaskTex);

// Precomputed nearest Fibonacci direction index for the sun (set from C# each frame).
int _BitmaskSunFibIndex;

// Number of baked directions (8, 32, or 64). Determines texture decode path.
int _BitmaskDirCount;

inline uint U16FromUNorm(float v) {
    return (uint)floor(v * 65535.0 + 0.5);
}

inline uint2 GetBitmaskAtVoxel(int3 voxelIdx) {
    voxelIdx = clamp(voxelIdx, int3(0,0,0), int3(_VoxelResolution) - int3(1,1,1));
    float4 raw = _BitmaskTex.Load(int4(voxelIdx, 0));

    if (_BitmaskDirCount <= 8) {
        // R8_UNorm: 8 bits packed in R channel
        return uint2((uint)floor(raw.r * 255.0 + 0.5), 0);
    }
    if (_BitmaskDirCount <= 32) {
        // RG16_UNorm: 32 bits across R and G
        uint lo = U16FromUNorm(raw.r);
        uint hi = U16FromUNorm(raw.g);
        return uint2(lo | (hi << 16), 0);
    }
    // RGBA16_UNorm: full 64 bits
    uint xLo = U16FromUNorm(raw.r);
    uint xHi = U16FromUNorm(raw.g);
    uint yLo = U16FromUNorm(raw.b);
    uint yHi = U16FromUNorm(raw.a);
    return uint2(xLo | (xHi << 16), yLo | (yHi << 16));
}

// Trilinear interpolation of a single occlusion bit across 8 neighboring voxels.
// Returns 0.0 (shadow) to 1.0 (lit).
inline float GetShadowBitTrilinear8Tap(float3 localPos, uint chosenIndex) {
    int3 baseIdx = int3(floor(localPos));
    float3 f = frac(localPos);

    float o000 = (float)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(0, 0, 0)), chosenIndex);
    float o100 = (float)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(1, 0, 0)), chosenIndex);
    float o010 = (float)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(0, 1, 0)), chosenIndex);
    float o110 = (float)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(1, 1, 0)), chosenIndex);
    float o001 = (float)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(0, 0, 1)), chosenIndex);
    float o101 = (float)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(1, 0, 1)), chosenIndex);
    float o011 = (float)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(0, 1, 1)), chosenIndex);
    float o111 = (float)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(1, 1, 1)), chosenIndex);

    float oX00 = lerp(o000, o100, f.x);
    float oX10 = lerp(o010, o110, f.x);
    float oX01 = lerp(o001, o101, f.x);
    float oX11 = lerp(o011, o111, f.x);
    float oXY0 = lerp(oX00, oX10, f.y);
    float oXY1 = lerp(oX01, oX11, f.y);

    return saturate(1.0 - lerp(oXY0, oXY1, f.z));
}

// Shadow query using the precomputed sun Fibonacci index.
// Returns 0.0 (shadow) to 1.0 (lit).
float GetBitmaskShadow(float3 worldPos) {
    float3 localPos = (worldPos - _SdfBoundsMin) * _InverseVoxelSize;
    uint chosenIndex = (uint)_BitmaskSunFibIndex;

    #if defined(BITMASK_POINT)
        int3 baseIdx = int3(floor(localPos));
        uint2 mask = GetBitmaskAtVoxel(baseIdx);
        return saturate(1.0 - (float)GetBit64(mask, chosenIndex));
    #else
        return GetShadowBitTrilinear8Tap(localPos, chosenIndex);
    #endif
}

// Variant with normal-based offset to reduce self-occlusion.
float GetBitmaskShadow(float3 worldPos, float3 normal) {
    float3 voxelSize = rcp(max(_InverseVoxelSize, 1e-6));
    float3 offsetPos = worldPos + normal * voxelSize * 1.2;
    return GetBitmaskShadow(offsetPos);
}

#endif
