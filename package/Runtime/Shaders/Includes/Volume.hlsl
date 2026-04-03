 // Global shader variables used by all fields.
// Use a larger epsilon for raymarching to avoid light leaking through thin walls.
// Because SDF:s have rounded corners, a ray can get very close to the surface without actually hitting it, which causes light to leak through. A larger epsilon means we consider it a hit even if we're not super close, which helps block light in these cases. The downside is that it can cause shadow acne or make shadows look blocky, especially on low-res volumes. A more robust solution would be to detect these near-hit cases and apply a bias based on the surface normal to prevent acne while still blocking light. 
// Value depends on the wall thickness and voxel size.
// Wall thickness 0.2, voxel size 0.16: requires 0.04
// Wall thickness 0.5, voxel size 0.16: requires 0.02
// TODO: Calculate and set this from C# side automatically.
#define MAX_SDF_DIST 30.0
#define EPSILON 0.04

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

float RaymarchSDF(float3 voxelCenter, float3 rayDir, out float3 hitPos) {
    return RayMarchTex3D(_DistanceField, linearClampSampler, voxelCenter, rayDir, _VolumePosition, _VolumeSize, 0, MAX_SDF_DIST, EPSILON, 0.01, 64, 0.5, hitPos);
}
