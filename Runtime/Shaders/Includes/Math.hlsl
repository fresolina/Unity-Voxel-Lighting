
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
static const float3 LOTECSOFTWARE_FIBONACCI_DIRS_64[64] = {
	float3(0.000000000f, 0.000000000f, 1.000000000f),
	float3(-0.182896377f, -0.167548061f, 0.968750000f),
	float3(0.030422868f, 0.346652851f, 0.937500000f),
	float3(0.257212756f, -0.335488503f, 0.906250000f),
	float3(-0.476722365f, 0.084325483f, 0.875000000f),
	float3(0.452874166f, 0.288081459f, 0.843750000f),
	float3(-0.151339251f, -0.562974405f, 0.812500000f),
	float3(-0.287706563f, 0.553961525f, 0.781250000f),
	float3(0.621302629f, -0.226898750f, 0.750000000f),
	float3(-0.642668460f, -0.265284166f, 0.718750000f),
	float3(0.307790371f, 0.657730064f, 0.687500000f),
	float3(0.225822666f, -0.719958375f, 0.656250000f),
	float3(-0.675405262f, 0.391411206f, 0.625000000f),
	float3(0.785881756f, 0.172773849f, 0.593750000f),
	float3(-0.475515495f, -0.676371765f, 0.562500000f),
	float3(-0.108876138f, 0.840190112f, 0.531250000f),
	float3(0.662205413f, -0.558107508f, 0.500000000f),
	float3(-0.882576563f, -0.036497241f, 0.468750000f),
	float3(0.637392513f, 0.634290576f, 0.437500000f),
	float3(-0.042208068f, -0.912786622f, 0.406250000f),
	float3(-0.593953207f, 0.711754584f, 0.375000000f),
	float3(0.930674820f, -0.125221074f, 0.343750000f),
	float3(-0.779747969f, -0.542528207f, 0.312500000f),
	float3(0.210622003f, 0.936235446f, 0.281250000f),
	float3(0.481393217f, -0.840095572f, 0.250000000f),
	float3(-0.929619416f, 0.296574069f, 0.218750000f),
	float3(0.891691148f, 0.411983795f, 0.187500000f),
	float3(-0.381319418f, -0.911142930f, 0.156250000f),
	float3(-0.335797588f, 0.933603224f, 0.125000000f),
	float3(0.881290796f, -0.463181897f, 0.093750000f),
	float3(-0.965079580f, -0.254391734f, 0.062500000f),
	float3(0.540573716f, 0.840716061f, 0.031250000f),
	float3(0.169376024f, -0.985551502f, 0.000000000f),
	float3(-0.790236939f, 0.612004099f, -0.031250000f),
	float3(0.994637327f, 0.082403509f, -0.062500000f),
	float3(-0.676088645f, -0.730831773f, -0.093750000f),
	float3(0.004840184f, 0.992144935f, -0.125000000f),
	float3(0.663632636f, -0.731558379f, -0.156250000f),
	float3(-0.978072970f, 0.090647760f, -0.187500000f),
	float3(0.777267627f, 0.589918192f, -0.218750000f),
	float3(-0.173300289f, -0.952610629f, -0.250000000f),
	float3(-0.511106564f, 0.812199802f, -0.281250000f),
	float3(0.916135938f, -0.251075074f, -0.312500000f),
	float3(-0.835469973f, -0.428749182f, -0.343750000f),
	float3(0.322249596f, 0.869212401f, -0.375000000f),
	float3(0.344527059f, -0.846322659f, -0.406250000f),
	float3(-0.812583900f, 0.385098890f, -0.437500000f),
	float3(0.844122311f, 0.260251726f, -0.468750000f),
	float3(-0.437881973f, -0.747167570f, -0.500000000f),
	float3(-0.177874636f, 0.828332090f, -0.531250000f),
	float3(0.674043939f, -0.478809480f, -0.562500000f),
	float3(-0.798472492f, -0.099511894f, -0.593750000f),
	float3(0.505977287f, 0.594442583f, -0.625000000f),
	float3(0.027497810f, -0.754042312f, -0.656250000f),
	float3(-0.509718618f, 0.517233680f, -0.687500000f),
	float3(0.694361541f, -0.035503353f, -0.718750000f),
	float3(-0.509902605f, -0.421306698f, -0.750000000f),
	float3(0.086254715f, 0.618230185f, -0.781250000f),
	float3(0.330609216f, -0.480147161f, -0.812500000f),
	float3(-0.523068024f, 0.120356883f, -0.843750000f),
	float3(0.421217013f, 0.238644564f, -0.875000000f),
	float3(-0.130449412f, -0.402111786f, -0.906250000f),
	float3(-0.144409996f, 0.316606227f, -0.937500000f),
	float3(0.228339619f, -0.096873401f, -0.968750000f)
};

inline float3 FibonacciDirection64_Table(uint index)
{
	return LOTECSOFTWARE_FIBONACCI_DIRS_64[index];
}

// Generate Fibonacci sphere direction (64 directions).
// index: 0..63
inline float3 FibonacciDirection64(uint index)
{
	// Use the precomputed table to avoid trig in hot paths.
	return FibonacciDirection64_Table(index);
}

// Back-compat alias.
inline float3 FibonacciDirection(uint index)
{
	return FibonacciDirection64(index);
}

inline uint NearestFibonacciDirectionIndex64(float3 dir)
{
	float bestDot = -2.0;
	uint bestIndex = 0u;

	[unroll]
	for (uint i = 0u; i < LOTECSOFTWARE_FIBONACCI_DIR_COUNT; i++)
	{
		float d = dot(dir, LOTECSOFTWARE_FIBONACCI_DIRS_64[i]);
		if (d > bestDot)
		{
			bestDot = d;
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
	if (abs(a) < 1e-6)
		return false; // Ray parallel to triangle

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
	return dist > 0.001; // Skip very close hits (self)
}

#endif

