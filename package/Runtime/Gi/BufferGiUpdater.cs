using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace Lotec.Lighting {
    /// <summary>
    /// Driver for the buffer-based GI (the textureless, cache-resident GI that runs behind the GI_VOXEL_BUFFER shader keyword).
    /// Owns the ComputeBuffers, voxelizes the scene mesh into the occupancy/albedo buffer once (GPU 3-axis
    /// raster, BufferGiVoxelize.shader), and runs the per-frame solve: inject (solid voxels
    /// emit/reflect) then gather (air voxels integrate 1 ray/frame with the temporal resolve fused
    /// in) then a blur pass. The lit shader reads it via BgiGatherIndirect (BufferGiRead.hlsl).
    ///
    /// All fields are a cubic grid whose resolution is this component's own _giResolution (snapped to a
    /// power of two so the shift/mask index math holds), independent of the volume's bake resolution
    /// (VoxelVolume._maxResolution, which the SDF/occlusion bakes use); the buffers resize when it
    /// changes. (Single fine cascade for now; a coarse cascade + scheduler is the planned next step.)
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    // The enabled updater owns the GI + tonemap keyword groups and the _ExposureLinear global (GiMethodSelector toggles
    // this component's enabled state to switch GI on/off).
    [AddComponentMenu("Lotec/Voxel Lighting/Buffer GI")]
    public class BufferGiUpdater : MonoBehaviour {
        // Concatenated fields: 0 = coarse (the big far volume), 1 = fine (the active volume). Coarse
        // is kept at slot 0 so any future fine fields stay contiguous (1..N-1) and just append.
        public const int FieldCount = 2;
        public const int CoarseField = 0;
        public const int FineField = 1;

        // Cubic grid resolution, derived from this component's own _giResolution (independent of the
        // volume's bake resolution) and snapped to a power of two (the index math is shift/mask based).
        // Instance state: it changes when _giResolution changes, forcing a buffer reallocation. Defaults
        // mirror the serialized 32^3 until SyncGridResolution runs.
        int _grid = 32;
        int _gridLog2 = 5;
        int _voxelCount = 32 * 32 * 32;   // Grid^3 per field
        int _totalVoxels = FieldCount * 32 * 32 * 32;

        /// <summary>Cubic grid resolution of each field (power of two, from _giResolution).</summary>
        public int Grid => _grid;
        /// <summary>log2(Grid) - the shift amount for the linear&lt;-&gt;3D index math.</summary>
        public int GridLog2 => _gridLog2;
        /// <summary>Voxels per field (Grid^3).</summary>
        public int VoxelCount => _voxelCount;
        /// <summary>Voxels across all concatenated fields (FieldCount * VoxelCount).</summary>
        public int TotalVoxels => _totalVoxels;

        // Hi-res OCCUPANCY grid: a second, finer subdivision of the SAME world box, carrying solidity
        // and nothing else. Instance state like _grid, resolved by SyncOccResolution.
        int _occGrid = 128;
        int _occGridLog2 = 7;
        // Words of hi-res occupancy per field. 4x4x4 blocks of two contiguous uints, so
        // (occGrid/4)^3 * 2. See BGI_OCC_BLOCK_LOG2 in BufferGiField.hlsl for why blocks and why 4.
        int _occWordsPerField;

        /// <summary>Cubic resolution of the hi-res occupancy grid (power of two, &gt;= <see cref="Grid"/>).</summary>
        public int OccGrid => _occGrid;
        /// <summary>log2(OccGrid).</summary>
        public int OccGridLog2 => _occGridLog2;
        /// <summary>4x4x4 occupancy blocks per axis.</summary>
        public int OccBlockGrid => _occGrid >> 2;
        /// <summary>Hi-res occupancy words in ONE field (two per 4x4x4 block).</summary>
        public int OccWordsPerField => _occWordsPerField;
        /// <summary>Hi-res occupancy words across all concatenated fields.</summary>
        public int TotalOccWords => FieldCount * _occWordsPerField;

        // Baked sun-shadow grid: a THIRD resolution over the same box, never finer than the occupancy
        // grid it is marched against.
        int _shadowGrid = 128;
        int _shadowGridLog2 = 7;
        /// <summary>Cubic resolution of the baked sun-visibility texture (power of two, &lt;= <see cref="OccGrid"/>).</summary>
        public int ShadowGrid => _shadowGrid;
        // 1-bit/voxel occupancy at the hi-res grid, blocked; and its OR-downsample onto the lighting
        // grid. For the containment check and the debug viewer.
        public ComputeBuffer OccupancyHiBuffer => _occupancyHiBuffer;
        public ComputeBuffer OccupancyTraversalBuffer => _occupancyTraversalBuffer;
        /// <summary>Bake scratch: real solids plus the cell behind each surfaced voxel. Exposed for
        /// analysis - it is the input to the gradient normal, so comparing a candidate normal source
        /// against anything else is comparing against the wrong thing.</summary>
        public ComputeBuffer OccupancyThickBuffer => _occupancyThickBuffer;

        // Per-field voxel sun-shadow mode (matches _BgiShadowMode* in BufferGiRead.hlsl). Each field picks
        // its main-light sun shadow explicitly - nothing is a hidden fall-through:
        //   Off (0)            : genuinely NO sun shadow (full direct light).
        //   Baked (1)          : the solve's pre-marched sun visibility (radiance.w), interpolated - soft, cheap.
        //   Sdf (2)            : crisp per-pixel raymarch of the hi-res SDF; needs an SDF baked on the volume.
        //   OcclusionField (3) : the volume's baked per-direction occlusion field.
        //   Bitmask (4)        : the volume's baked directional occlusion bitmask.
        // Sdf/OcclusionField/Bitmask each need their matching source present on the volume (a baked SDF,
        // or the occlusion-field / bitmask binder) so the textures they read are bound - otherwise they
        // read unbound data. They also have no effect where their source doesn't reach (e.g. the coarse
        // field beyond the fine SDF bounds).
        public enum ShadowMode { Off = 0, Baked = 1, Sdf = 2, OcclusionField = 3, Bitmask = 4 }

        // How many DIRECTIONS of outgoing radiance each solid voxel stores (matches _BgiRadianceDirs).
        // A voxel is one cell, but the geometry inside it can face more than one way: any wall, floor
        // slab or railing thinner than a voxel puts BOTH its faces in the same cell, and the voxelizer
        // resolves that with last-write-wins (BufferGiVoxelize, mesh-normal mode). One stored normal +
        // one radiance then means one side is right and the other reads the wrong value - or, today,
        // gets rejected by the gather's front/back test and falls back to the (black) ambient floor.
        //   Single (1) : one radiance per voxel - the original behaviour, cheapest, thin walls wrong.
        //   Cube (6)   : the six world axes (an "ambient cube"). Gives a thin wall's two faces their own
        //                values, ALSO fixes PERPENDICULAR faces sharing a cell - arcade bases, arch
        //                springings - and makes the field directional, so the fragment blends the 3 faces
        //                of its hemisphere instead of picking one. That directionality is also what lets
        //                the baked sun shadow stay a smooth texture tap on a sub-voxel wall (each slab
        //                carries its own face's visibility), which is why there is no cheaper two-slot
        //                mode in between: one existed, and its shadow staircased.
        //                Costs 6x the irradiance storage, a wider read stride, and 3 taps instead of 1.
        // Neither fixes SPATIAL ambiguity: two surfaces facing the SAME way in one cell still collide,
        // and everything still reports at the cell centre. That is bounded by voxel size, not direction
        // count - raising the resolution or moving to surface storage is the only cure for it.
        public enum RadianceDirections { Single = 1, Cube = 6 }

        /// <summary>
        /// How the fragment filters its irradiance tap in SINGLE mode (no effect in Cube, which
        /// already taps per axis). Both read the same one-value-per-voxel field - this is purely the
        /// fetch, so switching costs no storage, no re-bake and no re-solve.
        /// Fast: one hardware-trilinear tap at a continuous offset along the normal - the cheapest read
        /// in the package, and the reason Single exists.
        /// AxisSnapped: up to three taps, each snapped to the cell centre one step along its own axis
        /// and weighted by n^2. See BgiSampleFieldTexture for what that buys.
        /// </summary>
        public enum SingleTapFilter { Fast = 0, AxisSnapped = 1 }

        // Which occupancy level the SOLVE's rays march (matches _BgiSolveMarchLevel in
        // BufferGiSolve.compute). All three stay resident so the comparison is a uniform write rather
        // than a recompile; the branch is uniform across the dispatch and sits outside the march loop.
        public enum SolveMarchLevel { Coarse = 0, Flat = 1, TwoLevel = 2 }

        /// <summary>
        /// ANALYSIS views for the lit shader: show one term of the shaded result on its own, so an
        /// artifact can be attributed to the GI bounce, the sun visibility or the baked AO rather
        /// than guessed at from the composite. Must match the _BgiDebugView cases in VoxelLit.
        /// </summary>
        public enum DebugView {
            Off = 0,
            GiOnly = 1,        // indirect bounce, tonemapped (HDR)
            SunVisibility = 2, // raw 0..1 greyscale, no display transform
            // 3 was Ao, removed with the baked openness scalar. The remaining values keep their
            // numbers so a serialized selection does not silently become a different view.
            DirectOnly = 4,    // direct term only, tonemapped (HDR)
            /// <summary>Fraction of the GI tap's trilinear footprint that lands on SOLID cells -
            /// the contamination the in-plane leak is made of. Black = the pixel physically cannot
            /// leak; white = its GI came entirely from shell texels. Raw 0..1.</summary>
            GiSolidWeight = 5
        }

        public static BufferGiUpdater Instance { get; private set; }

        [Header("Solve")]
        [Tooltip("Total ray budget per voxel (quality): the field is a progressive average that " +
                 "accumulates rays until it reaches this many, then the solve idles. Quality depends " +
                 "on total rays, so bigger = cleaner. It's reached after maxSamples/samplesPerFrame frames.")]
        [Min(1)][SerializeField] int _maxSamples = 512;
        [Tooltip("Samples (rays) gathered per voxel per frame - a PERFORMANCE knob: it spends the " +
                 "maxSamples budget over fewer/more frames but does not change the converged result.")]
        [Min(1)][SerializeField] int _samplesPerFrame = 1;
        [Tooltip("Ease-in exponent for the displayed fade / light-change reveal. Higher keeps the " +
                 "noisy early accumulation frames hidden and ramps the reveal up later (1 = linear). " +
                 "Raise it when using few rays/frame (noisier early frames).")]
        [Range(1f, 8f)][SerializeField] float _confidenceCurve = 3f;
        [Tooltip("Keep solving every frame even after the field settles. Off (recommended): the " +
                 "solve idles once settled and only wakes when the sun changes or the scene is " +
                 "re-baked, so a static scene costs no GI compute.")]
        [SerializeField] bool _continuousGi;
        [Tooltip("Luminance ceiling for a single gathered bounce, to suppress emitter fireflies. " +
                 "0 disables.")]
        [Min(0f)][SerializeField] float _giFireflyClamp = 8f;
        [Tooltip("Irradiance color used for voxels inside geometry: a gather ray hitting the BACK " +
                 "of a surface contributes this instead of the surface's room-lit value. Voxels " +
                 "fully enclosed in geometry converge to this color. Black = dark interiors.")]
        [SerializeField] Color _ambientFloor = Color.black;
        [Tooltip("Non-physical 'reach' fill: how far light spreads into shadow. 1 = off (physical); " +
                 "higher weights DISTANT gather hits up (toward this multiplier at the grid diagonal), " +
                 "so bright surfaces seen from deep in shadow bleed more light in. Applied only to the " +
                 "displayed field, not the bounce feedback, so it can't diverge - but it fights " +
                 "auto-exposure (a brighter field pulls exposure down).")]
        [Min(1f)][SerializeField] float _reachBoost = 1f;
        [Tooltip("Grow every voxelized surface one voxel INWARD (opposite its normal), so a wall is " +
                 "solid-backed instead of a 1-voxel hollow shell. Purely a leak control - it answers " +
                 "'how much of the grid does the surface occupy', and has no bearing on normals.\n " +
                 "Why turn it on: a sub-voxel occluder (curtain, banner, railing) occupies one cell " +
                 "with lit air on BOTH sides, and the shell dilation fills that cell from both sides " +
                 "at equal weight - so surfaces on the dark side read the lit side's light straight " +
                 "through it. Thickening makes the occluder opaque in the grid and the leak stops.\n " +
                 "Why not: ALL thin geometry gains bulk (measured +73% solid cells in Sponza), which " +
                 "can close narrow gaps and read as bloating on foliage or railings. It also makes " +
                 "thin walls no longer thin, which retires the two-sided (Cube) handling for them.\n " +
                 "Changing it re-bakes.")]
        [SerializeField] bool _thickenWalls;
        [Tooltip("Sun-shadow for the FINE volume (the active, detailed field). None fall through - each " +
                 "is explicit.\n Off: no sun shadow at all - full direct light.\n Baked: the solve's " +
                 "pre-marched sun visibility, interpolated across the surface - soft and cheap (no " +
                 "per-pixel ray).\n Sdf: a crisp per-pixel raymarch of the hi-res SDF - needs an SDF " +
                 "baked on the volume.\n OcclusionField / Bitmask: the volume's baked occlusion source - " +
                 "needs the matching occlusion binder active on the volume.")]
        [SerializeField] ShadowMode _fineShadow = ShadowMode.Off;
        [Tooltip("Sun-shadow for the COARSE volume (the big far field the SDF shadow can't reach). None " +
                 "fall through - each is explicit.\n Off: no sun shadow at all - full direct light.\n " +
                 "Baked: the solve's pre-marched sun visibility, interpolated - the cheap way to get far " +
                 "shadows.\n Sdf: has no effect here (the SDF only covers the fine bounds); use Baked for " +
                 "the far field.\n OcclusionField / Bitmask: the volume's baked occlusion source, if its " +
                 "binder covers the far field.")]
        [SerializeField] ShadowMode _coarseShadow = ShadowMode.Off;
        [Tooltip("Baked shadow mode ONLY: stratified sun rays per texel of the shadow texture. This is " +
                 "the setting that controls what the baked sun shadow LOOKS like - supersampling is " +
                 "what turns a per-texel bit into the coverage fraction Baked Shadow Sharpness needs " +
                 "to reconstruct an edge, and at 1 the field is strictly binary.\n " +
                 "It only ever affects texels ON a shadow boundary. Measured on Sponza at a 128 shadow " +
                 "grid: 1 leaves 0.00% of texels fractional, 4 leaves 1.91%, 16 leaves 2.92%, and the " +
                 "field mean does not move. Cost is linear and paid only when the sun moves: 330 / 621 " +
                 "/ 1497 ms to re-march the volume.")]
        [Range(1, 16)][SerializeField] int _sunShadowSamples = 4;
        [Tooltip("Stratified sun rays for the direct term a SOLID voxel then BOUNCES (CSInject). A " +
                 "different pass at a different resolution from the setting above: this one runs on " +
                 "the GI grid, where a voxel is metres across, so the shadow texture's resolution " +
                 "buys it nothing.\n " +
                 "Not a look setting - it feeds indirect light, so changing it moves overall " +
                 "brightness rather than shadow edges. Dropping it to 1 measured 4.90% brighter " +
                 "overall on Sponza (96% of pixels), because a centre ray reads 'lit' too often near " +
                 "a shadow boundary.")]
        [Range(1, 16)][SerializeField] int _injectSunSamples = 4;
        [Tooltip("Baked shadow mode ONLY: steepens the shadow edge the fragment reconstructs from the " +
                 "voxel grid. Near a boundary the stored value is the voxel's sun coverage, which is a " +
                 "local distance to that boundary - so steepening it rebuilds an edge finer than the " +
                 "texel. 1 = off. The sun-visibility pass always supersamples, so there is always a " +
                 "real fraction here to steepen. Too high re-introduces hard texel edges.")]
        [Range(1f, 16f)][SerializeField] float _bakedShadowSharpness = 1f;
        [Tooltip("Baked shadow mode ONLY: how far off the surface the shadow tap sits, in SHADOW " +
                 "texels (not lighting voxels - the shadow texture has its own, much finer grid).\n " +
                 "1.0 is the MINIMUM that reconstructs correctly, and the floor of the range for that " +
                 "reason. Half a texel puts a POINT sample at the first air texel's centre, but the " +
                 "tap is TRILINEAR: its footprint still reaches a full texel back into the solid " +
                 "layer, and solid texels do not hold a neutral value (CSSunVisibility marches them " +
                 "too, from origins inside their own geometry, so they store an arbitrary partial " +
                 "coverage). Blending a varying fraction of that into a lit surface is shadow acne, " +
                 "and it reads as soft MOTTLING because the fraction depends on where the surface " +
                 "sits inside its texel. Measured on a sunlit Sponza wall: 0.5 left 91% of it " +
                 "spuriously dark, 1.0 leaves 3%.\n " +
                 "Raise toward 2 if surfaces still self-shadow; the cost is shadows detaching from " +
                 "their casters, which at this resolution is centimetres rather than most of a metre.")]
        [Range(1f, 3f)][SerializeField] float _shadowNormalOffset = 1f;
        [Tooltip("Directions of radiance stored per voxel - what a voxel can say about geometry that " +
                 "faces more than one way inside it.\n " +
                 "Single: one value; a wall thinner than a voxel gets one side right and the other dark.\n " +
                 "Cube: the six world axes. Gives a thin wall's two faces their own values, fixes " +
                 "perpendicular faces sharing a cell, and makes the field directional (so the baked sun " +
                 "shadow also resolves per side), at 6x the irradiance storage and 3 texture taps " +
                 "instead of 1.\n " +
                 "Changing this reallocates and restarts the accumulation.")]
        [SerializeField] RadianceDirections _radianceDirections = RadianceDirections.Single;
        [Tooltip("Single mode ONLY (Cube already taps per axis, and ignores this): how the fragment " +
                 "FETCHES its irradiance. Same field, same storage - only the filter changes, so " +
                 "switching is free and instant (no re-bake, no re-solve).\n " +
                 "Fast: one trilinear tap at a continuous offset along the normal. The cheapest read in " +
                 "the package. Where a surface sits mid-cell its footprint still catches the solid cell " +
                 "behind - and on a wall thinner than a voxel that cell is shared with the far face, so " +
                 "the two sides bleed into each other.\n " +
                 "AxisSnapped: up to three taps, each snapped to the cell centre one step along its own " +
                 "axis and weighted by n^2. The solid cell behind carries exactly zero weight, and the " +
                 "hard-edged patches Fast can show across curved/carved detail go away. An axis-aligned " +
                 "face still costs ONE tap (the other two weights are ~0 and are skipped); only swept " +
                 "normals pay for 2-3.\n " +
                 "Neither fixes the thin-wall bounce leak or the shared AO - those are baked before a " +
                 "pixel is drawn and need Cube (or a two-voxel-thick wall).")]
        [SerializeField] SingleTapFilter _singleTapFilter = SingleTapFilter.Fast;
        [Tooltip("Snap the GI tap to a voxel centre on any IN-PLANE axis whose trilinear pair spans a " +
                 "one-voxel wall - the light that bleeds sideways through a sub-voxel curtain or " +
                 "railing. The tap filter above clears the solid layer BEHIND the surface; this clears " +
                 "the one BESIDE it, and the two compose (this also applies in Cube).\n " +
                 "The condition is binary and engages exactly where it is a no-op, so it does not " +
                 "introduce the blockiness a tuned threshold would. Costs one point Load of the baked " +
                 "neighbour-solidity mask per shaded pixel, which is why it is a keyword and off by " +
                 "default.")]
        [SerializeField] bool _inPlaneSnap;
        [Tooltip("ANALYSIS: mute all direct lighting (sun + point + spot) in VoxelLit, leaving only " +
                 "the indirect bounce (and emission) on screen. Direct light is normally an order of " +
                 "magnitude brighter than the bounce, so it buries exactly the differences a GI A/B is " +
                 "trying to measure - and makes a leak through a thin wall impossible to attribute to " +
                 "the bounce rather than the sun.\n " +
                 "The SOLVE is untouched: the GI still receives and bounces the sun, so what remains is " +
                 "the bounce itself. Auto-exposure is unaffected too (it measures the GI field, not the " +
                 "framebuffer), so an A/B pair stays exposure-matched with this on.\n " +
                 "Costs one multiply in the lit shader and no extra variant. Leave off for normal rendering.")]
        [SerializeField] bool _muteDirectLighting;

        [Tooltip("ANALYSIS: replace the shaded result with ONE of its terms, to see which one is " +
                 "producing an artifact. Off = normal shading.\n " +
                 "GiOnly / DirectOnly are HDR radiance, so they keep exposure + tonemap and read like " +
                 "the normal image with the other term removed.\n " +
                 "SunVisibility / Ao are 0..1 scalars, so they bypass the display transform entirely " +
                 "and read as literal greyscale - the same number the Buffer GI Debug cubes show in " +
                 "SunVisibility mode, but resolved PER PIXEL through the actual fragment tap. That is " +
                 "the pairing that separates a bake problem from a read problem: if the cubes are " +
                 "crisp black/white but this view ramps between them, the leak is in the read.\n " +
                 "Pure fragment state - no solve restart, no rebake, so it can be flipped mid-A/B.")]
        [SerializeField] DebugView _debugView = DebugView.Off;

        [Header("Lighting")]
        [Tooltip("Display transform (exposure + tonemap operator), with optional auto-exposure. " +
                 "Published as the _ExposureLinear global (exp2 of the EV, precomputed) plus the " +
                 "TONEMAP_* keyword; the lit shader applies it whenever GI is on. Set explicitly so a " +
                 "stale value can't darken the image.")]
        [SerializeField] AutoExposure _exposureControl = new AutoExposure();

        [Header("Setup")]
        [Tooltip("Voxel resolution of the GI grid - occupancy AND every lighting field, one shared grid. " +
                 "Independent of the volume's bake resolution (VoxelVolume._maxResolution, which the SDF/" +
                 "occlusion bakes use). The solve cost scales ~resolution^3, so this is the main perf lever. " +
                 "Sharp sun shadows come from the SDF shadow mode (which stays at the volume's full " +
                 "resolution), so this can be low without softening shadows. Snapped to a power of two, " +
                 "clamped 4..256; a change reallocates the buffers and re-bakes.")]
        [Min(4)][SerializeField] int _giResolution = 32;
        [Tooltip("Voxel resolution of the OCCUPANCY grid - geometry only, no lighting. Independent of " +
                 "GI Resolution and normally much higher: solidity is 0.125 bits per voxel against the " +
                 "irradiance field's 8-48 BYTES, so accuracy here costs neither rays nor convergence " +
                 "time. Snapped to a power of two, clamped 64..256, and never below GI Resolution.\n " +
                 "64: Quest / WebGL (64 KB). 128: desktop default (512 KB). 256: high-end PC (4 MB).\n " +
                 "Changing it reallocates and re-bakes. The disk bake stores whatever it was baked at " +
                 "and is OR-downsampled at load, so ONE asset serves every platform.")]
        [Min(64)][SerializeField] int _occupancyResolution = 128;
        [Tooltip("Occupancy resolution used on mobile / Quest / WebGL instead of the value above. 0 = " +
                 "no override. This is a LOAD-TIME downsample of the same bake asset, not a second " +
                 "bake - so it costs a build variant of nothing.")]
        [SerializeField] int _occupancyResolutionMobile = 64;
        [Tooltip("Voxel resolution of the baked SUN-SHADOW texture. Its own setting because sun " +
                 "visibility and geometry have very different budgets: 2 bytes a texel against " +
                 "occupancy's 0.125 bits, so 256 is 67 MB of shadow next to 4 MB of occupancy.\n " +
                 "Clamped to the Occupancy Resolution and never above it - the shadow is evaluated " +
                 "by marching the occupancy field, so detail beyond it would be fabricated.\n " +
                 "This is what makes the Baked shadow sharp: it is re-evaluated at this resolution " +
                 "when the sun moves, not upsampled from the 32^3 solve.")]
        [Min(64)][SerializeField] int _shadowResolution = 128;
        [Tooltip("Sun-shadow resolution used on mobile / Quest / WebGL instead of the value above. " +
                 "0 = no override. At 64 the texture is 1 MB for both fields; at 128 it is 8.4 MB.")]
        [SerializeField] int _shadowResolutionMobile = 64;
        [Tooltip("Which occupancy level the SOLVE's rays are traced against (gather rays, sun rays, " +
                 "point/spot shadow rays). This is the accuracy of the GI itself, not of the baked " +
                 "shadow texture, which always marches the hi-res grid.\n " +
                 "Coarse: the 32^3 lighting grid - a sub-voxel curtain casts a whole-voxel shadow.\n " +
                 "Flat: straight over the hi-res occupancy. Correct, and the slowest.\n " +
                 "TwoLevel: the same result as Flat, skipping empty lighting cells wholesale.\n " +
                 "The origin cell stays exempt at LIGHTING resolution at every level, so a surface " +
                 "voxel never occludes its own rays.")]
        [SerializeField] SolveMarchLevel _solveMarchLevel = SolveMarchLevel.Flat;
        [Tooltip("Gate CSBlur's shell dilation on the GROWN occupancy: a solid voxel takes its " +
                 "displayed value only from air no surface grew into, so it cannot be filled from the " +
                 "far side of the wall or roof beside it.\n " +
                 "Why on: at a concave corner the stored normal is the LIGHTING-grid occupancy " +
                 "gradient, so it points at whichever air volume is biggest - the sky, outside a " +
                 "closed room - and the dilation fills the corner cell from outdoors. Measured on " +
                 "Playground's wall/ceiling junction: 0.045 against the 0.24 of the room it borders, " +
                 "which is the dark line down every corner. With the gate on it reads 0.252.\n " +
                 "Why off: the grown set is BGI_THICKEN's geometry, so it inherits that setting's " +
                 "assumption that growing one voxel inward never closes anything that matters. " +
                 "Off restores the pre-gate behaviour exactly - it is a display-side read, so the " +
                 "raster, the air field and every ray are byte-identical either way.")]
        [SerializeField] bool _grownDilationGate = true;
        // FormerlySerializedAs: this was _computeShader until the bake kernels moved to their own
        // asset, at which point "the compute shader" stopped naming one of two.
        [FormerlySerializedAs("_computeShader")]
        [SerializeField] ComputeShader _solveShader;
        [Tooltip("BufferGiBake.compute - the bake-time derive passes (occupancy, surface word, air " +
                 "distance, baked lights). Kept separate from the solve shader so that what runs PER " +
                 "FRAME is a property of the file, not of a comment. Auto-resolved by name when empty.")]
        [SerializeField] ComputeShader _bakeShader;
        [Tooltip("Shader 'Hidden/Lotec/BufferGiVoxelize' - GPU 3-axis rasterizer that voxelizes " +
                 "scene meshes into the occupancy/albedo buffer.")]
        [SerializeField] Shader _voxelizeShader;
        // The per-level field inputs (coarse field, detailed fields, disk bakes) used to be serialized
        // here, but this updater is a persistent bootstrap-scene singleton and those reference per-level
        // scene objects/assets. They now live on a BufferGiFields in the level scene, resolved from the
        // active volume's scene when the volume changes (see Update). Null when no level provides one.
        BufferGiFields _fields;

        /// <summary>The active level's Buffer GI field provider (null when no loaded level supplies one).</summary>
        public BufferGiFields Fields => _fields;
        /// <summary>Fine fields the editor bake button voxelizes (in addition to the coarse field).</summary>
        public IReadOnlyList<MeshBounds> DetailedFields =>
            _fields != null ? _fields.DetailedFields : System.Array.Empty<MeshBounds>();
        /// <summary>Coarse-field MeshBounds (null = fine only), for the editor bake button.</summary>
        public MeshBounds CoarseBounds => _fields != null ? _fields.CoarseField : null;
        /// <summary>Disk bakes uploaded instead of runtime voxelization; the editor bake button rewrites these.</summary>
        public List<BufferGiBakeAsset> BakeAssets => _fields != null ? _fields.BakeAssets : null;

#if UNITY_EDITOR
        /// <summary>Editor bake helper: bind the per-level provider so CoarseBounds/CoarseOrigin/Size and
        /// the coarse voxelize use it immediately, without waiting for the Update that normally resolves it.</summary>
        public void EditorBindFields(BufferGiFields fields) => _fields = fields;
#endif

        ComputeBuffer _materialBuffer;
        ComputeBuffer _radianceBuffer;
        ComputeBuffer _irradianceBuffer;
        ComputeBuffer _irradianceBlurBuffer;
        ComputeBuffer _surfaceBuffer; // per-voxel surface word (normal + reserved bits); always present
        bool _thickenWallsBaked;      // thickening the current bake used (a rebake-on-toggle input)
        // Field bounds the current voxelization used; SyncBakeInputs re-voxelizes when they change
        // (same-volume geometry edit / reassigned coarse field), so display/solve tweaks don't.
        Vector3 _bakedFineOrigin, _bakedFineSize, _bakedCoarseOrigin, _bakedCoarseSize;
        // The VoxelLights holders the baked-light injection reads. Scene-wide (the coarse field spans
        // every detailed volume, so it has to carry their lights too); refreshed on a volume switch and
        // on every voxelize.
        readonly List<VoxelLights> _lightHolders = new List<VoxelLights>();
        // On/off + colour/intensity of those lights as last INJECTED. A change re-dispatches the inject
        // instead of re-voxelizing, which is sound because albedo - hence occupancy and everything
        // derived from it - is toggle-invariant by construction (see LightEmissionBake.Inject).
        int _bakedLightState;
#if UNITY_EDITOR
        // Membership + positions as last VOXELIZED, plus the poll clock for it. Editor-only: unlike the
        // state above, a light that was added or moved needs cells re-stamped that the inject cannot
        // un-stamp, so it costs a full re-voxelization - an authoring action, and at runtime a light that
        // moves is a realtime light, not a baked one. Sampled a few times a second (nothing raises an
        // event for it, and the refresh is a scene-wide search) rather than every pumped editor frame.
        const double BakedLightPollInterval = 0.5;
        int _bakedLightLayout;
        double _nextBakedLightPoll;
#endif
        // 1-bit/voxel occupancy bitfield (uint packs 32 voxels): 4 KB per field, the hot solidity
        // data every DDA step / gate / fragment tap reads. Derived from _Material by CSBuildSurface.
        ComputeBuffer _occupancyBuffer;
        // Same 1-bit/voxel layout, but with each surfaced voxel's BACKING cell filled in. Bake-time
        // scratch read by OccupancyNormal only - see _OccupancyThick in BufferGiBake.compute for why the
        // gradient needs a backed wall that the real occupancy cannot supply.
        ComputeBuffer _occupancyThickBuffer;
        // GROWN occupancy, 1 bit/voxel on the LIGHTING grid: every rasterized voxel plus the one behind
        // each FRAGMENT along its triangle normal, written by the voxelizer to its own UAV. A raster
        // product like _Material, so it is captured into and uploaded from the bake asset. Read by
        // CSBlur alone - see _OccupancyGrown in BufferGiVoxelData.hlsl for why it is not _OccupancyThick
        // and not BGI_THICKEN.
        ComputeBuffer _occupancyGrownBuffer;
        // Hi-res occupancy (4x4x4 blocks, two uints each) at _occGrid, and its OR-downsample onto the
        // lighting grid - the always-hot coarse level a two-level march reads first. Both are bake
        // products; nothing per-frame writes them.
        ComputeBuffer _occupancyHiBuffer;
        ComputeBuffer _occupancyTraversalBuffer;
        uint[] _occupancyClear;
        uint[] _occHiClear;      // TotalOccWords zeros (the raster only ORs, so it needs a cleared target)
        uint[] _occHiReadback;   // TotalOccWords scratch for the capture / containment readback
        uint[] _grownReadback;   // TotalVoxels/32 scratch for the grown-bitfield capture readback
        uint[] _grownUpload;     // TotalVoxels/32 scratch assembling both fields' grown slices for upload
        uint[] _occHiUpload;     // TotalOccWords scratch assembled from field assets at load
        // Occupancy resolution the current buffers were allocated with, so a settings change is caught
        // the same way a radiance-stride change is.
        int _allocatedOccGrid;
        const string ThickenKeyword = "BGI_THICKEN";
        bool _materialBaked;
        // The fine field's volume (the manager's active volume); its Bounds already carry the
        // volume's own border, so the fine grid uses them as-is.
        VoxelVolume _volume;
        // Baked occlusion sources on the fine volume, resolved on volume switch. The OcclusionField /
        // Bitmask ShadowModes publish these on demand (SetGlobals); the holders no longer self-drive.
        VoxelOcclusionField _occField;
        VoxelOcclusionBitmask _occBitmask;
        Material _voxelizeMaterial;
        uint[] _materialClear;   // TotalVoxels zeros (whole-buffer clear)
        uint[] _fullReadback;    // TotalVoxels scratch for whole-buffer GetData during per-field capture
        uint[] _uploadMaterial;  // TotalVoxels scratch assembled from field assets, uploaded at load
        uint[] _uploadSurface;   // TotalVoxels scratch assembled from field assets, uploaded at load
        int _clearKernel = -1;
        int _injectKernel = -1;
        int _gatherKernel = -1;
        int _blurKernel = -1;
        RenderTexture _irradianceTex;          // fine field's blurred irradiance as a Texture3D (default read source)
        RenderTexture _irradianceTexCoarse;    // coarse field's blurred irradiance as a Texture3D
        // Baked sun visibility, split out of the irradiance mirror's alpha into its own R16 volume per
        // field (same dimensions, so Cube keeps its per-slab values). See _BgiSunVisTexWrite in
        // BufferGiSolve.compute for why it is a texture of its own rather than a channel.
        RenderTexture _sunVisTex;              // fine field
        RenderTexture _sunVisTexCoarse;        // coarse field
        // Each field's 7-bit neighbour-solidity mask at the LIGHTING grid (R8_UInt), the gate for the
        // in-plane snap. Built once per bake by CSBuildNeighbourMask - it is pure geometry, so it must
        // not be rebuilt when the sun moves. Point-loaded, never filtered.
        RenderTexture _neighbourMaskTex;       // fine field
        RenderTexture _neighbourMaskTexCoarse; // coarse field
        // Radiance slots per voxel the CURRENT _radianceBuffer was allocated with. Compared against
        // RadianceSlots each frame (SyncRadianceDirections) to catch an inspector/script mode change.
        int _allocatedRadianceSlots;
        int _allocatedIrradianceSlots;
        // Tap filter the BGI_TAP_AXIS_SNAPPED keyword currently reflects. Nullable so the FIRST sync
        // always publishes: global keywords survive play-mode exits and domain reloads, so "the field
        // says Fast" is not evidence that the keyword is off.
        SingleTapFilter? _appliedTapFilter;
        int _sunVisKernel = -1;
        int _initFineKernel = -1;
        int _averageLuminanceKernel = -1;
        int _buildOccupancyKernel = -1;
        int _buildTraversalMipKernel = -1;
        int _buildNeighbourMaskKernel = -1;
        int _buildNormalOccupancyKernel = -1;
        int _buildSurfaceKernel = -1;
        int _buildAirDistanceKernel = -1;
        int _injectBakedLightsKernel = -1;
        // Air-distance relaxation passes at bake (one voxel of city-block reach each). MUST match
        // BGI_MAX_AIR_DIST in BufferGiField.hlsl so the whole capped field converges.
        const int AirDistancePasses = 5;
        // The baked sun shadow needs re-marching: set at allocation and after every bake, cleared once
        // CSSunVisibility has run. A sun MOVE is caught separately by HasSunChanged.
        bool _sunVisDirty = true;
        bool _resetFineField;
        // Zero EVERY field's dynamic slices before the next solve (fresh/re-enabled buffers). Deferred
        // to Update rather than done at allocation time so the grid constants are bound first - CSClear's
        // bounds test reads _BgiCount, so a stale/unset one would silently skip the clear.
        bool _resetAllFields;
#if UNITY_EDITOR
        bool _reloadHookInstalled; // domain-reload release hook (see InstallReloadHook)
#endif
        bool _hasLoggedMissingReferences;
        bool _warnedBakeAssetMismatch; // warn once per change, not per voxelize attempt
        bool _warnedBakedLightAlsoRealtime; // same, for a light that is both baked and a realtime local light
        List<Light> _realtimeLightScratch;  // reused by that check; allocated on first use (bake-time only)
        // Progressive accumulation in SAMPLES (rays): _collectedSamples = total rays gathered since the last
        // change (0 = just changed), accumulated by samplesPerFrame each solve and capped at _maxSamples
        // (the ray budget). Quality depends on total rays, not frames - samplesPerFrame just spends the
        // budget faster (fewer frames to converge). The solve idles once the budget is spent.
        int _collectedSamples;
        Vector3 _prevSunDir;
        Vector4 _prevSunColor;

        // 0 at a change -> 1 once the ray budget is spent (_collectedSamples == _maxSamples), shaped by an
        // ease-in curve (_confidenceCurve) so the noisy early frames stay hidden and the reveal ramps up
        // as the field cleans. Auto-hides more with fewer rays (frame-1 confidence = samplesPerFrame/max).
        float Confidence {
            get {
                if (_maxSamples < 1) return 1f;
                float t = Mathf.Clamp01(_collectedSamples / (float)_maxSamples);
                return Mathf.Pow(t, _confidenceCurve);
            }
        }

        // Progressive-average blend weight = samplesPerFrame / totalSamples (== 1/frame during fill),
        // floored at samplesPerFrame/maxSamples by the sample cap. Frame 1 -> ~1 (hidden by Confidence≈0).
        float EmaWeight => _samplesPerFrame / (float)Mathf.Max(1, _collectedSamples);

        public ComputeBuffer MaterialBuffer => _materialBuffer;
        public ComputeBuffer RadianceBuffer => _radianceBuffer;
        public ComputeBuffer IrradianceBuffer => _irradianceBuffer;
        // Per-voxel surface word (normal in low bits). For the debug viewer.
        public ComputeBuffer SurfaceBuffer => _surfaceBuffer;
        // 1-bit/voxel occupancy bitfield (the runtime solidity source). For the debug viewer.
        public ComputeBuffer OccupancyBuffer => _occupancyBuffer;
        public VoxelVolume Volume => _volume;
        public Vector3 GridOrigin => _volume != null ? _volume.Bounds.min : Vector3.zero;
        public Vector3 GridSize => _volume != null ? _volume.Bounds.size : Vector3.one;
        // Per-axis voxel size: the 32^3 grid stretches to fill the (possibly non-cubic) bounds.
        public Vector3 VoxelSize => GridSize / Grid;

        // Samples (rays) gathered per voxel per frame - a performance knob (spends the maxSamples
        // budget over fewer/more frames); it doesn't change the converged result, so it needs no re-solve.
        public int SamplesPerFrame {
            get => _samplesPerFrame;
            set => _samplesPerFrame = Mathf.Max(1, value);
        }

        /// <summary>Ease-in exponent for the displayed fade / light-change reveal (1 = linear .. 8).</summary>
        public float ConfidenceCurve {
            get => _confidenceCurve;
            set => _confidenceCurve = Mathf.Clamp(value, 1f, 8f);
        }

        /// <summary>Sun-shadow mode for the FINE (active) volume: Off, Baked pre-marched visibility, or a per-pixel SDF raymarch.</summary>
        public ShadowMode FineShadow {
            get => _fineShadow;
            set => _fineShadow = value;
        }

        /// <summary>Stratified sun rays per texel of the shadow TEXTURE (CSSunVisibility) - the setting
        /// that controls what the baked shadow looks like. See InjectSunSamples for the bounce.</summary>
        public int SunShadowSamples {
            get => _sunShadowSamples;
            set {
                int clamped = Mathf.Clamp(value, 1, 16);
                if (_sunShadowSamples == clamped) return;
                _sunShadowSamples = clamped;
                _collectedSamples = 0;
                _sunVisDirty = true;   // ditto - scripted A/B must not depend on the sun moving
            }
        }

        /// <summary>Sun rays for the direct term a solid voxel BOUNCES (CSInject). Feeds indirect
        /// light, so it moves overall brightness rather than shadow edges - unlike
        /// <see cref="SunShadowSamples"/>, which is the shadow texture's. Restarts the solve; the sun
        /// visibility texture is not involved, so it needs no re-march.</summary>
        public int InjectSunSamples {
            get => _injectSunSamples;
            set {
                int clamped = Mathf.Clamp(value, 1, 16);
                if (_injectSunSamples == clamped) return;
                _injectSunSamples = clamped;
                _collectedSamples = 0;
            }
        }

        /// <summary>Baked-shadow edge sharpening. 1 = off. Fragment-side only, so no re-solve needed.</summary>
        public float BakedShadowSharpness {
            get => _bakedShadowSharpness;
            set => _bakedShadowSharpness = Mathf.Clamp(value, 1f, 16f);
        }

        /// <summary>Baked-shadow tap offset off the surface, in voxels. Fragment-side only.</summary>
        public float ShadowNormalOffset {
            get => _shadowNormalOffset;
            set => _shadowNormalOffset = Mathf.Clamp(value, 1f, 3f);
        }

        /// <summary>Directions of radiance stored per voxel. Reallocates on change.</summary>
        public RadianceDirections Directions {
            get => _radianceDirections;
            set {
                if (_radianceDirections == value) return;
                _radianceDirections = value;
                _collectedSamples = 0; // the field's meaning changed; restart the progressive average
            }
        }


        // The mode drives TWO independent strides, which is not obvious from the enum's name:
        //
        //   OUTGOING radiance (_Radiance) is a property of real geometry, and the voxelizer stores one
        //   normal per cell (plus its negation when the cell is two-sided). So it can be 1 or 2 - never
        //   6, because there is no way to know what the other four faces would even be without a
        //   per-face coverage mask from the rasterizer.
        //
        //   INCIDENT irradiance (_Irradiance / _IrradianceBlur / the mirror texture) has no such limit:
        //   the light arriving from a hemisphere is well defined for every voxel, air or solid. That is
        //   what Cube makes directional, and what lets the fragment blend 3 buckets by n^2 instead of
        //   reading one direction-less value.
        //
        // Hence Cube is "6 directions" on the irradiance but only 2 slots of radiance.

        /// <summary>ANALYSIS: mute all direct lighting in the lit shader, leaving only the indirect
        /// bounce. Pure fragment state - the solve is untouched and auto-exposure does not move, so it
        /// can be toggled mid-A/B without disturbing either.</summary>
        public bool MuteDirectLighting {
            get => _muteDirectLighting;
            set => _muteDirectLighting = value;
        }

        /// <summary>ANALYSIS: show one term of the shaded result on its own (see <see cref="DebugView"/>).
        /// Pure fragment state, like <see cref="MuteDirectLighting"/> - the solve is untouched and
        /// auto-exposure does not move, so it can be flipped mid-A/B.</summary>
        public DebugView View {
            get => _debugView;
            set => _debugView = value;
        }

        /// <summary>Single-mode irradiance tap filter. Pure fragment read state - no reallocation and
        /// no restart, so this is the one GI setting that can be A/B'd mid-frame without disturbing the
        /// solve (which is exactly what makes it measurable against the Fast baseline).</summary>
        public SingleTapFilter TapFilter {
            get => _singleTapFilter;
            set => _singleTapFilter = value;
        }

        /// <summary>Outgoing-radiance slots per voxel (1 or 2) - the stride of _Radiance.</summary>
        public int RadianceSlots => _radianceDirections == RadianceDirections.Single ? 1 : 2;

        /// <summary>Incident-irradiance buckets per voxel (1 or 6) - the stride of _Irradiance.</summary>
        public int IrradianceSlots => _radianceDirections == RadianceDirections.Cube ? 6 : 1;

        /// <summary>Display-transform controller (exposure + tonemap), e.g. to toggle in-shader tonemap from a UI.</summary>
        public AutoExposure ExposureControl => _exposureControl;

        // Coarse field: a scene-covering MeshBounds (same cubic grid, larger voxels). Falls back to the
        // fine bounds when unassigned so the read/visualizer degrade gracefully (empty slice -> 0).
        // MeshBounds is tight, so grow it by a border of coarse grid cells here (geometry exactly
        // on the boundary sits in half-clipped voxels): solving size' = size + 2*P*(size'/G) gives
        // the closed form size' = size * G/(G - 2P) per axis.
        const float CoarsePaddingVoxels = 2f;
        Bounds CoarseWorldBounds {
            get {
                Bounds b = _fields.CoarseField.Bounds;
                // max(1) guards the pathological tiny-grid case (G <= 2P) from an Inf/negative scale.
                b.size *= Grid / Mathf.Max(1f, Grid - 2f * CoarsePaddingVoxels);
                return b;
            }
        }
        public bool HasCoarse => _fields != null && _fields.CoarseField != null;
        public Vector3 CoarseOrigin => HasCoarse ? CoarseWorldBounds.min : GridOrigin;
        public Vector3 CoarseSize => HasCoarse ? CoarseWorldBounds.size : GridSize;
        public Vector3 CoarseVoxelSize => CoarseSize / Grid;

        LightingManager Manager => LightingManager.Instance;

        #region Shader Property IDs
        static readonly int s_radiance = Shader.PropertyToID("_Radiance");
        static readonly int s_irradiance = Shader.PropertyToID("_Irradiance");
        static readonly int s_irradianceBlur = Shader.PropertyToID("_IrradianceBlur");
        static readonly int s_bgiIrradianceTexWrite = Shader.PropertyToID("_BgiIrradianceTexWrite");
        static readonly int s_bgiIrradianceTex = Shader.PropertyToID("_BgiIrradianceTex");
        static readonly int s_bgiIrradianceTexCoarse = Shader.PropertyToID("_BgiIrradianceTexCoarse");
        static readonly int s_bgiSunVisTexWrite = Shader.PropertyToID("_BgiSunVisTexWrite");
        static readonly int s_bgiSunVisTex = Shader.PropertyToID("_BgiSunVisTex");
        static readonly int s_bgiSunVisTexCoarse = Shader.PropertyToID("_BgiSunVisTexCoarse");
        static readonly int s_bgiNeighbourMaskWrite = Shader.PropertyToID("_BgiNeighbourMaskWrite");
        static readonly int s_bgiNeighbourMask = Shader.PropertyToID("_BgiNeighbourMask");
        static readonly int s_bgiNeighbourMaskCoarse = Shader.PropertyToID("_BgiNeighbourMaskCoarse");
        static readonly int s_voxAlbedo = Shader.PropertyToID("_VoxAlbedo");
        static readonly int s_voxEmission = Shader.PropertyToID("_VoxEmission8");
        static readonly int s_voxBaseMap = Shader.PropertyToID("_VoxBaseMap");
        static readonly int s_voxBaseMapST = Shader.PropertyToID("_VoxBaseMap_ST");
        static readonly int s_voxCutoff = Shader.PropertyToID("_VoxCutoff");
        static readonly int s_voxAxis = Shader.PropertyToID("_VoxAxis");
        static readonly int s_gridOrigin = Shader.PropertyToID("_BgiGridOrigin");
        static readonly int s_gridSize = Shader.PropertyToID("_BgiGridSize");
        static readonly int s_voxelSize = Shader.PropertyToID("_BgiVoxelSize");
        static readonly int s_fieldOffset = Shader.PropertyToID("_FieldOffset");
        static readonly int s_bgiGrid = Shader.PropertyToID("_BgiGrid");
        static readonly int s_bgiGridLog2 = Shader.PropertyToID("_BgiGridLog2");
        static readonly int s_bgiCount = Shader.PropertyToID("_BgiCount");
        static readonly int s_coarseOrigin = Shader.PropertyToID("_BgiCoarseOrigin");
        static readonly int s_coarseVoxelSize = Shader.PropertyToID("_BgiCoarseVoxelSize");
        static readonly int s_confidence = Shader.PropertyToID("_Confidence");
        static readonly int s_emaWeight = Shader.PropertyToID("_EmaWeight");
        static readonly int s_coarseGridOrigin = Shader.PropertyToID("_CoarseGridOrigin");
        static readonly int s_coarseGridVoxelSize = Shader.PropertyToID("_CoarseGridVoxelSize");
        static readonly int s_material = Shader.PropertyToID("_Material");
        static readonly int s_surface = Shader.PropertyToID("_Surface");
        static readonly int s_occupancy = Shader.PropertyToID("_Occupancy");
        static readonly int s_occupancyThick = Shader.PropertyToID("_OccupancyThick");
        static readonly int s_occupancyHi = Shader.PropertyToID("_OccupancyHi");
        static readonly int s_occupancyHiWrite = Shader.PropertyToID("_OccupancyHiWrite");
        static readonly int s_occupancyTraversal = Shader.PropertyToID("_OccupancyTraversal");
        static readonly int s_occupancyGrown = Shader.PropertyToID("_OccupancyGrown");
        static readonly int s_bgiOccGrid = Shader.PropertyToID("_BgiOccGrid");
        static readonly int s_bgiOccGridLog2 = Shader.PropertyToID("_BgiOccGridLog2");
        static readonly int s_occFieldWordOffset = Shader.PropertyToID("_OccFieldWordOffset");
        static readonly int s_bgiShadowGrid = Shader.PropertyToID("_BgiShadowGrid");
        static readonly int s_bgiShadowGridLog2 = Shader.PropertyToID("_BgiShadowGridLog2");
        static readonly int s_bgiShadowSliceBase = Shader.PropertyToID("_BgiShadowSliceBase");
        static readonly int s_frameCount = Shader.PropertyToID("_FrameCount");
        static readonly int s_samplesPerFrame = Shader.PropertyToID("_SamplesPerFrame");
        static readonly int s_sampleBase = Shader.PropertyToID("_SampleBase");
        static readonly int s_giFireflyClamp = Shader.PropertyToID("_GiFireflyClamp");
        static readonly int s_reachBoost = Shader.PropertyToID("_ReachBoost");
        static readonly int s_bgiRadianceDirs = Shader.PropertyToID("_BgiRadianceDirs");
        static readonly int s_bgiIrradianceDirs = Shader.PropertyToID("_BgiIrradianceDirs");
        static readonly int s_solveMarchLevel = Shader.PropertyToID("_BgiSolveMarchLevel");
        static readonly int s_grownGate = Shader.PropertyToID("_BgiGrownGate");
        static readonly int s_shadowTexSamples = Shader.PropertyToID("_BgiShadowTexSamples");
        static readonly int s_injectSunSamples = Shader.PropertyToID("_BgiInjectSunSamples");
        static readonly int s_shadowSharpness = Shader.PropertyToID("_BgiShadowSharpness");
        static readonly int s_shadowNormalOffset = Shader.PropertyToID("_BgiShadowNormalOffset");
        static readonly int s_directLightDir = Shader.PropertyToID("_DirectLightDir");
        static readonly int s_directLightColor = Shader.PropertyToID("_DirectLightColor");
        static readonly int[] s_envSh = {
            Shader.PropertyToID("_EnvShAr"), Shader.PropertyToID("_EnvShAg"), Shader.PropertyToID("_EnvShAb"),
            Shader.PropertyToID("_EnvShBr"), Shader.PropertyToID("_EnvShBg"), Shader.PropertyToID("_EnvShBb"),
            Shader.PropertyToID("_EnvShC"),
        };
        static readonly int s_ambientFloor = Shader.PropertyToID("_AmbientFloor");
        static readonly int s_intensity = Shader.PropertyToID("_BgiIntensity");
        // Not a _Bgi* name: it lives in VoxelDirectLighting.hlsl and mutes the DIRECT term, which is
        // present in every GI variant - it is not part of the buffer-GI read the _Bgi prefix marks.
        static readonly int s_directMute = Shader.PropertyToID("_VoxelDirectMute");
        static readonly int s_debugView = Shader.PropertyToID("_BgiDebugView");
        static readonly int s_shadowModeFine = Shader.PropertyToID("_BgiShadowModeFine");
        static readonly int s_shadowModeCoarse = Shader.PropertyToID("_BgiShadowModeCoarse");
        static readonly int s_luminanceResult = Shader.PropertyToID("_LuminanceResult");
        static readonly int s_cameraPosition = Shader.PropertyToID("_CameraPosition");
        static readonly int s_cameraForward = Shader.PropertyToID("_CameraForward");
        static readonly int s_luminanceRadius = Shader.PropertyToID("_LuminanceRadius");
        #endregion

        void OnEnable() {
            Instance = this;
            // Switching the GI method flips this component's enabled state (GiMethodSelector). Only the
            // DYNAMIC light fields restart: clear them and gather the ray budget from scratch, so the
            // solver visibly begins again instead of idling on the field it held when GI was switched
            // off (a spent sample budget would otherwise never wake). The voxelization is untouched -
            // it's static baked data the buffers keep across a disable (see OnDisable).
            _resetAllFields = true;
            _collectedSamples = 0;
            InstallReloadHook();
#if UNITY_EDITOR
            // In edit mode the editor only ticks Update sporadically, so the temporal solve never
            // accumulates and the visualizer's per-frame draw is missed. Pumping the player loop
            // makes Update + render run continuously off-play, exactly like play mode.
            UnityEditor.EditorApplication.update += EditorPump;
#endif
        }

        void OnDisable() {
            if (Instance == this) Instance = null;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorPump;
#endif
            SetGiBufferKeyword(false);
            _exposureControl.ResetToDefault();
            _exposureControl.Release();
            // The buffers deliberately SURVIVE a disable. Most of their content (material, surface,
            // occupancy) is the static voxelization: rebuilding it on every GI-method toggle would
            // re-rasterize the whole scene (or re-upload the disk bakes) for data that hasn't changed.
            // Only the dynamic light fields are restarted, by OnEnable. Freed in OnDestroy instead
            // (plus before an editor domain reload, which drops the managed references).
            if (_voxelizeMaterial != null) {
                if (Application.isPlaying) Destroy(_voxelizeMaterial); else DestroyImmediate(_voxelizeMaterial);
                _voxelizeMaterial = null;
            }
        }

        void OnDestroy() {
#if UNITY_EDITOR
            if (_reloadHookInstalled) {
                UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= ReleaseBuffers;
                _reloadHookInstalled = false;
            }
#endif
            ReleaseBuffers();
        }

        // Release the buffers before an editor domain reload: they outlive a disable now, but the
        // managed references never survive a reload, so without this the native buffers are left to the
        // GC finalizer (and Unity's "not disposed" warning). Hooked from OnEnable and unhooked in
        // OnDestroy - NOT in OnDisable, since a disabled updater still owns live buffers.
        void InstallReloadHook() {
#if UNITY_EDITOR
            if (_reloadHookInstalled) return;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseBuffers;
            _reloadHookInstalled = true;
#endif
        }

#if UNITY_EDITOR
        void EditorPump() {
            if (!Application.isPlaying && isActiveAndEnabled) {
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
            }
        }
#endif

        bool _loggedEnvironment;
        // Which path last filled _Material: the disk bake, or the GPU rasterizer. Reported by
        // LogEnvironmentOnce because the two fail very differently on a restricted graphics API.
        string _voxelizeSource = "<none yet>";

        /// <summary>
        /// One-shot report of everything the GI needs from the PLATFORM, logged the first time this
        /// updater publishes. "The solve dispatches but I see no GI" has several possible causes that
        /// look identical from the solve's own logs - the fragment keyword never claimed, the mirrored
        /// irradiance textures never created, or a device that cannot run the compute at all - and a
        /// web build gives no other way to tell them apart.
        ///
        /// Deliberately one line per fact and one shot per session: this exists to be pasted out of a
        /// browser console, not to run every frame.
        /// </summary>
        void LogEnvironmentOnce() {
            if (_loggedEnvironment) return;
            _loggedEnvironment = true;
            bool keyword = Shader.IsKeywordEnabled(LightingKeywords.GiBuffer);
            Debug.Log(
                $"[BufferGI env] device={SystemInfo.graphicsDeviceType} ({SystemInfo.graphicsDeviceVersion})\n" +
                $"  compute={SystemInfo.supportsComputeShaders} 3dTex={SystemInfo.supports3DTextures} " +
                $"3dRT={SystemInfo.supports3DRenderTextures}\n" +
                $"  voxelization source={_voxelizeSource}   <- \"runtime raster\" needs UAV writes from a\n" +
                "     FRAGMENT shader (SetRandomWriteTarget). Where that is unsupported the raster writes\n" +
                "     nothing, occupancy stays empty, and the solve runs to completion producing no light\n" +
                $"  keyword {LightingKeywords.GiBuffer}={keyword}   <- false means the lit shader is " +
                "compiling its GI_OFF variant, so nothing this updater publishes can be read\n" +
                $"  bound buffers _Occupancy={(_occupancyBuffer != null && _occupancyBuffer.IsValid() ? "valid" : "NULL/INVALID")}\n" +
                "     <- the shipping fragment reads no buffer at all, but the BGI_DEBUG_VIEWS variant\n" +
                "        declares _Occupancy, and on WebGPU a declared-but-unbound global FAILS PIPELINE\n" +
                "        CREATION, which renders the object BLACK\n" +
                $"  irradianceTex fine={(_irradianceTex != null ? _irradianceTex.name + " created=" + _irradianceTex.IsCreated() : "NULL")} " +
                $"coarse={(_irradianceTexCoarse != null ? _irradianceTexCoarse.name + " created=" + _irradianceTexCoarse.IsCreated() : "NULL")}\n" +
                $"  sunVisTex fine={(_sunVisTex != null ? _sunVisTex.name + " created=" + _sunVisTex.IsCreated() : "NULL")} " +
                $"coarse={(_sunVisTexCoarse != null ? _sunVisTexCoarse.name + " created=" + _sunVisTexCoarse.IsCreated() : "NULL")}\n" +
                $"  solveShader={(_solveShader != null ? _solveShader.name : "NULL")} " +
                $"bakeShader={(_bakeShader != null ? _bakeShader.name : "NULL")} " +
                $"voxelizeShader={(_voxelizeShader != null ? _voxelizeShader.name : "NULL")}\n" +
                $"  kernels solve(inject={_injectKernel} gather={_gatherKernel} blur={_blurKernel}) " +
                $"bake(occ={_buildOccupancyKernel} surface={_buildSurfaceKernel})   <- -1 means the kernel " +
                "was not found and its dispatch is skipped\n" +
                $"  grid={Grid} occGrid={OccGrid} (requested={_occupancyResolution} mobileOverride={_occupancyResolutionMobile}) " +
                $"occWords/field={OccWordsPerField} shadowGrid={ShadowGrid}\n" +
                $"  volume={(_volume != null ? _volume.name : "NULL")} fields={(_fields != null ? _fields.name : "NULL")} " +
                $"hasCoarse={HasCoarse} directions={_radianceDirections}",
                this);
        }

        // GI_VOXEL_BUFFER only while this updater is actually solving/publishing (buffers bound); the
        // claim is change-only and ownership-aware, so this is safe to call every frame and safe
        // against the old owner clobbering the keyword while switching GI methods.
        void SetGiBufferKeyword(bool on) {
            if (on) LightingKeywords.ClaimGi(this, LightingKeywords.GiBuffer);
            else LightingKeywords.ReleaseGi(this);
            // Drop the tap keyword with the GI group so a disabled updater doesn't leave a variant of a
            // shader it no longer drives resident. Re-published by SyncTapFilterKeyword next SetGlobals.
            if (!on) SetTapKeyword(SingleTapFilter.Fast);
            // Same for the snap: a disabled updater must not leave its variant resident on a shader it
            // no longer drives (and its mask textures are about to be released).
            if (!on) SetSnapKeyword(false);
            // Same for the analysis variant: a disabled updater must not leave BGI_DEBUG_VIEWS resident
            // on a shader it no longer drives. Re-claimed by SetGlobals if a view is still selected.
            if (!on) SetDebugKeyword(false);
        }

        // BGI_TAP_AXIS_SNAPPED, change-only. Called every frame from SetGlobals, so it must not hit
        // Shader.EnableKeyword/DisableKeyword unless something actually moved - a keyword change
        // invalidates material/variant state engine-side and is not something to do 60 times a second.
        //
        // Gated on Single: the keyword only guards code inside `if (idirs == 1u)`, so publishing it in
        // Cube would swap the whole VoxelLit variant set for a branch nothing reaches.
        void SyncTapFilterKeyword() {
            // Gate on the ALLOCATED stride, not the property: that is the value published as
            // _BgiIrradianceDirs this same frame, so the keyword can never disagree with the branch the
            // fragment actually takes during the frame a mode switch lands.
            SetTapKeyword(_allocatedIrradianceSlots == 1 ? _singleTapFilter : SingleTapFilter.Fast);
        }

        // BGI_DEBUG_VIEWS, change-only for the same reason as SetTapKeyword below.
        bool? _appliedDebugKeyword;
        void SetDebugKeyword(bool on) {
            if (_appliedDebugKeyword == on) return;
            _appliedDebugKeyword = on;
            LightingKeywords.BgiDebug.Set(on ? LightingKeywords.BgiDebugViews : null);
        }

        void SetTapKeyword(SingleTapFilter filter) {
            if (_appliedTapFilter == filter) return;
            _appliedTapFilter = filter;
            LightingKeywords.BgiTap.Set(
                filter == SingleTapFilter.AxisSnapped ? LightingKeywords.BgiTapAxisSnapped : null);
        }

        // BGI_TAP_SNAP_INPLANE, change-only for the same reason as SetTapKeyword. NOT gated on Single -
        // unlike the tap filter, the snap applies in Cube too (its per-bucket taps leave the same two
        // in-plane coordinates continuous).
        bool? _appliedSnapKeyword;
        void SetSnapKeyword(bool on) {
            if (_appliedSnapKeyword == on) return;
            _appliedSnapKeyword = on;
            LightingKeywords.BgiSnap.Set(on ? LightingKeywords.BgiTapSnapInPlane : null);
        }

        /// <summary>Contaminated-axis snap (BGI_TAP_SNAP_INPLANE). Public so a debug panel can A/B it
        /// without the inspector; takes effect the next frame.</summary>
        public bool InPlaneSnap {
            get => _inPlaneSnap;
            set => _inPlaneSnap = value;
        }

        /// <summary>Gate the shell dilation on the grown occupancy (see the serialized tooltip).
        /// Public so the A/B can be driven from a script or an eval; takes effect on the next solve
        /// step, and since only the displayed shell changes, a re-solve is enough - no re-bake.</summary>
        public bool GrownDilationGate {
            get => _grownDilationGate;
            set => _grownDilationGate = value;
        }

        void OnValidate() {
            // This component is dominated by display/solve settings (exposure, tonemap, shadows, AO,
            // samples...) - none of which affect the VOXELIZATION. So an inspector change only restarts
            // the progressive accumulation to re-settle the change; it does NOT invalidate the bake (that
            // would needlessly re-voxelize + re-warn on every tweak). The bake's real inputs - the normal
            // source and the field bounds - are watched in Update by SyncBakeInputs instead.
            _collectedSamples = 0;
            // ...but restarting the SOLVE is no longer enough for everything on this component. P6 moved
            // sun visibility out of the per-frame solve into CSSunVisibility, which re-runs only on a sun
            // MOVE or on _sunVisDirty - so a setting that feeds it (samples, estimator) changed in the
            // inspector had no effect at all until the sun moved or the scene was re-baked, and the field
            // silently kept its old contents. Found by fs testing _sunShadowSamples and correctly
            // reporting that it did nothing. The inspector writes the backing FIELD and then calls this,
            // so it bypasses the property setters entirely; this is the only place that catches it.
            // Cheap to be unconditional: the pass is chunked across frames and only re-runs the volume.
            _sunVisDirty = true;
        }

        // Wake the solver when the sun changes. Local lights are intentionally excluded (GI may drop
        // them); add a local-light hash here if that changes. Sky/ambient changes come in via OnValidate.
        bool HasSunChanged() {
            Light sun = RenderSettings.sun;
            Vector3 dir = sun != null ? -sun.transform.forward : Vector3.down;
            Vector4 col = sun != null ? sun.FinalColor() : Vector4.zero;
            return dir != _prevSunDir || col != _prevSunColor;
        }

        void StoreSunState() {
            Light sun = RenderSettings.sun;
            _prevSunDir = sun != null ? -sun.transform.forward : Vector3.down;
            _prevSunColor = sun != null ? sun.FinalColor() : Vector4.zero;
        }

        // BufferGI needs a power-of-two cubic grid (the shift/mask index math + the word-aligned
        // occupancy bitfield both require it). Snap the requested GI resolution to the nearest power
        // of two and clamp to a sane range. Grid >= 4 guarantees Grid^3 is a multiple of 32.
        static int SnapGridResolution(int resolution) {
            return Mathf.Clamp(Mathf.ClosestPowerOfTwo(Mathf.Max(4, resolution)), 4, 256);
        }

        // Set the cubic grid resolution and the derived counts/log2. Caller reallocates the buffers.
        void SetGridResolution(int grid) {
            _grid = grid;
            _gridLog2 = 0;
            while ((1 << _gridLog2) < grid) _gridLog2++;
            _voxelCount = grid * grid * grid;
            _totalVoxels = FieldCount * _voxelCount;
        }

        // Match the grid resolution to this component's own _giResolution (independent of the volume's
        // bake resolution); on a change, release the buffers so they re-alloc + re-bake at the new size.
        void SyncGridResolution() {
            int grid = SnapGridResolution(_giResolution);
            if (grid != _grid) {
                SetGridResolution(grid);
                ReleaseBuffers();
            }
            SyncOccResolution();
        }

        // The occupancy resolution this build actually runs at: the mobile override where one applies,
        // else the authored value. Snapped to a power of two in [64, 256] and never below the lighting
        // grid - the traversal mip is an OR-DOWNSAMPLE of the hi-res field, so a hi-res grid coarser
        // than the field it feeds has nothing to downsample and the ratio shift would go negative.
        //
        // WebGL and the mobile players are the platforms the override exists for; the check is a
        // runtime one rather than a #if so an editor session can be told which it is simulating.
        public int ResolvedOccupancyResolution {
            get {
                bool mobile = Application.isMobilePlatform
                    || Application.platform == RuntimePlatform.WebGLPlayer;
                int requested = mobile && _occupancyResolutionMobile > 0
                    ? _occupancyResolutionMobile : _occupancyResolution;
                return SnapOccResolution(requested);
            }
        }

        // Same snap as the field grid, clamped to the three supported occupancy resolutions.
        static int SnapOccResolution(int resolution) {
            return Mathf.Clamp(Mathf.ClosestPowerOfTwo(Mathf.Max(64, resolution)), 64, 256);
        }

        void SetOccResolution(int occGrid) {
            _occGrid = occGrid;
            _occGridLog2 = 0;
            while ((1 << _occGridLog2) < occGrid) _occGridLog2++;
            int blocks = occGrid >> 2;
            _occWordsPerField = blocks * blocks * blocks * 2;
        }

        // The shadow-texture resolution this build runs at. Same mobile-override shape as the
        // occupancy one, and CAPPED at the occupancy resolution: the texture is produced by marching
        // the occupancy field, so texels finer than that carry no information the geometry has.
        public int ResolvedShadowResolution {
            get {
                bool mobile = Application.isMobilePlatform
                    || Application.platform == RuntimePlatform.WebGLPlayer;
                int requested = mobile && _shadowResolutionMobile > 0
                    ? _shadowResolutionMobile : _shadowResolution;
                return SnapOccResolution(requested);
            }
        }

        // Resolve + apply the occupancy and shadow resolutions. Called from SyncGridResolution, AFTER
        // the field grid is set: occupancy's floor is Grid and shadow's ceiling is occupancy, so the
        // three cannot be resolved independently.
        void SyncOccResolution() {
            int occ = Mathf.Max(ResolvedOccupancyResolution, _grid);
            int shadow = Mathf.Min(ResolvedShadowResolution, occ);
            if (occ == _occGrid && shadow == _shadowGrid && _occWordsPerField > 0) return;
            SetOccResolution(occ);
            _shadowGrid = shadow;
            _shadowGridLog2 = 0;
            while ((1 << _shadowGridLog2) < shadow) _shadowGridLog2++;
            ReleaseBuffers();
        }

        // Radiance slots per voxel changed (inspector or script): _Radiance is sized by it, so the
        // buffer has to come back at the new stride. Same shape as SyncGridResolution - release here,
        // EnsureInitialized reallocates and requests the field clear. The stored values are not
        // convertible between modes (different slot meanings), so the accumulation restarts too.
        void SyncRadianceDirections() {
            if (RadianceSlots == _allocatedRadianceSlots && IrradianceSlots == _allocatedIrradianceSlots) return;
            ReleaseBuffers();
            _collectedSamples = 0;
        }

        // Publish the grid resolution constants to the compute shaders (their BgiIndex/BgiCoord/
        // occupancy math reads them). They only change when the grid does, but re-setting each frame is
        // cheap and keeps the shader assets in sync regardless of dispatch ordering.
        //
        // BOTH shaders, always. Uniforms are per-ComputeShader, so a bake kernel that never got these
        // would read BGI_COUNT as 0 and early-out on every thread - an empty field, silently, with no
        // error anywhere. That is the one way the solve/bake split can break, so it is handled in one
        // place and neither caller gets to choose.
        void BindGridConstantsToCompute() {
            BindGridConstants(_solveShader);
            BindGridConstants(_bakeShader);
        }

        // The bake shader is a second asset, so an existing prefab/scene has an empty reference for it.
        // Resolve it BY NAME rather than by path: the project already does this for the SDF bakers
        // (VoxelBakerBase.FindComputeShaderByExactName), and a name lookup survives the file being
        // moved or the package folder layout changing. Editor-only, and the result is serialized, so a
        // build gets the reference without the user ever touching the inspector.
        void ResolveBakeShader() {
#if UNITY_EDITOR
            if (_bakeShader != null) return;
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("BufferGiBake t:ComputeShader")) {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                // FindAssets substring-matches, so filter to the exact file name.
                if (System.IO.Path.GetFileNameWithoutExtension(path) != "BufferGiBake") continue;
                _bakeShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if (_bakeShader != null) UnityEditor.EditorUtility.SetDirty(this);
                return;
            }
#endif
        }

        // FindKernel THROWS on a missing kernel, which after a solve/bake split is the likeliest
        // mistake (a kernel left in, or moved to, the wrong file). Name both the shader and the kernel
        // instead, and return -1 so the existing `< 0` guards skip the dispatch.
        int RequireKernel(ComputeShader cs, string kernel) {
            if (cs == null) {
                Debug.LogError($"Buffer GI: no compute shader assigned for kernel '{kernel}'.", this);
                return -1;
            }
            if (!cs.HasKernel(kernel)) {
                Debug.LogError($"Buffer GI: '{cs.name}' has no kernel '{kernel}'.", this);
                return -1;
            }
            return cs.FindKernel(kernel);
        }

        void BindGridConstants(ComputeShader cs) {
            if (cs == null) return;
            cs.SetInt(s_bgiGrid, _grid);
            cs.SetInt(s_bgiGridLog2, _gridLog2);
            cs.SetInt(s_bgiCount, _voxelCount);
            // Hi-res occupancy grid. Bound with the field constants for the same reason the radiance
            // stride is: BgiOccWord's index math depends on it exactly the way BgiIndex depends on the
            // field grid, and a kernel that missed it would address the wrong words silently.
            cs.SetInt(s_bgiOccGrid, _occGrid);
            cs.SetInt(s_bgiOccGridLog2, _occGridLog2);
            cs.SetInt(s_bgiShadowGrid, _shadowGrid);
            cs.SetInt(s_bgiShadowGridLog2, _shadowGridLog2);
            // Radiance stride. Bound alongside the grid constants because BgiRadianceSlot's index math
            // depends on it exactly the way BgiIndex depends on the grid.
            cs.SetInt(s_bgiRadianceDirs, _allocatedRadianceSlots);
            cs.SetInt(s_bgiIrradianceDirs, _allocatedIrradianceSlots);
        }

        void Update() {
            VoxelVolume active = Manager != null ? Manager.Volume : null;
            if (active != _volume) {
                // Switching between two live volumes: keep the buffers (fixed size) so the coarse
                // field and the global cold-start confidence survive; just rebuild the fine field
                // and let the read fade it in from the coarse field. A first assignment or a
                // teardown (null) falls back to a full cold-start (re)init.
                bool warmSwitch = _volume != null && active != null && _irradianceBuffer != null;
                _volume = active;
                // Resolve the fine volume's baked occlusion holders once per switch (SetGlobals binds
                // whichever the shadow modes ask for). These are per-pixel, fine-volume-bound sources.
                _occField = active != null ? active.GetComponent<VoxelOcclusionField>() : null;
                _occBitmask = active != null ? active.GetComponent<VoxelOcclusionBitmask>() : null;
                // Pull this level's coarse field + disk bakes from its BufferGiFields (the fine field
                // is the active volume itself). Null for a fine-only, runtime-voxelized level.
                _fields = BufferGiFields.Find(active);
                _hasLoggedMissingReferences = false;
                _warnedBakedLightAlsoRealtime = false; // new level: re-check its baked lights against the realtime set
                if (warmSwitch) {
                    _materialBaked = false;
                    _resetFineField = true; // clear + re-fill the fine field for the new bounds
                    _collectedSamples = 0;
                } else {
                    ReleaseBuffers();
                }
            }
            if (_volume == null || _solveShader == null) {
                SetGiBufferKeyword(false);
                return;
            }

            if (!IsReady(out string missingReason)) {
                SetGiBufferKeyword(false);
                if (!_hasLoggedMissingReferences) {
                    _hasLoggedMissingReferences = true;
                    Debug.LogWarning($"Buffer GI is missing required references: {missingReason}. Waiting for initialization.", this);
                }
                return;
            }
            _hasLoggedMissingReferences = false;

            // Resolve the cubic grid resolution from the active volume (snapped to a power of two). A
            // change (new volume with a different _maxResolution, or an inspector edit) forces a cold
            // realloc at the new size (overrides any warm switch above), since every buffer and the
            // shader index math depend on it.
            SyncGridResolution();
            SyncRadianceDirections();

            SyncBakeInputs();
            EnsureInitialized();
            BindGridConstantsToCompute();
            if (!_materialBaked) {
                Voxelize();
            }
            // Baked lights switched on/off (or retuned) since the last inject. Deliberately after the
            // voxelize and before the solve gate, so a switch reaches the field in the same frame.
            SyncBakedLightState();
            if (_resetAllFields) {
                // Cold start (fresh buffers, or this component was just re-enabled by a GI-method
                // toggle): zero every field so the solve refills from black instead of stale/undefined
                // data. Subsumes the fine-only reset below - a just-cleared coarse field has nothing
                // to seed from.
                ClearDynamicFields();
                _resetAllFields = false;
                _resetFineField = false;
            }
            if (_resetFineField) {
                // The fine bounds changed: reset the stale fine slice for the new volume (coarse is
                // untouched). With a coarse field, SEED the fine slice from it so the fine box starts
                // from the coarse approximation and refines - instead of restarting from black.
                if (HasCoarse) InitFineFromCoarse();
                else ClearField(FineField * VoxelCount);
                _resetFineField = false;
            }

            // Gate the solve: keep gathering until the ray budget is spent (_collectedSamples == maxSamples),
            // or always if _continuousGi. Otherwise idle so a static, settled scene costs no GI compute.
            // Samples are accumulated BEFORE the dispatch so the first solved frame's weight is ~1.
            // The baked sun shadow is re-marched when the sun moves or the geometry changed, and at
            // no other time - its lifecycle is the sun, not the solve frame. Checked BEFORE the solve
            // gate so a moved sun starts reaching the screen in the same frame it moved.
            if (HasSunChanged() || _sunVisDirty) {
                _collectedSamples = 0;
                _sunVisDirty = false;
                // NEVER restart a sweep that is already running. A CONTINUOUSLY moving sun (a sun
                // rotator) makes HasSunChanged fire every single frame, and resetting the slice cursor
                // here meant the sweep never got past its first chunk: measured 2 of 128 slices after
                // 120 frames of rotation, so 126 slices kept whatever the last completed sweep left
                // and the shadow was effectively frozen. Queue instead, and start the next sweep the
                // moment this one lands.
                if (SunVisibilityPending) _sunVisRestartQueued = true;
                else StartSunVisibilitySweep();
            }
            // Spend one bounded chunk per frame until the volume is covered. Deliberately not one
            // dispatch: see SunVisTexelsPerDispatch.
            if (!SunVisibilityPending && _sunVisRestartQueued) {
                _sunVisRestartQueued = false;
                StartSunVisibilitySweep();
            }
            if (SunVisibilityPending) DispatchSunVisibilityChunk();
            if (_collectedSamples < _maxSamples || _continuousGi) {
                // Rays already gathered BEFORE this frame. The gather indexes its sample sequence by
                // the ray ordinal (_SampleBase + rayIndex) rather than by the frame, so the same ray
                // budget draws the same points however it is sliced across frames - which is what makes
                // samplesPerFrame a pure convergence-RATE knob. Read before the increment.
                int sampleBase = _collectedSamples;
                _collectedSamples = Mathf.Min(_collectedSamples + Mathf.Max(1, _samplesPerFrame), _maxSamples);
                DispatchSolve(sampleBase);
            }
            StoreSunState();

            SetGlobals();
            ClaimGiKeywordWhenSafe();
            // Display transform (exposure + tonemap); runs every frame so auto-exposure keeps
            // adapting even when the solve is idle (a static scene the camera moves through).
            _exposureControl.Apply(DispatchLuminance);
        }

        // Backend luminance measurement for AutoExposure: average the DISPLAYED field's air-voxel
        // luminance in a camera-centred radius into the controller's 2-uint buffer. AutoExposure owns
        // the clear + readback + adaptation; this only picks the field to read and dispatches it.
        //
        // Field selection follows the camera: the FINE (active) field when the camera is inside it,
        // else the COARSE (far) field. Outside both, nothing is dispatched - the buffer stays 0 and
        // AutoExposure falls back to its open-sky estimate rather than reading empty/dark air.
        void DispatchLuminance(ComputeBuffer luminanceBuffer) {
            if (_averageLuminanceKernel < 0 || _irradianceBlurBuffer == null || Camera.main == null) return;
            Vector3 camPos = Camera.main.transform.position;

            Vector3 origin, size, voxelSize;
            int fieldOffset;
            if (Contains(GridOrigin, GridSize, camPos)) {
                origin = GridOrigin; size = GridSize; voxelSize = VoxelSize;
                fieldOffset = FineField * VoxelCount;
            } else if (HasCoarse && Contains(CoarseOrigin, CoarseSize, camPos)) {
                origin = CoarseOrigin; size = CoarseSize; voxelSize = CoarseVoxelSize;
                fieldOffset = CoarseField * VoxelCount;
            } else {
                return; // outside both fields -> AutoExposure uses its open-sky fallback
            }

            SetGridUniforms(origin, size, voxelSize);
            _solveShader.SetInt(s_fieldOffset, fieldOffset);
            _solveShader.SetBuffer(_averageLuminanceKernel, s_occupancy, _occupancyBuffer);
            _solveShader.SetBuffer(_averageLuminanceKernel, s_irradianceBlur, _irradianceBlurBuffer);
            _solveShader.SetBuffer(_averageLuminanceKernel, s_luminanceResult, luminanceBuffer);
            _solveShader.SetVector(s_cameraPosition, camPos);
            _solveShader.SetVector(s_cameraForward, Camera.main.transform.forward);
            _solveShader.SetFloat(s_luminanceRadius, _exposureControl.MeasureRadius);
            _solveShader.Dispatch(_averageLuminanceKernel, Groups, 1, 1);
        }

        // Axis-aligned contains test for a grid given as min corner (origin) + size.
        static bool Contains(Vector3 origin, Vector3 size, Vector3 p) =>
            p.x >= origin.x && p.x <= origin.x + size.x &&
            p.y >= origin.y && p.y <= origin.y + size.y &&
            p.z >= origin.z && p.z <= origin.z + size.z;

        // Publish the buffers + grid mapping + confidence the lit shader's BgiGatherIndirect reads.
        bool _warnedUnboundWhileClaimed;

        /// <summary>
        /// The one combination WebGPU turns into a black screen: GI_VOXEL_BUFFER claimed - so the lit
        /// shader compiles a variant that DECLARES a global - while that global is not actually
        /// bound. The driver validates declared globals against the pipeline layout and
        /// fails pipeline creation outright; D3D11 and Vulkan tolerate it, so this only ever shows up
        /// in a browser build, and it shows up as unlit geometry rather than as an error.
        ///
        /// Cheap enough to check every frame (two null tests), warns once, and names the fix.
        /// </summary>
        /// <summary>Every global the GI_VOXEL_BUFFER variant DECLARES, and therefore every global WebGPU
        /// will validate against the pipeline layout. Keep this in step with the declarations at the top
        /// of BufferGiRead.hlsl - a global declared there but missing here is exactly the black screen
        /// this guard exists to prevent.</summary>
        bool GiVariantGlobalsBound(out string missing) {
            // _Occupancy is declared only by the BGI_DEBUG_VIEWS variant now, but the check stays
            // unconditional: the keyword can be flipped from script mid-frame, and a null buffer here
            // is a bug either way.
            if (_occupancyBuffer == null || !_occupancyBuffer.IsValid()) { missing = "_Occupancy"; return false; }
            if (_irradianceTex == null || !_irradianceTex.IsCreated()) { missing = "_BgiIrradianceTex"; return false; }
            if (_irradianceTexCoarse == null || !_irradianceTexCoarse.IsCreated()) { missing = "_BgiIrradianceTexCoarse"; return false; }
            if (_sunVisTex == null || !_sunVisTex.IsCreated()) { missing = "_BgiSunVisTex"; return false; }
            if (_sunVisTexCoarse == null || !_sunVisTexCoarse.IsCreated()) { missing = "_BgiSunVisTexCoarse"; return false; }
            if (_neighbourMaskTex == null || !_neighbourMaskTex.IsCreated()) { missing = "_BgiNeighbourMask"; return false; }
            if (_neighbourMaskTexCoarse == null || !_neighbourMaskTexCoarse.IsCreated()) { missing = "_BgiNeighbourMaskCoarse"; return false; }
            missing = null;
            return true;
        }

        /// <summary>
        /// Claim GI_VOXEL_BUFFER only when every global that variant declares is actually bound.
        ///
        /// WebGPU validates declared globals against the bound pipeline layout and FAILS PIPELINE
        /// CREATION when one is unbound - which does not raise an error, it renders the object BLACK.
        /// D3D11 and Vulkan tolerate it, so an editor session gives no warning at all. Withholding the
        /// keyword instead degrades to direct lighting: the scene loses its GI, which is visible and
        /// diagnosable, rather than turning into silhouettes.
        /// </summary>
        void ClaimGiKeywordWhenSafe() {
            if (GiVariantGlobalsBound(out string missing)) {
                SetGiBufferKeyword(true);
                return;
            }
            SetGiBufferKeyword(false);
            if (_warnedUnboundWhileClaimed) return;
            _warnedUnboundWhileClaimed = true;
            Debug.LogError(
                $"Buffer GI: not claiming {LightingKeywords.GiBuffer} - the variant declares {missing}, " +
                "which is not bound. On WebGPU that would fail pipeline creation for every VoxelLit " +
                "material and render them BLACK, so GI stays off and the scene keeps direct lighting. " +
                "Fix the missing global rather than forcing the keyword.",
                this);
        }

        void SetGlobals() {
            LogEnvironmentOnce();
            // Grid resolution constants for the fragment index math (shared by both fields).
            Shader.SetGlobalInt(s_bgiGrid, _grid);
            Shader.SetGlobalInt(s_bgiGridLog2, _gridLog2);
            Shader.SetGlobalInt(s_bgiCount, _voxelCount);
            // The SHADOW grid, for BgiSampleShadowTexture's offset. Easy to forget because the other
            // two grids are only needed compute-side - and forgetting it is silent: unbound it reads
            // 0, the offset divides by it, and every uvw goes out of range, which the tap reports as
            // LIT. The whole baked shadow disappears with no error anywhere.
            Shader.SetGlobalInt(s_bgiShadowGrid, _shadowGrid);
            Shader.SetGlobalInt(s_bgiShadowGridLog2, _shadowGridLog2);
            // The lit shader reads NO buffer in a shipping variant - everything it needs arrives
            // through the mirrored irradiance textures below. _Occupancy is still published because
            // the BGI_DEBUG_VIEWS variant declares it (BgiTapSolidWeight), and a declared-but-unbound
            // global fails pipeline creation on WebGPU. _Surface is no longer declared anywhere in the
            // fragment, so it is no longer published either.
            Shader.SetGlobalBuffer(s_occupancy, _occupancyBuffer);
            // Fine field bounds + coarse field bounds for the fragment read (hard fine/coarse switch).
            Shader.SetGlobalVector(s_gridOrigin, GridOrigin);
            Shader.SetGlobalVector(s_gridSize, GridSize);
            Shader.SetGlobalVector(s_voxelSize, VoxelSize);
            Shader.SetGlobalVector(s_coarseOrigin, CoarseOrigin);
            Shader.SetGlobalVector(s_coarseVoxelSize, CoarseVoxelSize);
            // GI gain from the sun's Indirect Multiplier (Light.bounceIntensity) - the standard
            // Unity control for indirect strength, used instead of a custom field.
            Light sun = RenderSettings.sun;
            Shader.SetGlobalFloat(s_intensity, sun != null ? sun.bounceIntensity : 1f);
            // Analysis mute for the lit shader's direct term (see _muteDirectLighting). Published every
            // frame like the other read parameters - it is one float and takes effect immediately, with
            // no solve restart, which is what lets it be flipped in the middle of an A/B.
            Shader.SetGlobalFloat(s_directMute, _muteDirectLighting ? 1f : 0f);
            // Analysis view selector (see DebugView). Same reasoning as the mute above: one float,
            // published every frame, effective immediately, no variant and no solve restart.
            Shader.SetGlobalFloat(s_debugView, (float)_debugView);
            // The analysis code is behind BGI_DEBUG_VIEWS so it costs a shipping build nothing; claim
            // the variant only while a view is actually selected. Change-only, same reason as the tap
            // keyword - a keyword write invalidates variant state and must not happen every frame.
            SetDebugKeyword(_debugView != DebugView.Off);
            // Sun-shadow, per field. Baked taps the sun visibility CSBlur mirrored into the irradiance
            // texture's alpha; Sdf marches the hi-res SDF per pixel (the _SdfHires global the active
            // volume already publishes - see VoxelVolume.ApplyShaderGlobals).
            // Irradiance stride (1 or 6) for the fragment's bucket select - both the GI tap and the
            // baked-shadow alpha tap need it. A plain int uniform, NOT a keyword: a keyword here would
            // multiply the VoxelLit variant set (already GI_* x 4 tonemaps) for one scalar. The
            // RADIANCE stride is compute-side only, so it is not published here.
            Shader.SetGlobalInt(s_bgiIrradianceDirs, _allocatedIrradianceSlots);
            // Single-mode tap filter. A KEYWORD unlike the stride above, and for the opposite reason:
            // the stride is one scalar every path reads anyway, while this selects between two whole
            // tap implementations whose register footprints differ - the Fast variant must not be
            // compiled alongside the other. Change-only (see SyncTapFilterKeyword).
            SyncTapFilterKeyword();
            SetSnapKeyword(_inPlaneSnap);
            Shader.SetGlobalInt(s_shadowModeFine, (int)_fineShadow);
            Shader.SetGlobalInt(s_shadowModeCoarse, (int)_coarseShadow);
            // Fragment-side knobs for the Baked mode (BgiSampleShadowTexture). Both are pure read
            // parameters, so they take effect immediately without a re-solve.
            Shader.SetGlobalFloat(s_shadowSharpness, Mathf.Max(1f, _bakedShadowSharpness));
            Shader.SetGlobalFloat(s_shadowNormalOffset, Mathf.Clamp(_shadowNormalOffset, 1f, 3f));
            PublishOcclusionSources();
            // Mirrored irradiance textures (the fragment read source), one per field, plus the R16
            // sun-visibility volumes the Baked shadow mode taps. All four are declared unconditionally
            // by the GI_VOXEL_BUFFER variant, so all four must be bound whenever it is claimed.
            Shader.SetGlobalTexture(s_bgiIrradianceTex, _irradianceTex);
            Shader.SetGlobalTexture(s_bgiIrradianceTexCoarse, _irradianceTexCoarse);
            Shader.SetGlobalTexture(s_bgiSunVisTex, _sunVisTex);
            Shader.SetGlobalTexture(s_bgiSunVisTexCoarse, _sunVisTexCoarse);
            // Bound whether or not the snap keyword is on: BufferGiRead declares these unconditionally
            // (a keyword-dependent global set fails WebGPU pipeline creation for the other variant).
            Shader.SetGlobalTexture(s_bgiNeighbourMask, _neighbourMaskTex);
            Shader.SetGlobalTexture(s_bgiNeighbourMaskCoarse, _neighbourMaskTexCoarse);
            // The display transform (_ExposureLinear + the TONEMAP_* keyword) is published by _exposureControl.Apply
            // in Update - explicitly, so a stale value can't darken it.
        }

        // Publish the baked occlusion globals for whichever per-pixel occlusion mode a field asks for.
        // BufferGiUpdater is the sole driver here: the holders no longer self-drive, so nothing is bound
        // (and no idle Update runs) unless a ShadowMode selects it. OcclusionField / Bitmask are
        // fine-volume-bound - meaningful for the fine field; the coarse field is a different volume, so
        // Off / Baked are its only coherent modes (a coarse OcclusionField tap lands outside this
        // texture -> lit). The two publish disjoint globals, so both can be bound the same frame.
        void PublishOcclusionSources() {
            // Lazy-resolve when a mode wants a holder we don't have cached yet: a holder AddComponent'd by
            // its baker after the last volume switch would otherwise stay unseen until a play-mode reload.
            // GetComponent only fires while the ref is null, so this stays free once resolved.
            if (_fineShadow == ShadowMode.OcclusionField || _coarseShadow == ShadowMode.OcclusionField) {
                if (_occField == null && _volume != null) _occField = _volume.GetComponent<VoxelOcclusionField>();
                if (_occField != null && _occField.HasData) _occField.Bind();
            }
            if (_fineShadow == ShadowMode.Bitmask || _coarseShadow == ShadowMode.Bitmask) {
                if (_occBitmask == null && _volume != null) _occBitmask = _volume.GetComponent<VoxelOcclusionBitmask>();
                if (_occBitmask != null && _occBitmask.HasData) _occBitmask.Bind();
            }
        }

        /// <summary>Re-resolve + republish the baked occlusion holders for the updater driving
        /// <paramref name="volume"/>. Called by the occlusion bakers so a fresh bake shows in edit mode
        /// immediately, without entering play: the holders no longer self-publish, and a just-baked
        /// (newly added) holder isn't in the switch-time cache yet.</summary>
        public static void RefreshOcclusionSourcesFor(VoxelVolume volume) {
            if (volume == null) return;
            BufferGiUpdater[] updaters = FindObjectsByType<BufferGiUpdater>();
            for (int i = 0; i < updaters.Length; i++) {
                if (updaters[i]._volume != volume) continue;
                updaters[i]._occField = volume.GetComponent<VoxelOcclusionField>();
                updaters[i]._occBitmask = volume.GetComponent<VoxelOcclusionBitmask>();
                updaters[i].PublishOcclusionSources();
            }
        }

        bool IsReady(out string reason) {
            if (_solveShader == null) { reason = "ComputeShader"; return false; }
            if (_voxelizeShader == null) { reason = "Voxelize Shader (Hidden/Lotec/BufferGiVoxelize)"; return false; }
            if (_volume.BakeRoot == null) { reason = "the volume's MeshBounds root (mesh geometry to voxelize)"; return false; }
            reason = null;
            return true;
        }

        void EnsureInitialized() {
            // IsValid too, not just non-null: after a domain reload the managed field can survive while
            // the native buffer is gone, and the early-return would then keep a dead buffer forever.
            if (_materialBuffer != null && _materialBuffer.IsValid()
                && _radianceBuffer != null && _radianceBuffer.IsValid()
                && _irradianceBuffer != null && _irradianceBuffer.IsValid()
                // The hi-res field is sized by its OWN resolution, so a change there must reallocate
                // even though every lighting buffer is still the right size.
                && _occupancyHiBuffer != null && _occupancyHiBuffer.IsValid() && _allocatedOccGrid == _occGrid) {
                return;
            }
            ReleaseBuffers();

            ResolveBakeShader();

            _clearKernel = RequireKernel(_solveShader, "CSClear");
            _injectKernel = RequireKernel(_solveShader, "CSInject");
            _gatherKernel = RequireKernel(_solveShader, "CSGather");
            _blurKernel = RequireKernel(_solveShader, "CSBlur");
            _sunVisKernel = RequireKernel(_solveShader, "CSSunVisibility");
            _initFineKernel = RequireKernel(_solveShader, "CSInitFineFromCoarse");
            _averageLuminanceKernel = RequireKernel(_solveShader, "CSAverageLuminance");
            _buildOccupancyKernel = RequireKernel(_bakeShader, "CSBuildOccupancy");
            _buildTraversalMipKernel = RequireKernel(_bakeShader, "CSBuildTraversalMip");
            _buildNeighbourMaskKernel = RequireKernel(_bakeShader, "CSBuildNeighbourMask");
            _buildNormalOccupancyKernel = RequireKernel(_bakeShader, "CSBuildNormalOccupancy");
            _buildSurfaceKernel = RequireKernel(_bakeShader, "CSBuildSurface");
            _buildAirDistanceKernel = RequireKernel(_bakeShader, "CSBuildAirDistance");
            _injectBakedLightsKernel = RequireKernel(_bakeShader, "CSInjectBakedLights");

            // uint material, uint2 radiance/irradiance. Sized for all fields (concatenated slices).
            _materialBuffer = new ComputeBuffer(TotalVoxels, sizeof(uint));
            // Radiance is the one buffer with a per-voxel STRIDE: RadianceSlots directions per voxel
            // (see RadianceDirections). Slot layout is voxelSlot * RadianceSlots + direction, so the
            // directions of one voxel are contiguous and a slot select is one add - the shader side is
            // BgiRadianceSlot in BufferGiField.hlsl, which is the only place that knows the mode.
            _allocatedRadianceSlots = RadianceSlots;
            _radianceBuffer = new ComputeBuffer(TotalVoxels * _allocatedRadianceSlots, sizeof(uint) * 2);
            _allocatedIrradianceSlots = IrradianceSlots;
            _irradianceBuffer = new ComputeBuffer(TotalVoxels * _allocatedIrradianceSlots, sizeof(uint) * 2);
            _irradianceBlurBuffer = new ComputeBuffer(TotalVoxels * _allocatedIrradianceSlots, sizeof(uint) * 2);
            _surfaceBuffer = new ComputeBuffer(TotalVoxels, sizeof(uint));      // 32-bit surface word/voxel
            _occupancyBuffer = new ComputeBuffer(TotalVoxels / 32, sizeof(uint)); // 1 bit/voxel
            _occupancyThickBuffer = new ComputeBuffer(TotalVoxels / 32, sizeof(uint)); // ditto, bake-only
            _occupancyGrownBuffer = new ComputeBuffer(TotalVoxels / 32, sizeof(uint));  // ditto, raster product
            // Hi-res occupancy + its traversal mip. Both are filled by the bake (raster or asset
            // upload) before anything reads them, but a fresh ComputeBuffer holds GARBAGE, and garbage
            // in an occupancy field reads as scene-wide phantom geometry - so clear at allocation.
            _allocatedOccGrid = _occGrid;
            _occupancyHiBuffer = new ComputeBuffer(TotalOccWords, sizeof(uint));
            _occupancyTraversalBuffer = new ComputeBuffer(TotalVoxels / 32, sizeof(uint));
            ClearOccupancyHi();
            ClearTraversalMip();
            // Each field's blurred irradiance mirrored into a Texture3D for the default trilinear read,
            // plus the matching R16 sun-visibility volume the Baked shadow mode taps.
            _irradianceTex = CreateIrradianceTexture("BgiIrradianceTex");
            _irradianceTexCoarse = CreateIrradianceTexture("BgiIrradianceTexCoarse");
            _sunVisTex = CreateSunVisTexture("BgiSunVisTex");
            _sunVisTexCoarse = CreateSunVisTexture("BgiSunVisTexCoarse");
            _neighbourMaskTex = CreateNeighbourMaskTexture("BgiNeighbourMaskTex");
            _neighbourMaskTexCoarse = CreateNeighbourMaskTexture("BgiNeighbourMaskTexCoarse");
            _materialBaked = false;
            // A freshly allocated ComputeBuffer holds undefined data: request the whole-field clear.
            // Update runs it once the grid constants are bound (CSClear's bounds test needs them).
            _resetAllFields = true;
            _sunVisDirty = true;   // fresh textures hold nothing yet
        }

        // Invalidate the voxelization when one of its actual inputs changed since the last bake:
        //  - wall thickening (it changes the raster itself, not just what is derived from it), or
        //  - the fine/coarse field bounds (a same-volume geometry edit that recomputed MeshBounds, or a
        //    reassigned coarse field). Volume SWITCHES are handled separately by Update's warm-switch.
        // This replaces OnValidate's blanket invalidation, so display/solve tweaks don't re-voxelize.
        void SyncBakeInputs() {
            if (!_materialBaked) return;
            bool changed = _thickenWalls != _thickenWallsBaked
                || !NearlyEqual(_bakedFineOrigin, GridOrigin) || !NearlyEqual(_bakedFineSize, GridSize)
                || !NearlyEqual(_bakedCoarseOrigin, CoarseOrigin) || !NearlyEqual(_bakedCoarseSize, CoarseSize);
#if UNITY_EDITOR
            // The baked lights' LAYOUT (which lights there are, and where) is a voxelization input too,
            // and nothing raises an event when one is added, moved or removed - so poll a cheap hash of
            // it while authoring. Edit mode only, because this forces the whole voxelization again: the
            // inject can stamp a light's new cell but never un-stamp the one it left behind. Switching a
            // light on or off, or retuning it, needs none of that - SyncBakedLightState covers those, at
            // runtime too. Re-collecting here is also how a VoxelLights added since the last voxelize
            // (or a light dragged into an existing list) gets noticed at all.
            if (!changed && !Application.isPlaying
                && UnityEditor.EditorApplication.timeSinceStartup >= _nextBakedLightPoll) {
                _nextBakedLightPoll = UnityEditor.EditorApplication.timeSinceStartup + BakedLightPollInterval;
                LightEmissionBake.CollectHolders(_lightHolders);
                changed = LightEmissionBake.LayoutHash(_lightHolders) != _bakedLightLayout;
            }
#endif
            if (changed) {
                _materialBaked = false;
                _warnedBakeAssetMismatch = false; // inputs changed: re-evaluate (and re-report) the bake match
            }
        }

        void SetGridUniforms(Vector3 origin, Vector3 size, Vector3 voxelSize) {
            _solveShader.SetVector(s_gridOrigin, origin);
            _solveShader.SetVector(s_gridSize, size);
            _solveShader.SetVector(s_voxelSize, voxelSize);
        }

        // Groups to cover ONE field's voxels (each field is dispatched separately with its offset).
        int Groups => Mathf.CeilToInt(_voxelCount / 64f);

        void ClearDynamicFields() {
            for (int f = 0; f < FieldCount; f++) ClearField(f * VoxelCount);
        }

        // Zero one field's radiance + irradiance + blur slice (the blur too, so CSBlur's confidence
        // ease starts from black rather than a stale/garbage value).
        void ClearField(int fieldOffset) {
            if (_clearKernel < 0) return;
            _solveShader.SetBuffer(_clearKernel, s_radiance, _radianceBuffer);
            _solveShader.SetBuffer(_clearKernel, s_irradiance, _irradianceBuffer);
            _solveShader.SetBuffer(_clearKernel, s_irradianceBlur, _irradianceBlurBuffer);
            _solveShader.SetInt(s_fieldOffset, fieldOffset);
            _solveShader.Dispatch(_clearKernel, Groups, 1, 1);
        }

        // Seed the (freshly-switched) fine slice from the coarse field's displayed values, so the fine
        // box starts from the coarse approximation instead of black while it re-converges. Runs after
        // Voxelize so the fine material slice already matches the new bounds.
        void InitFineFromCoarse() {
            if (_initFineKernel < 0) return;
            SetGridUniforms(GridOrigin, GridSize, VoxelSize); // fine grid = voxel world positions
            _solveShader.SetVector(s_coarseGridOrigin, CoarseOrigin);
            _solveShader.SetVector(s_coarseGridVoxelSize, CoarseVoxelSize);
            _solveShader.SetBuffer(_initFineKernel, s_occupancy, _occupancyBuffer);
            _solveShader.SetBuffer(_initFineKernel, s_radiance, _radianceBuffer);
            _solveShader.SetBuffer(_initFineKernel, s_irradiance, _irradianceBuffer);
            _solveShader.SetBuffer(_initFineKernel, s_irradianceBlur, _irradianceBlurBuffer);
            _solveShader.Dispatch(_initFineKernel, Groups, 1, 1);
        }

        // Fill the material/surface slices: upload the disk bakes when the coarse + active-fine assets
        // are present and match, else rasterize the scene geometry. Both paths end in the same GPU
        // derive passes.
        public void Voxelize() {
            if (_voxelizeShader == null || _materialBuffer == null) return;
            // Refresh the baked-light holders before either path stamps them: a level may have loaded, or
            // the bake button may have just filled a list.
            LightEmissionBake.CollectHolders(_lightHolders);
            if (TryLoadBakeAssets()) { _voxelizeSource = "disk bake"; return; }
            _voxelizeSource = "runtime raster";
            VoxelizeScene();
        }

        // Upload the disk-baked slices instead of rasterizing: the coarse asset into the coarse slot +
        // the asset matching the active fine volume into the fine slot. Only the RASTER products are
        // stored; the derive passes re-run on GPU, so the assets survive derive-kernel changes. Needs
        // BOTH the fine match and (when a coarse field exists) the coarse match, else falls back.
        bool TryLoadBakeAssets() {
            List<BufferGiBakeAsset> bakeAssets = BakeAssets;
            if (bakeAssets == null || bakeAssets.Count == 0) return false;

            BufferGiBakeAsset coarse = null, fine = null;
            foreach (BufferGiBakeAsset a in bakeAssets) {
                if (a == null || !BakeAssetValid(a)) continue;
                if (a.isCoarse) {
                    if (HasCoarse && a.MatchesBounds(CoarseOrigin, CoarseSize)) coarse = a;
                } else if (a.MatchesBounds(GridOrigin, GridSize)) {
                    fine = a;
                }
            }

            if (fine == null || (HasCoarse && coarse == null)) {
                if (!_warnedBakeAssetMismatch) {
                    _warnedBakeAssetMismatch = true;
                    LogBakeMismatchDiagnostics(fine, coarse);
                }
                return false;
            }

            // Assemble the full concatenated buffers on the CPU (each field's slice into its slot; the
            // rest, e.g. an absent coarse field, stays zero) and upload in one whole-buffer SetData.
            // Whole-buffer transfers only - the sliced 4-arg SetData/GetData overloads are avoided.
            if (_uploadMaterial == null || _uploadMaterial.Length != TotalVoxels) _uploadMaterial = new uint[TotalVoxels];
            if (_uploadSurface == null || _uploadSurface.Length != TotalVoxels) _uploadSurface = new uint[TotalVoxels];
            if (_occHiUpload == null || _occHiUpload.Length != TotalOccWords) _occHiUpload = new uint[TotalOccWords];
            if (_grownUpload == null || _grownUpload.Length != TotalVoxels / 32) _grownUpload = new uint[TotalVoxels / 32];
            System.Array.Clear(_uploadMaterial, 0, TotalVoxels);
            System.Array.Clear(_uploadSurface, 0, TotalVoxels);
            System.Array.Clear(_occHiUpload, 0, TotalOccWords);
            System.Array.Clear(_grownUpload, 0, TotalVoxels / 32);
            CopyFieldSlice(fine, FineField * VoxelCount);
            if (coarse != null) CopyFieldSlice(coarse, CoarseField * VoxelCount);
            UploadOccupancyHiSlice(fine, FineField);
            if (coarse != null) UploadOccupancyHiSlice(coarse, CoarseField);
            _materialBuffer.SetData(_uploadMaterial);
            _surfaceBuffer.SetData(_uploadSurface); // mesh-mode normals; derive rebuilds the rest
            _occupancyHiBuffer.SetData(_occHiUpload);
            _occupancyGrownBuffer.SetData(_grownUpload);
            // The assets carry GEOMETRY only - the baked lights are re-stamped here, live, from the
            // scene's VoxelLights lists. That is what lets a player switch one off (and an author retune
            // one without re-baking); freezing them into the asset would rule out both, and would make a
            // light that moved since the bake burn in two places at once.
            InjectBakedLightsAllFields();
            RunDerivePasses();
            return true;
        }

        // Place one field asset's VoxelCount-word slices into the upload scratch at the field slot.
        void CopyFieldSlice(BufferGiBakeAsset a, int fieldOffset) {
            System.Array.Copy(a.material, 0, _uploadMaterial, fieldOffset, VoxelCount);
            System.Array.Copy(a.surface, 0, _uploadSurface, fieldOffset, VoxelCount);
            // Grown bits, same slice, in WORDS rather than voxels. BakeAssetValid has already checked
            // the length, so a v6 asset always carries this.
            int grownWords = VoxelCount / 32;
            System.Array.Copy(a.occupancyGrown, 0, _grownUpload, (fieldOffset / Mathf.Max(1, VoxelCount)) * grownWords, grownWords);
        }

        // Place one field asset's hi-res occupancy into the upload scratch, OR-DOWNSAMPLING it when
        // the asset was baked finer than this platform runs. That downsample is the whole reason there
        // is one asset instead of one per platform: bake at 256, run at 64 on Quest, 128 on desktop.
        //
        // OR, not majority - the same conservatism the traversal mip needs, and for a sharper reason
        // here. The geometry is thin shells: in a 4x4x4 block an axis-aligned wall one cell thick is
        // 16 of 64 cells, so any >50% rule deletes every wall in the scene. Over-occluding is the safe
        // error; deleting geometry is not.
        void UploadOccupancyHiSlice(BufferGiBakeAsset a, int field) {
            int dst = field * OccWordsPerField;
            if (a.occGrid == OccGrid) {
                System.Array.Copy(a.occupancyHi, 0, _occHiUpload, dst, OccWordsPerField);
                return;
            }
            // Per-cell rather than per-word: the block layout interleaves the axes, so there is no
            // word-level shortcut. Runs once at load, over at most 16.7M cells at 256^3.
            int ratioLog2 = 0;
            while ((OccGrid << ratioLog2) < a.occGrid) ratioLog2++;
            int ratio = 1 << ratioLog2;
            int srcBlocks = a.occGrid >> 2, dstBlocks = OccGrid >> 2;
            for (int z = 0; z < OccGrid; z++)
            for (int y = 0; y < OccGrid; y++)
            for (int x = 0; x < OccGrid; x++) {
                bool any = false;
                for (int sz = 0; sz < ratio && !any; sz++)
                for (int sy = 0; sy < ratio && !any; sy++)
                for (int sx = 0; sx < ratio && !any; sx++) {
                    if (OccBit(a.occupancyHi, 0, srcBlocks,
                               (x << ratioLog2) + sx, (y << ratioLog2) + sy, (z << ratioLog2) + sz)) any = true;
                }
                if (any) SetOccBit(_occHiUpload, dst, dstBlocks, x, y, z);
            }
        }

        // The block-layout addressing, shared by the two helpers below and mirroring BgiOccWord /
        // BgiOccBitMask in BufferGiField.hlsl. Keep the two in step: a divergence here silently
        // reinterprets every baked bit.
        static void OccAddress(int blocksPerAxis, int x, int y, int z, out int word, out uint mask) {
            int block = (x >> 2) + (y >> 2) * blocksPerAxis + (z >> 2) * blocksPerAxis * blocksPerAxis;
            int bit = (x & 3) | ((y & 3) << 2) | ((z & 3) << 4);
            word = block * 2 + (bit >> 5);
            mask = 1u << (bit & 31);
        }

        static bool OccBit(uint[] words, int baseWord, int blocksPerAxis, int x, int y, int z) {
            OccAddress(blocksPerAxis, x, y, z, out int w, out uint m);
            return (words[baseWord + w] & m) != 0u;
        }

        static void SetOccBit(uint[] words, int baseWord, int blocksPerAxis, int x, int y, int z) {
            OccAddress(blocksPerAxis, x, y, z, out int w, out uint m);
            words[baseWord + w] |= m;
        }

        // Structurally usable (right version/grid/size/thickening); bounds are matched separately.
        bool BakeAssetValid(BufferGiBakeAsset a) {
            return a.version == BufferGiBakeAsset.Version && a.grid == Grid
                && a.material != null && a.material.Length == VoxelCount
                && a.surface != null && a.surface.Length == VoxelCount
                && a.thickened == _thickenWalls
                // The hi-res slice must be present, self-consistent, and at least as fine as this
                // platform runs. A coarser asset is REJECTED rather than upsampled: upsampling would
                // invent the sub-voxel detail the field exists to record, and the failure would be
                // invisible - geometry that simply is not there.
                && a.occGrid >= OccGrid
                && a.occupancyHi != null
                && a.occupancyHi.Length == BufferGiBakeAsset.OccWordsFor(a.occGrid)
                // The grown bitfield is a v6 raster product and has no reconstruction path, so an asset
                // missing it is rejected rather than loaded with the gate silently disabled.
                && a.occupancyGrown != null
                && a.occupancyGrown.Length == VoxelCount / 32;
        }

        // One-shot dump of WHY no disk bake matched, so a bundle/build discrepancy (unresolved
        // reference, empty arrays after serialization, bounds/normal drift) can be read straight from
        // the Console. Prints the active volume's expectations, then each candidate asset's actual
        // fields and the two gates it must pass: BakeAssetValid (structure) and bounds match.
        void LogBakeMismatchDiagnostics(BufferGiBakeAsset fine, BufferGiBakeAsset coarse) {
            var sb = new System.Text.StringBuilder();
            string missing = fine == null ? "the active FINE volume" : "the COARSE field";
            sb.AppendLine($"Buffer GI: no matching disk bake for {missing}; voxelizing at runtime instead. Diagnostics:");
            sb.AppendLine($"  expected: grid={Grid} version={BufferGiBakeAsset.Version} VoxelCount={VoxelCount} thickened={_thickenWalls} occGrid>={OccGrid}");
            sb.AppendLine($"  expected FINE   origin={GridOrigin.ToString("F4")} size={GridSize.ToString("F4")}");
            sb.AppendLine($"  HasCoarse={HasCoarse}" + (HasCoarse ? $" expected COARSE origin={CoarseOrigin.ToString("F4")} size={CoarseSize.ToString("F4")}" : ""));
            List<BufferGiBakeAsset> bakeAssets = BakeAssets;
            sb.AppendLine($"  BufferGiFields={(_fields != null ? _fields.name : "<none>")} bakeAssets.Count={(bakeAssets == null ? -1 : bakeAssets.Count)}");
            if (bakeAssets != null) {
                for (int i = 0; i < bakeAssets.Count; i++) {
                    BufferGiBakeAsset a = bakeAssets[i];
                    if (a == null) {
                        sb.AppendLine($"  [{i}] <null> - reference did not resolve (asset not in the bundle?).");
                        continue;
                    }
                    Vector3 eo = a.isCoarse ? CoarseOrigin : GridOrigin;
                    Vector3 es = a.isCoarse ? CoarseSize : GridSize;
                    bool boundsMatch = a.MatchesBounds(eo, es) && (!a.isCoarse || HasCoarse);
                    sb.AppendLine(
                        $"  [{i}] '{a.name}' isCoarse={a.isCoarse} version={a.version} grid={a.grid} " +
                        $"content=0x{a.ContentHash():X8} " +
                        $"material={(a.material == null ? "null" : a.material.Length.ToString())} " +
                        $"surface={(a.surface == null ? "null" : a.surface.Length.ToString())} " +
                        $"occGrid={a.occGrid} occupancyHi={(a.occupancyHi == null ? "null" : a.occupancyHi.Length.ToString())} " +
                        $"thickened={a.thickened} " +
                        $"origin={a.origin.ToString("F4")} size={a.size.ToString("F4")} " +
                        $"=> valid={BakeAssetValid(a)} boundsMatch={boundsMatch}");
                }
            }
            Debug.LogWarning(sb.ToString(), this);
        }

        // Bounds must match within a millimetre: the baked voxel content is only valid for the exact
        // grid mapping it was rasterized against.
        static bool NearlyEqual(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-6f;

        // Editor-side capture of ONE field: rasterize just this field's geometry into its slice and
        // read the raster products + grid metadata back into the asset (no derive - that re-runs at
        // load). Synchronous GPU readback; meant for the editor bake button, not per-frame use.
        // A detailed field's runtime grid is its sibling VoxelVolume's padded bounds, so pass those.
        public bool CaptureFieldToAsset(BufferGiBakeAsset asset, bool isCoarse, Transform root, Vector3 origin, Vector3 size) {
            if (asset == null) return false;
            if (_solveShader == null || _voxelizeShader == null) {
                Debug.LogError("Buffer GI can't capture a field bake: assign the compute + voxelize shaders first.", this);
                return false;
            }
            if (root == null) {
                Debug.LogError("Buffer GI can't capture a field bake: the field has no mesh root.", this);
                return false;
            }
            // Resolve the grid from the active volume before allocating (the bake button releases the
            // buffers first and doesn't wait for Update, so _grid could be stale). All fields share the
            // active volume's snapped resolution.
            SyncGridResolution();
            // Force valid buffers up front - the editor pump may not have run EnsureInitialized yet
            // (freshly enabled, or just after a domain reload), and RasterizeFieldSlice needs them.
            EnsureInitialized();
            int fieldOffset = (isCoarse ? CoarseField : FineField) * VoxelCount;
            RasterizeFieldSlice(root, origin, size, fieldOffset);

            asset.version = BufferGiBakeAsset.Version;
            asset.grid = Grid;
            asset.isCoarse = isCoarse;
            asset.thickened = _thickenWalls;
            asset.origin = origin;
            asset.size = size;
            if (asset.material == null || asset.material.Length != VoxelCount) asset.material = new uint[VoxelCount];
            if (asset.surface == null || asset.surface.Length != VoxelCount) asset.surface = new uint[VoxelCount];
            // Whole-buffer readback + managed slice copy (avoids the sliced 4-arg GetData overload).
            if (_fullReadback == null || _fullReadback.Length != TotalVoxels) _fullReadback = new uint[TotalVoxels];
            _materialBuffer.GetData(_fullReadback);
            System.Array.Copy(_fullReadback, fieldOffset, asset.material, 0, VoxelCount);
            _surfaceBuffer.GetData(_fullReadback);
            System.Array.Copy(_fullReadback, fieldOffset, asset.surface, 0, VoxelCount);

            // Hi-res occupancy: store ONLY the finest level that was baked, at its own resolution. A
            // platform running coarser OR-downsamples it at load (see UploadOccupancyHiSlice), which is
            // what lets ONE asset serve 64 / 128 / 256 instead of multiplying build variants. 2 MB per
            // field on disk at 256^3 - the cheapest field in the system by a wide margin.
            asset.occGrid = OccGrid;
            if (asset.occupancyHi == null || asset.occupancyHi.Length != OccWordsPerField)
                asset.occupancyHi = new uint[OccWordsPerField];
            if (_occHiReadback == null || _occHiReadback.Length != TotalOccWords) _occHiReadback = new uint[TotalOccWords];
            _occupancyHiBuffer.GetData(_occHiReadback);
            System.Array.Copy(_occHiReadback, (fieldOffset / Mathf.Max(1, VoxelCount)) * OccWordsPerField,
                asset.occupancyHi, 0, OccWordsPerField);

            // Grown occupancy, the other raster product. Word-aligned per field: VoxelCount is a power
            // of two >= 32, so a field slice never starts mid-word and the copy is a plain range.
            int grownWords = VoxelCount / 32;
            if (asset.occupancyGrown == null || asset.occupancyGrown.Length != grownWords)
                asset.occupancyGrown = new uint[grownWords];
            if (_grownReadback == null || _grownReadback.Length != TotalVoxels / 32)
                _grownReadback = new uint[TotalVoxels / 32];
            _occupancyGrownBuffer.GetData(_grownReadback);
            System.Array.Copy(_grownReadback, (fieldOffset / Mathf.Max(1, VoxelCount)) * grownWords,
                asset.occupancyGrown, 0, grownWords);
            return true;
        }

        // Resolve a detailed field's voxelize inputs: geometry root (the MeshBounds root) + the runtime
        // fine grid (its sibling VoxelVolume's padded, voxel-aligned bounds - what the fragment reads,
        // so the bake must match it). Returns false (with a warning) if there's no VoxelVolume sibling.
        public bool TryGetDetailedFieldGrid(MeshBounds field, out Transform root, out Vector3 origin, out Vector3 size) {
            root = null; origin = Vector3.zero; size = Vector3.one;
            if (field == null) return false;
            VoxelVolume vv = field.GetComponent<VoxelVolume>();
            if (vv == null) {
                Debug.LogWarning($"Buffer GI detailed field '{field.name}' has no VoxelVolume sibling; skipping (its runtime grid is undefined).", field);
                return false;
            }
            root = field.Root != null ? field.Root : vv.BakeRoot;
            origin = vv.Bounds.min;
            size = vv.Bounds.size;
            return root != null;
        }

        // Rasterize one field's geometry into its buffer slice. Clears the WHOLE buffer first (whole-
        // buffer SetData only; rasterization then writes just this field's covered voxels). The other
        // field's transient content doesn't matter - capture reads back only this field's slice, and
        // the runtime reload reassembles both fields afterwards. Shared setup with VoxelizeScene.
        // The voxelizer's one remaining switch, applied here so the two rasterization entry points
        // (disk bake and runtime voxelize) can never drift apart: BGI_THICKEN grows each solid one
        // voxel inward. The triangle normal is now written unconditionally - CSBuildSurface prefers
        // the occupancy gradient anyway and consults the triangle only where the gradient cancels
        // (sub-voxel walls), so withholding it bought nothing and cost those cells their orientation.
        void ApplyVoxelizeKeywords() {
            if (_thickenWalls) _voxelizeMaterial.EnableKeyword(ThickenKeyword);
            else _voxelizeMaterial.DisableKeyword(ThickenKeyword);
        }

        // The bit-only HI-RES occupancy raster (pass 1). A second command buffer rather than more draws
        // in the first: it needs its own dummy render target, sized to the OCCUPANCY grid, or the
        // rasterizer generates one fragment per lighting cell and most hi-res cells are never visited.
        //
        // Always on. A conditional hi-res bake creates a "did I bake?" failure mode - a scene that
        // renders correctly in the editor and drops every sub-voxel occluder in a build - which is a
        // far worse trade than the bake time. Revisit only if bake time actually hurts authoring.
        //
        // `fields` lists (root, origin, size, field index) so both fields go into one command buffer
        // and one clear: the clear covers ALL fields, and the kernel only ORs, so a per-field clear
        // would wipe the field rasterized before it.
        void RasterizeOccupancyHi(CommandBuffer cmd, Transform root, Vector3 origin, Vector3 size, int field) {
            if (root == null) return;
            VoxelizeFieldInto(cmd, root, origin, size, size / OccGrid, field * VoxelCount, 1);
        }

        // Set up, run and tear down the hi-res raster for a list of fields. Separate from the material
        // raster because the render target differs; shares _voxelizeMaterial, which the caller must
        // already have created (this is called from paths that do not go through VoxelizeScene).
        void RunOccupancyHiRaster(System.Action<CommandBuffer> record) {
            if (_occupancyHiBuffer == null || _voxelizeShader == null) return;
            if (_voxelizeMaterial == null) {
                _voxelizeMaterial = new Material(_voxelizeShader) { hideFlags = HideFlags.HideAndDontSave };
            }
            ApplyVoxelizeKeywords();
            ClearOccupancyHi();

            RenderTexture dummy = RenderTexture.GetTemporary(OccGrid, OccGrid, 0,
                RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
            var cmd = new CommandBuffer { name = "BufferGI Voxelize Occupancy (hi-res)" };
            cmd.SetRenderTarget(dummy);
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.SetRandomWriteTarget(1, _occupancyHiBuffer); // u1, the slot pass 0 uses for _MaterialWrite
            record(cmd);
            cmd.ClearRandomWriteTargets();
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            RenderTexture.ReleaseTemporary(dummy);
        }

        void RasterizeFieldSlice(Transform root, Vector3 origin, Vector3 size, int fieldOffset) {
            if (_voxelizeMaterial == null) {
                _voxelizeMaterial = new Material(_voxelizeShader) { hideFlags = HideFlags.HideAndDontSave };
            }
            ApplyVoxelizeKeywords();

            if (_materialClear == null || _materialClear.Length != TotalVoxels) _materialClear = new uint[TotalVoxels];
            _materialBuffer.SetData(_materialClear);
            _surfaceBuffer.SetData(_materialClear);
            ClearGrown();

            RenderTexture dummy = RenderTexture.GetTemporary(Grid, Grid, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
            CommandBuffer cmd = new CommandBuffer { name = "BufferGI Voxelize Field" };
            cmd.SetRenderTarget(dummy);
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.SetRandomWriteTarget(1, _materialBuffer);
            cmd.SetRandomWriteTarget(2, _surfaceBuffer);
            cmd.SetRandomWriteTarget(3, _occupancyGrownBuffer); // u3 = _GrownWrite
            VoxelizeFieldInto(cmd, root, origin, size, size / Grid, fieldOffset);
            cmd.ClearRandomWriteTargets();
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            RenderTexture.ReleaseTemporary(dummy);
            // Hi-res occupancy for the same field, so the capture below can store it. The clear inside
            // covers every field, which is correct here: this method rasterizes ONE field and reads
            // back only that field's slice.
            RunOccupancyHiRaster(hi => RasterizeOccupancyHi(hi, root, origin, size, fieldOffset / Mathf.Max(1, VoxelCount)));
            // No baked-light injection: the asset this capture reads back stores GEOMETRY only, and the
            // lights are stamped on top of it every time it is uploaded (see TryLoadBakeAssets).
        }

        // GPU 3-axis rasterization of the volume's mesh geometry into each field's material slice.
        // One-shot (geometry is static): clears the whole buffer, then rasterizes the fine and coarse
        // fields, each into its own slice with its own grid; each fragment writes via a fragment UAV.
        void VoxelizeScene() {
            Transform fineRoot = _volume.BakeRoot;
            if (fineRoot == null) { _materialBaked = true; return; }

            if (_voxelizeMaterial == null) {
                _voxelizeMaterial = new Material(_voxelizeShader) { hideFlags = HideFlags.HideAndDontSave };
            }
            ApplyVoxelizeKeywords();

            // Rasterization only writes covered voxels, so clear all field slices to empty first.
            if (_materialClear == null || _materialClear.Length != TotalVoxels) _materialClear = new uint[TotalVoxels];
            _materialBuffer.SetData(_materialClear);
            // Clear _Surface (zeros = a valid default normal via BgiSurfaceNormal) so a solid voxel a
            // degenerate-normal triangle leaves unwritten (mesh mode) reads a deterministic value.
            _surfaceBuffer.SetData(_materialClear);
            ClearGrown();

            RenderTexture dummy = RenderTexture.GetTemporary(Grid, Grid, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
            CommandBuffer cmd = new CommandBuffer { name = "BufferGI Voxelize" };
            cmd.SetRenderTarget(dummy);
            // We output clip space directly from the vertex shader, so neutralize the view-projection.
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.SetRandomWriteTarget(1, _materialBuffer);
            cmd.SetRandomWriteTarget(2, _surfaceBuffer); // u2 = _SurfaceWrite
            cmd.SetRandomWriteTarget(3, _occupancyGrownBuffer); // u3 = _GrownWrite

            // Each field rasterizes its OWN volume's geometry into its slice (coarse = a separate,
            // scene-covering MeshBounds with its own root).
            VoxelizeFieldInto(cmd, fineRoot, GridOrigin, GridSize, VoxelSize, FineField * VoxelCount);
            Transform coarseRoot = HasCoarse ? _fields.CoarseField.Root : null;
            if (coarseRoot != null) {
                VoxelizeFieldInto(cmd, coarseRoot, CoarseOrigin, CoarseSize, CoarseVoxelSize, CoarseField * VoxelCount);
            }

            cmd.ClearRandomWriteTargets();
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            RenderTexture.ReleaseTemporary(dummy);

            // The same geometry again, bit only, at the occupancy grid.
            RunOccupancyHiRaster(hi => {
                RasterizeOccupancyHi(hi, fineRoot, GridOrigin, GridSize, FineField);
                if (coarseRoot != null) RasterizeOccupancyHi(hi, coarseRoot, CoarseOrigin, CoarseSize, CoarseField);
            });

            // Baked lights become emissive voxels in each field, after the raster (geometry must not
            // overwrite a light voxel) and before the derive passes (which turn _Material into the
            // occupancy bitfield, so the light voxel comes out solid + emissive like any other emitter).
            InjectBakedLightsAllFields();

            RunDerivePasses();
        }

        // Stamp the VoxelLights' baked point lights into BOTH fields, each against its own grid: a light
        // inside a detailed volume has to burn in the coarse field too, or it would wink out the moment
        // the camera leaves the detailed box. Each inject drops whatever falls outside its grid.
        void InjectBakedLightsAllFields() {
            InjectBakedLights(GridOrigin, VoxelSize, FineField * VoxelCount);
            if (HasCoarse) InjectBakedLights(CoarseOrigin, CoarseVoxelSize, CoarseField * VoxelCount);
        }

        // Stamp the baked point lights inside this field's grid into its material slice as emissive
        // voxels (see LightEmissionBake). One dispatch per 16 lit voxels.
        void InjectBakedLights(Vector3 origin, Vector3 voxelSize, int fieldOffset) {
            if (_injectBakedLightsKernel < 0 || _materialBuffer == null || _surfaceBuffer == null) return;
            // The kernel's BgiSlot index math reads the grid constants, and the editor capture path
            // (CaptureFieldToAsset) never goes through Update - so bind them here rather than assume.
            BindGridConstantsToCompute();
            _bakeShader.SetInt(s_fieldOffset, fieldOffset);
            LightEmissionBake.Inject(_bakeShader, _injectBakedLightsKernel,
                _materialBuffer, _surfaceBuffer, _lightHolders, origin, voxelSize, Grid);
        }

        // The runtime light switch. Re-inject when a baked light's on/off state (or colour/intensity)
        // changed: only the EMISSION byte of its voxel depends on that - LightEmissionBake stamps the
        // albedo from every listed light, switched on or not - so the occupancy bitfield and the surface
        // + air-distance fields built from it are already correct and no derive pass has to rerun. The
        // whole cost is one 64-thread dispatch per field, plus re-solving the field.
        //
        // Polled rather than event-driven: nothing raises an event when a Light component is ticked off,
        // and this walks a handful of serialized references, so checking every frame is cheaper than the
        // machinery needed to avoid checking.
        void SyncBakedLightState() {
            if (!_materialBaked) return;
            int state = LightEmissionBake.StateHash(_lightHolders);
            if (state == _bakedLightState) return;
            _bakedLightState = state;
            InjectBakedLightsAllFields();
            // Spend the ray budget again. The solve is a progressive average, so without this a settled
            // field would go on displaying the light that was just switched off - and an idle one would
            // never even re-read the new emission. Same reset a moved sun takes.
            _collectedSamples = 0;
        }

        // A light that is BOTH baked into the voxelization and published as a realtime local light is
        // counted twice: its emissive voxel lights the room through the solve while the fragment path
        // adds the same light again directly. Report it once - it is an authoring mistake, and which of
        // the two lists to drop it from is not a call this component can make.
        void WarnIfBakedLightAlsoRealtime() {
            if (_warnedBakedLightAlsoRealtime) return;
            LocalLightsPublisher publisher = LocalLightsPublisher.Instance;
            if (publisher == null) return;
            // The EFFECTIVE realtime set, not just the publisher's own list: the same clash is just as
            // likely from a level's LocalLightsProvider. Gathered on demand rather than read off the
            // publisher's last frame, because the editor bake path runs outside the Update order.
            if (_realtimeLightScratch == null) _realtimeLightScratch = new List<Light>();
            publisher.GatherLights(_realtimeLightScratch);
            List<Light> realtime = _realtimeLightScratch;
            foreach (VoxelLights holder in _lightHolders) {
                if (holder == null) continue;
                IReadOnlyList<Light> baked = holder.Lights;
                for (int i = 0; i < baked.Count; i++) {
                    if (baked[i] == null) continue;
                    for (int j = 0; j < realtime.Count; j++) {
                        if (realtime[j] != baked[i]) continue;
                        _warnedBakedLightAlsoRealtime = true;
                        Debug.LogWarning(
                            $"Buffer GI: light '{baked[i].name}' is baked into the voxelization (listed on " +
                            $"'{holder.name}'s Voxel Lights) AND published as a realtime local light by " +
                            $"'{publisher.name}' (its own list or a level's LocalLightsProvider). It lights " +
                            "the scene twice - remove it from one of the two.",
                            baked[i]);
                        return;
                    }
                }
            }
        }

        // Bake-time derive passes (both fields; un-voxelized coarse slice packs to zeros):
        // 1) occupancy bitfield from _Material, 2) surface word - CSBuildSurface rebuilds the normal
        // from the now-complete occupancy, falling back to the triangle normal the voxelizer wrote
        // only where the gradient cancels; it also bakes the sun-ray origin and seeds the air-distance
        // field, 3) relax the air-distance transform to convergence. Shared by both voxelize paths.
        void RunDerivePasses() {
            for (int f = 0; f < FieldCount; f++) BuildOccupancy(f * VoxelCount);
            // The traversal mip is derived, never rasterized: OR-downsampling the hi-res field is the
            // only construction that cannot under-estimate, and a coarse level that under-estimates
            // silently skips occluders. Zeroed once for ALL fields - the kernel only ORs.
            ClearTraversalMip();
            for (int f = 0; f < FieldCount; f++) BuildTraversalMip(f);
            // Zero the normal-occupancy scratch once for ALL fields: its kernel only ORs bits in, and
            // it is dispatched per field below.
            int occWords = TotalVoxels / 32;
            if (_occupancyClear == null || _occupancyClear.Length != occWords) _occupancyClear = new uint[occWords];
            _occupancyThickBuffer.SetData(_occupancyClear);
            for (int f = 0; f < FieldCount; f++) BuildNormalOccupancy(f * VoxelCount);
            for (int f = 0; f < FieldCount; f++) BuildSurface(f * VoxelCount);
            for (int f = 0; f < FieldCount; f++) BuildAirDistance(f * VoxelCount);
            // Pure geometry, so it belongs here with the rest of the bake and NOT on the sun-change
            // path: a read-side gate that moved when the light moved would be a different feature.
            BuildNeighbourMask(CoarseField, _neighbourMaskTexCoarse);
            BuildNeighbourMask(FineField, _neighbourMaskTex);
            // Snapshot the inputs this voxelization used, so SyncBakeInputs can tell when they change.
            _thickenWallsBaked = _thickenWalls;
            _bakedFineOrigin = GridOrigin; _bakedFineSize = GridSize;
            _bakedCoarseOrigin = CoarseOrigin; _bakedCoarseSize = CoarseSize;
            // The baked lights were just injected, so record their state: without this the per-frame
            // switch check would immediately re-inject what this voxelization already stamped.
            _bakedLightState = LightEmissionBake.StateHash(_lightHolders);
#if UNITY_EDITOR
            _bakedLightLayout = LightEmissionBake.LayoutHash(_lightHolders);
#endif
            WarnIfBakedLightAlsoRealtime();
            _materialBaked = true;
            // New geometry: the baked sun shadow is stale whatever the sun is doing.
            _sunVisDirty = true;
            // A fresh voxelization invalidates the solved field (new geometry, or a baked light that
            // moved/changed): spend the ray budget again, or a settled solve would idle on the old one.
            _collectedSamples = 0;
        }

        // Pack one field's _Material occupancy into the 1-bit/voxel _Occupancy bitfield (1024 words,
        // one thread per word). Runs first so CSBuildSurface's gradient sees complete occupancy.
        void BuildOccupancy(int fieldOffset) {
            if (_buildOccupancyKernel < 0) return;
            _bakeShader.SetInt(s_fieldOffset, fieldOffset);
            _bakeShader.SetBuffer(_buildOccupancyKernel, s_material, _materialBuffer);
            _bakeShader.SetBuffer(_buildOccupancyKernel, s_occupancy, _occupancyBuffer);
            _bakeShader.Dispatch(_buildOccupancyKernel, Mathf.CeilToInt(VoxelCount / 32f / 64f), 1, 1);
        }

        // Zero the hi-res occupancy (all fields). The raster only ORs bits in, so its target has to
        // start empty; a fresh ComputeBuffer holds garbage rather than zeros.
        void ClearOccupancyHi() {
            if (_occupancyHiBuffer == null) return;
            if (_occHiClear == null || _occHiClear.Length != TotalOccWords) _occHiClear = new uint[TotalOccWords];
            _occupancyHiBuffer.SetData(_occHiClear);
        }

        // Zero the traversal mip (all fields). Same reason: CSBuildTraversalMip only ORs.
        void ClearTraversalMip() {
            if (_occupancyTraversalBuffer == null) return;
            int words = TotalVoxels / 32;
            if (_occupancyClear == null || _occupancyClear.Length != words) _occupancyClear = new uint[words];
            _occupancyTraversalBuffer.SetData(_occupancyClear);
        }

        // Zero the grown bitfield (all fields) before a raster. The voxelizer only ORs bits in - it has
        // to, since neighbouring fragments share bit words - so a stale bit would never be cleared and
        // would permanently seal a cell the geometry no longer covers.
        void ClearGrown() {
            if (_occupancyGrownBuffer == null) return;
            int words = TotalVoxels / 32;
            if (_occupancyClear == null || _occupancyClear.Length != words) _occupancyClear = new uint[words];
            _occupancyGrownBuffer.SetData(_occupancyClear);
        }

        // OR-downsample one field's hi-res occupancy onto the lighting grid. Runs after the hi-res
        // field is filled (raster or asset upload) and needs no other derive product, so it sits at
        // the front of the chain beside BuildOccupancy.
        void BuildTraversalMip(int field) {
            if (_buildTraversalMipKernel < 0 || _occupancyHiBuffer == null) return;
            _bakeShader.SetInt(s_fieldOffset, field * VoxelCount);
            _bakeShader.SetInt(s_occFieldWordOffset, field * OccWordsPerField);
            _bakeShader.SetBuffer(_buildTraversalMipKernel, s_occupancyHi, _occupancyHiBuffer);
            _bakeShader.SetBuffer(_buildTraversalMipKernel, s_occupancyTraversal, _occupancyTraversalBuffer);
            _bakeShader.Dispatch(_buildTraversalMipKernel, Groups, 1, 1);
        }

        // One field's 7-bit neighbour-solidity mask, for the in-plane snap's read-side gate. Reads the
        // finished lighting-grid bitfield, so it must run after BuildOccupancy; nothing else depends on
        // it, so it can sit anywhere after that. Writes every texel, so the volume needs no clear.
        void BuildNeighbourMask(int field, RenderTexture tex) {
            if (_buildNeighbourMaskKernel < 0 || tex == null) return;
            _bakeShader.SetInt(s_fieldOffset, field * VoxelCount);
            _bakeShader.SetBuffer(_buildNeighbourMaskKernel, s_occupancy, _occupancyBuffer);
            _bakeShader.SetTexture(_buildNeighbourMaskKernel, s_bgiNeighbourMaskWrite, tex);
            _bakeShader.Dispatch(_buildNeighbourMaskKernel, Groups, 1, 1);
        }

        // Fill one field's _OccupancyThick: real solids + the cell behind each surfaced voxel, along
        // the TRIANGLE normal the voxelizer left in _Surface. Must run after BuildOccupancy (it reads
        // the finished bitfield) and before BuildSurface (which overwrites those normal bits). The
        // buffer is zeroed once for all fields by the caller - the kernel only ORs bits in.
        void BuildNormalOccupancy(int fieldOffset) {
            if (_buildNormalOccupancyKernel < 0) return;
            _bakeShader.SetInt(s_fieldOffset, fieldOffset);
            _bakeShader.SetBuffer(_buildNormalOccupancyKernel, s_occupancy, _occupancyBuffer);
            _bakeShader.SetBuffer(_buildNormalOccupancyKernel, s_occupancyThick, _occupancyThickBuffer);
            _bakeShader.SetBuffer(_buildNormalOccupancyKernel, s_surface, _surfaceBuffer);
            _bakeShader.Dispatch(_buildNormalOccupancyKernel, Groups, 1, 1);
        }

        // Fill one field's _Surface word (per voxel): the gradient normal (gradient mode; mesh mode
        // keeps the voxelizer's) + the static openness/AO (both modes). Future air-distance/flags too.
        void BuildSurface(int fieldOffset) {
            if (_buildSurfaceKernel < 0) return;
            _bakeShader.SetInt(s_fieldOffset, fieldOffset);
            // The two-sided test measures real slab thickness on the HI-RES grid, so this pass needs
            // the hi-res field and its per-field word offset alongside the lighting-grid ones.
            _bakeShader.SetInt(s_occFieldWordOffset,
                (fieldOffset / Mathf.Max(1, VoxelCount)) * OccWordsPerField);
            _bakeShader.SetBuffer(_buildSurfaceKernel, s_occupancy, _occupancyBuffer);
            _bakeShader.SetBuffer(_buildSurfaceKernel, s_occupancyThick, _occupancyThickBuffer);
            _bakeShader.SetBuffer(_buildSurfaceKernel, s_occupancyHi, _occupancyHiBuffer);
            _bakeShader.SetBuffer(_buildSurfaceKernel, s_surface, _surfaceBuffer);
            _bakeShader.Dispatch(_buildSurfaceKernel, Groups, 1, 1);
        }

        // Relax one field's AIR-voxel city-block distance-to-nearest-solid (CSBuildSurface seeded it at
        // the cap). Each pass extends the front by one voxel, so AirDistancePasses passes converge the
        // whole capped field. Feeds the far-air gather skip. Solid voxels are untouched (distance-0 seeds).
        void BuildAirDistance(int fieldOffset) {
            if (_buildAirDistanceKernel < 0) return;
            _bakeShader.SetInt(s_fieldOffset, fieldOffset);
            _bakeShader.SetBuffer(_buildAirDistanceKernel, s_occupancy, _occupancyBuffer);
            _bakeShader.SetBuffer(_buildAirDistanceKernel, s_surface, _surfaceBuffer);
            for (int pass = 0; pass < AirDistancePasses; pass++)
                _bakeShader.Dispatch(_buildAirDistanceKernel, Groups, 1, 1);
        }

        // Rasterize a volume's geometry into one field's slice. The voxelize shader reads the grid +
        // field offset as globals (BgiWorldToGrid / BgiSlot), so set them before the draws.
        // `pass` picks the shader pass: 0 = material + surface normal at the LIGHTING grid,
        // 1 = the bit-only hi-res occupancy at the OCCUPANCY grid (see BufferGiVoxelize.shader).
        void VoxelizeFieldInto(CommandBuffer cmd, Transform root, Vector3 origin, Vector3 size, Vector3 voxelSize, int fieldOffset, int pass = 0) {
            // Same eligibility as the volume bounds / SDF bake (active + static + casts shadows):
            // inactive meshes must not light the scene, non-static ones wouldn't track movement anyway
            // (the voxelization only reruns on a re-bake), and a Cast Shadows = Off renderer is a VFX
            // card that must not become a solid occluder (see MeshBounds.IsBakeEligible).
            MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>();
            cmd.SetGlobalVector(s_gridOrigin, origin);
            cmd.SetGlobalVector(s_gridSize, size);
            cmd.SetGlobalVector(s_voxelSize, voxelSize);
            cmd.SetGlobalInt(s_fieldOffset, fieldOffset);
            // Grid resolution for the voxelizer's bounds check + BgiSlot index math.
            cmd.SetGlobalInt(s_bgiGrid, _grid);
            cmd.SetGlobalInt(s_bgiGridLog2, _gridLog2);
            cmd.SetGlobalInt(s_bgiCount, _voxelCount);
            // The hi-res pass indexes a different grid AND a different per-field word offset (the two
            // grids have different counts), so both go out here rather than being derived shader-side.
            cmd.SetGlobalInt(s_bgiOccGrid, _occGrid);
            cmd.SetGlobalInt(s_bgiOccGridLog2, _occGridLog2);
            cmd.SetGlobalInt(s_occFieldWordOffset, (fieldOffset / Mathf.Max(1, VoxelCount)) * OccWordsPerField);

            for (int axis = 0; axis < 3; axis++) {
                cmd.SetGlobalInt(s_voxAxis, axis);
                foreach (MeshRenderer mr in renderers) {
                    if (mr == null || !MeshBounds.IsBakeEligible(mr) || !mr.TryGetComponent(out MeshFilter mf)) continue;
                    Mesh mesh = mf.sharedMesh;
                    if (mesh == null) continue;
                    Matrix4x4 l2w = mr.transform.localToWorldMatrix;
                    Material[] mats = mr.sharedMaterials;
                    int subMeshCount = Mathf.Max(1, mesh.subMeshCount);

                    for (int sm = 0; sm < subMeshCount; sm++) {
                        Material src = (mats != null && sm < mats.Length) ? mats[sm] : null;
                        GetMaterialVoxelProps(src, out Color albedo, out float emission8,
                            out Texture baseMap, out Vector4 baseMapST, out float cutoff);
                        // Fresh MPB per draw so each submesh's props are captured independently.
                        MaterialPropertyBlock mpb = new MaterialPropertyBlock();
                        mpb.SetColor(s_voxAlbedo, albedo);
                        mpb.SetFloat(s_voxEmission, emission8);
                        mpb.SetTexture(s_voxBaseMap, baseMap != null ? baseMap : Texture2D.whiteTexture);
                        mpb.SetVector(s_voxBaseMapST, baseMapST);
                        mpb.SetFloat(s_voxCutoff, cutoff);
                        cmd.DrawMesh(mesh, l2w, _voxelizeMaterial, sm, pass, mpb);
                    }
                }
            }
        }

        const float EmissionIntensityMax = 1024f;

        // Voxelizer inputs from a scene material: base color+alpha, emission, the base-map texture
        // (sampled in the voxelize fragment so per-voxel albedo picks up the texture's local color)
        // and the alpha-clip threshold. cutoff = 0 for opaque materials (their base-map alpha is often
        // repurposed data and must never punch holes); alpha-clipped materials use their _Cutoff;
        // plain transparent materials (render queue) use 0.5 - a mostly-transparent voxel (window)
        // stays EMPTY, so it neither occupies nor blocks GI rays.
        static void GetMaterialVoxelProps(Material mat, out Color albedo, out float emission8,
                out Texture baseMap, out Vector4 baseMapST, out float cutoff) {
            albedo = Color.white;
            baseMap = null;
            baseMapST = new Vector4(1f, 1f, 0f, 0f);
            cutoff = 0f;
            float emission = 0f;
            if (mat != null) {
                if (mat.HasProperty("_BaseColor")) albedo = mat.GetColor("_BaseColor");
                else if (mat.HasProperty("_Color")) albedo = mat.GetColor("_Color");

                string texProp = mat.HasProperty("_BaseMap") ? "_BaseMap"
                    : (mat.HasProperty("_MainTex") ? "_MainTex" : null);
                if (texProp != null) {
                    baseMap = mat.GetTexture(texProp);
                    Vector2 sc = mat.GetTextureScale(texProp);
                    Vector2 off = mat.GetTextureOffset(texProp);
                    baseMapST = new Vector4(sc.x, sc.y, off.x, off.y);
                }

                bool alphaClip = mat.HasProperty("_AlphaClip")
                    ? mat.GetFloat("_AlphaClip") > 0.5f
                    : mat.IsKeywordEnabled("_ALPHATEST_ON");
                bool transparent = (mat.HasProperty("_Surface") && mat.GetFloat("_Surface") > 0.5f)
                    || mat.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.Transparent;
                if (alphaClip) cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f;
                else if (transparent) cutoff = 0.5f;
                else albedo.a = 1f; // opaque: alpha must never clip

                if (mat.HasProperty("_EmissionColor")) {
                    bool on = mat.HasProperty("_Emission") ? mat.GetFloat("_Emission") > 0.5f : mat.IsKeywordEnabled("_EMISSION");
                    if (on) {
                        Color e = mat.GetColor("_EmissionColor");
                        Color eLin = QualitySettings.activeColorSpace == ColorSpace.Gamma ? e.linear : e;
                        emission = Mathf.Max(0f, eLin.maxColorComponent);
                    }
                }
            }
            emission8 = EncodeEmission8(emission);
        }

        // Matches DecodeEmissionIntensityFrom8Bit in Math.hlsl (log2 encoding, max 1024). Internal so
        // LightEmissionBake encodes baked lights into the same packed material word.
        internal static float EncodeEmission8(float intensity) {
            float clamped = Mathf.Clamp(intensity, 0f, EmissionIntensityMax);
            float encoded = Mathf.Log(1f + clamped, 2f) / Mathf.Log(1f + EmissionIntensityMax, 2f);
            return Mathf.Clamp01(Mathf.Round(encoded * 255f) / 255f);
        }

        void DispatchSolve(int sampleBase) {
            if (_injectKernel < 0 || _gatherKernel < 0 || !_materialBaked) return;

            // Per-frame shared uniforms (same for every field).
            _solveShader.SetInt(s_frameCount, Time.frameCount);
            _solveShader.SetInt(s_samplesPerFrame, Mathf.Max(1, _samplesPerFrame));
            _solveShader.SetInt(s_sampleBase, sampleBase);
            // Progressive gather weight (CSGather) + convergence confidence 0->1 (CSBlur, hides the
            // noisy warm-up). Both derive from _collectedSamples so they stay aligned.
            _solveShader.SetFloat(s_emaWeight, EmaWeight);
            _solveShader.SetFloat(s_confidence, Confidence);
            _solveShader.SetFloat(s_giFireflyClamp, _giFireflyClamp);
            _solveShader.SetFloat(s_reachBoost, _reachBoost);
            // Baked sun-shadow estimator. Samples only matter in Supersampled; Centre and Temporal both
            // cast one ray, so pass 1 there to keep the compute's loop bound honest.
            _solveShader.SetInt(s_solveMarchLevel, (int)_solveMarchLevel);
            // Display-side only: it changes which air the shell dilation averages, nothing that traces
            // a ray. A uniform, not a keyword - the branch is uniform across the dispatch and sits
            // outside the 26-neighbour walk, so keeping both paths resident costs nothing and makes
            // the A/B a write rather than a recompile (same argument as _BgiSolveMarchLevel).
            _solveShader.SetInt(s_grownGate, _grownDilationGate ? 1 : 0);
            _solveShader.SetInt(s_injectSunSamples, Mathf.Clamp(_injectSunSamples, 1, 16));
            _solveShader.SetVector(s_ambientFloor, (Vector4)_ambientFloor);
            SetDirectionalLightUniforms();
            LocalLightsPublisher.Instance?.LocalLights?.ApplyToCompute(_solveShader);

            // The EMA blend weight (samplesPerFrame/maxSamples) is computed in the compute itself.
            // CSBlur mirrors each field's blurred irradiance straight into its Texture3D (the fragment's
            // 1-tap read source). No coarse write when there's no coarse field: the read-side bounds check
            // means the coarse texture is never sampled with a valid uvw outside the fine box.
            SolveField(GridOrigin, GridSize, VoxelSize, FineField * VoxelCount, _irradianceTex);
            if (HasCoarse) {
                SolveField(CoarseOrigin, CoarseSize, CoarseVoxelSize, CoarseField * VoxelCount,
                    _irradianceTexCoarse);
            }
        }

        // Texels one CSSunVisibility dispatch may cover, per field. The pass is bounded by this and
        // spent over as many frames as it takes, because the whole volume in one submission is an
        // over-long dispatch at the higher resolutions: 256^3 is 16.7M texels x _sunShadowSamples rays
        // x up to 3*256 DDA steps, and it TDR'd the device outright when it was written that way.
        // 2^15 measured ~10 ms a chunk for both fields on an AMD iGPU. The TOTAL sweep is fixed by the
        // work (~80 ms at 64^3, ~650 ms at 128^3 there); the chunk size only trades how big a
        // per-frame hitch that arrives in. 2^18 was tried first, measured 81 ms in ONE dispatch, and
        // 64 of those back to back at 256^3 is what killed the device.
        const int SunVisTexelsPerDispatch = 1 << 15;

        // Slice of the shadow volume the next chunk starts at. >= ShadowGrid means the current sun
        // direction is fully marched and there is nothing to do.
        int _sunVisSliceBase;
        // The sun moved while a sweep was in flight. The sweep is allowed to finish first (see Update)
        // and the next one starts immediately after, so a moving sun converges within two sweeps
        // instead of restarting forever.
        bool _sunVisRestartQueued;
        // The sun direction the in-flight sweep is marching against, LATCHED at its start. The whole
        // volume has to be marched against ONE direction: reading the live sun per chunk would give
        // slices at the front of the sweep a different sun from slices at the back, and the seam
        // between them is a sheared shadow rather than a stale one.
        Vector3 _sunVisDir = Vector3.down;

        void StartSunVisibilitySweep() {
            _sunVisSliceBase = 0;
            Light sun = RenderSettings.sun;
            _sunVisDir = sun != null ? -sun.transform.forward : Vector3.down;
        }

        /// <summary>True while the baked sun shadow is still being re-marched after a sun move or a
        /// re-bake. The texture holds a mix of old and new slices until it clears.</summary>
        public bool SunVisibilityPending => _sunVisSliceBase < ShadowGrid;

        // Re-evaluate the baked sun visibility for both fields, at the SHADOW grid, by marching the
        // hi-res occupancy. NOT part of the solve: it depends only on geometry and sun direction, so
        // it runs when one of those changes and idles otherwise - which is also why it can afford a
        // supersampled estimator per texel where the solve could not.
        //
        // One bounded chunk per call. The caller keeps calling while SunVisibilityPending.
        void DispatchSunVisibilityChunk() {
            if (_sunVisKernel < 0 || _occupancyHiBuffer == null || _sunVisTex == null) {
                _sunVisSliceBase = ShadowGrid; // nothing to march into; don't spin
                return;
            }
            // The LATCHED direction, not the live sun (see _sunVisDir). This kernel needs nothing else
            // from SetDirectionalLightUniforms, and DispatchSolve republishes the live values for the
            // solve later in the same frame.
            _solveShader.SetVector(s_directLightDir, _sunVisDir);
            // Whole Z slices at a time, at least one - a single slice is grid^2 texels, which is
            // 65,536 even at 256, comfortably inside the budget.
            int slicesPerDispatch = Mathf.Max(1, SunVisTexelsPerDispatch / (ShadowGrid * ShadowGrid));
            int slices = Mathf.Min(slicesPerDispatch, ShadowGrid - _sunVisSliceBase);
            // Always supersampled: one centre ray per texel is a BIT, and no filter downstream can
            // turn a bit back into the coverage fraction _BgiShadowSharpness reconstructs an edge
            // from. The solve's own Centre/Temporal estimators do not apply here - this pass is not a
            // progressive average, each texel is finished the moment it is written.
            _solveShader.SetInt(s_shadowTexSamples, Mathf.Clamp(_sunShadowSamples, 1, 16));
            _solveShader.SetInt(s_bgiShadowSliceBase, _sunVisSliceBase);
            _solveShader.SetBuffer(_sunVisKernel, s_occupancyHi, _occupancyHiBuffer);

            int groups = Mathf.CeilToInt(slices * ShadowGrid * ShadowGrid / 64f);
            DispatchSunVisibilityField(GridOrigin, GridSize, VoxelSize, FineField, _sunVisTex, groups);
            if (HasCoarse) {
                DispatchSunVisibilityField(CoarseOrigin, CoarseSize, CoarseVoxelSize, CoarseField,
                    _sunVisTexCoarse, groups);
            }
            _sunVisSliceBase += slices;
        }

        void DispatchSunVisibilityField(Vector3 origin, Vector3 size, Vector3 voxelSize, int field,
                                        RenderTexture tex, int groups) {
            SetGridUniforms(origin, size, voxelSize);
            _solveShader.SetInt(s_occFieldWordOffset, field * OccWordsPerField);
            _solveShader.SetTexture(_sunVisKernel, s_bgiSunVisTexWrite, tex);
            _solveShader.Dispatch(_sunVisKernel, groups, 1, 1);
        }

        // Inject -> gather -> blur for one field's slice; blur also mirrors the result into irradianceTex.
        void SolveField(Vector3 origin, Vector3 size, Vector3 voxelSize, int fieldOffset,
                        RenderTexture irradianceTex) {
            SetGridUniforms(origin, size, voxelSize);
            _solveShader.SetInt(s_fieldOffset, fieldOffset);
            // This field's slice of the hi-res occupancy, for the P7 march levels. Set unconditionally
            // even at march level 0: the buffers are DECLARED in the kernel whatever the level, and an
            // unbound declared buffer fails pipeline creation on WebGPU (the same trap _Occupancy hit).
            _solveShader.SetInt(s_occFieldWordOffset,
                (fieldOffset / Mathf.Max(1, VoxelCount)) * OccWordsPerField);

            // Inject: solid voxels emit/reflect. Bounce = the surface's own last-frame incident
            // irradiance (its _Irradiance slot, built by gather). The ONLY kernel that still reads
            // _Material (albedo/emission); solidity comes from the bitfield.
            _solveShader.SetBuffer(_injectKernel, s_occupancy, _occupancyBuffer);
            _solveShader.SetBuffer(_injectKernel, s_occupancyHi, _occupancyHiBuffer);
            _solveShader.SetBuffer(_injectKernel, s_occupancyTraversal, _occupancyTraversalBuffer);
            _solveShader.SetBuffer(_injectKernel, s_material, _materialBuffer);
            _solveShader.SetBuffer(_injectKernel, s_radiance, _radianceBuffer);
            _solveShader.SetBuffer(_injectKernel, s_irradiance, _irradianceBuffer);
            _solveShader.SetBuffer(_injectKernel, s_surface, _surfaceBuffer);
            BufferGiSolveProfiler.Begin(BufferGiSolveProfiler.Stage.Inject);
            _solveShader.Dispatch(_injectKernel, Groups, 1, 1);
            BufferGiSolveProfiler.End(BufferGiSolveProfiler.Stage.Inject);

            // Gather: off the fresh _Radiance, fold into _Irradiance - AIR voxels omnidirectionally
            // (the read field), SOLID voxels over their front hemisphere (next frame's inject bounce).
            // All its solidity (DDA + gates) is the bitfield; it never touches _Material.
            _solveShader.SetBuffer(_gatherKernel, s_occupancy, _occupancyBuffer);
            _solveShader.SetBuffer(_gatherKernel, s_occupancyHi, _occupancyHiBuffer);
            _solveShader.SetBuffer(_gatherKernel, s_occupancyTraversal, _occupancyTraversalBuffer);
            _solveShader.SetBuffer(_gatherKernel, s_radiance, _radianceBuffer);
            _solveShader.SetBuffer(_gatherKernel, s_irradiance, _irradianceBuffer);
            _solveShader.SetBuffer(_gatherKernel, s_surface, _surfaceBuffer);
            BufferGiSolveProfiler.Begin(BufferGiSolveProfiler.Stage.Gather);
            _solveShader.Dispatch(_gatherKernel, Groups, 1, 1);
            BufferGiSolveProfiler.End(BufferGiSolveProfiler.Stage.Gather);

            // Blur: occupancy-gated spatial smoothing + the confidence ease (CSBlur) that hides the
            // warm-up, written to _IrradianceBlur AND mirrored into this field's Texture3D (the fragment's
            // 1-tap read source) in the same pass - no separate SSBO->texture copy dispatch.
            _solveShader.SetBuffer(_blurKernel, s_occupancy, _occupancyBuffer);
            // The shell dilation's grown gate. CSBlur is the ONLY consumer - binding it here rather
            // than beside the march buffers is the point: nothing that traces a ray may see it, or it
            // becomes BGI_THICKEN with all of thickening's costs.
            _solveShader.SetBuffer(_blurKernel, s_occupancyGrown, _occupancyGrownBuffer);
            _solveShader.SetBuffer(_blurKernel, s_irradiance, _irradianceBuffer);
            _solveShader.SetBuffer(_blurKernel, s_irradianceBlur, _irradianceBlurBuffer);
            // Surface flags: the blur reads them only for SOLID voxels, to let a baked-light voxel take
            // the air path instead of being zeroed into a hole (CSBlur).
            _solveShader.SetBuffer(_blurKernel, s_surface, _surfaceBuffer);
            // CSBlur reads _Radiance only for the emissive/two-sided shell logic now - the sun
            // visibility it used to mirror moved to CSSunVisibility.
            _solveShader.SetBuffer(_blurKernel, s_radiance, _radianceBuffer);
            _solveShader.SetTexture(_blurKernel, s_bgiIrradianceTexWrite, irradianceTex);
            BufferGiSolveProfiler.Begin(BufferGiSolveProfiler.Stage.Blur);
            _solveShader.Dispatch(_blurKernel, Groups, 1, 1);
            BufferGiSolveProfiler.End(BufferGiSolveProfiler.Stage.Blur);
        }

        void SetDirectionalLightUniforms() {
            Light sun = RenderSettings.sun;
            if (sun != null) {
                _solveShader.SetVector(s_directLightDir, -sun.transform.forward);
                // FinalColor: the solve must bounce the SAME colour the fragment shades with, which for
                // the main light is URP's already colour-space-converted value.
                _solveShader.SetVector(s_directLightColor, sun.FinalColor());
            } else {
                _solveShader.SetVector(s_directLightDir, Vector3.down);
                _solveShader.SetVector(s_directLightColor, Vector4.zero);
            }

            // Environment lighting as the ambient-probe SH, evaluated per ray direction. The probe
            // reflects the Lighting window's Environment Source (Skybox / Gradient / Color), so this
            // follows that setting automatically.
            PackAmbientProbeSH(RenderSettings.ambientProbe, s_shScratch);
            for (int i = 0; i < 7; i++) _solveShader.SetVector(s_envSh[i], s_shScratch[i]);
        }

        static readonly Vector4[] s_shScratch = new Vector4[7];

        // Pack a SphericalHarmonicsL2 into 7 float4 the same way Unity's unity_SH* / ShadeSH9 expect.
        static void PackAmbientProbeSH(SphericalHarmonicsL2 sh, Vector4[] outCoeff) {
            for (int c = 0; c < 3; c++) {
                outCoeff[c] = new Vector4(sh[c, 3], sh[c, 1], sh[c, 2], sh[c, 0] - sh[c, 6]); // L0 + L1
                outCoeff[c + 3] = new Vector4(sh[c, 4], sh[c, 5], sh[c, 6] * 3f, sh[c, 7]);   // L2 (4 of 5)
            }
            outCoeff[6] = new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 1f);                       // L2 (5th)
        }

        // Create a field's irradiance Texture3D (RGBA16F for reliable compute random-write + trilinear
        // sampling; can drop to RGB111110 later). Grid^3, bilinear/clamp.
        //
        // In Cube mode the six direction buckets are STACKED ALONG Z in this one texture
        // (Grid x Grid x Grid*6) rather than living in six textures: six bindings would risk the
        // per-stage sampler limit on mobile, on top of the WebGPU pipeline-layout surface.
        // No border padding between slabs - the read clamps its slab-local Z to [0.5, Grid-0.5], so a
        // trilinear footprint can never reach a neighbouring slab's texels with nonzero weight, and
        // X/Y filtering stays inside the slab regardless. That clamp is also what wrapMode.Clamp
        // already did at the volume's own Z extremes, so it costs nothing else.
        RenderTexture CreateIrradianceTexture(string name) =>
            CreateFieldVolume(name, RenderTextureFormat.ARGBHalf, Grid, Grid * IrradianceSlots);

        // The sun-visibility volume: a plain cube at the SHADOW grid, one scalar per texel, NOT
        // slabbed in either mode (see _BgiSunVisTex in BufferGiRead.hlsl for why Cube stopped needing
        // slabs). RHalf (fp16) and not R8 because _BgiShadowSharpness amplifies quantisation along
        // with the signal, and it is clamped at the bottom but not at the top.
        RenderTexture CreateSunVisTexture(string name) =>
            CreateFieldVolume(name, RenderTextureFormat.RHalf, ShadowGrid, ShadowGrid);

        // The neighbour-solidity mask: 7 bits per LIGHTING cell, so a plain cube at Grid, R8_UInt, and
        // POINT filtered - it is Load()ed, and an interpolated bitmask would be nonsense. 32 KB per
        // field at Grid 32. Integer format, so the shared CreateFieldVolume's GL.Clear does not apply;
        // CSBuildNeighbourMask writes every texel of every slice on the first bake, and the field is
        // not read before that bake completes (BufferGI does not publish until _materialBaked).
        RenderTexture CreateNeighbourMaskTexture(string name) {
            var desc = new RenderTextureDescriptor(Grid, Grid, GraphicsFormat.R8_UInt, 0) {
                dimension = TextureDimension.Tex3D,
                volumeDepth = Grid,
                enableRandomWrite = true,
                msaaSamples = 1
            };
            var rt = new RenderTexture(desc) {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = name
            };
            rt.Create();
            return rt;
        }

        RenderTexture CreateFieldVolume(string name, RenderTextureFormat format, int size, int depth) {
            var desc = new RenderTextureDescriptor(size, size, format, 0) {
                dimension = TextureDimension.Tex3D,
                volumeDepth = depth,
                enableRandomWrite = true,
                msaaSamples = 1
            };
            var rt = new RenderTexture(desc) {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = name
            };
            rt.Create();
            // A freshly created RenderTexture holds undefined data, exactly like a fresh ComputeBuffer.
            // CSBlur does rewrite every texel of every slice, so this only covers the window before the
            // first solve lands - but that window is real (a field whose bake was rejected never
            // dispatches a solve at all), and garbage there reads as uncorrelated noise on screen.
            RenderTexture prev = RenderTexture.active;
            for (int z = 0; z < desc.volumeDepth; z++) {
                Graphics.SetRenderTarget(rt, 0, CubemapFace.Unknown, z);
                GL.Clear(false, true, Color.clear);
            }
            RenderTexture.active = prev;
            return rt;
        }

        public void ReleaseBuffers() {
            _materialBuffer?.Release();
            _radianceBuffer?.Release();
            _irradianceBuffer?.Release();
            _irradianceBlurBuffer?.Release();
            _surfaceBuffer?.Release();
            _occupancyBuffer?.Release();
            _occupancyThickBuffer?.Release();
            _occupancyGrownBuffer?.Release(); _occupancyGrownBuffer = null;
            _occupancyHiBuffer?.Release();
            _occupancyTraversalBuffer?.Release();
            _materialBuffer = null;
            _radianceBuffer = null;
            _irradianceBuffer = null;
            _irradianceBlurBuffer = null;
            _surfaceBuffer = null;
            _occupancyBuffer = null;
            _occupancyThickBuffer = null;
            _occupancyHiBuffer = null;
            _occupancyTraversalBuffer = null;
            _allocatedOccGrid = 0;
            if (_irradianceTex != null) { _irradianceTex.Release(); _irradianceTex = null; }
            if (_irradianceTexCoarse != null) { _irradianceTexCoarse.Release(); _irradianceTexCoarse = null; }
            if (_sunVisTex != null) { _sunVisTex.Release(); _sunVisTex = null; }
            if (_sunVisTexCoarse != null) { _sunVisTexCoarse.Release(); _sunVisTexCoarse = null; }
            if (_neighbourMaskTex != null) { _neighbourMaskTex.Release(); _neighbourMaskTex = null; }
            if (_neighbourMaskTexCoarse != null) { _neighbourMaskTexCoarse.Release(); _neighbourMaskTexCoarse = null; }
            _materialBaked = false;
            _resetFineField = false;
            _allocatedRadianceSlots = 0; // no buffer -> no stride; forces EnsureInitialized to size it
            _allocatedIrradianceSlots = 0;
            _collectedSamples = 0; // gather from scratch while the freshly-cleared field fills in
        }
    }
}
