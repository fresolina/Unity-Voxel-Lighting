#ifndef LOTEC_VOLUME_INCLUDED
#define LOTEC_VOLUME_INCLUDED

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

#endif // LOTEC_VOLUME_INCLUDED
