#ifndef LOTEC_BUFFER_GI_INCLUDED
#define LOTEC_BUFFER_GI_INCLUDED

// Fragment-side read for the buffer GI. Normal-oriented per field: pick the face the surface looks
// through (dominant normal axis) and read the air layer ONE voxel in FRONT of the surface - leak-free
// by construction, never touches voxels behind - interpolating only WITHIN that layer. Both read paths
// now use that same geometry: the SSBO gather as an explicit 9-tap quadratic B-spline over the layer,
// the default texture path as one hardware-trilinear tap snapped to the layer's voxel centres along
// the normal axis. Samples the FINE field, falls back to the COARSE field outside it (blended at the
// fine edges). No raymarching, no SDF, all cache-resident. The companion solve is BufferGi.compute
// (which dilates irradiance into the first solid shell for the texture path); layout is BufferGiField.hlsl.

// Everything here is scoped to the GI_VOXEL_BUFFER variant. VoxelLit includes this header
// unconditionally but only calls into it under GI_VOXEL_BUFFER, so the other variants
// (GI_OFF / GI_UNITY) must not carry these fragment-stage StructuredBuffers: WebGPU
// validates every declared global against the bound pipeline layout and fails pipeline creation
// for a variant that declares _Occupancy / _Irradiance while they are unbound (null). D3D11/Vulkan
// silently strip/tolerate them, which is why this only bites in a WebGPU (browser) build.
#if defined(GI_VOXEL_BUFFER)

#include "BufferGiField.hlsl"
// GetShadowFromSdf, for the per-field Sdf shadow mode. Self-contained like the other shadow-source
// headers; the include guard makes it a no-op after VoxelDirectLighting's earlier include, and it
// declares no NEW global here - _SdfHires is already declared unconditionally in every variant via
// that same header (so this adds nothing to the WebGPU pipeline-layout surface).
#include "VoxelSdfShadows.hlsl"
// GetOccFieldShadow / GetBitmaskShadow, for the per-field OcclusionField / Bitmask shadow modes. Same
// deal: guarded (no-op after VoxelDirectLighting's earlier unconditional include) and declares no NEW
// global - _OccFieldTex / _BitmaskTex are already declared unconditionally in every variant via it.
#include "VoxelOcclusion.hlsl"

// Bound as globals by BufferGiUpdater. The buffers are concatenated over all fields (coarse at
// offset 0, fine at offset BGI_COUNT); the *fine* field's bounds are the shared _BgiGrid* (above).
// Occupancy is the 1-bit/voxel bitfield (grid^3/8 bytes per field); the material buffer is no
// longer bound to the lit shader at all.
StructuredBuffer<uint>  _Occupancy;  // 1 bit/voxel solidity - rejects solid probes
StructuredBuffer<uint2> _Irradiance; // accumulated incoming light (rgb) + sample count (w)
StructuredBuffer<uint>  _Surface;    // per-voxel surface word - static openness/AO in bits 16-23
StructuredBuffer<uint2> _Radiance;   // per-solid-voxel outgoing radiance (rgb) + baked sun visibility (w)

// Each field's blurred irradiance, written straight into a Texture3D by CSBlur (fused - no separate
// copy pass). The DEFAULT read path taps these with ONE hardware-trilinear fetch instead of the 9-tap
// SSBO B-spline (the Adreno win). Bound whenever BufferGI is active (BufferGiUpdater), so they are
// never unbound WebGPU globals. One per field (fine + coarse).
Texture3D<float4> _BgiIrradianceTex;             // fine field
SamplerState sampler_BgiIrradianceTex;
Texture3D<float4> _BgiIrradianceTexCoarse;       // coarse field
SamplerState sampler_BgiIrradianceTexCoarse;

bool BgiSolidBit(uint slot)
{
    return (_Occupancy[slot >> 5] >> (slot & 31u)) & 1u;
}

// Uniforms below stay `float`: they are loose globals (not a CBUFFER block), set from C# with
// SetGlobalFloat/SetGlobalVector, and a uniform load costs the same either way - the fp16 win is in
// the ALU, so they are narrowed at the point of use instead.
// Coarse field bounds (the big box for far-off GI).
float3 _BgiCoarseOrigin;
float3 _BgiCoarseVoxelSize;
// Indirect gain on the GI contribution = the sun's Light.bounceIntensity (Unity's Indirect
// Multiplier). Scales only the bounce, leaving direct lighting and emission untouched.
float _BgiIntensity;
// Strength of the baked static AO (0 = off). Darkens the GI in concave/contact regions using the
// surface voxel's precomputed openness - restores the contact shadowing the omni gather reads weakly.
float _BgiAoStrength;
// Sun-shadow mode PER FIELD: 0 = off (caller falls back to its GetShadow source), 1 = baked
// (interpolated pre-marched visibility from _Radiance.w), 2 = SDF (crisp per-pixel raymarch of the
// hi-res SDF, VoxelGI-parity, independent of the shadow-source keyword). The fragment picks fine vs
// coarse by whether the shading point is inside the fine volume.
int _BgiShadowModeFine;
int _BgiShadowModeCoarse;
// Baked shadow edge sharpening (see BgiSampleShadowTexture). 1 = passthrough / original behaviour.
float _BgiShadowSharpness;
// How far off the surface the baked shadow tap sits, in voxels. 0.5 = first air voxel's centre,
// 1.0 = the historical value.
float _BgiShadowNormalOffset;

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

// Baked sun visibility from the displayed field's mirror texture ALPHA (the air-layer sun-vis CSBlur
// wrote): ONE hardware trilinear tap, replacing the 4-8 StructuredBuffer face taps the SSBO Baked read
// used. Offset ~1 voxel along the normal into the air layer in front (same as the GI texture read), so
// the tap sits in the air voxels that actually carry sun-vis rather than the solid cell. Fine field
// inside its box, coarse outside (its grid size = coarse voxel size * grid - only the voxel size is
// published). This keeps the tunable continuous offset rather than the GI tap's snap-to-layer: alpha
// is a coverage signal the sharpening below reconstructs an edge from, and snapping would quantise it.
// Returns 1 (lit) outside the sampled field, matching the "no info -> lit" face-read fallback.
half BgiSampleShadowTexture(float3 worldPos, float3 normal, bool insideFine)
{
    float3 origin   = insideFine ? _BgiGridOrigin   : _BgiCoarseOrigin;
    float3 vox      = insideFine ? _BgiVoxelSize     : _BgiCoarseVoxelSize;
    float3 gridSize = insideFine ? _BgiGridSize      : _BgiCoarseVoxelSize * (float)BGI_GRID;
    // Offset off the surface into the air layer that carries sun-vis. The shading point sits ON the
    // solid/air voxel boundary, so 0.5 voxel lands exactly at the FIRST air voxel's centre; the
    // historical 1.0 lands on the boundary between the first and second air layers, so the trilinear
    // tap blends in air a whole voxel further out. That over-reach is why a floor next to a tall
    // occluder reads shadowed - the tap is sampling air inside the occluder's shadow rather than the
    // air just above the floor - and at a grazing sun it doubles the along-surface displacement.
    float3 uvw = (worldPos + normal * vox * _BgiShadowNormalOffset - origin) / max(gridSize, 1e-6);
    if (any(uvw < 0.0) || any(uvw > 1.0)) return 1.0h; // outside the field -> lit (no baked info)
    half a = insideFine
        ? (half)_BgiIrradianceTex.SampleLevel(sampler_BgiIrradianceTex, uvw, 0).a
        : (half)_BgiIrradianceTexCoarse.SampleLevel(sampler_BgiIrradianceTexCoarse, uvw, 0).a;

    // Sharpen the reconstructed edge. Near a shadow boundary the stored value is the voxel's sun
    // COVERAGE, and coverage is a local signed distance to that boundary - so re-centring on 0.5 and
    // steepening rebuilds an edge finer than the voxel it came from. Same SDF-font trick as
    // GetOccFieldShadow's _OccFieldDecode, and the reason it works here at all is that the solve now
    // stores a fraction: against the old 0/1 field this would only harden the staircase.
    // 1 = passthrough (identical to before). Costs one MAD.
    return saturate((a - 0.5h) * (half)_BgiShadowSharpness + 0.5h);
}

// One face-plane read that yields BOTH the baked static AO (openness, _Surface bits 16-23) and the
// baked sun visibility (_Radiance.w) for a surface point. These used to run as two independent 4-tap
// loops (BgiSurfaceAO + BgiTrySunShadow) over the IDENTICAL 4 face voxels, each re-reading _Occupancy;
// this shares the occupancy test, the in-plane index math and the weight renormalisation, and only
// touches the payload buffer(s) actually needed (skips _Surface when AO is off, _Radiance when the
// sun-shadow mode is Off). Non-solid / out-of-bounds taps are SKIPPED and the weights RENORMALISED
// over the solid taps, so a face edge takes the value of the surface voxels actually present.
//   ao          : AO multiplier for the GI term (1 = no AO), already faded by _BgiAoStrength.
//   shadow      : main-light sun visibility. This function is the SOLE authority for the buffer-GI
//                 main-light shadow: Off (0) leaves it 1.0 = OFF means genuinely no sun shadow (full
//                 direct light), NOT a fall-through to any other shadow source; Baked (1) is the baked
//                 value interpolated across the face; Sdf (2) is the per-pixel SDF raymarch result.
// lightDir is the UNIT direction TOWARD the main light (used only by the Sdf mode's raymarch).
// `normal` MUST be the geometric (vertex) normal, never a normal-mapped one - it both offsets the
// sample off the surface and picks the dominant face-plane axis, and the voxel grid knows nothing
// about normal maps. Feeding it a per-texel normal makes both jump within a single flat face.
void BgiSampleFaceAoShadow(float3 worldPos, float3 normal, float3 lightDir, out half ao, out half shadow)
{
    ao = 1.0h;
    shadow = 1.0h; // Off (mode 0) keeps this: no sun shadow, full direct light.

    bool insideFine; float3 origin, voxelSize; uint baseOffset;
    BgiSelectField(worldPos, insideFine, origin, voxelSize, baseOffset);
    int mode = insideFine ? _BgiShadowModeFine : _BgiShadowModeCoarse;

    // External per-pixel shadow sources - resolved directly here, independent of the face read below
    // (which then runs only for AO, or for the SSBO-read Baked path). Selected per BufferGI field and
    // NOT gated by the shadow-source keyword:
    //   Sdf (2)            : crisp per-pixel raymarch of the hi-res SDF (needs a baked SDF on the volume).
    //   OcclusionField (3) : the volume's baked per-direction occlusion field (needs its occlusion binder).
    //   Bitmask (4)        : the volume's baked directional occlusion bitmask (needs its occlusion binder).
    // The occlusion modes read the same _OccFieldTex / _BitmaskTex the material's GetShadow source uses,
    // so the matching occlusion binder must be active for their textures to be bound (like Sdf/SDF).
    // saturate() on the occlusion sources: keeps them in [0,1] and, crucially, maps a NaN (e.g. an
    // occlusion binder that isn't fully publishing its grid uniforms) to 0 rather than letting it
    // poison the whole fragment to black - a mis-bound source then reads as "fully shadowed", not a crash.
    if (mode == 2)      shadow = GetShadowFromSdf(lightDir, worldPos, 1.0e+10f); // lightDir is unit
    else if (mode == 3) shadow = saturate(GetOccFieldShadow(worldPos, normal));
    else if (mode == 4) shadow = saturate(GetBitmaskShadow(worldPos, normal));

    // BAKED mode (1): on the default texture read path, resolve it as ONE hardware trilinear tap of the
    // mirror texture's alpha (the air-layer sun visibility), NOT a StructuredBuffer face read - that's
    // the whole point, since on Adreno the SSBO face taps dominate while a sampler tap is ~free. Under
    // BGI_SSBO_READ (the A/B keyword, no mirror texture) it stays on the face-plane loop below.
    bool bakedShadow = (mode == 1);
#if !defined(BGI_SSBO_READ)
    if (bakedShadow) {
        shadow = BgiSampleShadowTexture(worldPos, normal, insideFine);
        bakedShadow = false; // resolved via texture; the face loop below is now AO-only
    }
#endif

    bool wantAo = _BgiAoStrength > 0.0;
    bool wantShadow = bakedShadow; // Off/Sdf resolved above (or 1.0); only the SSBO-read Baked path
                                   // still reads the shadow from the face plane here
    if (!wantAo && !wantShadow) return; // nothing left to gather from the face plane

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

    // Radiance carries more than one direction per voxel, so a tap has to pick a face. Uniform, so
    // this is a scalar branch, not per-pixel divergence.
    bool directional = _BgiRadianceDirs > 1;

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
            // One surface load covers both consumers. `directional` is a uniform compare, so in the
            // default Single mode the load is skipped entirely unless AO wants it - the slot select
            // costs the hot path nothing there.
            bool needSurface = wantAo || directional;
            uint sword = needSurface ? _Surface[slot] : 0u;
            if (wantAo)
                opennessAcc += BgiSurfaceOpenness(sword) * wgt;
            if (wantShadow) {
                // Pick the face by the SHADING surface's own normal, so the two sides of a sub-voxel
                // wall read their own sun visibility instead of sharing one. This is an SSBO read, so
                // each of the 4 taps resolves independently - the mirror texture's single alpha per
                // voxel cannot do this, which is why the SSBO path is the accurate one for thin walls.
                uint radSlot = directional ? BgiRadianceSlot(slot, normal, BgiSurfaceNormal(sword))
                                           : slot;
                half3 rgb; half w; BgiUnpackRgbH(_Radiance[radSlot], rgb, w);
                shadowAcc += w * wgt;
            }
            wsum += wgt;
        }
    }

    // wsum == 0 (no solid tap): leave ao/shadow at 1.0 - the previous per-source "outside" fallback.
    if (wsum > (half)1e-3) {
        if (wantAo)     ao     = lerp(1.0h, opennessAcc / wsum, (half)_BgiAoStrength);
        if (wantShadow) shadow = saturate(shadowAcc / wsum);
    }
}

// One field's 9-tap front-face read. `origin`/`voxelSize` are that field's grid; `baseOffset` is its
// slice in the concatenated buffers (0 = fine, BGI_COUNT = coarse). Returns raw irradiance (no gain).
half3 BgiSampleField(float3 worldPos, float3 normal, float3 origin, float3 voxelSize, uint baseOffset)
{
    // Dominant normal axis (+sign) + the two in-plane axes, expressed directly as buffer-index
    // STRIDES: BgiIndex is x | y<<L | z<<2L, so one voxel along an axis is a constant add. That lets
    // the tap loop step a flat index instead of rebuilding an int3 coordinate + shift/or per tap.
    // Scale-invariant, so no normalize needed.
    int SX = 1, SY = 1 << BGI_GRID_LOG2, SZ = 1 << (BGI_GRID_LOG2 * 2u);
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
    if (nCell < 0 || nCell >= (int)BGI_GRID) return 0.0h;

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
            half3 col; half n;
            BgiUnpackRgbH(_Irradiance[idx], col, n);
            acc += col * w;
            wsum += w;
        }
    }

    return (wsum > 1e-3h) ? (acc / wsum) : 0.0h;
}

// One hardware-trilinear tap of a field's mirrored irradiance texture at a surface point, taken in
// the SAME place the SSBO gather reads: the CENTRE of the air cell one step along the DOMINANT
// normal axis. Snapping that one coordinate to a voxel centre puts zero trilinear weight on the
// solid layer behind the surface, so the black CSBlur stores in solid voxels can no longer bleed
// darkness onto walls - the free-form `worldPos + normal * voxelSize` offset this replaces only
// moved a full cell for an axis-aligned face (0.71 at 45 degrees, less again with the per-axis
// voxel size of a non-cubic volume), so its footprint kept straddling the solid cell. The two
// in-plane coordinates stay continuous, so the tap still interpolates smoothly across the face.
// Like the SSBO gather, the sampled plane jumps one cell where the dominant axis flips.
// `normal` MUST be the geometric normal (see BgiSampleFaceAoShadow) - it picks the axis.
// In-plane neighbours can still be solid at a concave corner; CSBlur dilates the first solid shell
// with its air neighbours' irradiance so those taps read a plausible value instead of a hole.
half3 BgiSampleFieldTexture(Texture3D<float4> tex, SamplerState smp, float3 worldPos, float3 normal,
                            float3 origin, float3 voxelSize)
{
    float3 g = (worldPos - origin) / max(voxelSize, 1e-6); // continuous grid coords
    float3 aN = abs(normal);
    // Dominant normal axis as a mask - same pick as BgiSampleField's stride selection.
    float3 axis = (aN.x >= aN.y && aN.x >= aN.z) ? float3(1, 0, 0)
                : (aN.y >= aN.z)                 ? float3(0, 1, 0)
                                                 : float3(0, 0, 1);
    float gN     = dot(g, axis);
    float sgn    = dot(normal, axis) >= 0.0 ? 1.0 : -1.0;
    float target = floor(gN) + sgn + 0.5; // centre of the air cell one step in front
    g += axis * (target - gN);

    // Voxel centre c+0.5 maps to (c+0.5)/GRID, which is exactly texel c's centre - no half-texel fixup.
    float3 uvw = g * (1.0 / (float)BGI_GRID);
    if (any(uvw < 0.0) || any(uvw > 1.0)) return 0.0h;
    return (half3)tex.SampleLevel(smp, uvw, 0).rgb;
}

// Raw buffer-GI irradiance at a surface point (NO AO - the caller multiplies in the merged AO from
// BgiSampleFaceAoShadow). Fine field inside its box, coarse outside; scaled by _BgiIntensity.
half3 BgiGatherIndirect(float3 worldPos, float3 normal)
{
    bool insideFine; float3 origin, voxelSize; uint baseOff;
    BgiSelectField(worldPos, insideFine, origin, voxelSize, baseOff);

    half3 result;
#if defined(BGI_SSBO_READ)
    // A/B baseline: the original StructuredBuffer 9-tap B-spline gather.
    result = BgiSampleField(worldPos, normal, origin, voxelSize, baseOff);
#else
    // Default: one hardware-trilinear texture tap of the mirrored irradiance field (fine or coarse).
    // origin/voxelSize come from BgiSelectField above - the tap works in grid units, so the field's
    // world-space extent is never needed (which also drops the coarse box's voxelSize * GRID rebuild).
    // A Texture3D can't be selected by a ternary, so the branch picks the sampler pair.
    result = insideFine
        ? BgiSampleFieldTexture(_BgiIrradianceTex, sampler_BgiIrradianceTex,
                                worldPos, normal, origin, voxelSize)
        : BgiSampleFieldTexture(_BgiIrradianceTexCoarse, sampler_BgiIrradianceTexCoarse,
                                worldPos, normal, origin, voxelSize);
#endif

    // Final safety net: guarantee finite, non-negative GI so the additive term can never darken a
    // surface (a NaN/negative here renders black even over directly-lit pixels). The threshold is
    // 60000 (just under fp16's 65504 max) rather than the old 1e8, which is not representable in
    // half; the comparison is false for both +Inf and NaN, so they still map to 0. Values above it
    // are garbage anyway - the field is fp16-packed at the source.
    result = (result < 60000.0h) ? max(result, 0.0h) : 0.0h;
    return result * (half)_BgiIntensity;
}

#endif // GI_VOXEL_BUFFER

#endif // LOTEC_BUFFER_GI_INCLUDED
