#ifndef LOTEC_VOXEL_SUN_SHADOW_INCLUDED
#define LOTEC_VOXEL_SUN_SHADOW_INCLUDED

// ENGINE-AGNOSTIC, like every header in this folder: HLSL intrinsics and our own headers only. No
// URP includes, no vertex/fragment semantics, no Core.hlsl texture macros. That means any of these
// can be included from a fragment shader, a compute shader or the voxelize raster alike.
//
// The engine boundary is the .shader / .compute ENTRY POINTS. VoxelLit.shader includes URP's
// Core.hlsl and Lighting.hlsl and calls GetMainLight(), then hands this library plain values.
// Guarded by Shaders/Compute/BufferGiCommonCanary.compute, which includes every header here and fails
// moment one acquires an engine dependency - do not "fix" that by adding an include to the canary.
//
// A Unity-shadowmap backend therefore CANNOT live here. URP's TransformWorldToShadowCoord /
// MainLightRealtimeShadow are engine calls, so that mode resolves in VoxelLit.shader and hands the
// resulting scalar in - the same shape as GetMainDirectLightingShadow already takes.


// THE MAIN-LIGHT SUN SHADOW, and nothing else. This header owns every resource, uniform and function
// behind BgiSampleSunShadow - the sole authority for the buffer-GI main light's shadow term, called
// once from VoxelLit's fragment.
//
// Split out of BufferGiRead.hlsl (S1 of docs/direct-shadow-extraction.md), which is a file-layout
// change and not a behavioural one: nothing here was edited on the way across. The shadow stopped
// depending on the GI solve when P6 moved sun visibility out of it into CSSunVisibility, so the only
// coupling left was that the two shared a file. Keeping them apart is what lets a second backend
// (Unity shadowmaps, a per-pixel raymarch) be added without touching the GI read at all.
//
// DEPENDENCY DIRECTION. This header must not include BufferGiRead.hlsl. The GI read includes THIS
// one, so the shadow can eventually exist without the GI but never the reverse. BgiSelectField moved
// down into BufferGiField.hlsl for exactly that reason - it is field geometry, shared by both, and
// owned by neither.

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

// Each field's baked SUN VISIBILITY: an R16 scalar volume at the SHADOW grid (_BgiShadowGrid), which
// is the occupancy resolution rather than the lighting one. Not slabbed, in either mode.
//
// It lived in the mirror's ALPHA, at the lighting grid, with six Z slabs in Cube so that a sub-voxel
// wall's two sides did not have to share one number. Both of those went away together: CSSunVisibility
// re-evaluates the scalar against the hi-res occupancy, and at that resolution the two sides of a wall
// are DIFFERENT TEXELS - there is no ambiguity left for a slab index to resolve.
//
// Still one tap, still hardware-filtered, still the same normalized uvw over the field's box - so
// raising the resolution changed nothing on this side except the offset scale below.
Texture3D<float>  _BgiSunVisTex;                 // fine field
SamplerState sampler_BgiSunVisTex;
Texture3D<float>  _BgiSunVisTexCoarse;           // coarse field
SamplerState sampler_BgiSunVisTexCoarse;

// Uniforms below stay `float`: they are loose globals (not a CBUFFER block), set from C# with
// SetGlobalFloat/SetGlobalVector, and a uniform load costs the same either way - the fp16 win is in
// the ALU, so they are narrowed at the point of use instead.
// Sun-shadow mode PER FIELD: 0 = off (caller falls back to its GetShadow source), 1 = baked
// (the pre-marched visibility CSBlur mirrored into the R16 sun-visibility texture), 2 = SDF (crisp
// per-pixel raymarch of the hi-res SDF, VoxelGI-parity, independent of the shadow-source keyword).
// The fragment picks fine vs coarse by whether the shading point is inside the fine volume.
int _BgiShadowModeFine;
int _BgiShadowModeCoarse;
// Baked shadow edge sharpening (see BgiSampleShadowTexture). 1 = passthrough / original behaviour.
float _BgiShadowSharpness;
// How far off the surface the baked shadow tap sits, in voxels. 0.5 = first air voxel's centre,
// 1.0 = the historical value.
float _BgiShadowNormalOffset;

// Baked sun visibility: ONE hardware trilinear tap of the displayed field's SUN-VIS texture, at the
// SHADOW grid. Fine field inside its box, coarse outside (its grid size = coarse voxel size * grid -
// only the voxel size is published).
//
// The offset steps SHADOW cells, not lighting voxels. That is the whole point of the finer texture:
// the shading point sits on a solid/air boundary, so 0.5 lands at the first shadow texel's centre in
// front of the surface, and at the occupancy resolution that is a few centimetres out rather than
// most of a metre. The over-reach that made a floor beside a tall occluder read shadowed scales down
// with the cell, so the same `_BgiShadowNormalOffset` is a far smaller world displacement than it was.
//
// Still a continuous offset rather than the GI tap's snap-to-layer: the stored value is a coverage
// FRACTION the sharpening below reconstructs an edge from, and snapping would quantise it.
//
// No Cube branch. The six Z slabs existed so one lighting-grid texel could answer for both sides of a
// sub-voxel wall; at the shadow grid those sides are different texels, so there is nothing to
// disambiguate and the read is the same single tap in both modes.
// Returns 1 (lit) outside the sampled field: no baked info is read as unshadowed.
half BgiSampleShadowTexture(float3 worldPos, float3 normal, bool insideFine)
{
    float3 origin   = insideFine ? _BgiGridOrigin   : _BgiCoarseOrigin;
    float3 gridSize = insideFine ? _BgiGridSize      : _BgiCoarseVoxelSize * (float)BGI_GRID;
    float3 shadowVox = gridSize / (float)BGI_SHADOW_GRID;
    // NORMAL-OFFSET BIAS, and it must clear a WHOLE texel on the dominant axis.
    //
    // `normal * shadowVox` moves less than one texel on EVERY axis for any normal that is not axis
    // aligned - a 45-degree wall gets 0.71 texels, and the trilinear footprint then still straddles
    // the solid layer behind the surface. That layer does not hold a neutral value: CSSunVisibility
    // marches solid texels too, from jittered origins inside their own geometry, so they store some
    // arbitrary partial coverage (0.25 on the Sponza wall this was found on). Blending a varying
    // fraction of that into a fully lit surface is textbook shadow acne, and because the fraction
    // depends on where the surface sits inside its texel it appears as soft MOTTLING rather than as a
    // uniform darkening.
    //
    // Scaling the normal so its largest component is exactly 1 makes `_BgiShadowNormalOffset` mean
    // "texels cleared on the dominant axis" whatever the orientation, while the other two axes move
    // proportionally. Same construction and the same reason as the Fast GI tap's offset above; the
    // shadow tap simply never got it, and it matters more here because the shadow grid is anisotropic
    // (0.169 x 0.117 x 0.267 m on Sponza - a 2.3x spread).
    float3 aN = abs(normal);
    float3 biasDir = normal / max(max(aN.x, aN.y), max(aN.z, 1e-4));
    float3 uvw = (worldPos + biasDir * shadowVox * _BgiShadowNormalOffset - origin) / max(gridSize, 1e-6);
    if (any(uvw < 0.0) || any(uvw > 1.0)) return 1.0h; // outside the field -> lit (no baked info)
    half a = insideFine
        ? (half)_BgiSunVisTex.SampleLevel(sampler_BgiSunVisTex, uvw, 0).r
        : (half)_BgiSunVisTexCoarse.SampleLevel(sampler_BgiSunVisTexCoarse, uvw, 0).r;

    // Sharpen the reconstructed edge. Near a shadow boundary the stored value is the voxel's sun
    // COVERAGE, and coverage is a local signed distance to that boundary - so re-centring on 0.5 and
    // steepening rebuilds an edge finer than the voxel it came from. Same SDF-font trick as
    // GetOccFieldShadow's _OccFieldDecode, and the reason it works here at all is that the solve now
    // stores a fraction: against the old 0/1 field this would only harden the staircase.
    // 1 = passthrough (identical to before). Costs one MAD.
    return saturate((a - 0.5h) * (half)_BgiShadowSharpness + 0.5h);
}

// Mode 5, ENGINE: the shadow this library cannot resolve.
//
// URP's TransformWorldToShadowCoord / MainLightRealtimeShadow are engine calls, and every header in
// this folder is engine-agnostic (BufferGiCommonCanary.compute fails the build the moment one is not).
// So this mode is a DECLARATION here and a resolution in VoxelLit.shader: the sampler below returns
// "not mine" and the .shader entry point, which already includes URP's Lighting.hlsl, supplies the
// value. That is the same boundary the whole package draws - only the entry points know URP exists.
//
// Fails open. If the mode is selected but the entry point does not resolve it (a shader that never
// compiled the URP shadow keywords, or a pipeline with main-light shadows off), the value stays 1.0
// and the surface is LIT. Every "no information" case in this path reads as lit, so a mistake shows
// up as a missing shadow rather than as black geometry.
#define BGI_SHADOW_MODE_ENGINE 5

// --- MODE 6: PER-PIXEL RAYMARCH ------------------------------------------------------------------
// A shadow ray per PIXEL against the hi-res occupancy, instead of a tap of a volume that was marched
// per TEXEL. At 128 the occupancy grid holds ~2.1M cells against ~2.1M pixels at 1920x1080, so this
// is not obviously the more expensive of the two - and it needs no reconstruction at all.
//
// Everything the architecture doc records about _BgiShadowSharpness - the +-half-texel clamp on the
// stored coverage, the lattice faceting a sharpened edge exposes, the rounded convex corners a
// distance field adds - is an artifact of rebuilding an edge from a lattice. None of it applies here:
// there is no lattice to reconstruct against, only a ray that hits or does not.
//
// The occupancy arrives as a Texture3D rather than the StructuredBuffer the compute passes march,
// because the shipping fragment declares no StructuredBuffer (WebGPU fails pipeline creation on a
// declared-but-unbound global). One uint per 32 cells along X - see CSBuildOccupancyMirror.
Texture3D<uint> _BgiOccupancyTex;         // fine field
Texture3D<uint> _BgiOccupancyTexCoarse;   // coarse field

// Hard cap on DDA steps. The compute march bounds itself at 3*BGI_OCC_GRID - 384 at 128, 768 at 256 -
// which is a compute budget, not a fragment one. A truncated ray returns LIT, matching every other
// "no information" case in this path: a mistake shows up as a missing shadow rather than as black
// geometry. Unbound it reads 0 and the mode produces no shadow at all, which is the same safe end.
int _BgiRaymarchMaxSteps;

// Cells at the START of the ray whose hits are ignored, so the surface cannot shadow itself.
//
// This replaces biasing the origin, which cannot be made to work: small enough to keep near-surface
// occluders is too small to clear the shading point's own cell (voxel-scale acne striping), and large
// enough to clear it displaces the origin so far that it leaves the structure doing the occluding -
// at occupancy 128 on Sponza one cell is 0.169 x 0.117 x 0.267 m, so a 1-cell normal offset lifts a
// point out from under an arcade vault and the whole arcade reads sunlit.
//
// Ignoring a BOUNDED count instead keeps the origin exactly on the shading point. Unbounded ("ignore
// until the first air cell") was tried and is far too permissive: it suppresses every occluder that
// belongs to the same connected structure as the receiver.
int _BgiRaymarchSkipCells;

// Solidity of one hi-res cell in the flat mirror. `wordCache`/`cachedWord` let a caller that steps
// along X reuse a fetched word - which is the whole reason the mirror is packed 32:1 on that axis.
//
// THE CACHE KEY IS THE WHOLE TEXEL COORDINATE, not just the slab. A word holds 32 cells along X for
// ONE (y, z) row, so `c.x >> 5` alone does not identify it: step in Y or Z and the slab is unchanged
// while the word is a different one entirely.
//
// Keying on the slab alone produced this mode's headline failure. The Playground sun is
// (0, 0.9397, 0.342) - no X component at all - so along any ray c.x never moved, the slab never
// changed, and the word fetched at the origin cell was reused for all 256 steps. Every step then
// tested the SHADING POINT'S OWN CELL: pixels landing in a solid cell read shadowed immediately,
// pixels landing in the air cell beside it read lit forever, and every sunlit face came out striped
// at the occupancy cell period (~5 cm). It survived a _BgiRaymarchSkipCells sweep to 24 cells
// (1.15 m) unchanged, which is what ruled out ordinary self-shadow acne: no exclusion DISTANCE can
// help when the same hit is re-reported at every distance.
bool BgiOccTexSolid(bool insideFine, int3 c, inout uint wordCache, inout int3 cachedWord)
{
    int3 wc = int3(c.x >> 5, c.y, c.z);
    if (any(wc != cachedWord)) {
        cachedWord = wc;
        wordCache = insideFine
            ? _BgiOccupancyTex.Load(int4(wc, 0)).x
            : _BgiOccupancyTexCoarse.Load(int4(wc, 0)).x;
    }
    return ((wordCache >> ((uint)c.x & 31u)) & 1u) != 0u;
}

// Amanatides-Woo over the hi-res grid, in grid units, with world-space anisotropy taken from the
// field's own box. Same walk as MarchOccupancyHiFrom in BufferGiVoxelData.hlsl - deliberately not
// shared with it, because that one reads a StructuredBuffer this stage may not declare, and merging
// them behind a macro would hide exactly the constraint that keeps them apart.
//
// Returns 1 (lit) on escape, on running out of steps, and when the ray never enters the grid.
half BgiRaymarchSunShadow(float3 worldPos, float3 normal, float3 lightDir,
                          bool insideFine, float3 origin, float3 gridSize)
{
    int grid = (int)BGI_OCC_GRID;
    float3 hiVox = gridSize / (float)grid;

    // SELF-SHADOW EXCLUSION. Applied to WHERE THE RAY STARTS, never to WHEN IT CROSSED A BOUNDARY.
    //
    // The region a surface can wrongly shadow itself in is the slab of its own voxelisation, roughly
    // one cell thick along its normal, so `_BgiRaymarchSkipCells` is still a count of cells measured
    // PERPENDICULAR to the surface and its serialized meaning is unchanged. What changed is how that
    // distance is enforced.
    //
    // It used to be a per-step predicate, `tCross * ndl >= skipDist`, and that is what made shadow
    // edges saw-toothed wherever the occluder sat inside the exclusion distance. The reason is a
    // swap of which side of the comparison is quantised:
    //
    //   Silhouette test (no skipDist involved): "which cell is the ray in when it crosses a fixed
    //   lattice plane". A CONTINUOUS quantity against a FIXED threshold -> the boundary on the
    //   receiver is exact, and stays a straight line down to the pixel.
    //
    //   Old per-step predicate: "is the last boundary crossing that landed in solid geometry farther
    //   than skipDist". tCross only takes values on the DDA's crossing lattice, so this is a
    //   LATTICE-QUANTISED quantity against a CONTINUOUS threshold. As the shading point slides along
    //   the surface the deciding crossing drifts, then jumps to the next one - a ramp, then a
    //   snap-back. Sawtooth, with a period of exactly one crossing on the dominant axis.
    //
    // Measured on Playground, fine field at occupancy 128: on the yellow wall directly beneath the
    // roof's overhang, the blocking cell walked (96, 96, 88) -> (96, 90, 88) - X and Z pinned, Y
    // decrementing once per 20 image rows - and the shadow edge sawtoothed at exactly that period.
    // The other edge in the same frame, cast by a wall 11 cells away and therefore decided by the
    // silhouette rather than by skipDist, was pixel-perfect straight in the same image, and did not
    // move at all when _BgiRaymarchSkipCells went 2 -> 4. One knob, two edges, one of them immune:
    // that is what identified the predicate rather than the lattice as the cause.
    //
    // So: advance the ORIGIN along the ray by the distance that clears the slab, and then count
    // everything. `skipDist / ndl` is the travel needed to gain `skipDist` perpendicular, which is
    // the same slope-correct quantity the predicate used, evaluated once instead of per step.
    //
    // This is NOT the normal-offset bias the header used to warn about, and the old warning does not
    // apply: that moved the ray SIDEWAYS off its own line, which is what lifted a shading point out
    // from under an occluder it was genuinely beneath. This slides the origin ALONG the ray, so the
    // set of occluders on the line is untouched - only the prefix that the exclusion was always meant
    // to discard is dropped.
    //
    // N.L <= 0 means the surface faces away from the sun: self-shadowed by definition, the geometric
    // gate already kills its direct term, so nothing is excluded there. As N.L -> 0 the advance
    // diverges and the start leaves the box, which returns LIT - the same end the old predicate
    // reached (it required tCross >= skipDist / ndl, which no crossing satisfied).
    float3 nUnit = normalize(normal);
    float ndl = dot(nUnit, lightDir);
    float cellAlongN = dot(abs(nUnit), hiVox);          // world size of one cell along the normal
    float skipDist = (float)max(_BgiRaymarchSkipCells, 0) * cellAlongN;
    float advance = (ndl > 1e-3) ? skipDist / ndl : 0.0;

    float3 p = (worldPos + lightDir * advance - origin) / max(gridSize, 1e-6) * (float)grid;
    if (any(p < 0.0) || any(p >= (float)grid)) return 1.0h;  // outside the field -> no information -> lit

    int3 cell = (int3)floor(p);
    uint wordCache = 0u; int3 cachedWord = int3(-1, -1, -1);

    // TEST THE STARTING CELL, before any step. This is the half of the fix that makes the edge sharp.
    //
    // The old loop stepped first and never tested its origin cell, which is correct only when the
    // origin is the shading point itself. Here the origin has already been advanced clear of the
    // surface, so the cell it lands in is ordinary scene geometry and skipping it would reintroduce
    // the quantisation by the back door: "is the first STEP into solid" depends on which axis happens
    // to be crossed first, which flips on the lattice. "Is the cell I am standing in solid" is a pure
    // function of the continuous start position, and that is what keeps the boundary a straight line.
    if (BgiOccTexSolid(insideFine, cell, wordCache, cachedWord)) return 0.0h;

    float3 inCell = p - (float3)cell;
    float3 d = lightDir / max(hiVox, 1e-6);
    int3 stepDir = int3(d.x >= 0 ? 1 : -1, d.y >= 0 ? 1 : -1, d.z >= 0 ? 1 : -1);
    float3 inv = 1.0 / max(abs(d), 1e-6);
    float3 tMax = inv * float3(d.x >= 0 ? 1.0 - inCell.x : inCell.x,
                               d.y >= 0 ? 1.0 - inCell.y : inCell.y,
                               d.z >= 0 ? 1.0 - inCell.z : inCell.z);

    int maxSteps = max(_BgiRaymarchMaxSteps, 0);
    [loop]
    for (int s = 0; s < maxSteps; s++) {
        if (tMax.x < tMax.y) {
            if (tMax.x < tMax.z) { cell.x += stepDir.x; tMax.x += inv.x; }
            else                 { cell.z += stepDir.z; tMax.z += inv.z; }
        } else {
            if (tMax.y < tMax.z) { cell.y += stepDir.y; tMax.y += inv.y; }
            else                 { cell.z += stepDir.z; tMax.z += inv.z; }
        }
        // ESCAPED THE VOLUME. Returned as LIT. 'Lit' is wrong for a ray that leaves an interior
        // sideways; 'shadowed' is wrong for an exterior wall whose ray genuinely leaves into open
        // sky. Covering the difference needs a source beyond the box - the coarse field. Measured in
        // Playground: 53.3% of air cells escape, but 85% of those leave through the TOP face where
        // 'lit' is right, so only 7.8% of rays are in the population this branch can get wrong.
        // See the S5 notes in docs/direct-shadow-extraction.md.
        if (any(cell < 0) || any(cell >= grid)) return 1.0h;
        if (BgiOccTexSolid(insideFine, cell, wordCache, cachedWord)) return 0.0h;
    }
    return 1.0h;   // budget spent without a hit: fail open (measured to happen for 0% of pixels)
}
// The buffer-GI main-light sun visibility for a shading point, from whichever per-field ShadowMode is
// selected. This function is the SOLE authority for the buffer-GI main-light shadow: Off (0) returns
// 1.0 = genuinely no sun shadow (full direct light), NOT a fall-through to any other shadow source;
// Baked (1) is one trilinear tap of the sun-visibility texture; Sdf (2) is the per-pixel SDF raymarch.
//
// It used to resolve a baked static AO from the same call, off a 4-tap _Surface read on the face
// plane. That is gone: a 32^3 openness scalar is the wrong spatial scale for contact occlusion, it
// double-counted against the gather's own occlusion, and it was the fragment's only StructuredBuffer
// consumer - so removing it makes this whole path purely texture-based (see the architecture doc).
//
// lightDir is the UNIT direction TOWARD the main light (used only by the Sdf mode's raymarch).
// `normal` MUST be the geometric (vertex) normal, never a normal-mapped one - it offsets the sample
// off the surface, and the voxel grid knows nothing about normal maps. Feeding it a per-texel normal
// makes the sampled point jump within a single flat face.
half BgiSampleSunShadow(float3 worldPos, float3 normal, float3 lightDir, out bool wantsEngineShadow)
{
    half shadow = 1.0h; // Off (mode 0) keeps this: no sun shadow, full direct light.
    wantsEngineShadow = false;

    bool insideFine; float3 origin, voxelSize; uint baseOffset;
    BgiSelectField(worldPos, insideFine, origin, voxelSize, baseOffset);
    int mode = insideFine ? _BgiShadowModeFine : _BgiShadowModeCoarse;

    // Shadow source per BufferGI field. Each is fully resolved here - no fall-through:
    //   Baked (1)          : one hardware-trilinear tap of the R16 SUN-VIS texture (see
    //                        BgiSampleShadowTexture). In Cube each Z slab carries its own face's
    //                        visibility, so a sub-voxel wall's two sides resolve separately and still
    //                        hardware-filtered - no StructuredBuffer face taps anywhere on this path,
    //                        which is the whole point on Adreno (a sampler tap is ~free, SSBO taps
    //                        dominate the fragment).
    //   Sdf (2)            : crisp per-pixel raymarch of the hi-res SDF (needs a baked SDF on the volume).
    //   OcclusionField (3) : the volume's baked per-direction occlusion field (needs its occlusion binder).
    //   Bitmask (4)        : the volume's baked directional occlusion bitmask (needs its occlusion binder).
    //   UnityShadowmap (5) : URP's own cascaded map - DECLARED here, resolved by the .shader entry point.
    //   Raymarch (6)       : a shadow ray per pixel against the hi-res occupancy mirror. No lattice to
    //                        reconstruct from, so none of the baked mode's sharpening applies.
    // The occlusion modes read the same _OccFieldTex / _BitmaskTex the material's GetShadow source uses,
    // so the matching occlusion binder must be active for their textures to be bound (like Sdf/SDF).
    // saturate() on the occlusion sources: keeps them in [0,1] and, crucially, maps a NaN (e.g. an
    // occlusion binder that isn't fully publishing its grid uniforms) to 0 rather than letting it
    // poison the whole fragment to black - a mis-bound source then reads as "fully shadowed", not a crash.
    if (mode == 1)      shadow = BgiSampleShadowTexture(worldPos, normal, insideFine);
    else if (mode == 2) shadow = GetShadowFromSdf(lightDir, worldPos, 1.0e+10f); // lightDir is unit
    else if (mode == 3) shadow = saturate(GetOccFieldShadow(worldPos, normal));
    else if (mode == 4) shadow = saturate(GetBitmaskShadow(worldPos, normal));
    else if (mode == BGI_SHADOW_MODE_ENGINE) wantsEngineShadow = true; // resolved by the caller; stays 1.0 here
    else if (mode == 6) {
        float3 gridSize = insideFine ? _BgiGridSize : _BgiCoarseVoxelSize * (float)BGI_GRID;
        shadow = BgiRaymarchSunShadow(worldPos, normal, lightDir, insideFine, origin, gridSize);
    }
    return shadow;
}

// Signature-compatible overload for callers that cannot resolve the engine shadow (anything that is
// not a URP .shader entry point). Mode 5 reads as LIT through this one, by the fail-open rule above.
half BgiSampleSunShadow(float3 worldPos, float3 normal, float3 lightDir)
{
    bool ignored;
    return BgiSampleSunShadow(worldPos, normal, lightDir, ignored);
}

#endif // LOTEC_VOXEL_SUN_SHADOW_INCLUDED
