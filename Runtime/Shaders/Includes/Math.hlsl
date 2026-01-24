
#ifndef LOTEC_MATH_INCLUDED
#define LOTEC_MATH_INCLUDED

static const float LOTEC_MATH_PI = 3.14159265f;

/**
 * Extracts a single bit from the 64-bit mask (uint2).
 * Since bitmask is not one 64bit integer, we have to create a 2x32 bit shift helper.
 * float value = (float)((bitmask >> bit) & 1);
 * HIGH-PERFORMANCE VERSION (Use inside loops)
 * Expects the 32-bit bucket to be pre-selected.
 * @param bit    The original 0-63 bit (we only use the lower 5 bits here).
 * @param word32 Either bitmask.x or bitmask.y.
 */
inline uint GetBit32(uint bit, uint word32) {
    // Standard bitwise ops are the safest and most portable.
    // The compiler optimizes this to a single 'BFE' instruction automatically.
    return (word32 >> (bit & 31u)) & 1u;
}
/**
 * Extracts a single bit from the 64-bit value (uint2).
 * Since bitmask is not one 64bit integer, we have to create a 2x32 bit shift helper.
 * float bit = (float)((bitmask >> bit) & 1);
 * CONVENIENCE VERSION (Use for single lookups)
 * Handles bucket selection automatically.
 * @param bitmask  The full uint2 (64-bit) mask.
 * @param bit    The 0-63 bit to extract.
 */
inline uint GetBit64(uint2 bitmask, uint bit) {
    // bit >> 5 results in 0 for (0-31) and 1 for (32-63)
    uint word32 = bitmask[bit >> 5];
    return GetBit32(bit, word32);
}

// Maps a 3D direction to the 2D texture UVs
float2 PackOctahedral(float3 dir) {
    dir /= (abs(dir.x) + abs(dir.y) + abs(dir.z) + 1e-8);
    float2 p = dir.xz;
    if (dir.y < 0.0) {
        p = (1.0 - abs(p.yx)) * (p >= 0.0 ? 1.0 : -1.0);
    }
    return p * 0.5 + 0.5;
}

// Intersect ray (ro + rd * t) with unit AABB [0,1]^3.
// ro/rd are in UVW space; t is still in world-distance units (because rd already includes invSize).
inline bool RayIntersectUnitAabb(float3 ro, float3 rd, out float tEnter, out float tExit)
{
    const float kHuge = 1e20;
    bool3 parallel = abs(rd) < 1e-8;

    // If we're parallel to a slab and outside it, no hit.
    if (parallel.x && (ro.x < 0.0 || ro.x > 1.0)) { tEnter = 0.0; tExit = 0.0; return false; }
    if (parallel.y && (ro.y < 0.0 || ro.y > 1.0)) { tEnter = 0.0; tExit = 0.0; return false; }
    if (parallel.z && (ro.z < 0.0 || ro.z > 1.0)) { tEnter = 0.0; tExit = 0.0; return false; }

    float3 invRd = rcp(rd);
    float3 t0 = (0.0 - ro) * invRd;
    float3 t1 = (1.0 - ro) * invRd;

    // Ignore parallel axes by making their interval unbounded.
    t0 = float3(parallel.x ? -kHuge : t0.x, parallel.y ? -kHuge : t0.y, parallel.z ? -kHuge : t0.z);
    t1 = float3(parallel.x ?  kHuge : t1.x, parallel.y ?  kHuge : t1.y, parallel.z ?  kHuge : t1.z);

    float3 tMin3 = min(t0, t1);
    float3 tMax3 = max(t0, t1);

    tEnter = max(max(tMin3.x, tMin3.y), tMin3.z);
    tExit  = min(min(tMax3.x, tMax3.y), tMax3.z);

    return tExit >= tEnter;
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

// Compute closest point on triangle and squared distance
static float3 ClosestPointOnTriangle(float3 p, float3 a, float3 b, float3 c) {
    float3 ab = b - a;
    float3 ac = c - a;
    float3 ap = p - a;
    float d1 = dot(ab, ap);
    float d2 = dot(ac, ap);
    if (d1 <= 0.0 && d2 <= 0.0) return a;

    float3 bp = p - b;
    float d3 = dot(ab, bp);
    float d4 = dot(ac, bp);
    if (d3 >= 0.0 && d4 <= d3) return b;

    float vc = d1 * d4 - d3 * d2;
    if (vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0) {
        float v = d1 / (d1 - d3);
        return a + v * ab;
    }

    float3 cp = p - c;
    float d5 = dot(ab, cp);
    float d6 = dot(ac, cp);
    if (d6 >= 0.0 && d5 <= d6) return c;

    float vb = d5 * d2 - d1 * d6;
    if (vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0) {
        float w = d2 / (d2 - d6);
        return a + w * ac;
    }

    float va = d3 * d6 - d5 * d4;
    if (va <= 0.0 && (d4 - d3) >= 0.0 && (d5 - d6) >= 0.0) {
        float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
        return b + w * (c - b);
    }

    // Inside face region. Compute barycentric coordinates (u,v,w)
    float denom = 1.0 / (va + vb + vc);
    float v = vb * denom;
    float w = vc * denom;
    return a + ab * v + ac * w;
}

#endif
