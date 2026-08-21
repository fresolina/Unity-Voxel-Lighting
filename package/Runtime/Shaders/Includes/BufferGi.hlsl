#ifndef LOTEC_BUFFER_GI_INCLUDED
#define LOTEC_BUFFER_GI_INCLUDED

// LAYER: ENGINE-COUPLED - vertex/fragment only. Its own code is engine-free, but it includes
// VoxelOcclusion.hlsl, which needs the URP Core.hlsl texture macros. Do NOT include from a
// compute shader; the compute side reads the same fields via BufferGiVoxelData.hlsl.

// Fragment-side read for the buffer GI. Normal-oriented per field: pick the face the surface looks
// through (dominant normal axis) and read the air layer ONE voxel in FRONT of the surface - leak-free
// by construction, never touches voxels behind - as ONE hardware-trilinear tap of the mirrored
// irradiance Texture3D, snapped to that layer's voxel centres along the normal axis. Samples the FINE
// field, falls back to the COARSE field outside it (blended at the
// fine edges). No raymarching, no SDF, all cache-resident. The companion solve is BufferGi.compute
// (which dilates irradiance into the first solid shell for the texture path); layout is BufferGiField.hlsl.
//
// BGI_TAP_AXIS_SNAPPED (global keyword, BufferGiUpdater.SingleTapFilter) selects the SINGLE-mode tap
// filter; see BgiSampleFieldTexture. Compile-time rather than a uniform branch for the same reason as
// TONEMAP_*: a fragment kernel's register allocation covers every path it contains, and the Fast
// variant exists precisely to be the cheapest read in the package - carrying the other path's
// registers would tax it whether or not it runs. Bare-default off = today's one-tap read, byte for
// byte. Cube is unaffected (it already taps per axis) and compiles identically either way.

// Everything here is scoped to the GI_VOXEL_BUFFER variant. VoxelLit includes this header
// unconditionally but only calls into it under GI_VOXEL_BUFFER, so the other variants
// (GI_OFF / GI_UNITY) must not carry these fragment-stage StructuredBuffers: WebGPU
// validates every declared global against the bound pipeline layout and fails pipeline creation
// for a variant that declares _Occupancy / _Surface while they are unbound (null). D3D11/Vulkan
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
// Occupancy is the 1-bit/voxel bitfield (grid^3/8 bytes per field).
// These two are the ONLY buffers the fragment reads, and only for the AO face plane: all the actual
// lighting arrives through the mirror textures below, so _Material / _Irradiance / _Radiance are
// compute-side and are not declared (nor bound) here at all.
StructuredBuffer<uint>  _Occupancy;  // 1 bit/voxel solidity - rejects air taps in the AO face read
StructuredBuffer<uint>  _Surface;    // per-voxel surface word - static openness/AO in bits 16-23

// Each field's blurred irradiance, written straight into a Texture3D by CSBlur (fused - no separate
// copy pass). The read path taps these with ONE hardware-trilinear fetch - the Adreno win over the
// 9-tap SSBO B-spline this replaced. Bound whenever BufferGI is active (BufferGiUpdater), so they are
// never unbound WebGPU globals. One per field (fine + coarse).
Texture3D<float4> _BgiIrradianceTex;             // fine field
SamplerState sampler_BgiIrradianceTex;
Texture3D<float4> _BgiIrradianceTexCoarse;       // coarse field
SamplerState sampler_BgiIrradianceTexCoarse;

bool BgiSolidBit(uint slot)
{
    return (_Occupancy[slot >> 5] >> (slot & 31u)) & 1u;
}

// Minimum n^2 weight for an axis tap to be taken (BGI_TAP_AXIS_SNAPPED). 0.01 keeps a face within
// ~5.7 degrees of an axis on ONE tap - which is most of a built environment - while the error from
// skipping is at most 1% of the local spread between neighbouring samples. Raise it to buy speed on
// swept geometry at the cost of a (still bounded) seam; 0 takes all three taps always.
#define BGI_TAP_MIN_WEIGHT 0.01

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
// (the pre-marched visibility CSBlur mirrored into the irradiance texture's alpha), 2 = SDF (crisp
// per-pixel raymarch of the hi-res SDF, VoxelGI-parity, independent of the shadow-source keyword).
// The fragment picks fine vs coarse by whether the shading point is inside the fine volume.
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
// wrote): ONE hardware trilinear tap, replacing the 4-8 StructuredBuffer face taps this used to cost.
// Offset ~1 voxel along the normal into the air layer in front (same as the GI texture read), so
// the tap sits in the air voxels that actually carry sun-vis rather than the solid cell. Fine field
// inside its box, coarse outside (its grid size = coarse voxel size * grid - only the voxel size is
// published). This keeps the tunable continuous offset rather than the GI tap's snap-to-layer: alpha
// is a coverage signal the sharpening below reconstructs an edge from, and snapping would quantise it.
// Returns 1 (lit) outside the sampled field: no baked info is read as unshadowed.
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
    // In Cube the texture is six Z-stacked slabs, so a raw [0,1] z would sweep across all of them.
    uint idirs = BgiIrradianceDirs();
    half a;
    if (idirs == 1u) {
        a = insideFine
            ? (half)_BgiIrradianceTex.SampleLevel(sampler_BgiIrradianceTex, uvw, 0).a
            : (half)_BgiIrradianceTexCoarse.SampleLevel(sampler_BgiIrradianceTexCoarse, uvw, 0).a;
    } else {
        // CUBE: read the alpha the same way the GI reads the rgb - the three slabs in the normal's
        // hemisphere, weighted by n^2. Sun visibility at a point is a scalar, but which FACE's
        // visibility applies is not, and the slabs are exactly that distinction: CSBlur stores each
        // face's own value in the slab pointing that way, so an interior surface weights the
        // interior-facing slabs and never picks up the sunlit exterior across a sub-voxel wall. A
        // single-slab alpha cannot say which side, which is why Single leaks a bright seam along such
        // a join; this is hardware-filtered too, so the edge stays smooth and _BgiShadowSharpness applies.
        //
        // Air cells carry the same value in every slab (a point in air has no sides), so nothing is
        // lost there; only solid shell cells differ per slab, which is where the ambiguity lived.
        float invSlabs = 1.0 / (float)idirs;
        float zLocal = clamp(uvw.z, 0.5 / (float)BGI_GRID, 1.0 - 0.5 / (float)BGI_GRID);
        float3 n2 = normal * normal;
        float accA = 0.0, wsum = 0.0;
        [unroll]
        for (uint s = 0u; s < 3u; s++) {
            float d  = (s == 0u) ? normal.x : ((s == 1u) ? normal.y : normal.z);
            float w2 = (s == 0u) ? n2.x     : ((s == 1u) ? n2.y     : n2.z);
            uint slab = s * 2u + (d >= 0.0 ? 1u : 0u);
            float3 suvw = float3(uvw.x, uvw.y, ((float)slab + zLocal) * invSlabs);
            accA += (insideFine
                ? _BgiIrradianceTex.SampleLevel(sampler_BgiIrradianceTex, suvw, 0).a
                : _BgiIrradianceTexCoarse.SampleLevel(sampler_BgiIrradianceTexCoarse, suvw, 0).a) * w2;
            wsum += w2;
        }
        a = (half)(accA / max(wsum, 1e-4));
    }

    // Sharpen the reconstructed edge. Near a shadow boundary the stored value is the voxel's sun
    // COVERAGE, and coverage is a local signed distance to that boundary - so re-centring on 0.5 and
    // steepening rebuilds an edge finer than the voxel it came from. Same SDF-font trick as
    // GetOccFieldShadow's _OccFieldDecode, and the reason it works here at all is that the solve now
    // stores a fraction: against the old 0/1 field this would only harden the staircase.
    // 1 = passthrough (identical to before). Costs one MAD.
    return saturate((a - 0.5h) * (half)_BgiShadowSharpness + 0.5h);
}

// The buffer-GI surface read for a shading point: the main-light sun visibility (from whichever
// per-field ShadowMode is selected) plus the baked static AO (openness, _Surface bits 16-23).
//   ao          : AO multiplier for the GI term (1 = no AO), already faded by _BgiAoStrength. Read
//                 from the 4 face voxels around the point; non-solid / out-of-bounds taps are SKIPPED
//                 and the weights RENORMALISED, so a face edge takes the value of the surface voxels
//                 actually present. Skipped entirely (no buffer traffic at all) when AO is off.
//   shadow      : main-light sun visibility. This function is the SOLE authority for the buffer-GI
//                 main-light shadow: Off (0) leaves it 1.0 = OFF means genuinely no sun shadow (full
//                 direct light), NOT a fall-through to any other shadow source; Baked (1) is one
//                 trilinear tap of the mirror texture's alpha; Sdf (2) is the per-pixel SDF raymarch.
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

    // Shadow source per BufferGI field. Each is fully resolved here - no fall-through:
    //   Baked (1)          : one hardware-trilinear tap of the mirror texture's ALPHA (see
    //                        BgiSampleShadowTexture). In Cube each Z slab carries its own face's
    //                        visibility, so a sub-voxel wall's two sides resolve separately and still
    //                        hardware-filtered - no StructuredBuffer face taps anywhere on this path,
    //                        which is the whole point on Adreno (a sampler tap is ~free, SSBO taps
    //                        dominate the fragment).
    //   Sdf (2)            : crisp per-pixel raymarch of the hi-res SDF (needs a baked SDF on the volume).
    //   OcclusionField (3) : the volume's baked per-direction occlusion field (needs its occlusion binder).
    //   Bitmask (4)        : the volume's baked directional occlusion bitmask (needs its occlusion binder).
    // The occlusion modes read the same _OccFieldTex / _BitmaskTex the material's GetShadow source uses,
    // so the matching occlusion binder must be active for their textures to be bound (like Sdf/SDF).
    // saturate() on the occlusion sources: keeps them in [0,1] and, crucially, maps a NaN (e.g. an
    // occlusion binder that isn't fully publishing its grid uniforms) to 0 rather than letting it
    // poison the whole fragment to black - a mis-bound source then reads as "fully shadowed", not a crash.
    if (mode == 1)      shadow = BgiSampleShadowTexture(worldPos, normal, insideFine);
    else if (mode == 2) shadow = GetShadowFromSdf(lightDir, worldPos, 1.0e+10f); // lightDir is unit
    else if (mode == 3) shadow = saturate(GetOccFieldShadow(worldPos, normal));
    else if (mode == 4) shadow = saturate(GetBitmaskShadow(worldPos, normal));

    if (_BgiAoStrength <= 0.0) return; // AO off -> nothing to read from the face plane

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
            opennessAcc += BgiSurfaceOpenness(_Surface[slot]) * wgt;
            wsum += wgt;
        }
    }

    // wsum == 0: not one of the four in-plane taps was SOLID (a surface whose own cell the voxelizer
    // did not mark - detail finer than a voxel), so there is no openness to report. AO keeps its 1.0:
    // no occlusion information is a fair "unoccluded".
    if (wsum > (half)1e-3)
        ao = lerp(1.0h, opennessAcc / wsum, (half)_BgiAoStrength);
}

// One hardware-trilinear tap of a field's mirrored irradiance texture at a surface point, taken at
// the CENTRE of the air cell one step along the DOMINANT normal axis. Snapping that one coordinate
// to a voxel centre puts zero trilinear weight on the
// solid layer behind the surface, so the black CSBlur stores in solid voxels can no longer bleed
// darkness onto walls - the free-form `worldPos + normal * voxelSize` offset this replaces only
// moved a full cell for an axis-aligned face (0.71 at 45 degrees, less again with the per-axis
// voxel size of a non-cubic volume), so its footprint kept straddling the solid cell. The two
// in-plane coordinates stay continuous, so the tap still interpolates smoothly across the face;
// the sampled plane jumps one cell where the dominant axis flips.
// `normal` MUST be the geometric normal (see BgiSampleFaceAoShadow) - it picks the axis.
// In-plane neighbours can still be solid at a concave corner; CSBlur dilates the first solid shell
// with its air neighbours' irradiance so those taps read a plausible value instead of a hole.
half3 BgiSampleFieldTexture(Texture3D<float4> tex, SamplerState smp, float3 worldPos, float3 normal,
                            float3 origin, float3 voxelSize)
{
    float3 g = (worldPos - origin) / max(voxelSize, 1e-6); // continuous grid coords
    uint idirs = BgiIrradianceDirs();

    if (idirs == 1u) {
#if defined(BGI_TAP_AXIS_SNAPPED)
        // SINGLE / AXIS-SNAPPED: up to three taps of this SAME one-bucket texture, each snapped to the
        // centre of the cell one step along ITS OWN axis and weighted by n^2. Trades the Fast path's
        // single tap for two properties it cannot have:
        //
        //  * The solid cell behind the surface carries ZERO trilinear weight. Sampling exactly at a
        //    texel centre along one axis degenerates that axis to nearest while the other two stay
        //    continuous - i.e. bilinear in-plane, nearest along the normal, which is precisely what
        //    this read wants. The Fast path's continuous offset keeps footprint on the cell it came
        //    from wherever the surface sits mid-cell, and on a SUB-VOXEL WALL that cell is shared by
        //    both faces (CSBlur can only fill it from one side - see its two-sided note), so the far
        //    side reads the near side's light. Snapping drops it out of the footprint entirely.
        //  * No dominant-axis discontinuity. Snapping ONE shared axis (what this read did before the
        //    Fast path replaced it) jumps a whole cell wherever the largest normal component changes -
        //    invisible on flat geometry, hard-edged patches across curved/carved detail. Here each tap
        //    is snapped along its own axis and weighted by n^2, so at a crossing the offset direction
        //    flips while its weight is exactly 0. Same construction as the CUBE path below, and the
        //    same reason it is continuous.
        //
        // Cost is the point of the toggle: 3 taps worst case against Fast's 1. The n^2 cutoff below is
        // what keeps that from being the common case - see BGI_TAP_MIN_WEIGHT.
        //
        // Note this fixes the FETCH only. A sub-voxel wall still leaks in the SOLVE (Single collapses
        // both faces onto one radiance slot) and still shares one openness scalar for its AO. Those are
        // baked before a pixel is drawn; only Cube (or a thicker wall) addresses them.
        float3 n2 = normal * normal;
        float3 acc = 0;
        float wsum = 0; // renormalises over the taps actually taken (cutoff + out-of-field skips)
        [unroll]
        for (uint a = 0u; a < 3u; a++) {
            float w2 = (a == 0u) ? n2.x : ((a == 1u) ? n2.y : n2.z);
            // Skip taps that would contribute almost nothing. This is the whole performance story: an
            // axis-aligned face (most of a wall, floor or ceiling) has n^2 = 1 on one axis and 0 on the
            // other two, so it takes ONE tap - the Fast path's cost - and only swept/curved normals pay
            // for 2-3. Dropping weight w2 and renormalising perturbs the result by at most w2 times the
            // spread between neighbouring irradiance samples, so the discontinuity this introduces is
            // bounded by the cutoff itself rather than by the field's dynamic range.
            if (w2 < BGI_TAP_MIN_WEIGHT) continue;
            float3 axisMask = (a == 0u) ? float3(1, 0, 0) : ((a == 1u) ? float3(0, 1, 0) : float3(0, 0, 1));
            float d  = dot(normal, axisMask);
            float gA = dot(g, axisMask);
            // This tap's plane: centre of the cell one step along its axis, in that component's sign.
            float3 ga = g + axisMask * ((floor(gA) + (d >= 0.0 ? 1.0 : -1.0) + 0.5) - gA);
            float3 uvw = ga * (1.0 / (float)BGI_GRID);
            if (any(uvw < 0.0) || any(uvw > 1.0)) continue;
            acc += (float3)tex.SampleLevel(smp, uvw, 0).rgb * w2;
            wsum += w2;
        }
        return (wsum > 1e-4) ? (half3)(acc / wsum) : 0.0h;
#else
        // SINGLE / FAST: one tap, the cheapest read there is. One value per voxel serves every
        // direction, so this mode assumes walls at least two voxels thick - a thinner wall shares one
        // shell cell between both faces and there is only one value to give them (CSBlur leaves those
        // black). BGI_TAP_AXIS_SNAPPED above keeps the same storage and buys the footprint back.
        //
        // The offset is CONTINUOUS in the normal, not snapped to the dominant axis' next cell centre.
        // Scaling the normal so its largest component is exactly 1 keeps the snap's actual guarantee -
        // the dominant axis clears a full cell, so the solid layer behind the surface carries no
        // trilinear weight - while the other two axes move proportionally instead of not at all.
        // A snap makes the sample position jump a whole cell wherever the largest component changes,
        // which is invisible on flat geometry (constant axis over a face) but paints hard-edged
        // patches across curved/carved detail, where the normal sweeps through those boundaries
        // continuously. max|n| is continuous and >= 1/sqrt(3) for a unit normal, so this is smooth
        // everywhere and the divide is always safe.
        //
        // Where the surface sits mid-cell the footprint can still catch the solid cell it came from;
        // that is what CSBlur's shell dilation fills, and it is why the snap this replaces is no
        // longer needed to keep solid black out of the tap.
        //
        // Stepping in GRID units also makes the offset one CELL on every axis. The world-space
        // `normal * voxelSize` this ultimately derives from was anisotropic on a non-cubic volume -
        // a Z step moved 1.07 m against Y's 0.47 m here - so it under-cleared the short axes.
        float3 aN = abs(normal);
        g += normal / max(max(aN.x, aN.y), max(aN.z, 1e-4));

        // Voxel centre c+0.5 maps to (c+0.5)/GRID, exactly texel c's centre - no half-texel fixup.
        float3 uvw = g * (1.0 / (float)BGI_GRID);
        if (any(uvw < 0.0) || any(uvw > 1.0)) return 0.0h;
        return (half3)tex.SampleLevel(smp, uvw, 0).rgb;
#endif // BGI_TAP_AXIS_SNAPPED
    }

    // CUBE: the ambient-cube evaluation - the 3 buckets in the normal's hemisphere, weighted by n^2
    // (which sums to exactly 1 for a unit normal).
    //
    // Each bucket is offset along ITS OWN axis, not along a shared dominant axis. Bucket +X holds the
    // light arriving from +X, so the place to read it is the cell one step in +X - the air an
    // X-facing surface actually sees. Pairing them this way removes the dominant-axis discontinuity
    // outright rather than smoothing it: where a normal component crosses zero its offset direction
    // AND its slab both flip, but n^2 is exactly 0 there, so the contribution is continuous through
    // the crossing. Still three taps - the same count the shared-plane version cost - because the
    // per-bucket plane replaces the shared one instead of multiplying it.
    //
    // The other two coordinates stay continuous, so each tap still interpolates smoothly across the
    // face; in-plane neighbours can be solid at a concave corner, which is what CSBlur's per-bucket
    // shell dilation fills.
    //
    // The buckets are Z slabs of one texture. Slab-local Z is clamped to [0.5, GRID-0.5] so the
    // trilinear footprint can never reach a neighbouring slab's texels with nonzero weight; that is
    // also exactly what wrapMode.Clamp already did at the volume's own Z extremes.
    float invSlabs = 1.0 / (float)idirs;
    float3 n2 = normal * normal;
    float3 acc = 0;
    float wsum = 0; // renormalises when a tap falls outside the field and is skipped
    [unroll]
    for (uint a = 0u; a < 3u; a++) {
        float3 axisMask = (a == 0u) ? float3(1, 0, 0) : ((a == 1u) ? float3(0, 1, 0) : float3(0, 0, 1));
        float d  = dot(normal, axisMask);
        float w2 = dot(n2, axisMask);
        float gA = dot(g, axisMask);
        // This bucket's own plane: centre of the cell one step along ITS axis, in the bucket's sign.
        float3 ga = g + axisMask * ((floor(gA) + (d >= 0.0 ? 1.0 : -1.0) + 0.5) - gA);
        float3 uvw = ga * (1.0 / (float)BGI_GRID);
        if (any(uvw < 0.0) || any(uvw > 1.0)) continue;
        uint slab = a * 2u + (d >= 0.0 ? 1u : 0u);
        float zLocal = clamp(ga.z, 0.5, (float)BGI_GRID - 0.5) * (1.0 / (float)BGI_GRID);
        float3 suvw = float3(uvw.x, uvw.y, ((float)slab + zLocal) * invSlabs);
        acc += (float3)tex.SampleLevel(smp, suvw, 0).rgb * w2;
        wsum += w2;
    }
    return (wsum > 1e-4) ? (half3)(acc / wsum) : 0.0h;
}

// Raw buffer-GI irradiance at a surface point (NO AO - the caller multiplies in the merged AO from
// BgiSampleFaceAoShadow). Fine field inside its box, coarse outside; scaled by _BgiIntensity.
half3 BgiGatherIndirect(float3 worldPos, float3 normal)
{
    bool insideFine; float3 origin, voxelSize; uint baseOff;
    BgiSelectField(worldPos, insideFine, origin, voxelSize, baseOff);

    // One hardware-trilinear texture tap of the mirrored irradiance field (fine or coarse).
    // origin/voxelSize come from BgiSelectField above - the tap works in grid units, so the field's
    // world-space extent is never needed (which also drops the coarse box's voxelSize * GRID rebuild).
    // A Texture3D can't be selected by a ternary, so the branch picks the sampler pair.
    half3 result = insideFine
        ? BgiSampleFieldTexture(_BgiIrradianceTex, sampler_BgiIrradianceTex,
                                worldPos, normal, origin, voxelSize)
        : BgiSampleFieldTexture(_BgiIrradianceTexCoarse, sampler_BgiIrradianceTexCoarse,
                                worldPos, normal, origin, voxelSize);

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
