
#ifndef LOTEC_MATH_INCLUDED
#define LOTEC_MATH_INCLUDED

#define EMISSION_INTENSITY_MAX 1024.0

static const float LOTEC_MATH_PI = 3.14159265f;
static const float LOTEC_MATH_INV_PI = 0.318309886f;
static const uint BLUE_NOISE_SIZE = 128;

float Random(float3 position, float seed) {
    return frac(sin(dot(position + seed, float3(12.9898, 78.233, 45.164))) * 43758.5453);
}

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

// Octahedral Packing (Vector3 -> Vector2)
float2 PackDirection(float3 n) {
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    if (n.z < 0) {
        n.xy = (1.0 - abs(n.yx)) * (n.xy >= 0 ? 1.0 : -1.0);
    }
    return n.xy * 0.5 + 0.5;
}

float3 UnpackDirection(float2 p) {
    p = p * 2.0 - 1.0;
    float3 n = float3(p.x, p.y, 1.0 - abs(p.x) - abs(p.y));
    float t = max(-n.z, 0.0);
    n.x += n.x >= 0 ? -t : t;
    n.y += n.y >= 0 ? -t : t;
    return normalize(n);
}

// Simple 32-bit integer hash (Wang/Jenkins-style) to produce decorrelated seeds
inline uint WangHash(uint v) {
    v = (v ^ 61u) ^ (v >> 16);
    v *= 9u;
    v = v ^ (v >> 4);
    v *= 0x27d4eb2du;
    v = v ^ (v >> 15);
    return v;
}

inline float HashTo01(uint v) {
    // convert to float in [0,1)
    return (float)WangHash(v) * (1.0 / 4294967296.0);
}

float3 GetRandomDirection(float3 seedPos, uint frame) {
    float r1 = Random(seedPos, frame);
    float r2 = Random(seedPos, frame + 1.34);
    float r3 = Random(seedPos, frame + 2.51);
    return normalize(float3(r1 - 0.5, r2 - 0.5, r3 - 0.5));
}

// Map 1D noise to Sphere Direction
// (Golden Angle Sampling for better distribution than random)
float3 GetRandomDirectionFromNoise(float noiseVal) {
    float z = 1.0 - 2.0 * noiseVal;
    float r = sqrt(max(0.0, 1.0 - z * z));
    float phi = 2.0 * LOTEC_MATH_PI * frac(noiseVal * 1.61803); // Golden ratio
    return float3(r * cos(phi), r * sin(phi), z);
}

float3 GetRandomDirectionFromNoise2(float2 noiseVal) {
    float z = 1.0 - 2.0 * noiseVal.x;
    float r = sqrt(max(0.0, 1.0 - z * z));
    float phi = 2.0 * LOTEC_MATH_PI * noiseVal.y; // Golden ratio
    return float3(r * cos(phi), r * sin(phi), z);
}

float2 GetBlueNoise2(Texture2D<float> tex, uint3 id, uint frame, uint salt) {
    // Spatial blue-noise value, decorrelated per ray (salt) but NOT per frame: the
    // integer tile offset is taken mod BLUE_NOISE_SIZE, which would repeat exactly
    // every BLUE_NOISE_SIZE frames and cap the effective sample count.
    uint2 baseCoord = uint2(id.x, id.y) + uint2(salt * 101u, salt * 59u);
    uint2 coord0 = baseCoord & (BLUE_NOISE_SIZE - 1);
    uint2 coord1 = (baseCoord + uint2(37u, 61u)) & (BLUE_NOISE_SIZE - 1);
    float2 blueNoise = float2(tex.Load(int3(coord0, 0)).r, tex.Load(int3(coord1, 0)).r);

    // Animate over frames with a Cranley-Patterson rotation by the R2 low-discrepancy
    // sequence (Roberts' generalized golden ratio). The irrational increments never
    // repeat, so the cumulative temporal average keeps converging toward zero noise
    // instead of plateauing - and each frame stays a well-distributed sample.
    float2 r2 = frac(float(frame) * float2(0.7548776662, 0.5698402909));
    return frac(blueNoise + r2);
}

float2 GetNoise2(uint3 id, uint frame, uint salt) {
    uint seed = id.x * 73856093u ^ id.y * 19349663u ^ id.z * 83492791u;
    seed += salt * 1013904223u + frame * 747796405u;
    float n0 = HashTo01(seed);
    float n1 = HashTo01(seed ^ 0x9e3779b9u);
    return float2(n0, n1);
}

void BuildOrthonormalBasis(float3 n, out float3 t, out float3 b) {
    float3 up = (abs(n.z) < 0.999f) ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0);
    t = normalize(cross(up, n));
    b = cross(n, t);
}

float3 CosineSampleHemisphere(float3 normal, float2 u) {
    float r = sqrt(u.x);
    float theta = 2.0 * LOTEC_MATH_PI * u.y;
    float2 d = r * float2(cos(theta), sin(theta));
    float z = sqrt(max(0.0, 1.0 - u.x));

    float3 t, b;
    BuildOrthonormalBasis(normal, t, b);
    return normalize(t * d.x + b * d.y + normal * z);
}

/// Compute luminance of a color via Rec. 709 luminance coefficients.
float GetRec709Luminance(float3 color) {
    return dot(color, float3(0.2126, 0.7152, 0.0722));
}

/// fp16 flavour, for colours that already live in half registers (the fp16-packed GI fields).
/// Deliberately a DIFFERENT NAME rather than an overload: on backends where `half` lowers to
/// `float` (FXC / D3D), two functions differing only in half3 vs float3 are a redefinition.
half GetRec709LuminanceH(half3 color) {
    return dot(color, half3(0.2126h, 0.7152h, 0.0722h));
}

float DecodeEmissionIntensityFrom8Bit(float encodedIntensity) {
    float t = saturate(encodedIntensity);
    return exp2(t * log2(1.0 + EMISSION_INTENSITY_MAX)) - 1.0;
}

// Generates a value 0.0 -> 1.0 that is spatially balanced
float GetIGN(float3 position, int frame) {
    float3 p = position + float(frame) * 5.588238f;
    return frac(52.9829189f * frac(0.06711056f * p.x + 0.00583715f * p.y + 0.0123456f * p.z));
}

struct AabbHit {
    bool hit;
    float tEnter;
    float tExit;
};

// Intersect ray (ro + rd * t) with unit AABB [0,1]^3.
// ro/rd are in UVW space; t is still in world-distance units (because rd already includes invSize).
inline AabbHit RayIntersectUnitAabb(float3 ro, float3 rd)
{
    float3 invRd = rcp(rd);
    float3 t0 = (0.0 - ro) * invRd;
    float3 t1 = (1.0 - ro) * invRd;

    float3 tMin3 = min(t0, t1);
    float3 tMax3 = max(t0, t1);

    AabbHit result = (AabbHit)0;
    result.tEnter = max(max(tMin3.x, tMin3.y), tMin3.z);
    result.tExit  = min(min(tMax3.x, tMax3.y), tMax3.z);
    result.hit = result.tExit >= result.tEnter;
    return result;
}

// Ray-Triangle intersection (Möller-Trumbore)
// Returns true if hit with positive distance; dist is the ray parameter.
inline bool RayTriangleIntersect(float3 rayOrigin, float3 rayDir, float3 v0, float3 v1, float3 v2, out float dist)
{
	// Single exit on purpose: with early returns, Unity's Vulkan compiler reports a false
	// "potentially uninitialized variable" for `dist` wherever the result feeds a short-circuiting
	// && at the call site (as the occlusion field bake's cell scan does).
	float3 e0 = v1 - v0;
	float3 e1 = v2 - v0;
	float3 h = cross(rayDir, e1);
	float a = dot(e0, h);

	bool hit = abs(a) >= 1e-8; // otherwise the ray is parallel to the triangle

	// Accept both backfaces and frontfaces by using the reciprocal of a.
	float f = hit ? 1.0 / a : 0.0;
	float3 s = rayOrigin - v0;
	float u = f * dot(s, h);
	hit = hit && u >= 0.0 && u <= 1.0;

	float3 q = cross(s, e0);
	float v = f * dot(rayDir, q);
	hit = hit && v >= 0.0 && u + v <= 1.0;

	// Accept hits slightly above zero; origin offset is applied by caller to avoid self-intersection.
	float t = f * dot(e1, q);
	hit = hit && t > 1e-5;

	dist = hit ? t : 1e6;
	return hit;
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

/**
 * Computes triangle UV at the closest point on a triangle.
 *
 * Expected usage:
 * 1) Compute world-space closest point `cp` with `ClosestPointOnTriangle`.
 * 2) Call this function with triangle vertices and matching per-vertex UVs.
 *
 * The function derives barycentric weights of `cp` in triangle (v0,v1,v2),
 * then interpolates (uv0,uv1,uv2) with those weights.
 *
 * Degenerate triangle handling:
 * - If the barycentric denominator is near zero, the function falls back to
 *   `u=1, v=0, w=0`, effectively returning `uv0`.
 */
inline float2 ComputeClosestPointUV(float3 cp, float3 v0, float3 v1, float3 v2, float2 uv0, float2 uv1, float2 uv2) {
    float3 v0v1 = v1 - v0;
    float3 v0v2 = v2 - v0;
    float3 vp = cp - v0;
    float d00 = dot(v0v1, v0v1);
    float d01 = dot(v0v1, v0v2);
    float d11 = dot(v0v2, v0v2);
    float d20 = dot(vp, v0v1);
    float d21 = dot(vp, v0v2);
    float denom = d00 * d11 - d01 * d01;

    float v = 0.0;
    float w = 0.0;
    if (abs(denom) > 1e-8) {
        v = (d11 * d20 - d01 * d21) / denom;
        w = (d00 * d21 - d01 * d20) / denom;
    }
    float u = 1.0 - v - w;

    return uv0 * u + uv1 * v + uv2 * w;
}

#endif
