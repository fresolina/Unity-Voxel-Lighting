 // Global shader variables used by all fields.
//
// EPSILON: hit threshold for SDF raymarching (RayMarchTex3D treats d <= EPSILON
// as a surface hit and returns full occlusion).
//
// Why we need a non-trivial epsilon at all:
//   The discrete SDF is reconstructed by trilinear filtering, which rounds off
//   sharp features. A ray that grazes a thin wall can stay above d == 0 for
//   the entire traversal even though geometrically it should have hit. With
//   epsilon = 0 we would get pinhole light leaks through walls that are only
//   one or two voxels thick. A positive epsilon converts those near-misses
//   into hits and seals the wall.
//
// Trade-offs:
//   * Too small  -> light leaks through thin walls / acute corners.
//   * Too large  -> shadow acne; rays self-occlude when started near a surface
//                   unless their startOffset clears the epsilon shell around
//                   the originating voxel.
//   * Effectively a per-step bias, so it is independent of step size; the
//                   raymarcher's softness term scales with d/t, not with d - eps.
//
// Where the value matters:
//   * Direct light visibility from CSComputeRadiance (sun/point/spot).
//   * Path-traced bounce visibility in CSComputeIrradiancePathTracing.
//   * Anything else that calls RaymarchSDF in this volume.
// The fragment-side shadow shader (VoxelSdfShadows.hlsl) uses its own
// _SdfShadowEpsilon supplied from C# so it can be tuned independently.
//
// Choosing a value:
//   The minimum safe epsilon is roughly the largest amount the trilinearly
//   reconstructed SDF can deviate from the true distance near a thin feature.
//   That depends on wall thickness and voxel size:
//     Wall thickness 0.2, voxel size 0.16 -> 0.04
//     Wall thickness 0.5, voxel size 0.16 -> 0.02
//   When raising EPSILON, raise the startOffset in the radiance/irradiance
//   passes by the same amount so near-surface voxels are not self-occluded.
//
// TODO: Calculate and set this from C# side automatically based on the
//       smallest wall thickness in the scene and the current voxel size.
#define EPSILON 0.04
#define RAYMARCH_MIN_STEP 0.01
#define RAYMARCH_MAX_STEPS 64
#define RAYMARCH_SOFTNESS 0.5

#include "Raymarch.hlsl"

float3 _VolumePosition;
float3 _VolumeSize;
float3 _VoxelSize;
Texture3D<float> _DistanceField;
SamplerState linearClampSampler;
SamplerState pointClampSampler;

// Normalise to local [0,1] for texture sampling. Assumes the volume is axis-aligned and starts at _VolumePosition.
// TODO: Maybe support non-axis-aligned to world volumes with _VolumeRotation or _VolumeMatrix?
float3 WorldToVoxelUV(float3 worldPos)
{
    return (worldPos - _VolumePosition) / _VolumeSize;
}

// Helpers for raymarching the SDF field.
float SampleSDF(float3 pos) {
    float3 uvw = WorldToVoxelUV(pos);
    return _DistanceField.SampleLevel(linearClampSampler, uvw, 0).r;
}

float3 GetNormalFromSDF(float3 pos) {
    float2 k = float2(1, -1);
    float3 h = _VoxelSize * 0.1;
    return normalize(
        k.xyy * SampleSDF(pos + k.xyy * h) +
        k.yyx * SampleSDF(pos + k.yyx * h) +
        k.yxy * SampleSDF(pos + k.yxy * h) +
        k.xxx * SampleSDF(pos + k.xxx * h)
    );
}

// RayMarchTex3D treats d <= EPSILON as a hit. Starting exactly EPSILON away is
// therefore still ambiguous because equality self-hits. The extra min-step
// clearance guarantees the first sampled point is just outside the epsilon shell
// in the common case where the ray is moving away from the surface.
float ComputeRaymarchStartOffset(float dist)
{
    return max(0.0, (EPSILON + RAYMARCH_MIN_STEP) - dist);
}

float RaymarchSDF(float3 voxelCenter, float3 rayDir, float maxDistance, out float3 hitPos) {
    return RayMarchTex3D(_DistanceField, linearClampSampler, voxelCenter, rayDir, _VolumePosition, _VolumeSize, 0, maxDistance, EPSILON, RAYMARCH_MIN_STEP, RAYMARCH_MAX_STEPS, RAYMARCH_SOFTNESS, hitPos);
}

// Overload with explicit startOffset (bias to avoid self-occlusion at near-surface voxels).
float RaymarchSDF(float3 voxelCenter, float3 rayDir, float startOffset, float maxDistance, out float3 hitPos) {
    return RayMarchTex3D(_DistanceField, linearClampSampler, voxelCenter, rayDir, _VolumePosition, _VolumeSize, startOffset, maxDistance, EPSILON, RAYMARCH_MIN_STEP, RAYMARCH_MAX_STEPS, RAYMARCH_SOFTNESS, hitPos);
}
