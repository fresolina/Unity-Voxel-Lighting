#ifndef LOTEC_BUFFER_GI_INCLUDED
#define LOTEC_BUFFER_GI_INCLUDED

// Fragment-side read for the buffer GI. Normal-oriented 9-tap per field: pick the face the surface
// looks through (dominant normal axis), sample the 3x3 air layer ONE voxel in FRONT of the surface
// (leak-free by construction - never touches voxels behind), and interpolate smoothly across the
// face with a quadratic B-spline. Samples the FINE field, falls back to the COARSE field outside it
// (blended at the fine edges). No raymarching, no SDF, all cache-resident. The companion solve is
// BufferGi.compute; layout is BufferGiField.hlsl.

// Everything here is scoped to the GI_VOXEL_BUFFER variant. VoxelLit includes this header
// unconditionally but only calls into it under GI_VOXEL_BUFFER, so the other variants
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
StructuredBuffer<uint2> _Radiance;   // per-solid-voxel outgoing radiance (rgb) + baked sun visibility (w)

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
// Voxel sun-shadow mode PER FIELD: 0 = off, 1 = baked (interpolated pre-marched visibility from
// _Radiance.w), 2 = realtime (per-pixel occupancy DDA toward the sun, like the SDF shadow). The
// fragment picks fine vs coarse by whether the shading point is inside the fine volume.
int _BgiShadowModeFine;
int _BgiShadowModeCoarse;
// Two independent fast-path variants (multi_compile in VoxelLit, driven by BufferGiUpdater) so each
// code path's cost/register pressure can be measured in isolation:
//   BGI_FAST_PATH : GI gather 9-tap B-spline -> 1 nearest tap (BgiGatherIndirect).
//   BGI_FACE_1TAP : AO + sun-shadow face read 4-tap bilinear -> 1 nearest tap (BgiSampleFaceAoShadow).
// Real variants (not uniform branches) so the fast path does not compile the heavy loop - honest
// occupancy. Both on = the old bundled "fast GI" (~2 reads/pixel).

// Which field (fine vs coarse) a shading point falls in, plus that field's grid + buffer slice.
// Shared by the GI gather and the AO/sun-shadow face read so they all agree on the same voxels.
void BgiSelectField(float3 worldPos, out bool insideFine, out float3 origin, out float3 voxelSize, out uint baseOffset)
{
    float3 fuv = (worldPos - _BgiGridOrigin) / max(_BgiGridSize, 1e-6);
    insideFine = all(fuv >= 0.0) && all(fuv <= 1.0);
    origin     = insideFine ? _BgiGridOrigin    : _BgiCoarseOrigin;
    voxelSize  = insideFine ? _BgiVoxelSize      : _BgiCoarseVoxelSize;
    baseOffset = insideFine ? BGI_FINE_OFFSET    : BGI_COARSE_OFFSET;
}

// One face-plane read that yields BOTH the baked static AO (openness, _Surface bits 16-23) and the
// baked sun visibility (_Radiance.w) for a surface point. These used to run as two independent 4-tap
// loops (BgiSurfaceAO + BgiTrySunShadow) over the IDENTICAL 4 face voxels, each re-reading _Occupancy;
// this shares the occupancy test, the in-plane index math and the weight renormalisation, and only
// touches the payload buffer(s) actually needed (skips _Surface when AO is off, _Radiance when the
// sun-shadow mode is Off). Non-solid / out-of-bounds taps are SKIPPED and the weights RENORMALISED
// over the solid taps, so a face edge takes the value of the surface voxels actually present.
//   ao          : AO multiplier for the GI term (1 = no AO), already faded by _BgiAoStrength.
//   shadow      : interpolated sun visibility (meaningful only when shadowValid is true).
//   shadowValid : false when this field's sun-shadow mode is Off -> caller falls back to its
//                 GetShadow source (SDF / bitmask / occlusion) for the main light.
void BgiSampleFaceAoShadow(float3 worldPos, float3 normal, out half ao, out half shadow, out bool shadowValid)
{
    ao = 1.0h;
    shadow = 1.0h;

    bool insideFine; float3 origin, voxelSize; uint baseOffset;
    BgiSelectField(worldPos, insideFine, origin, voxelSize, baseOffset);
    int mode = insideFine ? _BgiShadowModeFine : _BgiShadowModeCoarse;
    shadowValid = (mode != 0);

#if defined(BGI_FACE_1TAP)
    // Face read collapsed to ONE nearest surface voxel (vs the 4-tap bilinear blend): reads the same
    // openness (AO) + radiance.w (sun vis) from the single cell the shading point sits in. AO still
    // honours _BgiAoStrength, shadow still honours the mode - only the tap COUNT drops.
    bool wantAo = _BgiAoStrength > 0.0;
    if (wantAo || shadowValid) {
        int3 c = (int3)floor(BgiWorldToGridAt(worldPos, origin, voxelSize));
        if (BgiInBounds(c)) {
            uint slot = baseOffset + BgiIndex((uint3)c);
            if (BgiSolidBit(slot)) {
                if (wantAo)     ao     = lerp(1.0h, (half)BgiSurfaceOpenness(_Surface[slot]), (half)_BgiAoStrength);
                if (shadowValid) { float3 rgb; float w; BgiUnpackRgb(_Radiance[slot], rgb, w); shadow = saturate((half)w); }
            }
        }
    }
    return;
#else
    bool wantAo = _BgiAoStrength > 0.0;
    bool wantShadow = shadowValid;
    if (!wantAo && !wantShadow) return; // nothing to gather

    // Face plane: the dominant normal axis is fixed; u,v are the two in-plane axes we interpolate over.
    float3 aN = abs(normal);
    int3 uDir, vDir;
    if (aN.x >= aN.y && aN.x >= aN.z)  { uDir = int3(0, 1, 0); vDir = int3(0, 0, 1); }
    else if (aN.y >= aN.z)             { uDir = int3(1, 0, 0); vDir = int3(0, 0, 1); }
    else                               { uDir = int3(1, 0, 0); vDir = int3(0, 1, 0); }
    int3 absAxis = int3(1, 1, 1) - abs(uDir) - abs(vDir); // the remaining (normal) axis

    // The solid surface voxel is the cell CONTAINING the shading point (same layer the GI read treats
    // as solid). Continuous in-plane position; -0.5 puts voxel centres on integers so the fraction is
    // the bilinear weight between a voxel and its neighbour.
    float3 g = BgiWorldToGridAt(worldPos, origin, voxelSize);
    int nN = (int)floor(dot(g, (float3)absAxis));
    float u = dot(g, (float3)uDir) - 0.5; int u0 = (int)floor(u); half fu = (half)(u - u0);
    float v = dot(g, (float3)vDir) - 0.5; int v0 = (int)floor(v); half fv = (half)(v - v0);

    half opennessAcc = 0.0h;
    half shadowAcc   = 0.0h;
    half wsum        = 0.0h;
    [unroll]
    for (int du = 0; du <= 1; du++) {
        [unroll]
        for (int dv = 0; dv <= 1; dv++) {
            int3 c = nN * absAxis + (u0 + du) * uDir + (v0 + dv) * vDir;
            if (!BgiInBounds(c)) continue;
            uint slot = baseOffset + BgiIndex((uint3)c);
            if (!BgiSolidBit(slot)) continue; // skip air taps: don't let "off-surface" pull the result
            half wgt = (du == 0 ? 1.0h - fu : fu) * (dv == 0 ? 1.0h - fv : fv);
            if (wantAo)
                opennessAcc += (half)BgiSurfaceOpenness(_Surface[slot]) * wgt;
            if (wantShadow) {
                float3 rgb; float w; BgiUnpackRgb(_Radiance[slot], rgb, w);
                shadowAcc += (half)w * wgt;
            }
            wsum += wgt;
        }
    }

    // wsum == 0 (no solid tap): leave ao/shadow at 1.0 - the previous per-source "outside" fallback.
    if (wsum > (half)1e-3) {
        if (wantAo)     ao     = lerp(1.0h, opennessAcc / wsum, (half)_BgiAoStrength);
        if (wantShadow) shadow = saturate(shadowAcc / wsum);
    }
#endif // BGI_FACE_1TAP
}

// One field's 9-tap front-face read. `origin`/`voxelSize` are that field's grid; `baseOffset` is its
// slice in the concatenated buffers (0 = fine, BGI_COUNT = coarse). Returns raw irradiance (no gain).
float3 BgiSampleField(float3 worldPos, float3 normal, float3 origin, float3 voxelSize, uint baseOffset)
{
    // Dominant normal axis (+sign) + the two in-plane axes, expressed directly as buffer-index
    // STRIDES: BgiIndex is x | y<<L | z<<2L, so one voxel along an axis is a constant add. That lets
    // the tap loop step a flat index instead of rebuilding an int3 coordinate + shift/or per tap.
    // Scale-invariant, so no normalize needed.
    const int SX = 1, SY = 1 << BGI_GRID_LOG2, SZ = 1 << (BGI_GRID_LOG2 * 2u);
    float3 g = BgiWorldToGridAt(worldPos, origin, voxelSize);
    float3 aN = abs(normal);
    float gN, gU, gV;
    int strideU, strideV, sgn;
    int nStride; // stride along the normal axis
    if (aN.x >= aN.y && aN.x >= aN.z) {
        sgn = normal.x >= 0 ? 1 : -1;
        gN = g.x; gU = g.y; gV = g.z; nStride = SX; strideU = SY; strideV = SZ;
    } else if (aN.y >= aN.z) {
        sgn = normal.y >= 0 ? 1 : -1;
        gN = g.y; gU = g.x; gV = g.z; nStride = SY; strideU = SX; strideV = SZ;
    } else {
        sgn = normal.z >= 0 ? 1 : -1;
        gN = g.z; gU = g.x; gV = g.y; nStride = SZ; strideU = SX; strideV = SY;
    }

    // Air layer one voxel in front. The normal-axis bound holds for all 9 taps, so test it once up
    // front (empty-sum result) instead of inside every tap.
    int nCell = (int)floor(gN) + sgn;
    if (nCell < 0 || nCell >= (int)BGI_GRID) return 0.0;

    // Voxel centers sit at integer+0.5, so subtract 0.5 to put centers on integers for interpolation.
    gU -= 0.5; gV -= 0.5;
    int cU = (int)round(gU); half fU = (half)(gU - cU); // nearest in-plane center + fractional offset
    int cV = (int)round(gV); half fV = (half)(gV - cV);

    // Quadratic B-spline weights (fp16): smooth (C1), position-based, sum to 1, all 3 taps non-zero so
    // the result interpolates continuously with the pixel position instead of stepping per voxel.
    half wU[3] = { 0.5h * (0.5h - fU) * (0.5h - fU), 0.75h - fU * fU, 0.5h * (0.5h + fU) * (0.5h + fU) };
    half wV[3] = { 0.5h * (0.5h - fV) * (0.5h - fV), 0.75h - fV * fV, 0.5h * (0.5h + fV) * (0.5h + fV) };

    // Signed int math: with cU/cV == -1 or GRID-1 an intermediate index can leave the field slice,
    // but such taps fail the per-axis bound below and are never used, so it can't misread.
    int baseIdx = (int)baseOffset + nCell * nStride + cU * strideU + cV * strideV;

    // fp16 accumulation: the stored irradiance is f16-packed (BgiPackRgb), so it fits half by
    // construction - and the gather is low-frequency, so half carries it without visible banding.
    half3 acc = 0.0h;
    half wsum = 0.0h;
    [unroll]
    for (int du = -1; du <= 1; du++) {
        int u = cU + du;
        if (u < 0 || u >= (int)BGI_GRID) continue; // row off-grid: skip all 3 taps
        half wu = wU[du + 1];
        int rowIdx = baseIdx + du * strideU;
        [unroll]
        for (int dv = -1; dv <= 1; dv++) {
            int v = cV + dv;
            if (v < 0 || v >= (int)BGI_GRID) continue;
            uint idx = (uint)(rowIdx + dv * strideV);
            if (BgiSolidBit(idx)) continue;

            half w = wu * wV[dv + 1];
            float3 col; float n;
            BgiUnpackRgb(_Irradiance[idx], col, n);
            acc += (half3)col * w;
            wsum += w;
        }
    }

    return (wsum > (half)1e-3) ? (float3)(acc / wsum) : 0.0;
}

// Fast path: single NEAREST tap of the air voxel one step in front of the surface - no B-spline blend,
// no neighbour renormalise (~1 buffer read vs the 9-tap gather). The perf floor for the GI gather.
float3 BgiSampleFieldNearest(float3 worldPos, float3 normal, float3 origin, float3 voxelSize, uint baseOffset)
{
    int3 cell = (int3)floor(BgiWorldToGridAt(worldPos, origin, voxelSize));
    float3 aN = abs(normal);
    int3 stepDir = int3(0, 0, 0);
    if (aN.x >= aN.y && aN.x >= aN.z)      stepDir.x = normal.x >= 0 ? 1 : -1;
    else if (aN.y >= aN.z)                 stepDir.y = normal.y >= 0 ? 1 : -1;
    else                                   stepDir.z = normal.z >= 0 ? 1 : -1;
    int3 c = cell + stepDir; // the air voxel one step in front of the surface
    if (!BgiInBounds(c)) return 0.0;
    uint idx = baseOffset + BgiIndex((uint3)c);
    if (BgiSolidBit(idx)) return 0.0;
    float3 col; float n; BgiUnpackRgb(_Irradiance[idx], col, n);
    return col;
}

// Raw buffer-GI irradiance at a surface point (NO AO - the caller multiplies in the merged AO from
// BgiSampleFaceAoShadow). Fine field inside its box, coarse outside; scaled by _BgiIntensity.
float3 BgiGatherIndirect(float3 worldPos, float3 normal)
{
    bool insideFine; float3 origin, voxelSize; uint baseOff;
    BgiSelectField(worldPos, insideFine, origin, voxelSize, baseOff);

#if defined(BGI_FAST_PATH)
    float3 result = BgiSampleFieldNearest(worldPos, normal, origin, voxelSize, baseOff);
#else
    float3 result = BgiSampleField(worldPos, normal, origin, voxelSize, baseOff);
#endif

    // Final safety net: guarantee finite, non-negative GI so the additive term can never darken a
    // surface (a NaN/negative here renders black even over directly-lit pixels). (<1e8 catches Inf+NaN.)
    result = (result < 1e8) ? max(result, 0.0) : 0.0;
    return result * _BgiIntensity;
}

#endif // GI_VOXEL_BUFFER

#endif // LOTEC_BUFFER_GI_INCLUDED
