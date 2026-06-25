// VoxelGI.hlsl

#include "VoxelGiField.hlsl"

// Bind these globals from C# script (Shader.SetGlobalTexture, etc.)
Texture3D<float4> _IrradianceFieldFinal;
SamplerState sampler_IrradianceFieldFinal; // Unity binds this automatically if named matches texture

// Confidence in the accumulated GI, 0..1, published by GiFieldUpdater. Ramps from 0 on a cold
// start (single-ray, firefly-prone frames) to 1 once enough samples have accumulated. Fading the
// applied indirect light by this hides the noisy warm-up frames: the displayed indirect noise is
// confidence * bufferNoise, and because confidence is lowest exactly when the buffer is noisiest
// (frame 1 = one ray), that product only ever ramps up to the converged level - never spikes.
float _GiConfidence;

// _RadianceFieldVoxelSize, _SdfLowres, and SampleSDF come from VoxelGiField.hlsl above.

// Main Sampling Function
float3 SampleVoxelGI(float3 worldPos, float3 normal)
{
    // -- Read-side voxel sampling offset --
    //
    // Trilinear sampling of the irradiance field at a wall surface will blend
    // the (lit) air voxel and the (dark / inside-geometry) boundary voxel.
    // For an axis-aligned wall the surface sits exactly on a voxel boundary,
    // so the blend weight on the dark voxel is up to 50%. Without an offset
    // the wall reads as half-bright at best, fully black at worst.
    //
    // Pushing the sample point along the surface normal by 0.5 * voxelSize is
    // the principled minimum for this grid. VoxelVolume expands the baked
    // bounds so the SDF / GI voxels stay cubic, so the scalar voxel edge length
    // is enough here. Using the diagonal would overshoot and pull light from the
    // next cell, increasing bleed.
    //
    // We do this on the read side rather than the bake side because the
    // fragment shader has access to the true geometric/normal-mapped surface
    // normal, which is much more reliable than the SDF gradient near corners
    // (the SDF is rounded by construction, so its normal is unstable there).
    float voxel = _RadianceFieldVoxelSize.x;
    float3 unitNormal = normalize(normal);
    float desiredOffset = 0.5 * voxel;

    // Clamp the offset by the SDF distance at the proposed point. This handles
    // two failure modes that a fixed offset cannot:
    //   * Thin walls (thickness <= 1 voxel): if 0.5 * voxel pushes through to
    //     the *other* surface of the wall, sdfAtOffset becomes small/negative
    //     and we shrink the offset, preventing through-wall light bleed.
    //   * Concave (inside) corners: the rounded SDF makes the corner voxel
    //     dimmer than its neighbors. Without clamping we would be tempted to
    //     reduce the offset everywhere; with clamping the corner naturally
    //     pulls the sample back toward worldPos, accepting the corner voxel's
    //     own (modest) value instead of mis-sampling a brighter neighbor.
    // The small floor keeps a minimum escape from the dark boundary voxel
    // even when the SDF reports near-zero distance.
    float3 probePos = worldPos + normal * desiredOffset;
    float sdfAtProbe = SampleSDF(probePos);
    float offset = max(0.05 * voxel, min(desiredOffset, sdfAtProbe));
    float3 samplePos = worldPos + unitNormal * offset;

    // Normalize position to [0,1] for texture sampling. Assumes the volume is axis-aligned and starts at _VoxelVolumeBoundsMin.
    float3 uvw = WorldToVoxelUV(samplePos);

    if (any(uvw < 0) || any(uvw > 1))
    {
        return float3(0, 0, 0);
    }

    // The GPU automatically unpacks the R11G11B10 HDR format to floats here.
    // We use .rgb because this format has no Alpha channel.
    // Fade in by accumulation confidence so the noisy cold-start frames stay hidden.
    return _IrradianceFieldFinal.Sample(sampler_IrradianceFieldFinal, uvw).rgb * _GiConfidence;
}
