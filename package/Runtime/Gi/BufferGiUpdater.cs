using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    /// <summary>
    /// Driver for the buffer-based GI (the textureless, cache-resident GI that runs behind the GI_VOXEL_BUFFER shader keyword).
    /// Owns the ComputeBuffers, voxelizes the scene mesh into the occupancy/albedo buffer once (GPU 3-axis
    /// raster, BufferGiVoxelize.shader), and runs the per-frame solve: inject (solid voxels
    /// emit/reflect) then gather (air voxels integrate 1 ray/frame with the temporal resolve fused
    /// in) then a blur pass. The lit shader reads it via SampleBufferGI (BufferGi.hlsl).
    ///
    /// All fields are a fixed 32^3 so a voxel index fits in 16 bits and the whole grid stays
    /// resident in the GPU L2. (Single fine cascade for now; a coarse cascade + scheduler is the
    /// planned next step.)
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    // GI methods are mutually exclusive: enabling this component disables GiFieldUpdater (and vice
    // versa), and the enabled updater owns the GI keyword group + the _Exposure global.
    [AddComponentMenu("Lotec/Voxel Lighting/Buffer GI")]
    public class BufferGiUpdater : MonoBehaviour {
        public const int Grid = 32;
        public const int VoxelCount = Grid * Grid * Grid; // 32768 per field
        // Concatenated fields: 0 = coarse (the big far volume), 1 = fine (the active volume). Coarse
        // is kept at slot 0 so any future fine fields stay contiguous (1..N-1) and just append.
        public const int FieldCount = 2;
        public const int TotalVoxels = FieldCount * VoxelCount;
        public const int CoarseField = 0;
        public const int FineField = 1;

        public static BufferGiUpdater Instance { get; private set; }

        [SerializeField] ComputeShader _computeShader;
        [Tooltip("Shader 'Hidden/Lotec/BufferGiVoxelize' - GPU 3-axis rasterizer that voxelizes " +
                 "scene meshes into the occupancy/albedo buffer.")]
        [SerializeField] Shader _voxelizeShader;

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
        [Tooltip("Bake an exact surface normal per voxel at voxelize time instead of computing the " +
                 "occupancy gradient at runtime. Costs a normal buffer (~256 KB) but is faster to " +
                 "read AND fixes hollow/thin walls the gradient reads as ambiguous - so walls no " +
                 "longer need to be voxelized 2 voxels thick (the inward-thicken is skipped).")]
        [SerializeField] bool _bakedNormals;

        [Header("Lighting")]
        [Tooltip("Display transform (exposure + tonemap), with optional auto-exposure. Published as " +
                 "the _Exposure global + TONEMAP_OFF keyword; the lit shader applies exp2(_Exposure) " +
                 "whenever GI is on. Set explicitly so a stale value can't darken the image.")]
        [SerializeField] AutoExposure _exposureControl = new AutoExposure();

        [Header("Coarse field")]
        [Tooltip("MeshBounds covering the whole scene, used as the coarse (low-detail, far) GI " +
                 "field - a 32^3 grid over its larger bounds (bigger voxels). Should be larger " +
                 "than the active fine volume. Leave null for fine only.")]
        [SerializeField] MeshBounds _coarseBounds;

        ComputeBuffer _materialBuffer;
        ComputeBuffer _radianceBuffer;
        ComputeBuffer _irradianceBuffer;
        ComputeBuffer _irradianceBlurBuffer;
        ComputeBuffer _normalBuffer; // baked oct-packed per-voxel normals (only when _bakedNormals)
        // 1-bit/voxel occupancy bitfield (uint packs 32 voxels): 4 KB per field, the hot solidity
        // data every DDA step / gate / fragment tap reads. Derived from _Material by CSBuildSurface.
        ComputeBuffer _occupancyBuffer;
        const string BakedNormalsKeyword = "BGI_BAKED_NORMALS";
        bool _materialBaked;
        // The fine field's volume (the manager's active volume); its Bounds already carry the
        // volume's own border, so the fine grid uses them as-is.
        VoxelVolume _volume;
        Material _voxelizeMaterial;
        uint[] _materialClear;
        int _clearKernel = -1;
        int _injectKernel = -1;
        int _gatherKernel = -1;
        int _blurKernel = -1;
        int _initFineKernel = -1;
        int _averageLuminanceKernel = -1;
        int _buildSurfaceKernel = -1;
        bool _resetFineField;
        bool _hasLoggedMissingReferences;
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
        // Baked per-voxel normals (only when BakedNormals is on); null otherwise. For the debug viewer.
        public ComputeBuffer NormalBuffer => _normalBuffer;
        public bool BakedNormals => _bakedNormals;
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

        // Coarse field: a scene-covering MeshBounds (32^3 grid, larger voxels). Falls back to the
        // fine bounds when unassigned so the read/visualizer degrade gracefully (empty slice -> 0).
        // MeshBounds is tight, so grow it by a border of coarse grid cells here (geometry exactly
        // on the boundary sits in half-clipped voxels): solving size' = size + 2*P*(size'/G) gives
        // the closed form size' = size * G/(G - 2P) per axis.
        const float CoarsePaddingVoxels = 2f;
        Bounds CoarseWorldBounds {
            get {
                Bounds b = _coarseBounds.Bounds;
                b.size *= Grid / (Grid - 2f * CoarsePaddingVoxels);
                return b;
            }
        }
        public bool HasCoarse => _coarseBounds != null;
        public Vector3 CoarseOrigin => HasCoarse ? CoarseWorldBounds.min : GridOrigin;
        public Vector3 CoarseSize => HasCoarse ? CoarseWorldBounds.size : GridSize;
        public Vector3 CoarseVoxelSize => CoarseSize / Grid;

        LightingManager Manager => LightingManager.Instance;

        #region Shader Property IDs
        static readonly int s_radiance = Shader.PropertyToID("_Radiance");
        static readonly int s_irradiance = Shader.PropertyToID("_Irradiance");
        static readonly int s_irradianceBlur = Shader.PropertyToID("_IrradianceBlur");
        static readonly int s_voxAlbedo = Shader.PropertyToID("_VoxAlbedo");
        static readonly int s_voxEmission = Shader.PropertyToID("_VoxEmission8");
        static readonly int s_voxAxis = Shader.PropertyToID("_VoxAxis");
        static readonly int s_gridOrigin = Shader.PropertyToID("_BgiGridOrigin");
        static readonly int s_gridSize = Shader.PropertyToID("_BgiGridSize");
        static readonly int s_voxelSize = Shader.PropertyToID("_BgiVoxelSize");
        static readonly int s_fieldOffset = Shader.PropertyToID("_FieldOffset");
        static readonly int s_coarseOrigin = Shader.PropertyToID("_BgiCoarseOrigin");
        static readonly int s_coarseVoxelSize = Shader.PropertyToID("_BgiCoarseVoxelSize");
        static readonly int s_confidence = Shader.PropertyToID("_Confidence");
        static readonly int s_emaWeight = Shader.PropertyToID("_EmaWeight");
        static readonly int s_coarseGridOrigin = Shader.PropertyToID("_CoarseGridOrigin");
        static readonly int s_coarseGridVoxelSize = Shader.PropertyToID("_CoarseGridVoxelSize");
        static readonly int s_material = Shader.PropertyToID("_Material");
        static readonly int s_normal = Shader.PropertyToID("_Normal");
        static readonly int s_occupancy = Shader.PropertyToID("_Occupancy");
        static readonly int s_frameCount = Shader.PropertyToID("_FrameCount");
        static readonly int s_samplesPerFrame = Shader.PropertyToID("_SamplesPerFrame");
        static readonly int s_giFireflyClamp = Shader.PropertyToID("_GiFireflyClamp");
        static readonly int s_reachBoost = Shader.PropertyToID("_ReachBoost");
        static readonly int s_directLightDir = Shader.PropertyToID("_DirectLightDir");
        static readonly int s_directLightColor = Shader.PropertyToID("_DirectLightColor");
        static readonly int[] s_envSh = {
            Shader.PropertyToID("_EnvShAr"), Shader.PropertyToID("_EnvShAg"), Shader.PropertyToID("_EnvShAb"),
            Shader.PropertyToID("_EnvShBr"), Shader.PropertyToID("_EnvShBg"), Shader.PropertyToID("_EnvShBb"),
            Shader.PropertyToID("_EnvShC"),
        };
        static readonly int s_ambientFloor = Shader.PropertyToID("_AmbientFloor");
        static readonly int s_intensity = Shader.PropertyToID("_BgiIntensity");
        static readonly int s_luminanceResult = Shader.PropertyToID("_LuminanceResult");
        static readonly int s_cameraPosition = Shader.PropertyToID("_CameraPosition");
        static readonly int s_cameraForward = Shader.PropertyToID("_CameraForward");
        static readonly int s_luminanceRadius = Shader.PropertyToID("_LuminanceRadius");
        #endregion

        void OnEnable() {
            Instance = this;
            // GI methods are mutually exclusive - the enabled component selects the method, so
            // enabling this one turns the texture GI off.
            GiFieldUpdater other = FindAnyObjectByType<GiFieldUpdater>();
            if (other != null && other.enabled) {
                Debug.LogWarning("Buffer GI enabled - disabling the texture GI (GiFieldUpdater); only one GI method can be active.", this);
                other.enabled = false;
            }
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
            _exposureControl.ResetKeyword();
            _exposureControl.Release();
            ReleaseBuffers();
            if (_voxelizeMaterial != null) {
                if (Application.isPlaying) Destroy(_voxelizeMaterial); else DestroyImmediate(_voxelizeMaterial);
                _voxelizeMaterial = null;
            }
        }

#if UNITY_EDITOR
        void EditorPump() {
            if (!Application.isPlaying && isActiveAndEnabled) {
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
            }
        }
#endif

        // GI_VOXEL_BUFFER only while this updater is actually solving/publishing (buffers bound); the
        // claim is change-only and ownership-aware, so this is safe to call every frame and safe
        // against the old owner clobbering the keyword while switching GI methods.
        void SetGiBufferKeyword(bool on) {
            if (on) LightingKeywords.ClaimGi(this, LightingKeywords.GiBuffer);
            else LightingKeywords.ReleaseGi(this);
        }

        void OnValidate() {
            // A geometry/threshold change invalidates the bake; redo it on the next Update, and restart
            // the accumulation so the change settles into the (possibly idle) field.
            _materialBaked = false;
            _collectedSamples = 0;
        }

        // Wake the solver when the sun changes. Local lights are intentionally excluded (GI may drop
        // them); add a local-light hash here if that changes. Sky/ambient changes come in via OnValidate.
        bool HasSunChanged() {
            Light sun = RenderSettings.sun;
            Vector3 dir = sun != null ? -sun.transform.forward : Vector3.down;
            Vector4 col = sun != null ? (Vector4)sun.color * sun.intensity : Vector4.zero;
            return dir != _prevSunDir || col != _prevSunColor;
        }

        void StoreSunState() {
            Light sun = RenderSettings.sun;
            _prevSunDir = sun != null ? -sun.transform.forward : Vector3.down;
            _prevSunColor = sun != null ? (Vector4)sun.color * sun.intensity : Vector4.zero;
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
                _hasLoggedMissingReferences = false;
                if (warmSwitch) {
                    _materialBaked = false;
                    _resetFineField = true; // clear + re-fill the fine field for the new bounds
                    _collectedSamples = 0;
                } else {
                    ReleaseBuffers();
                }
            }
            if (_volume == null || _computeShader == null) {
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

            SyncNormalMode();
            EnsureInitialized();
            if (!_materialBaked) {
                Voxelize();
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
            if (HasSunChanged()) {
                _collectedSamples = 0;
            }
            if (_collectedSamples < _maxSamples || _continuousGi) {
                _collectedSamples = Mathf.Min(_collectedSamples + Mathf.Max(1, _samplesPerFrame), _maxSamples);
                DispatchSolve();
            }
            StoreSunState();

            SetGlobals();
            SetGiBufferKeyword(true);
            // Display transform (exposure + tonemap); runs every frame so auto-exposure keeps
            // adapting even when the solve is idle (a static scene the camera moves through).
            _exposureControl.Apply(DispatchLuminance);
        }

        // Backend luminance measurement for AutoExposure: average the DISPLAYED fine field's air-voxel
        // luminance in a camera-centred radius into the controller's 2-uint buffer. AutoExposure owns
        // the clear + readback + adaptation; this only binds + dispatches the fine-field kernel.
        void DispatchLuminance(ComputeBuffer luminanceBuffer) {
            if (_averageLuminanceKernel < 0 || _irradianceBlurBuffer == null || Camera.main == null) return;
            SetGridUniforms(GridOrigin, GridSize, VoxelSize);
            _computeShader.SetInt(s_fieldOffset, FineField * VoxelCount);
            _computeShader.SetBuffer(_averageLuminanceKernel, s_occupancy, _occupancyBuffer);
            _computeShader.SetBuffer(_averageLuminanceKernel, s_irradianceBlur, _irradianceBlurBuffer);
            _computeShader.SetBuffer(_averageLuminanceKernel, s_luminanceResult, luminanceBuffer);
            _computeShader.SetVector(s_cameraPosition, Camera.main.transform.position);
            _computeShader.SetVector(s_cameraForward, Camera.main.transform.forward);
            _computeShader.SetFloat(s_luminanceRadius, _exposureControl.MeasureRadius);
            _computeShader.Dispatch(_averageLuminanceKernel, Groups, 1, 1);
        }

        // Publish the buffers + grid mapping + confidence the lit shader's SampleBufferGI reads.
        void SetGlobals() {
            // Fragment solidity = the 8 KB bitfield; _Material is no longer bound to the lit shader.
            Shader.SetGlobalBuffer(s_occupancy, _occupancyBuffer);
            // Fragment reads the occupancy-gated blurred field (always on).
            Shader.SetGlobalBuffer(s_irradiance, _irradianceBlurBuffer);
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
            // The display transform (_Exposure + TONEMAP_OFF) is published by _exposureControl.Apply
            // in Update - explicitly, so a stale value (e.g. left by GiFieldUpdater) can't darken it.
        }

        bool IsReady(out string reason) {
            if (_computeShader == null) { reason = "ComputeShader"; return false; }
            if (_voxelizeShader == null) { reason = "Voxelize Shader (Hidden/Lotec/BufferGiVoxelize)"; return false; }
            if (_volume.BakeRoot == null) { reason = "the volume's MeshBounds root (mesh geometry to voxelize)"; return false; }
            reason = null;
            return true;
        }

        void EnsureInitialized() {
            if (_materialBuffer != null && _radianceBuffer != null && _irradianceBuffer != null) {
                return;
            }
            ReleaseBuffers();

            _clearKernel = _computeShader.FindKernel("CSClear");
            _injectKernel = _computeShader.FindKernel("CSInject");
            _gatherKernel = _computeShader.FindKernel("CSGather");
            _blurKernel = _computeShader.FindKernel("CSBlur");
            _initFineKernel = _computeShader.FindKernel("CSInitFineFromCoarse");
            _averageLuminanceKernel = _computeShader.FindKernel("CSAverageLuminance");
            _buildSurfaceKernel = _computeShader.FindKernel("CSBuildSurface");

            // uint material, uint2 radiance/irradiance. Sized for all fields (concatenated slices).
            _materialBuffer = new ComputeBuffer(TotalVoxels, sizeof(uint));
            _radianceBuffer = new ComputeBuffer(TotalVoxels, sizeof(uint) * 2);
            _irradianceBuffer = new ComputeBuffer(TotalVoxels, sizeof(uint) * 2);
            _irradianceBlurBuffer = new ComputeBuffer(TotalVoxels, sizeof(uint) * 2);
            if (_bakedNormals) _normalBuffer = new ComputeBuffer(TotalVoxels, sizeof(uint));
            _occupancyBuffer = new ComputeBuffer(TotalVoxels / 32, sizeof(uint)); // 1 bit/voxel
            _materialBaked = false;

            ClearDynamicFields();
        }

        // Keep the buffers + shader keyword in sync with the _bakedNormals toggle. A change forces a
        // buffer realloc + re-voxelize (the normal buffer only exists in baked mode, and the voxelizer
        // either bakes normals or thickens depending on the keyword).
        void SyncNormalMode() {
            if (_bakedNormals) _computeShader.EnableKeyword(BakedNormalsKeyword);
            else _computeShader.DisableKeyword(BakedNormalsKeyword);

            if (_bakedNormals != (_normalBuffer != null) && _materialBuffer != null) {
                ReleaseBuffers();   // realloc with/without the normal buffer on the next EnsureInitialized
                _materialBaked = false; // re-voxelize (bake normals vs thicken)
            }
        }

        void SetGridUniforms(Vector3 origin, Vector3 size, Vector3 voxelSize) {
            _computeShader.SetVector(s_gridOrigin, origin);
            _computeShader.SetVector(s_gridSize, size);
            _computeShader.SetVector(s_voxelSize, voxelSize);
        }

        // Groups to cover ONE field's voxels (each field is dispatched separately with its offset).
        static int Groups => Mathf.CeilToInt(VoxelCount / 64f);

        void ClearDynamicFields() {
            for (int f = 0; f < FieldCount; f++) ClearField(f * VoxelCount);
        }

        // Zero one field's radiance + irradiance + blur slice (the blur too, so CSBlur's confidence
        // ease starts from black rather than a stale/garbage value).
        void ClearField(int fieldOffset) {
            if (_clearKernel < 0) return;
            _computeShader.SetBuffer(_clearKernel, s_radiance, _radianceBuffer);
            _computeShader.SetBuffer(_clearKernel, s_irradiance, _irradianceBuffer);
            _computeShader.SetBuffer(_clearKernel, s_irradianceBlur, _irradianceBlurBuffer);
            _computeShader.SetInt(s_fieldOffset, fieldOffset);
            _computeShader.Dispatch(_clearKernel, Groups, 1, 1);
        }

        // Seed the (freshly-switched) fine slice from the coarse field's displayed values, so the fine
        // box starts from the coarse approximation instead of black while it re-converges. Runs after
        // Voxelize so the fine material slice already matches the new bounds.
        void InitFineFromCoarse() {
            if (_initFineKernel < 0) return;
            SetGridUniforms(GridOrigin, GridSize, VoxelSize); // fine grid = voxel world positions
            _computeShader.SetVector(s_coarseGridOrigin, CoarseOrigin);
            _computeShader.SetVector(s_coarseGridVoxelSize, CoarseVoxelSize);
            _computeShader.SetBuffer(_initFineKernel, s_occupancy, _occupancyBuffer);
            _computeShader.SetBuffer(_initFineKernel, s_radiance, _radianceBuffer);
            _computeShader.SetBuffer(_initFineKernel, s_irradiance, _irradianceBuffer);
            _computeShader.SetBuffer(_initFineKernel, s_irradianceBlur, _irradianceBlurBuffer);
            _computeShader.Dispatch(_initFineKernel, Groups, 1, 1);
        }

        // GPU 3-axis rasterization of the volume's mesh geometry into each field's material slice.
        // One-shot (geometry is static): clears the whole buffer, then rasterizes the fine and coarse
        // fields, each into its own slice with its own grid; each fragment writes via a fragment UAV.
        public void Voxelize() {
            if (_voxelizeShader == null || _materialBuffer == null) return;
            Transform fineRoot = _volume.BakeRoot;
            if (fineRoot == null) { _materialBaked = true; return; }

            if (_voxelizeMaterial == null) {
                _voxelizeMaterial = new Material(_voxelizeShader) { hideFlags = HideFlags.HideAndDontSave };
            }
            // Bake normals (write _NormalWrite, skip thickening) vs thicken - matches the compute path.
            if (_bakedNormals) _voxelizeMaterial.EnableKeyword(BakedNormalsKeyword);
            else _voxelizeMaterial.DisableKeyword(BakedNormalsKeyword);

            // Rasterization only writes covered voxels, so clear all field slices to empty first.
            if (_materialClear == null) _materialClear = new uint[TotalVoxels];
            _materialBuffer.SetData(_materialClear);
            // Clear the normal buffer too (zeros = a valid default normal via BgiUnpackNormal), so a
            // solid voxel written by a degenerate-normal triangle reads a deterministic value rather
            // than uninitialized memory. Reuses the zero array (same size/type).
            if (_bakedNormals) _normalBuffer.SetData(_materialClear);

            RenderTexture dummy = RenderTexture.GetTemporary(Grid, Grid, 0, RenderTextureFormat.R8);
            CommandBuffer cmd = new CommandBuffer { name = "BufferGI Voxelize" };
            cmd.SetRenderTarget(dummy);
            // We output clip space directly from the vertex shader, so neutralize the view-projection.
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.SetRandomWriteTarget(1, _materialBuffer);
            if (_bakedNormals) cmd.SetRandomWriteTarget(2, _normalBuffer); // u2 = _NormalWrite

            // Each field rasterizes its OWN volume's geometry into its slice (coarse = a separate,
            // scene-covering MeshBounds with its own root).
            VoxelizeFieldInto(cmd, fineRoot, GridOrigin, GridSize, VoxelSize, FineField * VoxelCount);
            Transform coarseRoot = _coarseBounds != null ? _coarseBounds.Root : null;
            if (coarseRoot != null) {
                VoxelizeFieldInto(cmd, coarseRoot, CoarseOrigin, CoarseSize, CoarseVoxelSize, CoarseField * VoxelCount);
            }

            cmd.ClearRandomWriteTargets();
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            RenderTexture.ReleaseTemporary(dummy);

            // Derive the per-field occupancy bitfields from the fresh voxelization (bake-time only).
            // Both fields unconditionally: an un-voxelized coarse slice just packs to zeros.
            for (int f = 0; f < FieldCount; f++) BuildSurface(f * VoxelCount);
            _materialBaked = true;
        }

        // Pack one field's _Material occupancy into the 1-bit/voxel _Occupancy bitfield (4 KB/field,
        // 1024 words, one thread per word). Future surface derivations (openness/AO, air distance)
        // belong in this same kernel - it runs on completed occupancy.
        void BuildSurface(int fieldOffset) {
            if (_buildSurfaceKernel < 0) return;
            _computeShader.SetInt(s_fieldOffset, fieldOffset);
            _computeShader.SetBuffer(_buildSurfaceKernel, s_material, _materialBuffer);
            _computeShader.SetBuffer(_buildSurfaceKernel, s_occupancy, _occupancyBuffer);
            _computeShader.Dispatch(_buildSurfaceKernel, Mathf.CeilToInt(VoxelCount / 32f / 64f), 1, 1);
        }

        // Rasterize a volume's geometry into one field's slice. The voxelize shader reads the grid +
        // field offset as globals (BgiWorldToGrid / BgiSlot), so set them before the draws.
        void VoxelizeFieldInto(CommandBuffer cmd, Transform root, Vector3 origin, Vector3 size, Vector3 voxelSize, int fieldOffset) {
            // Same eligibility as the volume bounds / SDF bake (active + static): inactive meshes
            // must not light the scene, and non-static ones wouldn't track movement anyway (the
            // voxelization only reruns on a re-bake).
            MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>();
            cmd.SetGlobalVector(s_gridOrigin, origin);
            cmd.SetGlobalVector(s_gridSize, size);
            cmd.SetGlobalVector(s_voxelSize, voxelSize);
            cmd.SetGlobalInt(s_fieldOffset, fieldOffset);

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
                        GetMaterialAlbedoEmission(src, out Color albedo, out float emission8);
                        // Fresh MPB per draw so each submesh's color is captured independently.
                        MaterialPropertyBlock mpb = new MaterialPropertyBlock();
                        mpb.SetColor(s_voxAlbedo, albedo);
                        mpb.SetFloat(s_voxEmission, emission8);
                        cmd.DrawMesh(mesh, l2w, _voxelizeMaterial, sm, 0, mpb);
                    }
                }
            }
        }

        const float EmissionIntensityMax = 1024f;

        static void GetMaterialAlbedoEmission(Material mat, out Color albedo, out float emission8) {
            albedo = Color.white;
            float emission = 0f;
            if (mat != null) {
                if (mat.HasProperty("_BaseColor")) albedo = mat.GetColor("_BaseColor");
                else if (mat.HasProperty("_Color")) albedo = mat.GetColor("_Color");

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

        // Matches DecodeEmissionIntensityFrom8Bit in Math.hlsl (log2 encoding, max 1024).
        static float EncodeEmission8(float intensity) {
            float clamped = Mathf.Clamp(intensity, 0f, EmissionIntensityMax);
            float encoded = Mathf.Log(1f + clamped, 2f) / Mathf.Log(1f + EmissionIntensityMax, 2f);
            return Mathf.Clamp01(Mathf.Round(encoded * 255f) / 255f);
        }

        void DispatchSolve() {
            if (_injectKernel < 0 || _gatherKernel < 0 || !_materialBaked) return;

            // Per-frame shared uniforms (same for every field).
            _computeShader.SetInt(s_frameCount, Time.frameCount);
            _computeShader.SetInt(s_samplesPerFrame, Mathf.Max(1, _samplesPerFrame));
            // Progressive gather weight (CSGather) + convergence confidence 0->1 (CSBlur, hides the
            // noisy warm-up). Both derive from _collectedSamples so they stay aligned.
            _computeShader.SetFloat(s_emaWeight, EmaWeight);
            _computeShader.SetFloat(s_confidence, Confidence);
            _computeShader.SetFloat(s_giFireflyClamp, _giFireflyClamp);
            _computeShader.SetFloat(s_reachBoost, _reachBoost);
            _computeShader.SetVector(s_ambientFloor, (Vector4)_ambientFloor);
            SetDirectionalLightUniforms();
            LocalLightsPublisher.Instance?.LocalLights?.ApplyToCompute(_computeShader);

            // The EMA blend weight (samplesPerFrame/maxSamples) is computed in the compute itself.
            SolveField(GridOrigin, GridSize, VoxelSize, FineField * VoxelCount);
            if (HasCoarse) {
                SolveField(CoarseOrigin, CoarseSize, CoarseVoxelSize, CoarseField * VoxelCount);
            }
        }

        // Inject -> gather -> (optional) blur for one field's slice.
        void SolveField(Vector3 origin, Vector3 size, Vector3 voxelSize, int fieldOffset) {
            SetGridUniforms(origin, size, voxelSize);
            _computeShader.SetInt(s_fieldOffset, fieldOffset);

            // Inject: solid voxels emit/reflect. Bounce = the surface's own last-frame incident
            // irradiance (its _Irradiance slot, built by gather). The ONLY kernel that still reads
            // _Material (albedo/emission); solidity comes from the bitfield.
            _computeShader.SetBuffer(_injectKernel, s_occupancy, _occupancyBuffer);
            _computeShader.SetBuffer(_injectKernel, s_material, _materialBuffer);
            _computeShader.SetBuffer(_injectKernel, s_radiance, _radianceBuffer);
            _computeShader.SetBuffer(_injectKernel, s_irradiance, _irradianceBuffer);
            if (_bakedNormals) _computeShader.SetBuffer(_injectKernel, s_normal, _normalBuffer);
            _computeShader.Dispatch(_injectKernel, Groups, 1, 1);

            // Gather: off the fresh _Radiance, fold into _Irradiance - AIR voxels omnidirectionally
            // (the read field), SOLID voxels over their front hemisphere (next frame's inject bounce).
            // All its solidity (DDA + gates) is the bitfield; it never touches _Material.
            _computeShader.SetBuffer(_gatherKernel, s_occupancy, _occupancyBuffer);
            _computeShader.SetBuffer(_gatherKernel, s_radiance, _radianceBuffer);
            _computeShader.SetBuffer(_gatherKernel, s_irradiance, _irradianceBuffer);
            if (_bakedNormals) _computeShader.SetBuffer(_gatherKernel, s_normal, _normalBuffer);
            _computeShader.Dispatch(_gatherKernel, Groups, 1, 1);

            // Blur: occupancy-gated spatial smoothing into the buffer the fragment read samples, and
            // the confidence ease (CSBlur) that hides the warm-up. Required, always run.
            _computeShader.SetBuffer(_blurKernel, s_occupancy, _occupancyBuffer);
            _computeShader.SetBuffer(_blurKernel, s_irradiance, _irradianceBuffer);
            _computeShader.SetBuffer(_blurKernel, s_irradianceBlur, _irradianceBlurBuffer);
            _computeShader.Dispatch(_blurKernel, Groups, 1, 1);
        }

        void SetDirectionalLightUniforms() {
            Light sun = RenderSettings.sun;
            if (sun != null) {
                _computeShader.SetVector(s_directLightDir, -sun.transform.forward);
                _computeShader.SetVector(s_directLightColor, (Vector4)sun.color * sun.intensity);
            } else {
                _computeShader.SetVector(s_directLightDir, Vector3.down);
                _computeShader.SetVector(s_directLightColor, Vector4.zero);
            }

            // Environment lighting as the ambient-probe SH, evaluated per ray direction. The probe
            // reflects the Lighting window's Environment Source (Skybox / Gradient / Color), so this
            // follows that setting automatically.
            PackAmbientProbeSH(RenderSettings.ambientProbe, s_shScratch);
            for (int i = 0; i < 7; i++) _computeShader.SetVector(s_envSh[i], s_shScratch[i]);
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

        public void ReleaseBuffers() {
            _materialBuffer?.Release();
            _radianceBuffer?.Release();
            _irradianceBuffer?.Release();
            _irradianceBlurBuffer?.Release();
            _normalBuffer?.Release();
            _occupancyBuffer?.Release();
            _materialBuffer = null;
            _radianceBuffer = null;
            _irradianceBuffer = null;
            _irradianceBlurBuffer = null;
            _normalBuffer = null;
            _occupancyBuffer = null;
            _materialBaked = false;
            _resetFineField = false;
            _collectedSamples = 0; // gather from scratch while the freshly-cleared field fills in
        }
    }
}
