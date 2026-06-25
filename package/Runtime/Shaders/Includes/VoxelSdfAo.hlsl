#ifndef LOTECSOFTWARE_VOXEL_SDF_AO_INCLUDED
#define LOTECSOFTWARE_VOXEL_SDF_AO_INCLUDED

// Cheap SDF ambient occlusion approximation.
// Returns 0..1 where 0 = fully enclosed, 1 = open ambient visibility.

// _SdfHires / sampler_SdfHires (hi-res SDF) + WorldToVoxelUV (volume mapping).
#include "VoxelSdfShadows.hlsl"

float _SdfAoStep;
float _SdfAoIntensity;

#if defined(SDF_AO_LQ)
    #define LOTECSDF_AO_SAMPLES 2
#define LOTECSDF_AO_ENABLED 1
#elif defined(SDF_AO_HQ)
    #define LOTECSDF_AO_SAMPLES 4
    #define LOTECSDF_AO_ENABLED 1
#else
    #define LOTECSDF_AO_SAMPLES 0
    #define LOTECSDF_AO_ENABLED 0
#endif

inline float GetAmbientOcclusionFromSdf(float3 worldPos, float3 normal)
{
#if LOTECSDF_AO_ENABLED
    float3 n = normalize(normal);
    const int AO_SAMPLES = LOTECSDF_AO_SAMPLES;
    const float AO_STEP_FALLBACK = 0.08;
    const float AO_INTENSITY_FALLBACK = 1.95;

    float aoStep = (_SdfAoStep > 0.0) ? _SdfAoStep : AO_STEP_FALLBACK;
    float aoIntensity = (_SdfAoIntensity > 0.0) ? _SdfAoIntensity : AO_INTENSITY_FALLBACK;

    float occlusion = 0.0;
    float weight = 1.0;

    [unroll]
    for (int sampleIndex = 1; sampleIndex <= AO_SAMPLES; sampleIndex++) {
        float h = aoStep * sampleIndex;
        float3 samplePos = worldPos + n * h;
        float3 uvw = WorldToVoxelUV(samplePos);
        if (!InsideVolumeUVW(uvw)) break;

        float d = _SdfHires.SampleLevel(sampler_SdfHires, uvw, 0).r;
        occlusion += max(h - d, 0.0) * weight;
        weight *= 0.5;
    }

    return saturate(1.0 - (occlusion * aoIntensity));
#else
    return 1.0;
#endif
}

#endif
