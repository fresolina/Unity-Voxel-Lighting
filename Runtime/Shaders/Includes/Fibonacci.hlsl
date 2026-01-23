#ifndef LOTEC_MATH_FIBONACCI_INCLUDED
#define LOTEC_MATH_FIBONACCI_INCLUDED

// PI constant and PackOctahedral
#include "Math.hlsl"

static const float LOTEC_MATH_GOLDEN_RATIO_CONJUGATE = 0.618033988f;

// Precomputed Fibonacci directions (set from C#)
// Usage: Map an index 0..63 to a direction vector.
float4 _FibonacciDirections[64];

// Precomputed 64-direction Fibonacci sphere set.
// Matches:
//   phi = acos(1 - 2*i/N)
//   theta = 2*pi*(i*0.618033988)
//   dir = (sin(phi)*cos(theta), sin(phi)*sin(theta), cos(phi))
// Generate Fibonacci sphere direction for given index and count.
// Uses centered sampling (i + 0.5) consistent with the baker.
inline float3 FibonacciDirection(uint index, uint N)
{
    float i = (float)index;
    float invPhi = LOTEC_MATH_GOLDEN_RATIO_CONJUGATE;
    float z = 1.0 - 2.0 * (i + 0.5) / (float)N;
    float r = sqrt(max(0.0, 1.0 - z * z));
    float theta = 2.0 * LOTEC_MATH_PI * frac(i * invPhi);
    float x = cos(theta) * r;
    float y = sin(theta) * r;
    return float3(x, y, z);
}

// Convenience for legacy 64-count name
inline float3 FibonacciDirection64(uint index)
{
    return FibonacciDirection(index, 64);
}

inline uint NearestFibonacciDirectionIndex64(float3 dir)
{
	float bestDot = -2.0;
	uint bestIndex = 0u;

	[unroll]
	for (uint i = 0u; i < 64; i++)
	{
		float3 d = FibonacciDirection(i, 64);
		float dd = dot(dir, d);
		if (dd > bestDot)
		{
			bestDot = dd;
			bestIndex = i;
		}
	}

	return bestIndex;
}

/**
* Decodes 4 Fibonacci direction indices from a texel.
* Each channel is stored as UNorm8 (0..255) representing index/255.
*/
inline uint4 DecodeFibIndicesFromTexel(half4 raw)
{
    // Texture stores indices as UNorm8: index/255.
    // Decode with rounding and clamp to [0,63].
    return (uint4)clamp((int4)round(raw * 255.0), 0, 63);
}

/*
* Fetches the 4 nearest Fibonacci direction indices for a given light direction.
* @param lightDir Normalized light direction
*/
inline uint4 FibonacciIndicesTex(Texture2D tex, SamplerState texSampler, float3 lightDir)
{
    float2 uv = PackOctahedral(lightDir);
    half4 raw = tex.SampleLevel(texSampler, uv, 0);
    return DecodeFibIndicesFromTexel(raw);
}

/*
* Finds the index of the nearest Fibonacci direction to the given direction.
* @param nDir Normalized direction
*/
inline uint FibonacciIndexTex(Texture2D tex, SamplerState texSampler, float3 nDir)
{
    // Map light direction to octahedral UVs
    float2 uv = PackOctahedral(nDir);

    // Get 4 closest Fibonacci direction indices to lightDir
    half4 rawIndices = tex.SampleLevel(texSampler, uv, 0);
    uint4 indices = DecodeFibIndicesFromTexel(rawIndices);

    // 4 dot products: cheapest reliable way to pick the closest.
    float d0 = dot(nDir, _FibonacciDirections[indices.x].xyz);
    float d1 = dot(nDir, _FibonacciDirections[indices.y].xyz);
    float d2 = dot(nDir, _FibonacciDirections[indices.z].xyz);
    float d3 = dot(nDir, _FibonacciDirections[indices.w].xyz);

    uint bestIndex = indices.x;
    float bestDot = d0;

    if (d1 > bestDot) { bestDot = d1; bestIndex = indices.y; }
    if (d2 > bestDot) { bestDot = d2; bestIndex = indices.z; }
    if (d3 > bestDot) { bestDot = d3; bestIndex = indices.w; }

    return bestIndex;
}

#endif
