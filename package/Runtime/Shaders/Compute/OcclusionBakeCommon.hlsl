#ifndef LOTEC_OCCLUSION_BAKE_COMMON_INCLUDED
#define LOTEC_OCCLUSION_BAKE_COMMON_INCLUDED

// Shared preamble for occlusion bake compute shaders (bitmask and field).

#include "../Includes/Math.hlsl"
#include "../Includes/Raymarch.hlsl"

// SDF volume
Texture3D<float> _SdfTex;
SamplerState sampler_SdfTex;

// Bounds
float3 _BoundsMin;
float3 _BoundsSize;
uint3 _Resolution;

// SDF raymarching parameters
int _RaymarchMaxSteps;
float _RaymarchMinStep;
float _RaymarchEpsilon;

// Directions uploaded from C# (float3 per direction)
StructuredBuffer<float3> _Directions;

// Compute voxel center, origin bias, and max ray distance from thread ID.
inline void ComputeVoxelParams(
    uint3 id,
    out float3 voxelCenter,
    out float originBias,
    out float maxDistance
) {
    float3 voxelSize = _BoundsSize / float3(_Resolution);
    voxelCenter = _BoundsMin + (float3(id) + 0.5) * voxelSize;
    float voxelDiag = length(voxelSize);
    originBias = max(_RaymarchMinStep * 0.5, voxelDiag * 0.01);
    maxDistance = length(_BoundsSize);
}

#endif
