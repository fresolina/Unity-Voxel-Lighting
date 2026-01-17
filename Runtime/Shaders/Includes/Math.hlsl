
#ifndef LOTECSOFTWARE_MATH_INCLUDED
#define LOTECSOFTWARE_MATH_INCLUDED

static const float LOTECSOFTWARE_PI = 3.14159265f;
static const float LOTECSOFTWARE_GOLDEN_RATIO_CONJUGATE = 0.618033988f;
static const uint LOTECSOFTWARE_FIBONACCI_DIR_COUNT = 64u;

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
    float invPhi = LOTECSOFTWARE_GOLDEN_RATIO_CONJUGATE;
    float z = 1.0 - 2.0 * (i + 0.5) / (float)N;
    float r = sqrt(max(0.0, 1.0 - z * z));
    float theta = 2.0 * LOTECSOFTWARE_PI * frac(i * invPhi);
    float x = cos(theta) * r;
    float y = sin(theta) * r;
    return float3(x, y, z);
}

// Convenience for legacy 64-count name
inline float3 FibonacciDirection64(uint index)
{
    return FibonacciDirection(index, LOTECSOFTWARE_FIBONACCI_DIR_COUNT);
}

inline uint NearestFibonacciDirectionIndex64(float3 dir)
{
	float bestDot = -2.0;
	uint bestIndex = 0u;

	[unroll]
	for (uint i = 0u; i < LOTECSOFTWARE_FIBONACCI_DIR_COUNT; i++)
	{
		float3 d = FibonacciDirection(i, LOTECSOFTWARE_FIBONACCI_DIR_COUNT);
		float dd = dot(dir, d);
		if (dd > bestDot)
		{
			bestDot = dd;
			bestIndex = i;
		}
	}

	return bestIndex;
}

// Ray-Triangle intersection (Möller-Trumbore)
// Returns true if hit with positive distance; dist is the ray parameter.
inline bool RayTriangleIntersect(float3 rayOrigin, float3 rayDir, float3 v0, float3 v1, float3 v2, out float dist)
{
	dist = 1e6;

	float3 e0 = v1 - v0;
	float3 e1 = v2 - v0;
	float3 h = cross(rayDir, e1);

	float a = dot(e0, h);
	if (abs(a) < 1e-8)
		return false; // Ray parallel to triangle

	// Accept both backfaces and frontfaces by using the reciprocal of a
	float f = 1.0 / a;
	float3 s = rayOrigin - v0;
	float u = f * dot(s, h);

	if (u < 0.0 || u > 1.0)
		return false;

	float3 q = cross(s, e0);
	float v = f * dot(rayDir, q);

	if (v < 0.0 || u + v > 1.0)
		return false;

	dist = f * dot(e1, q);
	// Accept hits slightly above zero; origin offset is applied by caller to avoid self-intersection.
	return dist > 1e-5;
}

// Wrapper that explicitly exposes non-culling behavior (accepts both sides).
// Kept separate so we can switch to a culling variant if desired.
inline bool RayTriangleIntersect_NoCull(float3 rayOrigin, float3 rayDir, float3 v0, float3 v1, float3 v2, out float dist)
{
	return RayTriangleIntersect(rayOrigin, rayDir, v0, v1, v2, dist);
}

#endif
