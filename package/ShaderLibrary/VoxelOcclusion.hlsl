#ifndef LOTEC_VOXEL_OCCLUSION_INCLUDED
#define LOTEC_VOXEL_OCCLUSION_INCLUDED

// ENGINE-AGNOSTIC, like every header in this folder: HLSL intrinsics and our own headers only. No
// URP includes, no vertex/fragment semantics, no Core.hlsl texture macros. That means any of these
// can be included from a fragment shader, a compute shader or the voxelize raster alike.
//
// The engine boundary is the .shader / .compute ENTRY POINTS. VoxelLit.shader includes URP's
// Core.hlsl and Lighting.hlsl and calls GetMainLight(), then hands this library plain values.
// Guarded by Shaders/Compute/BufferGiCommonCanary.compute, which includes every header here and fails
// moment one acquires an engine dependency - do not "fix" that by adding an include to the canary.


// Baked directional occlusion shadow sources, read by the buffer-GI per-field shadow modes
// (BgiSampleSunShadow):
//   GetBitmaskShadow   -> per-voxel directional bitmask (one point fetch, hard voxel edges)
//   GetOccFieldShadow  -> per-direction occlusion field (hardware trilinear, soft/sharp edges)
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
Texture3D<float4> _BitmaskTex;

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

// Single fetch of the occlusion bit in the voxel containing `localPos`.
// Returns 0.0 (shadow) or 1.0 (lit) - hard-edged at voxel granularity, by design.
//
// This deliberately does NOT interpolate. A bit has no sub-voxel information to interpolate: an
// 8-tap blend of eight 0/1 samples only dithers the same staircase over a voxel's width, and it
// costs eight dependent Loads on a pass that is already occupancy-bound. Softening the edge is the
// occlusion FIELD's job - it stores a signed distance and reconstructs a boundary from one hardware
// tap. The bitmask exists to be cheap, so it takes one Load and accepts the hard edge.
inline half GetShadowBitPoint(float3 localPos, uint chosenIndex) {
    int3 voxelIdx = int3(floor(localPos));
    return saturate(1.0h - (half)GetBit64(GetBitmaskAtVoxel(voxelIdx), chosenIndex));
}

// Shadow query using the precomputed sun Fibonacci index. Returns 0.0 (shadow) to 1.0 (lit).
half GetBitmaskShadow(float3 worldPos) {
    float3 localPos = (worldPos - _VoxelVolumeBoundsMin) * _VoxelSizeInverse;
    return GetShadowBitPoint(localPos, (uint)_BitmaskSunFibIndex);
}

// Variant with normal-based offset to reduce self-occlusion.
half GetBitmaskShadow(float3 worldPos, float3 normal) {
    float3 offsetPos = worldPos + normal * _VoxelSize * 1.2;
    return GetBitmaskShadow(offsetPos);
}

// -----------------------------------------------------------------------------
// OCCLUSION FIELD (per-direction lit value, hardware trilinear)
// -----------------------------------------------------------------------------

// Channel selectors for the two baked directions nearest the sun, as one-hot masks so the pick is a
// single dot product instead of a compare chain.
float4 _OccFieldSunMask;
float4 _OccFieldSunMaskB;

// Weight toward the second-nearest direction, 0..0.5. 0 disables the second tap entirely.
float _OccFieldBlend;

// Slope of the decode ramp, published by VoxelOcclusionField.Bind (see DecodeScale there).
float _OccFieldDecode;

// Active occlusion field textures (bound per-frame to the textures holding the two nearest directions).
Texture3D<float4> _OccFieldTex;
SamplerState sampler_OccFieldTex;
Texture3D<float4> _OccFieldTexB;
SamplerState sampler_OccFieldTexB;

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
    half stored = dot(raw, (half4)_OccFieldSunMask);

    // Slide toward the second-nearest baked direction. Snapping to one direction leaves the shadow
    // displaced by the angular gap (~15 deg at 8 directions, which shifts a shadow by ~0.28x the
    // occluder distance and detaches it from the caster's base) and pops when the sun crosses cells.
    //
    // Blending the STORED value before the ramp is what makes this work under the SignedDistance
    // encoding: interpolating two distance fields slides ONE boundary between the two directions.
    // Blending decoded shadow terms instead would cross-fade two shadows and ghost.
    //
    // Uniform branch, so it is either taken by every fragment or none - and when it is not taken the
    // second fetch is genuinely skipped, which is why a single-direction bake pays nothing for this.
    if (_OccFieldBlend > 0.0) {
        half4 rawB = (half4)_OccFieldTexB.SampleLevel(sampler_OccFieldTexB, uvw, 0);
        stored = lerp(stored, dot(rawB, (half4)_OccFieldSunMaskB), (half)_OccFieldBlend);
    }

    return saturate((stored - 0.5h) * (half)_OccFieldDecode + 0.5h);
}

// Variant with normal-based offset to reduce self-occlusion.
half GetOccFieldShadow(float3 worldPos, float3 normal) {
    float3 offsetPos = worldPos + normal * _VoxelSize * 1.2;
    return GetOccFieldShadow(offsetPos);
}

#endif // LOTEC_VOXEL_OCCLUSION_INCLUDED
