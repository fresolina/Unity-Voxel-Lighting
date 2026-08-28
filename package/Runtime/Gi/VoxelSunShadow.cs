using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// The main light's sun shadow, as a subsystem rather than a stage of the GI.
    /// <para>
    /// Owns the shadow settings, the sun-visibility volumes, the chunked re-march that fills them,
    /// and every shadow global the fragment reads. Its one input is geometry, through
    /// <see cref="IVoxelOccupancySource"/>; it hands nothing back. S3 of
    /// docs/direct-shadow-extraction.md.
    /// </para>
    /// <para>
    /// WHY IT IS SEPARABLE AT ALL: the sun shadow stopped depending on the solve when P6 moved sun
    /// visibility out of it into its own pass. What remained was a shared file and a shared component.
    /// The lifecycles were never the same - the solve runs every frame until its ray budget is spent,
    /// this runs when the SUN MOVES and idles otherwise - and this class is that difference made
    /// structural, so a second backend can be added without touching the GI.
    /// </para>
    /// <para>
    /// ORDERING IS LOAD-BEARING. <see cref="Tick"/> is called explicitly by
    /// <see cref="BufferGiUpdater"/> after it has published the grid constants, NOT from an Update of
    /// its own. The march reads those constants; running first would march a zero grid and write an
    /// empty volume with no error anywhere. An execution-order attribute would express the same thing
    /// invisibly, at the wrong end of the codebase.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    [AddComponentMenu("Lotec/Voxel Lighting/Voxel Sun Shadow")]
    public class VoxelSunShadow : MonoBehaviour {
        // Per-field sun-shadow source. Each field picks explicitly - nothing is a hidden fall-through:
        //   Off (0)            : genuinely NO sun shadow (full direct light).
        //   Baked (1)          : the pre-marched sun visibility volume, interpolated - soft, cheap.
        //   Sdf (2)            : crisp per-pixel raymarch of the hi-res SDF; needs an SDF baked on the volume.
        //   OcclusionField (3) : the volume's baked per-direction occlusion field.
        //   Bitmask (4)        : the volume's baked directional occlusion bitmask.
        //   UnityShadowmap (5) : URP's own cascaded main-light shadow map, resolved in VoxelLit.shader
        //                        (the library is engine-agnostic and cannot call into URP).
        //   Raymarch (6)       : a shadow ray per PIXEL against the hi-res occupancy mirror. No volume,
        //                        no lattice, nothing to re-march when the sun moves - it just costs
        //                        the frame instead of the sun move.
        // Values match _BgiShadowMode* in ShaderLibrary/VoxelSunShadow.hlsl and must not be renumbered.
        //
        // Sdf/OcclusionField/Bitmask each need their matching source present on the volume so the
        // textures they read are bound; they also have no effect where their source does not reach.
        public enum ShadowMode { Off = 0, Baked = 1, Sdf = 2, OcclusionField = 3, Bitmask = 4, UnityShadowmap = 5, Raymarch = 6 }

        [Tooltip("Sun-shadow for the FINE volume. None fall through - each is explicit.\n " +
                 "Off: no sun shadow at all - full direct light.\n " +
                 "Baked: the pre-marched sun visibility, interpolated across the surface - soft and " +
                 "cheap (no per-pixel ray).\n " +
                 "Sdf: a crisp per-pixel raymarch of the hi-res SDF - needs an SDF baked on the " +
                 "volume.\n " +
                 "OcclusionField / Bitmask: the volume's baked occlusion source - needs the matching " +
                 "occlusion binder active on the volume.")]
        [SerializeField] ShadowMode _fineShadow = ShadowMode.Off;
        [Tooltip("Sun-shadow for the COARSE volume (the big far field the SDF shadow can't reach). " +
                 "None fall through - each is explicit.\n " +
                 "Off: no sun shadow at all - full direct light.\n " +
                 "Baked: the pre-marched sun visibility, interpolated - the cheap way to get far " +
                 "shadows.\n " +
                 "Sdf: has no effect here (the SDF only covers the fine bounds); use Baked for the " +
                 "far field.\n " +
                 "OcclusionField / Bitmask: the volume's baked occlusion source, if its binder covers " +
                 "the far field.")]
        [SerializeField] ShadowMode _coarseShadow = ShadowMode.Off;
        [Tooltip("Baked shadow mode ONLY: stratified sun rays per texel of the shadow texture. This " +
                 "is the setting that controls what the baked sun shadow LOOKS like - supersampling " +
                 "is what turns a per-texel bit into the coverage fraction Baked Shadow Sharpness " +
                 "needs to reconstruct an edge, and at 1 the field is strictly binary.\n " +
                 "It only ever affects texels ON a shadow boundary. Measured on Sponza at a 128 " +
                 "shadow grid: 1 leaves 0.00% of texels fractional, 4 leaves 1.91%, 16 leaves 2.92%, " +
                 "and the field mean does not move. Cost is linear and paid only when the sun moves: " +
                 "330 / 621 / 1497 ms to re-march the volume.")]
        [Range(1, 16)][SerializeField] int _sunShadowSamples = 4;
        [Tooltip("Baked shadow mode ONLY: steepens the shadow edge the fragment reconstructs from the " +
                 "voxel grid. Near a boundary the stored value is the voxel's sun coverage, which is " +
                 "a local distance to that boundary - so steepening it rebuilds an edge finer than " +
                 "the texel. 1 = off. The sun-visibility pass always supersamples, so there is always " +
                 "a real fraction here to steepen. Too high re-introduces hard texel edges.")]
        [Range(1f, 16f)][SerializeField] float _bakedShadowSharpness = 1f;
        [Tooltip("Baked shadow mode ONLY: how far off the surface the shadow tap sits, in SHADOW " +
                 "texels (not lighting voxels - the shadow texture has its own, much finer grid).\n " +
                 "1.0 is the MINIMUM that reconstructs correctly, and the floor of the range for that " +
                 "reason. Half a texel puts a POINT sample at the first air texel's centre, but the " +
                 "tap is TRILINEAR: its footprint still reaches a full texel back into the solid " +
                 "layer, and solid texels do not hold a neutral value (the sun-visibility pass marches " +
                 "them too, from origins inside their own geometry, so they store an arbitrary partial " +
                 "coverage). Blending a varying fraction of that into a lit surface is shadow acne, " +
                 "and it reads as soft MOTTLING because the fraction depends on where the surface " +
                 "sits inside its texel. Measured on a sunlit Sponza wall: 0.5 left 91% of it " +
                 "spuriously dark, 1.0 leaves 3%.\n " +
                 "Raise toward 2 if surfaces still self-shadow; the cost is shadows detaching from " +
                 "their casters, which at this resolution is centimetres rather than most of a metre.")]
        [Range(1f, 3f)][SerializeField] float _shadowNormalOffset = 1f;
        [Tooltip("Raymarch mode ONLY: hard cap on DDA steps per pixel. The compute march bounds itself " +
                 "at 3x the occupancy resolution - 384 at 128 - which is a compute budget, not a " +
                 "fragment one.\n " +
                 "A ray that runs out of steps returns LIT, so raising this closes distant shadows and " +
                 "lowering it opens them. That direction is deliberate: every 'no information' case on " +
                 "this path reads as lit, so a budget set too low shows up as a missing shadow rather " +
                 "than as black geometry.\n " +
                 "Measured on Bootstrap at occupancy 128: the frame stops changing at 256 (32/64/128 " +
                 "all under-shadow, 256/384/512 are identical), so the default is 2x the occupancy " +
                 "resolution rather than the 3x the compute march bounds itself at.")]
        [Range(1, 512)][SerializeField] int _raymarchMaxSteps = 256;
        [Tooltip("Raymarch mode ONLY: how far off the surface the ray starts, in OCCUPANCY cells, " +
                 "measured on the dominant normal axis.\n " +
                 "1.0 is the floor and the default, because the ray has to clear the wall's own voxel " +
                 "layer - starting inside it is self-shadowing, and it shows as regular voxel-scale " +
                 "STRIPING across sunlit walls. 0.5 was tried: it moves the frame's mean luminance " +
                 "toward the Baked mode's, and it is visibly wrong on screen. Do not tune this by mean.\n " +
                 "Raising it past 1 trades contact shadows for nothing; the acne is already gone at 1.")]
        [Range(1f, 3f)][SerializeField] float _raymarchStartOffset = 1f;
        [Tooltip("VoxelSunShadow.compute - the sun-visibility march that fills the Baked volume. " +
                 "Auto-resolved by name when empty.")]
        [SerializeField] ComputeShader _sunShadowShader;

        // --- Shader property ids -------------------------------------------------------------------
        static readonly int s_shadowModeFine = Shader.PropertyToID("_BgiShadowModeFine");
        static readonly int s_shadowModeCoarse = Shader.PropertyToID("_BgiShadowModeCoarse");
        static readonly int s_shadowSharpness = Shader.PropertyToID("_BgiShadowSharpness");
        static readonly int s_shadowNormalOffset = Shader.PropertyToID("_BgiShadowNormalOffset");
        static readonly int s_raymarchMaxSteps = Shader.PropertyToID("_BgiRaymarchMaxSteps");
        static readonly int s_raymarchStartOffset = Shader.PropertyToID("_BgiRaymarchStartOffset");
        static readonly int s_bgiSunVisTex = Shader.PropertyToID("_BgiSunVisTex");
        static readonly int s_bgiSunVisTexCoarse = Shader.PropertyToID("_BgiSunVisTexCoarse");
        static readonly int s_bgiSunVisTexWrite = Shader.PropertyToID("_BgiSunVisTexWrite");
        static readonly int s_bgiShadowSliceBase = Shader.PropertyToID("_BgiShadowSliceBase");
        static readonly int s_shadowTexSamples = Shader.PropertyToID("_BgiShadowTexSamples");
        static readonly int s_directLightDir = Shader.PropertyToID("_DirectLightDir");
        static readonly int s_occupancyHi = Shader.PropertyToID("_OccupancyHi");
        static readonly int s_occFieldWordOffset = Shader.PropertyToID("_OccFieldWordOffset");
        static readonly int s_gridOrigin = Shader.PropertyToID("_BgiGridOrigin");
        static readonly int s_gridSize = Shader.PropertyToID("_BgiGridSize");
        static readonly int s_voxelSize = Shader.PropertyToID("_BgiVoxelSize");

        // --- Runtime state -------------------------------------------------------------------------
        RenderTexture _sunVisTex;         // fine field
        RenderTexture _sunVisTexCoarse;   // coarse field
        int _sunVisKernel = -1;
        int _allocatedShadowGrid;         // resolution the current textures were made at

        // Texels one dispatch may cover, per field. The pass is bounded by this and spent over as many
        // frames as it takes, because the whole volume in one submission is an over-long dispatch at
        // the higher resolutions: 256^3 is 16.7M texels x samples rays x up to 3*256 DDA steps, and it
        // TDR'd the device outright when it was written that way. 2^15 measured ~10 ms a chunk for both
        // fields on an AMD iGPU. The TOTAL sweep is fixed by the work (~80 ms at 64^3, ~650 ms at
        // 128^3 there); the chunk size only trades how big a per-frame hitch that arrives in. 2^18 was
        // tried first, measured 81 ms in ONE dispatch, and 64 of those back to back at 256^3 is what
        // killed the device.
        const int SunVisTexelsPerDispatch = 1 << 15;

        // Slice the next chunk starts at. >= ShadowGrid means the current sun direction is fully
        // marched and there is nothing to do.
        int _sunVisSliceBase;
        // The sun moved while a sweep was in flight. The sweep finishes first and the next starts
        // immediately after, so a moving sun converges within two sweeps instead of restarting forever.
        bool _sunVisRestartQueued;
        // The sun direction the in-flight sweep is marching against, LATCHED at its start. The whole
        // volume has to be marched against ONE direction: reading the live sun per chunk would give
        // slices at the front of the sweep a different sun from slices at the back, and the seam
        // between them is a sheared shadow rather than a stale one.
        Vector3 _sunVisDir = Vector3.down;
        // Something other than the sun's position invalidated the volume: a settings change, a
        // re-bake, a resolution change, fresh textures. A sun MOVE is caught separately by the caller.
        bool _sunVisDirty = true;

        // Baked occlusion holders, resolved lazily and only for the modes that read them.
        VoxelOcclusionField _occField;
        VoxelOcclusionBitmask _occBitmask;

        // --- Public API ----------------------------------------------------------------------------

        /// <summary>Sun-shadow source for the FINE (active) volume.</summary>
        public ShadowMode FineShadow {
            get => _fineShadow;
            set => _fineShadow = value;
        }

        /// <summary>Sun-shadow source for the COARSE volume.</summary>
        public ShadowMode CoarseShadow {
            get => _coarseShadow;
            set => _coarseShadow = value;
        }

        /// <summary>Stratified sun rays per texel of the shadow volume - the setting that controls
        /// what the baked shadow looks like. Re-marches the volume.</summary>
        public int SunShadowSamples {
            get => _sunShadowSamples;
            set {
                int clamped = Mathf.Clamp(value, 1, 16);
                if (_sunShadowSamples == clamped) return;
                _sunShadowSamples = clamped;
                // A scripted A/B must not have to move the sun to take effect. This was a real,
                // user-reported bug before the flag existed: the sweep re-runs on a sun MOVE, so a
                // settings change alone left the previous volume in place and "changing the sample
                // count did nothing".
                _sunVisDirty = true;
            }
        }

        /// <summary>Baked-shadow edge sharpening. 1 = off. Fragment-side only, so no re-march.</summary>
        public float BakedShadowSharpness {
            get => _bakedShadowSharpness;
            set => _bakedShadowSharpness = Mathf.Clamp(value, 1f, 16f);
        }

        /// <summary>Baked-shadow tap offset off the surface, in shadow texels. Fragment-side only.</summary>
        public float ShadowNormalOffset {
            get => _shadowNormalOffset;
            set => _shadowNormalOffset = Mathf.Clamp(value, 1f, 3f);
        }

        /// <summary>True while the baked shadow is still being re-marched after a sun move, a
        /// settings change or a re-bake. The volume holds a mix of old and new slices until it clears.</summary>
        public bool SunVisibilityPending => _sunVisSliceBase < _allocatedShadowGrid;

        /// <summary>Both volumes exist and are created. The GI publish gate checks this: the shadow
        /// globals are declared unconditionally by the GI_VOXEL_BUFFER variant, and on WebGPU a
        /// declared-but-unbound global fails pipeline creation, which renders every object BLACK.</summary>
        public bool VolumesReady =>
            _sunVisTex != null && _sunVisTex.IsCreated()
            && _sunVisTexCoarse != null && _sunVisTexCoarse.IsCreated();

        /// <summary>Force a full re-march on the next tick. Call after anything that changes the
        /// geometry or the estimator but leaves the sun where it is.</summary>
        public void Invalidate() => _sunVisDirty = true;

        // --- Driving -------------------------------------------------------------------------------

        /// <summary>
        /// One frame of shadow work: allocate if needed, publish the fragment globals, and spend one
        /// bounded chunk of the re-march.
        /// <para>
        /// Called by <see cref="BufferGiUpdater"/> AFTER it has published the grid constants - see the
        /// class remarks on ordering. <paramref name="sunMoved"/> comes from the updater's own change
        /// detection rather than a second copy here: a sun move restarts the solve as well, and two
        /// detectors for one event drift.
        /// </para>
        /// </summary>
        public void Tick(IVoxelOccupancySource geometry, bool sunMoved) {
            if (geometry == null) return;
            EnsureResources(geometry);
            // Uniforms are per-ComputeShader, so this asset needs its own copy of the grid, occupancy
            // and shadow-grid constants. Without them the kernel reads BGI_COUNT as 0, early-outs on
            // every thread and writes an empty volume - with no error anywhere.
            if (_sunShadowShader != null) geometry.BindGridConstants(_sunShadowShader);
            SetGlobals();

            // Checked BEFORE any solve gate the caller applies, so a moved sun starts reaching the
            // screen in the same frame it moved.
            // Nothing reads the volume in the other modes, so do not spend the march filling it. The
            // dirty flag is deliberately LEFT SET: switching back to Baked must re-march, and a flag
            // cleared while the work was skipped would leave a volume nobody ever filled.
            if (!VolumeIsRead) return;

            if (sunMoved || _sunVisDirty) {
                _sunVisDirty = false;
                // NEVER restart a sweep that is already running. A CONTINUOUSLY moving sun (a sun
                // rotator) fires this every single frame, and resetting the slice cursor here meant the
                // sweep never got past its first chunk: measured 2 of 128 slices after 120 frames of
                // rotation, so 126 slices kept whatever the last completed sweep left and the shadow
                // was effectively frozen. Queue instead, and start the next sweep the moment this
                // one lands.
                if (SunVisibilityPending) _sunVisRestartQueued = true;
                else StartSweep();
            }
            if (!SunVisibilityPending && _sunVisRestartQueued) {
                _sunVisRestartQueued = false;
                StartSweep();
            }
            if (SunVisibilityPending) DispatchChunk(geometry);
        }

        /// <summary>Does any field actually READ the sun-visibility volume? Only the Baked mode does.
        /// <para>
        /// The march is by far the most expensive thing this component can do - hundreds of
        /// milliseconds of GPU work per sun move at 128 - and every other mode ignores its output
        /// entirely. Before S4 it ran regardless, because there was only ever one backend worth
        /// gating on; with a second one in the list the waste became worth naming.
        /// </para>
        /// <para>
        /// The VOLUMES are still allocated and still bound. That is not an oversight: the shadow
        /// globals are declared unconditionally by the GI_VOXEL_BUFFER variant, and on WebGPU a
        /// declared-but-unbound global fails pipeline creation and renders everything BLACK. Skipping
        /// the work is safe; skipping the binding is not.
        /// </para></summary>
        public bool VolumeIsRead => _fineShadow == ShadowMode.Baked || _coarseShadow == ShadowMode.Baked;

        void StartSweep() {
            _sunVisSliceBase = 0;
            Light sun = RenderSettings.sun;
            _sunVisDir = sun != null ? -sun.transform.forward : Vector3.down;
        }

        // Re-evaluate the baked sun visibility for both fields, at the shadow grid, by marching the
        // hi-res occupancy. One bounded chunk per call; the caller keeps calling while pending.
        void DispatchChunk(IVoxelOccupancySource geo) {
            if (_sunVisKernel < 0 || _sunShadowShader == null
                || geo.OccupancyHiBuffer == null || _sunVisTex == null) {
                _sunVisSliceBase = _allocatedShadowGrid; // nothing to march into; don't spin
                return;
            }
            int shadowGrid = _allocatedShadowGrid;
            // The LATCHED direction, not the live sun (see _sunVisDir). This kernel needs nothing else
            // from the light: it produces a visibility fraction, not a colour.
            _sunShadowShader.SetVector(s_directLightDir, _sunVisDir);
            // Whole Z slices at a time, at least one - a single slice is grid^2 texels, which is
            // 65,536 even at 256, comfortably inside the budget.
            int slicesPerDispatch = Mathf.Max(1, SunVisTexelsPerDispatch / (shadowGrid * shadowGrid));
            int slices = Mathf.Min(slicesPerDispatch, shadowGrid - _sunVisSliceBase);
            // Always supersampled: one centre ray per texel is a BIT, and no filter downstream can turn
            // a bit back into the coverage fraction the sharpening reconstructs an edge from.
            _sunShadowShader.SetInt(s_shadowTexSamples, Mathf.Clamp(_sunShadowSamples, 1, 16));
            _sunShadowShader.SetInt(s_bgiShadowSliceBase, _sunVisSliceBase);
            _sunShadowShader.SetBuffer(_sunVisKernel, s_occupancyHi, geo.OccupancyHiBuffer);

            int groups = Mathf.CeilToInt(slices * shadowGrid * shadowGrid / 64f);
            DispatchField(geo, geo.GridOrigin, geo.GridSize, geo.VoxelSize,
                          BufferGiUpdater.FineField, _sunVisTex, groups);
            if (geo.HasCoarse) {
                DispatchField(geo, geo.CoarseOrigin, geo.CoarseSize, geo.CoarseVoxelSize,
                              BufferGiUpdater.CoarseField, _sunVisTexCoarse, groups);
            }
            _sunVisSliceBase += slices;
        }

        void DispatchField(IVoxelOccupancySource geo, Vector3 origin, Vector3 size, Vector3 voxelSize,
                           int field, RenderTexture tex, int groups) {
            // Uniforms are per-ComputeShader: this asset gets its own copy of the field box, and the
            // solve's is irrelevant to it.
            _sunShadowShader.SetVector(s_gridOrigin, origin);
            _sunShadowShader.SetVector(s_gridSize, size);
            _sunShadowShader.SetVector(s_voxelSize, voxelSize);
            _sunShadowShader.SetInt(s_occFieldWordOffset, field * geo.OccWordsPerField);
            _sunShadowShader.SetTexture(_sunVisKernel, s_bgiSunVisTexWrite, tex);
            _sunShadowShader.Dispatch(_sunVisKernel, groups, 1, 1);
        }

        // --- Globals -------------------------------------------------------------------------------

        /// <summary>Publish every shadow global the fragment reads. Separate from the march because
        /// the read parameters take effect immediately and the volume does not have to be rebuilt for
        /// them - sharpness and the tap offset are pure fragment-side knobs.</summary>
        public void SetGlobals() {
            Shader.SetGlobalInt(s_shadowModeFine, (int)_fineShadow);
            Shader.SetGlobalInt(s_shadowModeCoarse, (int)_coarseShadow);
            Shader.SetGlobalFloat(s_shadowSharpness, Mathf.Max(1f, _bakedShadowSharpness));
            Shader.SetGlobalFloat(s_shadowNormalOffset, Mathf.Clamp(_shadowNormalOffset, 1f, 3f));
            Shader.SetGlobalInt(s_raymarchMaxSteps, Mathf.Clamp(_raymarchMaxSteps, 1, 512));
            Shader.SetGlobalFloat(s_raymarchStartOffset, Mathf.Clamp(_raymarchStartOffset, 1f, 3f));
            // Both volumes, always. ShaderLibrary/VoxelSunShadow.hlsl declares them unconditionally in
            // the GI_VOXEL_BUFFER variant, and on WebGPU a declared-but-unbound global fails pipeline
            // creation outright - so "bind only the one this mode reads" is not an option.
            Shader.SetGlobalTexture(s_bgiSunVisTex, _sunVisTex);
            Shader.SetGlobalTexture(s_bgiSunVisTexCoarse, _sunVisTexCoarse);
            PublishOcclusionSources();
        }

        // Publish the baked occlusion globals for whichever per-pixel occlusion mode a field asks for.
        // Driven from here rather than by the holders themselves: nothing is bound (and no idle Update
        // runs) unless a ShadowMode selects it. OcclusionField / Bitmask are fine-volume-bound; the
        // coarse field is a different volume, so Off / Baked are its only coherent modes. The two
        // publish disjoint globals, so both can be bound in the same frame.
        void PublishOcclusionSources() {
            var volume = LightingManager.Instance != null ? LightingManager.Instance.Volume : null;
            // Lazy-resolve when a mode wants a holder we don't have cached: a holder AddComponent'd by
            // its baker after the last volume switch would otherwise stay unseen until a reload.
            if (_fineShadow == ShadowMode.OcclusionField || _coarseShadow == ShadowMode.OcclusionField) {
                if (_occField == null && volume != null) _occField = volume.GetComponent<VoxelOcclusionField>();
                if (_occField != null && _occField.HasData) _occField.Bind();
            }
            if (_fineShadow == ShadowMode.Bitmask || _coarseShadow == ShadowMode.Bitmask) {
                if (_occBitmask == null && volume != null) _occBitmask = volume.GetComponent<VoxelOcclusionBitmask>();
                if (_occBitmask != null && _occBitmask.HasData) _occBitmask.Bind();
            }
        }

        /// <summary>Drop the cached occlusion holders so the next publish re-resolves them. Called
        /// when a bake adds a holder that was not there at the last volume switch.</summary>
        public void ForgetOcclusionSources() {
            _occField = null;
            _occBitmask = null;
        }

        // --- Resources -----------------------------------------------------------------------------

        void EnsureResources(IVoxelOccupancySource geo) {
            ResolveShader();
            if (_sunVisKernel < 0 && _sunShadowShader != null)
                _sunVisKernel = FindKernel(_sunShadowShader, "CSSunVisibility");

            int grid = Mathf.Max(1, geo.ShadowGrid);
            if (_sunVisTex != null && _sunVisTex.IsCreated() && _allocatedShadowGrid == grid) return;

            ReleaseTextures();
            _allocatedShadowGrid = grid;
            // A plain cube at the shadow grid, one scalar per texel, NOT slabbed in either direction
            // mode - at this resolution a sub-voxel wall's two sides are different texels, so there is
            // nothing left for a slab index to disambiguate. RHalf and not R8 because the sharpening
            // amplifies quantisation along with the signal, and it is clamped at the bottom but not the
            // top.
            _sunVisTex = BufferGiUpdater.CreateFieldVolume("BgiSunVisTex", RenderTextureFormat.RHalf, grid, grid);
            _sunVisTexCoarse = BufferGiUpdater.CreateFieldVolume("BgiSunVisTexCoarse", RenderTextureFormat.RHalf, grid, grid);
            _sunVisDirty = true;   // fresh textures hold nothing yet
        }

        void ResolveShader() {
#if UNITY_EDITOR
            if (_sunShadowShader != null) return;
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("VoxelSunShadow t:ComputeShader")) {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                // FindAssets substring-matches, so filter to the exact file name.
                if (System.IO.Path.GetFileNameWithoutExtension(path) != "VoxelSunShadow") continue;
                _sunShadowShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if (_sunShadowShader != null) UnityEditor.EditorUtility.SetDirty(this);
                return;
            }
#endif
        }

        // FindKernel THROWS on a missing kernel. Name the shader and the kernel and return -1 instead,
        // so the `< 0` guard in DispatchChunk skips the dispatch rather than taking the frame down.
        int FindKernel(ComputeShader cs, string kernel) {
            if (!cs.HasKernel(kernel)) {
                Debug.LogError($"Voxel sun shadow: '{cs.name}' has no kernel '{kernel}'.", this);
                return -1;
            }
            return cs.FindKernel(kernel);
        }

        void ReleaseTextures() {
            if (_sunVisTex != null) { _sunVisTex.Release(); _sunVisTex = null; }
            if (_sunVisTexCoarse != null) { _sunVisTexCoarse.Release(); _sunVisTexCoarse = null; }
            _allocatedShadowGrid = 0;
            _sunVisSliceBase = 0;
        }

        void OnDisable() => ReleaseTextures();

        void OnValidate() {
            _sunShadowSamples = Mathf.Clamp(_sunShadowSamples, 1, 16);
            _bakedShadowSharpness = Mathf.Clamp(_bakedShadowSharpness, 1f, 16f);
            _shadowNormalOffset = Mathf.Clamp(_shadowNormalOffset, 1f, 3f);
            _raymarchMaxSteps = Mathf.Clamp(_raymarchMaxSteps, 1, 512);
            _raymarchStartOffset = Mathf.Clamp(_raymarchStartOffset, 1f, 3f);
            // The inspector writes the backing FIELD and never goes through the property setters, so
            // this is the only place that catches a sample-count change made by hand. Without it the
            // volume keeps its old contents until the sun happens to move, which is exactly the bug
            // that was reported as "changing the sample count does nothing".
            _sunVisDirty = true;
        }
    }
}
