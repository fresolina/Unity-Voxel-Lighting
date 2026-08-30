#ifndef LOTEC_BUFFER_GI_INCLUDED
#define LOTEC_BUFFER_GI_INCLUDED

// ENGINE-AGNOSTIC, like every header in this folder: HLSL intrinsics and our own headers only. No
// URP includes, no vertex/fragment semantics, no Core.hlsl texture macros. That means any of these
// can be included from a fragment shader, a compute shader or the voxelize raster alike.
//
// The engine boundary is the .shader / .compute ENTRY POINTS. VoxelLit.shader includes URP's
// Core.hlsl and Lighting.hlsl and calls GetMainLight(), then hands this library plain values.
// Guarded by Shaders/Compute/BufferGiCommonCanary.compute, which includes every header here and fails
// moment one acquires an engine dependency - do not "fix" that by adding an include to the canary.


// Fragment-side read for the buffer GI. Normal-oriented per field: pick the face the surface looks
// through (dominant normal axis) and read the air layer ONE voxel in FRONT of the surface - leak-free
// by construction, never touches voxels behind - as ONE hardware-trilinear tap of the mirrored
// irradiance Texture3D, snapped to that layer's voxel centres along the normal axis. Samples the FINE
// field, falls back to the COARSE field outside it (blended at the
// fine edges). No raymarching, no SDF, all cache-resident. The companion solve is BufferGiSolve.compute
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
// (GI_OFF / GI_UNITY) must not carry a fragment-stage StructuredBuffer: WebGPU validates every
// declared global against the bound pipeline layout and fails pipeline creation for a variant that
// declares one while it is unbound (null). D3D11/Vulkan silently strip/tolerate them, which is why
// this only bites in a WebGPU (browser) build.
//
// Since AO was removed the SHIPPING fragment declares NO StructuredBuffer at all - every value it
// reads arrives through the mirror Texture3D. _Occupancy comes back only under BGI_DEBUG_VIEWS, for
// the footprint-contamination analysis view.
#if defined(GI_VOXEL_BUFFER)

#include "BufferGiField.hlsl"
// BgiSampleSunShadow and everything behind it - the main-light sun shadow, which is its own
// subsystem and no longer lives in this file (S1 of docs/direct-shadow-extraction.md). Included
// here, inside the same GI_VOXEL_BUFFER guard the shadow globals were already declared under, so the
// set of globals each VoxelLit variant declares is unchanged by the move - which is what keeps the
// WebGPU pipeline-layout surface identical. It pulls its own shadow-source headers (SDF, occlusion).
//
// The include points THIS way on purpose: the GI read depends on the shadow header, never the
// reverse, so a shadow-only configuration stays possible.
#include "VoxelSunShadow.hlsl"

// ANALYSIS ONLY. Bound as a global by BufferGiUpdater; concatenated over all fields (coarse at
// offset 0, fine at offset BGI_COUNT), the 1-bit/voxel solidity bitfield (grid^3/8 bytes per field).
// The shipping fragment reads NO buffer - all lighting arrives through the mirror textures below, so
// _Material / _Surface / _Irradiance / _Radiance are compute-side and are not declared (nor bound)
// here at all. This one exists solely so BgiTapSolidWeight can report which pixels a leak fix could
// move, and it is compiled out of every shipped variant with the keyword.
#if defined(BGI_DEBUG_VIEWS)
StructuredBuffer<uint>  _Occupancy;

bool BgiSolidBit(uint slot)
{
    return (_Occupancy[slot >> 5] >> (slot & 31u)) & 1u;
}
#endif

// Each field's blurred irradiance, written straight into a Texture3D by CSBlur (fused - no separate
// copy pass). The read path taps these with ONE hardware-trilinear fetch - the Adreno win over the
// 9-tap SSBO B-spline this replaced. Bound whenever BufferGI is active (BufferGiUpdater), so they are
// never unbound WebGPU globals. One per field (fine + coarse).
Texture3D<float4> _BgiIrradianceTex;             // fine field
SamplerState sampler_BgiIrradianceTex;
Texture3D<float4> _BgiIrradianceTexCoarse;       // coarse field
SamplerState sampler_BgiIrradianceTexCoarse;

// Each field's NEIGHBOUR SOLIDITY MASK at the LIGHTING grid: 7 bits per cell, R8_UInt, built once at
// bake by CSBuildNeighbourMask (pure geometry - it must not move when the sun does).
//
//   bit 0    this cell is solid       bits 1,2  -X, +X    bits 3,4  -Y, +Y    bits 5,6  -Z, +Z
//
// Point-LOADED, never sampled: an interpolated bitmask is meaningless, which is also why it cannot
// ride a spare channel of any volume above - all of those are filtered. No sampler is declared for
// the same reason.
//
// Declared unconditionally alongside the other two pairs (not under the snap keyword) so the set of
// fragment globals does not depend on a keyword: WebGPU fails pipeline creation on a declared-but-
// unbound global, and BufferGiUpdater binds these whenever BufferGI is active.
Texture3D<uint>   _BgiNeighbourMask;             // fine field
Texture3D<uint>   _BgiNeighbourMaskCoarse;       // coarse field

// Minimum n^2 weight for an axis tap to be taken (BGI_TAP_AXIS_SNAPPED). 0.01 keeps a face within
// ~5.7 degrees of an axis on ONE tap - which is most of a built environment - while the error from
// skipping is at most 1% of the local spread between neighbouring samples. Raise it to buy speed on
// swept geometry at the cost of a (still bounded) seam; 0 takes all three taps always.
#define BGI_TAP_MIN_WEIGHT 0.01

// Uniforms below stay `float`: they are loose globals (not a CBUFFER block), set from C# with
// SetGlobalFloat/SetGlobalVector, and a uniform load costs the same either way - the fp16 win is in
// the ALU, so they are narrowed at the point of use instead.
// Indirect gain on the GI contribution = the sun's Light.bounceIntensity (Unity's Indirect
// Multiplier). Scales only the bounce, leaving direct lighting and emission untouched.
float _BgiIntensity;
// ANALYSIS view selector (BufferGiUpdater.DebugView): 0 = off (normal shading), 1 = GI only,
// 2 = sun visibility, 4 = direct only, 5 = GI tap solid weight. Applied in the lit shader's fragment, so it
// isolates the term AS THE FRAGMENT ACTUALLY READ IT - which is what separates a bake artifact from
// a read artifact when compared against the BufferGiDebug cubes showing the same quantity per voxel.
// Loose global like the rest; unbound it reads 0 and everything below is the identity.
float _BgiDebugView;

// How much of the trilinear footprint at grid position `ga` lands on SOLID cells. Texel c's centre
// is at c+0.5 in grid units, so the interpolation origin is ga-0.5 and the eight corner weights are
// the usual separable products. Out-of-grid corners are skipped rather than counted: the sampler
// clamps there, so they cannot contribute a shell texel.
#if defined(BGI_DEBUG_VIEWS)
float BgiSolidWeightAt(float3 ga, uint baseOffset)
{
    float3 t  = ga - 0.5;
    int3   c0 = (int3)floor(t);
    float3 f  = t - (float3)c0;
    float sw = 0.0;
    [unroll] for (int dz = 0; dz <= 1; dz++)
    [unroll] for (int dy = 0; dy <= 1; dy++)
    [unroll] for (int dx = 0; dx <= 1; dx++) {
        int3 c = c0 + int3(dx, dy, dz);
        if (!BgiInBounds(c)) continue;
        if (!BgiSolidBit(baseOffset + BgiIndex((uint3)c))) continue;
        sw += (dx == 0 ? 1.0 - f.x : f.x) * (dy == 0 ? 1.0 - f.y : f.y) * (dz == 0 ? 1.0 - f.z : f.z);
    }
    return sw;
}
#endif

// One hardware-trilinear tap of a field's mirrored irradiance texture at a surface point, taken at
// the CENTRE of the air cell one step along the DOMINANT normal axis. Snapping that one coordinate
// to a voxel centre puts zero trilinear weight on the
// solid layer behind the surface, so the black CSBlur stores in solid voxels can no longer bleed
// darkness onto walls - the free-form `worldPos + normal * voxelSize` offset this replaces only
// moved a full cell for an axis-aligned face (0.71 at 45 degrees, less again with the per-axis
// voxel size of a non-cubic volume), so its footprint kept straddling the solid cell. The two
// in-plane coordinates stay continuous, so the tap still interpolates smoothly across the face;
// the sampled plane jumps one cell where the dominant axis flips.
// `normal` MUST be the geometric normal (see BgiSampleSunShadow) - it picks the axis.
// In-plane neighbours can still be solid at a concave corner; CSBlur dilates the first solid shell
// with its air neighbours' irradiance so those taps read a plausible value instead of a hole.
#if defined(BGI_TAP_SNAP_INPLANE)
// P9 - CONTAMINATED-AXIS SNAP.
//
// BGI_TAP_AXIS_SNAPPED clears the solid layer BEHIND the surface, along the normal. This clears the
// solid layer BESIDE it: where the trilinear kernel spans a one-voxel wall IN THE SURFACE PLANE, the
// tap's footprint straddles a shell cell whose value belongs to a surface facing some other way, and
// the far side reads the near side's light. The two in-plane coordinates are exactly the ones the
// existing snap leaves continuous, which is why this is the other half of the same idea and not a
// competing one.
//
// Trilinear on axis a blends c0 = floor(t - 0.5) with c0 + 1, weight f on c0 + 1:
//     c0 + 1 solid -> f = 0        c0 solid -> f = 1        both air -> LEAVE IT ALONE
//
// The condition is BINARY, not a tuned threshold, and the snap engages where it is a NO-OP: the
// moment c0 + 1 becomes the wall cell is the moment the tap crossed a cell boundary, where f is
// already 0. So the sample position is continuous across the engage point - which is what both
// earlier attempts at this lacked, and why they read as blocky.
//
// `skipAxis` is the tap's OWN axis (the one BGI_TAP_AXIS_SNAPPED already placed at a cell centre, or
// the dominant normal axis on the Fast path). Snapping it here would move the tap off that centre
// and undo the normal-axis guarantee: f is 0 there, so c0 IS the tapped cell, and a solid tapped cell
// would push the sample to the next one.
// Written BRANCHLESS AND VECTORISED, all three axes at once, deliberately. The obvious form - an
// [unroll]ed loop over the axes with `continue` on skipAxis, dynamic `?:` component reads and
// per-component write-back - CRASHED the FXC compiler process outright ("Lost connection with shader
// compiler process. Suspected crash in FXC", which surfaces as a magenta error shader and names no
// line). This form has no loop, no dynamic component indexing and no branch except the early-out, and
// it is also simply less work.
float3 BgiSnapContaminatedAxes(float3 ga, Texture3D<uint> maskTex, int skipAxis)
{
    int3 ci = (int3)floor(ga);
    uint mask = maskTex.Load(int4(ci, 0));

    float3 c0 = floor(ga - 0.5);                  // the low cell of each axis' trilinear pair
    bool3 below = (ga - floor(ga)) < 0.5;         // below the cell centre -> the pair is (ci-1, ci)
    bool3 selfSolid  = (mask & 1u) != 0u;
    bool3 minusSolid = bool3((mask & 2u) != 0u, (mask & 8u)  != 0u, (mask & 32u) != 0u);
    bool3 plusSolid  = bool3((mask & 4u) != 0u, (mask & 16u) != 0u, (mask & 64u) != 0u);
    bool3 c0Solid = below ? minusSolid : selfSolid;
    bool3 c1Solid = below ? selfSolid  : plusSolid;

    //   c0+1 solid -> all weight on c0        c0 solid -> all weight on c0+1        else leave it
    //
    // BOTH SOLID MUST NOT SNAP. The first version resolved that case to c0 + 0.5 - "prefer the near
    // side" - which puts the whole trilinear weight on a cell that is itself solid, and an undilated
    // solid cell holds BLACK. It painted hard black rectangles into the atrium floor and the column
    // bases, which is exactly the blockiness this technique is supposed to avoid. When neither cell of
    // the pair is a clean read there is nothing to snap TO, so the tap stays continuous and the
    // footprint keeps whatever contamination it had.
    bool3 exactlyOne = c0Solid != c1Solid;
    float3 snapped = exactlyOne ? (c1Solid ? (c0 + 0.5) : (c0 + 1.5)) : ga;
    // CLAMP INTO THE FIELD. Both tap paths treat an out-of-range uvw as "no data" - the Fast path
    // returns black outright, the axis-snapped one drops the tap - so a snap that pushes the sample
    // past the last cell centre does not read a neighbour, it reads NOTHING. High up in the atrium
    // that painted hard black rectangles across the clerestory. The valid range is the centre of the
    // first cell to the centre of the last, which is also exactly the clamp the Cube path already
    // applies to its slab-local Z.
    snapped = clamp(snapped, 0.5, (float)BGI_GRID - 0.5);

    // The tap's own axis keeps its continuous coordinate - see the note above on why.
    bool3 keep = bool3(skipAxis == 0, skipAxis == 1, skipAxis == 2);
    // mask == 0 means nothing solid in the whole 6-neighbourhood, so no pair can straddle a wall.
    // In open space that is every pixel, and it is why the mask is worth a texture of its own.
    return (mask == 0u) ? ga : (keep ? ga : snapped);
}

// Field-selecting wrapper. A Texture3D CANNOT be chosen with a ternary - the same rule that makes
// BgiGatherIndirect branch over whole calls instead of over the texture - and doing it anyway does
// not produce a clean error: it CRASHED the FXC compiler process, which surfaces as
// "Lost connection with shader compiler process" and a magenta error shader, with nothing naming the
// line. Single-exit for the usual out-param/return-value warning reason.
float3 BgiSnapForField(float3 ga, bool insideFine, int skipAxis)
{
    float3 r;
    if (insideFine) r = BgiSnapContaminatedAxes(ga, _BgiNeighbourMask, skipAxis);
    else            r = BgiSnapContaminatedAxes(ga, _BgiNeighbourMaskCoarse, skipAxis);
    return r;
}
#endif

half3 BgiSampleFieldTexture(Texture3D<float4> tex, SamplerState smp, Texture3D<uint> maskTex,
                            float3 worldPos, float3 normal, float3 origin, float3 voxelSize)
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
#if defined(BGI_TAP_SNAP_INPLANE)
            ga = BgiSnapContaminatedAxes(ga, maskTex, (int)a);
#endif
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

#if defined(BGI_TAP_SNAP_INPLANE)
        // The Fast path has no snapped axis of its own, so the one to protect is the DOMINANT normal
        // axis - the one its continuous offset is clearing. Snapping that here would fight the offset.
        int dom = (aN.x >= aN.y) ? ((aN.x >= aN.z) ? 0 : 2) : ((aN.y >= aN.z) ? 1 : 2);
        g = BgiSnapContaminatedAxes(g, maskTex, dom);
#endif
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
#if defined(BGI_TAP_SNAP_INPLANE)
        ga = BgiSnapContaminatedAxes(ga, maskTex, (int)a);
#endif
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

#if defined(BGI_DEBUG_VIEWS)
// ANALYSIS. How much of the GI tap's footprint is contaminated by solid (shell) texels - the raw
// material of the in-plane leak. 0 = the pixel physically CANNOT leak; 1 = its GI came entirely from
// shell cells. This MIRRORS BgiSampleFieldTexture's tap placement branch for branch, so the number
// describes the read the fragment actually performed rather than an idealised one. Keep the two in
// sync - a divergence here would quietly report a fictional cause.
float BgiTapSolidWeight(float3 worldPos, float3 normal)
{
    bool insideFine; float3 origin, voxelSize; uint baseOff;
    BgiSelectField(worldPos, insideFine, origin, voxelSize, baseOff);
    float3 g = (worldPos - origin) / max(voxelSize, 1e-6);
    uint idirs = BgiIrradianceDirs();

    if (idirs == 1u) {
#if defined(BGI_TAP_AXIS_SNAPPED)
        float3 n2 = normal * normal;
        float acc = 0.0, wsum = 0.0;
        [unroll]
        for (uint a = 0u; a < 3u; a++) {
            float w2 = (a == 0u) ? n2.x : ((a == 1u) ? n2.y : n2.z);
            if (w2 < BGI_TAP_MIN_WEIGHT) continue;
            float3 axisMask = (a == 0u) ? float3(1, 0, 0) : ((a == 1u) ? float3(0, 1, 0) : float3(0, 0, 1));
            float d = dot(normal, axisMask), gA = dot(g, axisMask);
            float3 ga = g + axisMask * ((floor(gA) + (d >= 0.0 ? 1.0 : -1.0) + 0.5) - gA);
#if defined(BGI_TAP_SNAP_INPLANE)
            ga = BgiSnapForField(ga, insideFine, (int)a);
#endif
            if (any(ga < 0.0) || any(ga > (float)BGI_GRID)) continue;
            acc += BgiSolidWeightAt(ga, baseOff) * w2;
            wsum += w2;
        }
        return (wsum > 1e-4) ? acc / wsum : 0.0;
#else
        float3 aN = abs(normal);
        float3 ga = g + normal / max(max(aN.x, aN.y), max(aN.z, 1e-4));
#if defined(BGI_TAP_SNAP_INPLANE)
        int dom = (aN.x >= aN.y) ? ((aN.x >= aN.z) ? 0 : 2) : ((aN.y >= aN.z) ? 1 : 2);
        ga = BgiSnapForField(ga, insideFine, dom);
#endif
        if (any(ga < 0.0) || any(ga > (float)BGI_GRID)) return 0.0;
        return BgiSolidWeightAt(ga, baseOff);
#endif
    }

    // CUBE: per-axis planes, n^2 weighted, same as the sampled version. The slab packing is a Z
    // offset in the TEXTURE only - occupancy is indexed by the plain cell, so the walk is unchanged.
    float3 n2c = normal * normal;
    float accC = 0.0, wsumC = 0.0;
    [unroll]
    for (uint a2 = 0u; a2 < 3u; a2++) {
        float3 axisMask = (a2 == 0u) ? float3(1, 0, 0) : ((a2 == 1u) ? float3(0, 1, 0) : float3(0, 0, 1));
        float d = dot(normal, axisMask), w2 = dot(n2c, axisMask), gA = dot(g, axisMask);
        float3 ga = g + axisMask * ((floor(gA) + (d >= 0.0 ? 1.0 : -1.0) + 0.5) - gA);
#if defined(BGI_TAP_SNAP_INPLANE)
        ga = BgiSnapForField(ga, insideFine, (int)a2);
#endif
        if (any(ga < 0.0) || any(ga > (float)BGI_GRID)) continue;
        accC += BgiSolidWeightAt(ga, baseOff) * w2;
        wsumC += w2;
    }
    return (wsumC > 1e-4) ? accC / wsumC : 0.0;
}
#endif // BGI_DEBUG_VIEWS

// Raw buffer-GI irradiance at a surface point (NO AO - the caller multiplies in the merged AO from
// BgiSampleSunShadow). Fine field inside its box, coarse outside; scaled by _BgiIntensity.
half3 BgiGatherIndirect(float3 worldPos, float3 normal)
{
    bool insideFine; float3 origin, voxelSize; uint baseOff;
    BgiSelectField(worldPos, insideFine, origin, voxelSize, baseOff);

    // One hardware-trilinear texture tap of the mirrored irradiance field (fine or coarse).
    // origin/voxelSize come from BgiSelectField above - the tap works in grid units, so the field's
    // world-space extent is never needed (which also drops the coarse box's voxelSize * GRID rebuild).
    // A Texture3D can't be selected by a ternary, so the branch picks the sampler pair.
    half3 result = insideFine
        ? BgiSampleFieldTexture(_BgiIrradianceTex, sampler_BgiIrradianceTex, _BgiNeighbourMask,
                                worldPos, normal, origin, voxelSize)
        : BgiSampleFieldTexture(_BgiIrradianceTexCoarse, sampler_BgiIrradianceTexCoarse,
                                _BgiNeighbourMaskCoarse, worldPos, normal, origin, voxelSize);

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
