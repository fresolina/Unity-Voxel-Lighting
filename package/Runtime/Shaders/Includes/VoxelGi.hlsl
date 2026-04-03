// VoxelGI.hlsl

#include "Volume.hlsl"

// Bind these globals from C# script (Shader.SetGlobalTexture, etc.)
Texture3D<float4> _IrradianceFieldFinal; 
SamplerState sampler_IrradianceFieldFinal; // Unity binds this automatically if named matches texture

float3 _RadianceFieldVoxelSize; // meters per voxel

// Main Sampling Function
float3 SampleVoxelGI(float3 worldPos, float3 normal)
{
    // - Voxel sampling offset -
    // Problem: If you sample exactly at the surface (worldPos), you might 
    // be reading the voxel *inside* the wall, which is dark.
    // Fix: Push the sample point out into the air along the normal.
    // A value of 0.5 to 1.0 times the voxel size usually works best.
    float3 samplePos = worldPos + (normal * _RadianceFieldVoxelSize * 0.1);

    // Normalize position to [0,1] for texture sampling. Assumes the volume is axis-aligned and starts at _VolumePosition.
    float3 uvw = WorldToVoxelUV(samplePos);

    if (any(uvw < 0) || any(uvw > 1))
    {
        return float3(0, 0, 0);
    }

    // The GPU automatically unpacks the R11G11B10 HDR format to floats here.
    // We use .rgb because this format has no Alpha channel.
    return _IrradianceFieldFinal.Sample(sampler_IrradianceFieldFinal, uvw).rgb;
}
