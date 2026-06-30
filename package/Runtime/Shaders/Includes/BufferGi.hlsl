#ifndef LOTEC_BUFFER_GI_INCLUDED
#define LOTEC_BUFFER_GI_INCLUDED

// Fragment-side read for the buffer GI. Normal-oriented 9-tap: pick the face the surface looks
// through (dominant normal axis), sample the 3x3 air layer ONE voxel in FRONT of the surface (so
// it's leak-free by construction - never touches voxels behind), and interpolate smoothly across
// the face with a quadratic B-spline. No raymarching, no SDF, all cache-resident. The companion
// solve is BufferGi.compute; layout is BufferGiField.hlsl.
#include "BufferGiField.hlsl"

// Bound as globals by BufferGiUpdater (Shader.SetGlobalBuffer / SetGlobalVector / SetGlobalFloat).
StructuredBuffer<uint>  _Material;   // occupancy (rgb != 0 = solid) - rejects solid probes
StructuredBuffer<uint2> _Irradiance; // accumulated incoming light (rgb) + sample count (w)

// Cold-start confidence ramp (0..1) so the noisy single-ray warm-up frames stay hidden.
float _BgiConfidence;
// Indirect gain on the GI contribution = the sun's Light.bounceIntensity (Unity's Indirect
// Multiplier). Scales only the bounce, leaving direct lighting and emission untouched.
float _BgiIntensity;

float3 SampleBufferGI(float3 worldPos, float3 normal)
{
    // Pick the face the surface looks through (dominant normal axis), then build the two in-plane
    // axes. Sampling only the air layer ONE voxel in front of the surface makes the read leak-free
    // by construction - it never touches voxels behind. (No normalize needed: axis pick + sign are
    // scale-invariant.)
    float3 aN = abs(normal);
    int3 axisDir, uDir, vDir;
    if (aN.x >= aN.y && aN.x >= aN.z) {
        axisDir = int3(normal.x >= 0 ? 1 : -1, 0, 0); uDir = int3(0, 1, 0); vDir = int3(0, 0, 1);
    } else if (aN.y >= aN.z) {
        axisDir = int3(0, normal.y >= 0 ? 1 : -1, 0); uDir = int3(1, 0, 0); vDir = int3(0, 0, 1);
    } else {
        axisDir = int3(0, 0, normal.z >= 0 ? 1 : -1); uDir = int3(1, 0, 0); vDir = int3(0, 1, 0);
    }

    int3 absAxis = abs(axisDir);
    int sgn = axisDir.x + axisDir.y + axisDir.z; // the single non-zero component (+/-1)

    // Decompose the continuous grid position along the oriented axes. Voxel centers sit at
    // integer+0.5, so subtract 0.5 to put centers on integers for interpolation.
    float3 g = BgiWorldToGrid(worldPos);
    float gN = dot(g, (float3)absAxis);
    float gU = dot(g, (float3)uDir) - 0.5;
    float gV = dot(g, (float3)vDir) - 0.5;

    int nCell = (int)floor(gN) + sgn;           // air layer one voxel in front
    int cU = (int)round(gU); float fU = gU - cU; // nearest in-plane center + fractional offset
    int cV = (int)round(gV); float fV = gV - cV;

    // Quadratic B-spline weights: smooth (C1), position-based, sum to 1, all 3 taps non-zero so the
    // result interpolates continuously with the pixel position instead of stepping per voxel.
    float wU[3] = { 0.5 * (0.5 - fU) * (0.5 - fU), 0.75 - fU * fU, 0.5 * (0.5 + fU) * (0.5 + fU) };
    float wV[3] = { 0.5 * (0.5 - fV) * (0.5 - fV), 0.75 - fV * fV, 0.5 * (0.5 + fV) * (0.5 + fV) };

    // 9-tap B-spline over the 3x3 air layer in front. Solid taps (e.g. an adjoining wall at a
    // corner) are skipped (irradiance lives only in air); wsum renormalizes for the skipped ones.
    float3 acc = 0.0;
    float wsum = 0.0;
    [unroll]
    for (int du = -1; du <= 1; du++) {
        [unroll]
        for (int dv = -1; dv <= 1; dv++) {
            int3 vi = nCell * absAxis + (cU + du) * uDir + (cV + dv) * vDir;
            if (!BgiInBounds(vi)) continue;
            uint idx = BgiIndex((uint3)vi);
            if (BgiIsSolid(_Material[idx])) continue;

            float w = wU[du + 1] * wV[dv + 1];
            float3 col; float n;
            BgiUnpackRgb(_Irradiance[idx], col, n);
            acc += col * w;
            wsum += w;
        }
    }

    float3 result = (wsum > 1e-4) ? acc / wsum : 0.0;
    // Final safety net: guarantee finite, non-negative GI so the additive term can never darken a
    // surface (a NaN/negative here renders black even over directly-lit pixels). (<1e8 catches Inf+NaN.)
    result = (result < 1e8) ? max(result, 0.0) : 0.0;
    return result * (_BgiIntensity * _BgiConfidence);
}

#endif // LOTEC_BUFFER_GI_INCLUDED
