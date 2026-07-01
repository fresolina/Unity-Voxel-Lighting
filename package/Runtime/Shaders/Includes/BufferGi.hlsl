#ifndef LOTEC_BUFFER_GI_INCLUDED
#define LOTEC_BUFFER_GI_INCLUDED

// Fragment-side read for the buffer GI. Normal-oriented 9-tap per field: pick the face the surface
// looks through (dominant normal axis), sample the 3x3 air layer ONE voxel in FRONT of the surface
// (leak-free by construction - never touches voxels behind), and interpolate smoothly across the
// face with a quadratic B-spline. Samples the FINE field, falls back to the COARSE field outside it
// (blended at the fine edges). No raymarching, no SDF, all cache-resident. The companion solve is
// BufferGi.compute; layout is BufferGiField.hlsl.
#include "BufferGiField.hlsl"

// Bound as globals by BufferGiUpdater. The buffers are concatenated over all fields (fine at offset
// 0, coarse at offset BGI_COUNT); the *fine* field's bounds are the shared _BgiGrid* (above).
StructuredBuffer<uint>  _Material;   // occupancy (rgb != 0 = solid) - rejects solid probes
StructuredBuffer<uint2> _Irradiance; // accumulated incoming light (rgb) + sample count (w)

// Coarse field bounds (the big box for far-off GI).
float3 _BgiCoarseOrigin;
float3 _BgiCoarseVoxelSize;
// Blend band as a fraction of the fine box, over which fine cross-fades to coarse at its edges.
float _BgiBlendBand;

// Cold-start confidence ramp (0..1) so the noisy single-ray warm-up frames stay hidden.
float _BgiConfidence;
// Indirect gain on the GI contribution = the sun's Light.bounceIntensity (Unity's Indirect
// Multiplier). Scales only the bounce, leaving direct lighting and emission untouched.
float _BgiIntensity;

// One field's 9-tap front-face read. `origin`/`voxelSize` are that field's grid; `baseOffset` is its
// slice in the concatenated buffers (0 = fine, BGI_COUNT = coarse). Returns raw irradiance (no gain).
float3 BgiSampleField(float3 worldPos, float3 normal, float3 origin, float3 voxelSize, uint baseOffset)
{
    // Dominant normal axis (+sign) + the two in-plane axes. Scale-invariant, so no normalize needed.
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
    float3 g = BgiWorldToGridAt(worldPos, origin, voxelSize);
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

    float3 acc = 0.0;
    float wsum = 0.0;
    [unroll]
    for (int du = -1; du <= 1; du++) {
        [unroll]
        for (int dv = -1; dv <= 1; dv++) {
            int3 vi = nCell * absAxis + (cU + du) * uDir + (cV + dv) * vDir;
            if (!BgiInBounds(vi)) continue;
            uint idx = baseOffset + BgiIndex((uint3)vi);
            if (BgiIsSolid(_Material[idx])) continue;

            float w = wU[du + 1] * wV[dv + 1];
            float3 col; float n;
            BgiUnpackRgb(_Irradiance[idx], col, n);
            acc += col * w;
            wsum += w;
        }
    }

    return (wsum > 1e-4) ? acc / wsum : 0.0;
}

float3 SampleBufferGI(float3 worldPos, float3 normal)
{
    // Fine field - the high-detail volume.
    float3 fine = BgiSampleField(worldPos, normal, _BgiGridOrigin, _BgiVoxelSize, BGI_FINE_OFFSET);

    // Cross-fade weight: 1 well inside the fine box, ramping to 0 across a band at its faces (and 0
    // outside). Keeps the fine/coarse handover seamless instead of a hard edge.
    float3 fuv = (worldPos - _BgiGridOrigin) / max(_BgiGridSize, 1e-6);
    float3 edge = min(fuv, 1.0 - fuv);                 // distance to nearest face (<0 = outside)
    float fineW = saturate(min(edge.x, min(edge.y, edge.z)) / max(_BgiBlendBand, 1e-4));

    float3 result;
    if (fineW >= 0.999) {
        result = fine;
    } else {
        // Coarse field - the big far-GI volume.
        float3 coarse = BgiSampleField(worldPos, normal, _BgiCoarseOrigin, _BgiCoarseVoxelSize, BGI_COARSE_OFFSET);
        result = lerp(coarse, fine, fineW);
    }

    // Final safety net: guarantee finite, non-negative GI so the additive term can never darken a
    // surface (a NaN/negative here renders black even over directly-lit pixels). (<1e8 catches Inf+NaN.)
    result = (result < 1e8) ? max(result, 0.0) : 0.0;
    return result * (_BgiIntensity * _BgiConfidence);
}

#endif // LOTEC_BUFFER_GI_INCLUDED
