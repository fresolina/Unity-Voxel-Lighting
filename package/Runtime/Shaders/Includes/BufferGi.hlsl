#ifndef LOTEC_BUFFER_GI_INCLUDED
#define LOTEC_BUFFER_GI_INCLUDED

// Fragment-side read for the buffer GI. Normal-oriented 9-tap per field: pick the face the surface
// looks through (dominant normal axis), sample the 3x3 air layer ONE voxel in FRONT of the surface
// (leak-free by construction - never touches voxels behind), and interpolate smoothly across the
// face with a quadratic B-spline. Samples the FINE field, falls back to the COARSE field outside it
// (blended at the fine edges). No raymarching, no SDF, all cache-resident. The companion solve is
// BufferGi.compute; layout is BufferGiField.hlsl.

// Everything here is scoped to the GI_VOXEL_BUFFER variant. VoxelLit includes this header
// unconditionally but only calls SampleBufferGI under GI_VOXEL_BUFFER, so the other variants
// (GI_OFF / GI_VOXEL_TEXTURE) must not carry these fragment-stage StructuredBuffers: WebGPU
// validates every declared global against the bound pipeline layout and fails pipeline creation
// for a variant that declares _Occupancy / _Irradiance while they are unbound (null). D3D11/Vulkan
// silently strip/tolerate them, which is why this only bites in a WebGPU (browser) build.
#if defined(GI_VOXEL_BUFFER)

#include "BufferGiField.hlsl"

// Bound as globals by BufferGiUpdater. The buffers are concatenated over all fields (coarse at
// offset 0, fine at offset BGI_COUNT); the *fine* field's bounds are the shared _BgiGrid* (above).
// Occupancy is the 1-bit/voxel bitfield (8 KB total - trivially cache-resident for the 9 taps);
// the material buffer is no longer bound to the lit shader at all.
StructuredBuffer<uint>  _Occupancy;  // 1 bit/voxel solidity - rejects solid probes
StructuredBuffer<uint2> _Irradiance; // accumulated incoming light (rgb) + sample count (w)
StructuredBuffer<uint>  _Surface;    // per-voxel surface word - static openness/AO in bits 16-23

bool BgiSolidBit(uint slot)
{
    return (_Occupancy[slot >> 5] >> (slot & 31u)) & 1u;
}

// Coarse field bounds (the big box for far-off GI).
float3 _BgiCoarseOrigin;
float3 _BgiCoarseVoxelSize;
// Indirect gain on the GI contribution = the sun's Light.bounceIntensity (Unity's Indirect
// Multiplier). Scales only the bounce, leaving direct lighting and emission untouched.
float _BgiIntensity;
// Strength of the baked static AO (0 = off). Darkens the GI in concave/contact regions using the
// surface voxel's precomputed openness - restores the contact shadowing the omni gather reads weakly.
float _BgiAoStrength;

// Baked static AO at a surface point: bilinearly interpolate the baked openness across the surface
// FACE PLANE (the two axes perpendicular to the dominant normal), then fade by _BgiAoStrength. Reading
// only one voxel and fading to 0 at its edge leaves gaps -> per-voxel discs; blending the 4 in-plane
// neighbours interpolates continuously instead. Non-solid / out-of-bounds neighbours count as fully
// open (1), so the AO fades to nothing at surface edges. 1 (no AO) when off.
float BgiSurfaceAO(float3 worldPos, float3 normal, float3 origin, float3 voxelSize, uint baseOffset)
{
    if (_BgiAoStrength <= 0.0) return 1.0;

    // Face plane: the dominant normal axis is fixed; u,v are the two in-plane axes we interpolate over.
    float3 aN = abs(normal);
    int3 uDir, vDir;
    if (aN.x >= aN.y && aN.x >= aN.z)  { uDir = int3(0, 1, 0); vDir = int3(0, 0, 1); }
    else if (aN.y >= aN.z)             { uDir = int3(1, 0, 0); vDir = int3(0, 0, 1); }
    else                               { uDir = int3(1, 0, 0); vDir = int3(0, 1, 0); }
    int3 absAxis = int3(1, 1, 1) - abs(uDir) - abs(vDir); // the remaining (normal) axis

    // The solid surface layer sits half a voxel behind the pixel along the normal.
    float3 g = BgiWorldToGridAt(worldPos - normal * (0.5 * voxelSize), origin, voxelSize);
    int nN = (int)floor(dot(g, (float3)absAxis));         // solid cell index along the normal axis

    // Continuous in-plane position; -0.5 puts voxel centres on integers so the fraction is the
    // bilinear weight between a voxel and its neighbour (the tent reaches the neighbour's centre).
    float u = dot(g, (float3)uDir) - 0.5; int u0 = (int)floor(u); float fu = u - u0;
    float v = dot(g, (float3)vDir) - 0.5; int v0 = (int)floor(v); float fv = v - v0;

    float openness = 0.0;
    [unroll]
    for (int du = 0; du <= 1; du++) {
        [unroll]
        for (int dv = 0; dv <= 1; dv++) {
            int3 c = nN * absAxis + (u0 + du) * uDir + (v0 + dv) * vDir;
            float o = 1.0; // open by default: non-solid / out-of-bounds neighbours add no AO
            if (BgiInBounds(c)) {
                uint slot = baseOffset + BgiIndex((uint3)c);
                if (BgiSolidBit(slot)) o = BgiSurfaceOpenness(_Surface[slot]);
            }
            openness += o * (du == 0 ? 1.0 - fu : fu) * (dv == 0 ? 1.0 - fv : fv);
        }
    }
    return lerp(1.0, openness, _BgiAoStrength);
}

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
            if (BgiSolidBit(idx)) continue;

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
    // Hard switch: if the shading point is inside the active fine volume use the fine field, else the
    // coarse one (no blend band, no cross-fade).
    float3 fuv = (worldPos - _BgiGridOrigin) / max(_BgiGridSize, 1e-6);
    bool insideFine = all(fuv >= 0.0) && all(fuv <= 1.0);

    float3 origin    = insideFine ? _BgiGridOrigin    : _BgiCoarseOrigin;
    float3 voxelSize = insideFine ? _BgiVoxelSize      : _BgiCoarseVoxelSize;
    uint   baseOff   = insideFine ? BGI_FINE_OFFSET    : BGI_COARSE_OFFSET;

    float3 result = BgiSampleField(worldPos, normal, origin, voxelSize, baseOff);
    // Static contact/concave AO from the baked surface openness (no-op when _BgiAoStrength == 0).
    result *= BgiSurfaceAO(worldPos, normal, origin, voxelSize, baseOff);

    // Final safety net: guarantee finite, non-negative GI so the additive term can never darken a
    // surface (a NaN/negative here renders black even over directly-lit pixels). (<1e8 catches Inf+NaN.)
    result = (result < 1e8) ? max(result, 0.0) : 0.0;
    return result * _BgiIntensity;
}

#endif // GI_VOXEL_BUFFER

#endif // LOTEC_BUFFER_GI_INCLUDED
