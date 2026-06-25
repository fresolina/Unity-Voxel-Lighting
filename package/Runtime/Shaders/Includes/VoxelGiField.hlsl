#ifndef LOTEC_VOXEL_GI_FIELD_INCLUDED
#define LOTEC_VOXEL_GI_FIELD_INCLUDED

// The low-res GI distance field and its sampling helpers, shared by the GI compute solve and
// the VoxelGi fragment read. Universal volume space (bounds + WorldToVoxelUV) is in Volume.hlsl.
#include "Volume.hlsl"

float3 _RadianceFieldVoxelSize; // GI / radiance-field grid voxel size (set by the GI updater).
Texture3D<float> _SdfLowres;    // low-res signed distance field, the GI march target.
SamplerState linearClampSampler;
SamplerState pointClampSampler;

// Sample the low-res SDF at a world position.
float SampleSDF(float3 pos) {
    float3 uvw = WorldToVoxelUV(pos);
    return _SdfLowres.SampleLevel(linearClampSampler, uvw, 0).r;
}

float3 GetNormalFromSDF(float3 pos) {
    float2 k = float2(1, -1);
    float3 h = _RadianceFieldVoxelSize * 0.1;
    return normalize(
        k.xyy * SampleSDF(pos + k.xyy * h) +
        k.yyx * SampleSDF(pos + k.yyx * h) +
        k.yxy * SampleSDF(pos + k.yxy * h) +
        k.xxx * SampleSDF(pos + k.xxx * h)
    );
}

#endif // LOTEC_VOXEL_GI_FIELD_INCLUDED
