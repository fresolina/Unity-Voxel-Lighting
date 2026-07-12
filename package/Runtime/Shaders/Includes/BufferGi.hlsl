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

// Bilinearly interpolate a per-SOLID-voxel scalar across the surface FACE PLANE (the two axes
// perpendicular to the dominant normal). Reading only one voxel and fading to 0 at its edge leaves
// gaps -> per-voxel discs; blending the 4 in-plane neighbours interpolates continuously instead.
// `source` picks the payload: 0 = baked openness/AO (_Surface bits 16-23), 1 = baked sun visibility
// (_Radiance w). Non-solid / out-of-bounds taps are SKIPPED and the weights RENORMALISED over the
// solid taps - so a face edge takes the value of the surface voxels actually present instead of being
// pulled toward a fixed default (which brightened shadowed edges / darkened lit ones as the 2x2 block
// ran off the surface). `outside` is only the fallback when no tap is solid. Shared by AO + sun-shadow.
float BgiSampleFaceScalar(float3 worldPos, float3 normal, float3 origin, float3 voxelSize,
                          uint baseOffset, uint source, float outside)
{
    // Face plane: the dominant normal axis is fixed; u,v are the two in-plane axes we interpolate over.
    float3 aN = abs(normal);
    int3 uDir, vDir;
    if (aN.x >= aN.y && aN.x >= aN.z)  { uDir = int3(0, 1, 0); vDir = int3(0, 0, 1); }
    else if (aN.y >= aN.z)             { uDir = int3(1, 0, 0); vDir = int3(0, 0, 1); }
    else                               { uDir = int3(1, 0, 0); vDir = int3(0, 1, 0); }
    int3 absAxis = int3(1, 1, 1) - abs(uDir) - abs(vDir); // the remaining (normal) axis

    // The solid surface voxel is simply the cell CONTAINING the shading point: voxelization marks the
    // voxel a mesh point falls in as solid, and the surface point sits somewhere inside it (this is the
    // same layer the GI read treats as solid, stepping +sgn off it for the air in front). Do NOT push
    // back along the normal - a fixed half-voxel push only lands in the solid cell when the surface
    // happens to sit in the voxel's front half, which is grid-size dependent (worked for the big coarse
    // voxels, missed for the small fine ones - reading the air voxel behind and so losing the shadow).
    float3 g = BgiWorldToGridAt(worldPos, origin, voxelSize);
    int nN = (int)floor(dot(g, (float3)absAxis));         // solid cell index along the normal axis

    // Continuous in-plane position; -0.5 puts voxel centres on integers so the fraction is the
    // bilinear weight between a voxel and its neighbour (the tent reaches the neighbour's centre).
    float u = dot(g, (float3)uDir) - 0.5; int u0 = (int)floor(u); float fu = u - u0;
    float v = dot(g, (float3)vDir) - 0.5; int v0 = (int)floor(v); float fv = v - v0;

    float acc = 0.0;
    float wsum = 0.0;
    [unroll]
    for (int du = 0; du <= 1; du++) {
        [unroll]
        for (int dv = 0; dv <= 1; dv++) {
            int3 c = nN * absAxis + (u0 + du) * uDir + (v0 + dv) * vDir;
            if (!BgiInBounds(c)) continue;
            uint slot = baseOffset + BgiIndex((uint3)c);
            if (!BgiSolidBit(slot)) continue; // skip air taps: don't let "off-surface" pull the result
            float val;
            if (source == 0u) {
                val = BgiSurfaceOpenness(_Surface[slot]);
            } else {
                float3 rgb; float w; BgiUnpackRgb(_Radiance[slot], rgb, w); val = w;
            }
            float wgt = (du == 0 ? 1.0 - fu : fu) * (dv == 0 ? 1.0 - fv : fv);
            acc += val * wgt;
            wsum += wgt;
        }
    }
    return (wsum > 1e-5) ? acc / wsum : outside; // fallback only when no tap was solid
}

// Baked static AO at a surface point: interpolated openness, faded by _BgiAoStrength. 1 (no AO) off.
float BgiSurfaceAO(float3 worldPos, float3 normal, float3 origin, float3 voxelSize, uint baseOffset)
{
    if (_BgiAoStrength <= 0.0) return 1.0;
    float openness = BgiSampleFaceScalar(worldPos, normal, origin, voxelSize, baseOffset, 0u, 1.0);
    return lerp(1.0, openness, _BgiAoStrength);
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
    int cU = (int)round(gU); float fU = gU - cU; // nearest in-plane center + fractional offset
    int cV = (int)round(gV); float fV = gV - cV;

    // Quadratic B-spline weights: smooth (C1), position-based, sum to 1, all 3 taps non-zero so the
    // result interpolates continuously with the pixel position instead of stepping per voxel.
    float wU[3] = { 0.5 * (0.5 - fU) * (0.5 - fU), 0.75 - fU * fU, 0.5 * (0.5 + fU) * (0.5 + fU) };
    float wV[3] = { 0.5 * (0.5 - fV) * (0.5 - fV), 0.75 - fV * fV, 0.5 * (0.5 + fV) * (0.5 + fV) };

    // Signed int math: with cU/cV == -1 or GRID-1 an intermediate index can leave the field slice,
    // but such taps fail the per-axis bound below and are never used, so it can't misread.
    int baseIdx = (int)baseOffset + nCell * nStride + cU * strideU + cV * strideV;

    float3 acc = 0.0;
    float wsum = 0.0;
    [unroll]
    for (int du = -1; du <= 1; du++) {
        int u = cU + du;
        if (u < 0 || u >= (int)BGI_GRID) continue; // row off-grid: skip all 3 taps
        float wu = wU[du + 1];
        int rowIdx = baseIdx + du * strideU;
        [unroll]
        for (int dv = -1; dv <= 1; dv++) {
            int v = cV + dv;
            if (v < 0 || v >= (int)BGI_GRID) continue;
            uint idx = (uint)(rowIdx + dv * strideV);
            if (BgiSolidBit(idx)) continue;

            float w = wu * wV[dv + 1];
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

// Baked voxel sun-shadow SOURCE for the main directional light, selected PER FIELD (fine vs coarse).
// Dispatched from GetShadow (VoxelDirectLighting) as an alternative shadow source, so it REPLACES the
// SDF/bitmask/occlusion shadow rather than stacking with it. Returns false when the field's mode is
// Off (the caller then falls back to its selected source); otherwise writes `shadow` (the solve's
// pre-marched visibility from _Radiance.w, interpolated across the surface face) and returns true.
bool BgiTrySunShadow(float3 worldPos, float3 normal, out half shadow)
{
    shadow = 1.0;
    float3 fuv = (worldPos - _BgiGridOrigin) / max(_BgiGridSize, 1e-6);
    bool insideFine = all(fuv >= 0.0) && all(fuv <= 1.0);
    int mode = insideFine ? _BgiShadowModeFine : _BgiShadowModeCoarse;
    if (mode == 0) return false; // Off -> let GetShadow use its selected SDF/bitmask/occ source

    float3 origin    = insideFine ? _BgiGridOrigin : _BgiCoarseOrigin;
    float3 voxelSize = insideFine ? _BgiVoxelSize   : _BgiCoarseVoxelSize;
    uint   baseOff   = insideFine ? BGI_FINE_OFFSET : BGI_COARSE_OFFSET;

    shadow = saturate(BgiSampleFaceScalar(worldPos, normal, origin, voxelSize, baseOff, 1u, 1.0));
    return true;
}

#endif // GI_VOXEL_BUFFER

#endif // LOTEC_BUFFER_GI_INCLUDED
