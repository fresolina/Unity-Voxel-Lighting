#ifndef LOTECSOFTWARE_VOXEL_SDF_AO_INCLUDED
#define LOTECSOFTWARE_VOXEL_SDF_AO_INCLUDED

// Cheap SDF ambient occlusion approximation.
// Returns 0..1 where 0 = fully enclosed, 1 = open ambient visibility.
// Requires the following symbols to be defined by the including file:
//  - Texture3D<float> _SdfTex;
//  - SamplerState sampler_SdfTex;
//  - bool SdfWorldToUVW(float3 worldPos, out float3 uvw)

#if defined(_SDF_AO)

float _SdfAoStep;
float _SdfAoIntensity;

#if defined(SDF_AO_SAMPLES_2)
    #define LOTECSDF_AO_SAMPLES 2
#elif defined(SDF_AO_SAMPLES_6)
    #define LOTECSDF_AO_SAMPLES 6
#else
    #define LOTECSDF_AO_SAMPLES 4
#endif

inline float GetAmbientOcclusionFromSdf(float3 worldPos, float3 normal)
{
    float3 n = normalize(normal);
    const int AO_SAMPLES = LOTECSDF_AO_SAMPLES;
    const float AO_STEP_FALLBACK = 0.08;
    const float AO_INTENSITY_FALLBACK = 1.95;

    float aoStep = (_SdfAoStep > 0.0) ? _SdfAoStep : AO_STEP_FALLBACK;
    float aoIntensity = (_SdfAoIntensity > 0.0) ? _SdfAoIntensity : AO_INTENSITY_FALLBACK;

    float occlusion = 0.0;
    float weight = 1.0;
    float weightSum = 0.0;

    [unroll]
    for (int sampleIndex = 1; sampleIndex <= AO_SAMPLES; sampleIndex++) {
        float h = aoStep * sampleIndex;
        float3 samplePos = worldPos + n * h;
        float3 uvw;
        if (!SdfWorldToUVW(samplePos, uvw)) {
            continue;
        }

        float d = _SdfTex.SampleLevel(sampler_SdfTex, uvw, 0).r;
        occlusion += max(h - d, 0.0) * weight;
        weightSum += weight;
        weight *= 0.7;
    }

    float normalized = (weightSum > 0.0) ? (occlusion / weightSum) : 0.0;
    return saturate(1.0 - normalized * aoIntensity);
}

#endif

#endif
