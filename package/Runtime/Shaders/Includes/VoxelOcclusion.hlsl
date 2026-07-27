#ifndef LOTEC_VOXEL_OCCLUSION_INCLUDED
#define LOTEC_VOXEL_OCCLUSION_INCLUDED

// Baked directional occlusion shadow sources, read by the buffer-GI per-field shadow modes
// (BgiSampleFaceAoShadow):
//   GetBitmaskShadow   -> per-voxel directional bitmask (trilinear 8-tap)
//   GetOccFieldShadow  -> per-direction occlusion field (hardware trilinear)
// Both are baked alternatives to the runtime SDF shadow march.

#include "Volume.hlsl"   // _VoxelVolumeBounds*, WorldToVoxelUV
#include "Math.hlsl"     // GetBit64

// Occlusion-grid uniforms, published by the active occlusion binder.
float3 _VoxelSize;        // world-space size of one voxel
float3 _VoxelSizeInverse; // 1 / _VoxelSize, kept so world->voxel-index stays a multiply

// -----------------------------------------------------------------------------
// BITMASK (directional occlusion bits per voxel)
// -----------------------------------------------------------------------------

float3 _VoxelResolution; // bitmask grid resolution (as a float vector)

// Bitmask texture. Format depends on direction count:
//   8 dirs  -> R8_UNorm      (8 bits in R channel)
//   32 dirs -> RG16_UNorm    (32 bits: R,G = low/high 16 bits)
//   64 dirs -> RGBA16_UNorm  (64 bits: R,G = uint.x low/high, B,A = uint.y low/high)
TEXTURE3D(_BitmaskTex);

// Precomputed nearest Fibonacci direction index for the sun (set from C# each frame).
int _BitmaskSunFibIndex;

// Number of baked directions (8, 32, or 64). Determines texture decode path.
int _BitmaskDirCount;

// Stays fp32: 65535 is past fp16's 65504 max, and the product has to land on an exact
// integer for the bit decode to be right.
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
// Returns 0.0 (shadow) to 1.0 (lit). The taps are single bits and the weights are
// fractions in [0,1), so the whole blend runs in fp16; only `localPos` (a voxel-space
// coordinate that reaches the grid resolution) has to be fp32 for frac() to be exact.
inline half GetShadowBitTrilinear8Tap(float3 localPos, uint chosenIndex) {
    int3 baseIdx = int3(floor(localPos));
    half3 f = (half3)frac(localPos);

    half o000 = (half)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(0, 0, 0)), chosenIndex);
    half o100 = (half)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(1, 0, 0)), chosenIndex);
    half o010 = (half)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(0, 1, 0)), chosenIndex);
    half o110 = (half)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(1, 1, 0)), chosenIndex);
    half o001 = (half)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(0, 0, 1)), chosenIndex);
    half o101 = (half)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(1, 0, 1)), chosenIndex);
    half o011 = (half)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(0, 1, 1)), chosenIndex);
    half o111 = (half)GetBit64(GetBitmaskAtVoxel(baseIdx + int3(1, 1, 1)), chosenIndex);

    half oX00 = lerp(o000, o100, f.x);
    half oX10 = lerp(o010, o110, f.x);
    half oX01 = lerp(o001, o101, f.x);
    half oX11 = lerp(o011, o111, f.x);
    half oXY0 = lerp(oX00, oX10, f.y);
    half oXY1 = lerp(oX01, oX11, f.y);

    return saturate(1.0h - lerp(oXY0, oXY1, f.z));
}

// Shadow query using the precomputed sun Fibonacci index. Returns 0.0 (shadow) to 1.0 (lit).
half GetBitmaskShadow(float3 worldPos) {
    float3 localPos = (worldPos - _VoxelVolumeBoundsMin) * _VoxelSizeInverse;
    return GetShadowBitTrilinear8Tap(localPos, (uint)_BitmaskSunFibIndex);
}

// Variant with normal-based offset to reduce self-occlusion.
half GetBitmaskShadow(float3 worldPos, float3 normal) {
    float3 offsetPos = worldPos + normal * _VoxelSize * 1.2;
    return GetBitmaskShadow(offsetPos);
}

// -----------------------------------------------------------------------------
// OCCLUSION FIELD (per-direction lit value, hardware trilinear)
// -----------------------------------------------------------------------------

// Sun direction query results (set from C# each frame).
float3 _OccFieldSunDir;
int _OccFieldSunChannel;

// Slope of the decode ramp, published by VoxelOcclusionField.Bind (see DecodeScale there).
float _OccFieldDecode;

// Active occlusion field texture (bound per-frame to the texture matching the sun direction).
TEXTURE3D(_OccFieldTex);
SAMPLER(sampler_OccFieldTex);

// Sample the occlusion field shadow using the precomputed sun direction.
// Returns 0.0 (shadow) to 1.0 (lit). The fetch narrows to fp16 immediately; only the uvw lookup
// stays fp32.
//
// The ramp decodes BOTH baked encodings with no branch and no keyword, which matters because this
// runs per fragment on a pass that is already occupancy-bound:
//   _OccFieldDecode == 1          -> the channel is a lit value and passes straight through.
//   _OccFieldDecode == range/pen  -> the channel is a signed distance to the shadow boundary, and
//                                    the ramp turns it into an edge `pen` voxels wide. Because the
//                                    boundary is RECONSTRUCTED rather than interpolated, that edge
//                                    can be far sharper than the voxel grid - the same reason an SDF
//                                    font stays crisp when magnified.
half GetOccFieldShadow(float3 worldPos) {
    float3 uvw = WorldToVoxelUV(worldPos);
    half4 raw = (half4)_OccFieldTex.SampleLevel(sampler_OccFieldTex, uvw, 0);
    half stored;
    if (_OccFieldSunChannel == 0) stored = raw.r;
    else if (_OccFieldSunChannel == 1) stored = raw.g;
    else if (_OccFieldSunChannel == 2) stored = raw.b;
    else stored = raw.a;
    return saturate((stored - 0.5h) * (half)_OccFieldDecode + 0.5h);
}

// Variant with normal-based offset to reduce self-occlusion.
half GetOccFieldShadow(float3 worldPos, float3 normal) {
    float3 offsetPos = worldPos + normal * _VoxelSize * 1.2;
    return GetOccFieldShadow(offsetPos);
}

#endif // LOTEC_VOXEL_OCCLUSION_INCLUDED
