using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    /// <summary>
    /// Driver for the buffer-based GI (the textureless, cache-resident GI that runs alongside the
    /// texture GI in <see cref="GiFieldUpdater"/>, behind the GI_BUFFER shader keyword). Owns the
    /// ComputeBuffers, voxelizes the scene mesh into the occupancy/albedo buffer once (GPU 3-axis
    /// raster, BufferGiVoxelize.shader), and runs the per-frame solve: inject (solid voxels
    /// emit/reflect) then gather (air voxels integrate 1 ray/frame with the temporal resolve fused
    /// in) then a gated blur. The lit shader reads it via SampleBufferGI (BufferGi.hlsl).
    ///
    /// All fields are a fixed 32^3 so a voxel index fits in 16 bits and the whole grid stays
    /// resident in the GPU L2. (Single fine cascade for now; a coarse cascade + scheduler is the
    /// planned next step.)
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    // Run after GiFieldUpdater (order 0) so our globals - especially _Exposure and the GI keyword
    // - win when both happen to be present.
    [DefaultExecutionOrder(1000)]
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
        [Tooltip("Scene display exposure in EV stops, published as the _Exposure global. The lit " +
                 "shader applies exp2(_Exposure) whenever GI is on, so this must be set explicitly " +
                 "(otherwise a stale value from a previous GI updater darkens the image).")]
        [SerializeField] float _exposure;
        [Tooltip("Temporal blend window: per-voxel sample count is capped here and the EMA weight " +
                 "floors at 1/maxSamples, so the field keeps adapting to moving lights.")]
        [Min(1)][SerializeField] int _maxSamples = 90;
        [Min(1)][SerializeField] int _raysPerFrame = 1;
        [Tooltip("Keep solving every frame even after the field converges. Off (recommended): the " +
                 "solve idles once converged and only wakes when the sun changes or the scene is " +
                 "re-baked, so a static scene costs no GI compute.")]
        [SerializeField] bool _continuousGi;
        [Tooltip("Luminance ceiling for a single gathered bounce, to suppress emitter fireflies. " +
                 "0 disables.")]
        [Min(0f)][SerializeField] float _giFireflyClamp = 8f;
        [Tooltip("Irradiance color used for voxels inside geometry: a gather ray hitting the BACK " +
                 "of a surface contributes this instead of the surface's room-lit value. Voxels " +
                 "fully enclosed in geometry converge to this color. Black = dark interiors.")]
        [SerializeField] Color _ambientFloor = Color.black;
        [Tooltip("Occupancy-gated spatial blur of the irradiance field to hide the 1-ray/frame " +
                 "temporal shimmer. Off = the fragment reads the raw field directly (for A/B).")]
        [SerializeField] bool _spatialBlur = true;
        [Tooltip("Max DDA steps per ray. The grid diagonal crosses up to ~96 cells, so 96 covers " +
                 "corner-to-corner; lower trades reach for cost.")]
        [Min(1)][SerializeField] int _raymarchMaxSteps = 96;

        [Header("Coarse field")]
        [Tooltip("A separate VoxelVolume covering the whole scene, used as the coarse (low-detail, " +
                 "far) GI field - a 32^3 grid over its larger bounds (bigger voxels). Should be a " +
                 "different, larger volume than the active fine volume. Leave null for fine only.")]
        [SerializeField] VoxelVolume _coarseVolume;
        [Tooltip("Blend band as a fraction of the fine box, over which the fine field cross-fades to " +
                 "the coarse field at its edges (hides the seam).")]
        [Range(0f, 0.5f)][SerializeField] float _blendBand = 0.1f;

        ComputeBuffer _materialBuffer;
        ComputeBuffer _radianceBuffer;
        ComputeBuffer _irradianceBuffer;
        ComputeBuffer _irradianceBlurBuffer;
        bool _materialBaked;
        VoxelVolume _volume;
        Material _voxelizeMaterial;
        uint[] _materialClear;
        int _clearKernel = -1;
        int _injectKernel = -1;
        int _gatherKernel = -1;
        int _blurKernel = -1;
        int _solveFrames;
        // Separate warm-up counter for the fine field: reset on a volume switch (while the coarse
        // field + global confidence persist), so the read fades the new fine field in from coarse.
        int _fineSolveFrames;
        bool _resetFineField;
        bool _hasLoggedMissingReferences;
        // Convergence gating: once converged the solve idles until the sun changes (or a settle
        // window elapses). Local lights are intentionally excluded from the wake check for now.
        int _transitionFramesRemaining;
        Vector3 _prevSunDir;
        Vector4 _prevSunColor;
        const int SettleTimeConstants = 4; // EMA time constants to keep solving after a sun change

        public ComputeBuffer MaterialBuffer => _materialBuffer;
        public ComputeBuffer RadianceBuffer => _radianceBuffer;
        public ComputeBuffer IrradianceBuffer => _irradianceBuffer;
        public VoxelVolume Volume => _volume;
        public Vector3 GridOrigin => _volume != null ? _volume.Bounds.min : Vector3.zero;
        public Vector3 GridSize => _volume != null ? _volume.Bounds.size : Vector3.one;
        // Per-axis voxel size: the 32^3 grid stretches to fill the (possibly non-cubic) bounds.
        public Vector3 VoxelSize => GridSize / Grid;

        // Gather rays per voxel per frame. Runtime-settable (e.g. from a debug UI); wakes the solver
        // so a change takes effect even when the convergence gate has it idling.
        public int RaysPerFrame {
            get => _raysPerFrame;
            set {
                int v = Mathf.Max(1, value);
                if (v == _raysPerFrame) return;
                _raysPerFrame = v;
                _transitionFramesRemaining = _maxSamples * SettleTimeConstants;
            }
        }

        // Coarse field: its own scene-covering VoxelVolume (32^3 grid, larger voxels). Falls back to
        // the fine bounds when unassigned so the read/visualizer degrade gracefully (empty slice -> 0).
        public bool HasCoarse => _coarseVolume != null;
        public Vector3 CoarseOrigin => _coarseVolume != null ? _coarseVolume.Bounds.min : GridOrigin;
        public Vector3 CoarseSize => _coarseVolume != null ? _coarseVolume.Bounds.size : GridSize;
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
        static readonly int s_blendBand = Shader.PropertyToID("_BgiBlendBand");
        static readonly int s_fineConfidence = Shader.PropertyToID("_BgiFineConfidence");
        static readonly int s_material = Shader.PropertyToID("_Material");
        static readonly int s_frameCount = Shader.PropertyToID("_FrameCount");
        static readonly int s_raysPerFrame = Shader.PropertyToID("_RaysPerFrame");
        static readonly int s_maxSamples = Shader.PropertyToID("_MaxSamples");
        static readonly int s_raymarchMaxSteps = Shader.PropertyToID("_RaymarchMaxSteps");
        static readonly int s_giFireflyClamp = Shader.PropertyToID("_GiFireflyClamp");
        static readonly int s_directLightDir = Shader.PropertyToID("_DirectLightDir");
        static readonly int s_directLightColor = Shader.PropertyToID("_DirectLightColor");
        static readonly int[] s_envSh = {
            Shader.PropertyToID("_EnvShAr"), Shader.PropertyToID("_EnvShAg"), Shader.PropertyToID("_EnvShAb"),
            Shader.PropertyToID("_EnvShBr"), Shader.PropertyToID("_EnvShBg"), Shader.PropertyToID("_EnvShBb"),
            Shader.PropertyToID("_EnvShC"),
        };
        static readonly int s_ambientFloor = Shader.PropertyToID("_AmbientFloor");
        static readonly int s_confidence = Shader.PropertyToID("_BgiConfidence");
        static readonly int s_intensity = Shader.PropertyToID("_BgiIntensity");
        static readonly int s_exposure = Shader.PropertyToID("_Exposure");
        #endregion

        void OnEnable() {
            Instance = this;
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

        static void SetGiBufferKeyword(bool on) {
            // GI_OFF / GI_ON / GI_BUFFER share one multi_compile set, but Shader.EnableKeyword does
            // NOT enforce mutual exclusivity for global keywords - the siblings must be disabled
            // explicitly or the default GI_OFF variant keeps running (no GI at all).
            if (on) {
                Shader.EnableKeyword("GI_BUFFER");
                Shader.DisableKeyword("GI_ON");
                Shader.DisableKeyword("GI_OFF");
            } else {
                Shader.DisableKeyword("GI_BUFFER");
                Shader.DisableKeyword("GI_ON");
                Shader.EnableKeyword("GI_OFF");
            }
        }

        void OnValidate() {
            // A geometry/threshold change invalidates the bake; redo it on the next Update, and
            // wake the (possibly idle) solver so the change actually re-settles into the field.
            _materialBaked = false;
            _transitionFramesRemaining = _maxSamples * SettleTimeConstants;
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
                    _resetFineField = true;
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

            EnsureInitialized();
            if (!_materialBaked) {
                Voxelize();
            }
            if (_resetFineField) {
                // Clear only the fine slice (n=0) so it re-converges fast for the new bounds; the
                // coarse slice is untouched. _BgiFineConfidence then ramps the fine field back in.
                ClearField(FineField * VoxelCount);
                _fineSolveFrames = 0;
                _resetFineField = false;
            }

            // Gate the solve: run while still accumulating (cold start, or a fine-field warm-up after
            // a volume switch), for a settle window after the sun changes, or always if _continuousGi.
            // Otherwise idle so a static, converged scene costs no GI compute (matches GiFieldUpdater).
            if (HasSunChanged()) {
                _transitionFramesRemaining = _maxSamples * SettleTimeConstants;
            }
            bool converging = _solveFrames < _maxSamples || _fineSolveFrames < _maxSamples;
            if (converging || _transitionFramesRemaining > 0 || _continuousGi) {
                DispatchSolve();
                if (_transitionFramesRemaining > 0) _transitionFramesRemaining--;
            }
            StoreSunState();

            SetGlobals();
            SetGiBufferKeyword(true);
        }

        // Publish the buffers + grid mapping + confidence the lit shader's SampleBufferGI reads.
        void SetGlobals() {
            Shader.SetGlobalBuffer(s_material, _materialBuffer);
            // Fragment reads the blurred field when the blur is on, else the raw field (A/B).
            Shader.SetGlobalBuffer(s_irradiance, _spatialBlur ? _irradianceBlurBuffer : _irradianceBuffer);
            // Fine field bounds + coarse field bounds + blend band for the fragment read.
            Shader.SetGlobalVector(s_gridOrigin, GridOrigin);
            Shader.SetGlobalVector(s_gridSize, GridSize);
            Shader.SetGlobalVector(s_voxelSize, VoxelSize);
            Shader.SetGlobalVector(s_coarseOrigin, CoarseOrigin);
            Shader.SetGlobalVector(s_coarseVoxelSize, CoarseVoxelSize);
            Shader.SetGlobalFloat(s_blendBand, _blendBand);
            // Cold-start fade: displayed GI = buffer * confidence, so the first rays barely show and
            // we reach 100% only once the samples are complete. Ramps linearly over maxSamples.
            float confidence = Mathf.Clamp01(_solveFrames / (float)Mathf.Max(1, _maxSamples));
            Shader.SetGlobalFloat(s_confidence, confidence);
            // Fine-field warm-up (reset on a volume switch): the read cross-fades the fine field in
            // from the coarse field as this ramps 0->1, so a new active volume never resets to black.
            float fineConfidence = Mathf.Clamp01(_fineSolveFrames / (float)Mathf.Max(1, _maxSamples));
            Shader.SetGlobalFloat(s_fineConfidence, fineConfidence);
            // GI gain from the sun's Indirect Multiplier (Light.bounceIntensity) - the standard
            // Unity control for indirect strength, used instead of a custom field.
            Light sun = RenderSettings.sun;
            Shader.SetGlobalFloat(s_intensity, sun != null ? sun.bounceIntensity : 1f);
            // The lit shader applies exp2(_Exposure) in GI mode; publish it explicitly so a stale
            // value (e.g. a negative auto-exposure left by GiFieldUpdater) can't darken the image.
            Shader.SetGlobalFloat(s_exposure, _exposure);
        }

        bool IsReady(out string reason) {
            if (_computeShader == null) { reason = "ComputeShader"; return false; }
            if (_voxelizeShader == null) { reason = "Voxelize Shader (Hidden/Lotec/BufferGiVoxelize)"; return false; }
            if (_volume.BakeRoot == null) { reason = "VoxelVolume.BakeRoot (mesh geometry to voxelize)"; return false; }
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

            // uint material, uint2 radiance/irradiance. Sized for all fields (concatenated slices).
            _materialBuffer = new ComputeBuffer(TotalVoxels, sizeof(uint));
            _radianceBuffer = new ComputeBuffer(TotalVoxels, sizeof(uint) * 2);
            _irradianceBuffer = new ComputeBuffer(TotalVoxels, sizeof(uint) * 2);
            _irradianceBlurBuffer = new ComputeBuffer(TotalVoxels, sizeof(uint) * 2);
            _materialBaked = false;

            ClearDynamicFields();
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

        // Zero one field's radiance + irradiance slice (resets its temporal sample count to 0).
        void ClearField(int fieldOffset) {
            if (_clearKernel < 0) return;
            _computeShader.SetBuffer(_clearKernel, s_radiance, _radianceBuffer);
            _computeShader.SetBuffer(_clearKernel, s_irradiance, _irradianceBuffer);
            _computeShader.SetInt(s_fieldOffset, fieldOffset);
            _computeShader.Dispatch(_clearKernel, Groups, 1, 1);
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

            // Rasterization only writes covered voxels, so clear all field slices to empty first.
            if (_materialClear == null) _materialClear = new uint[TotalVoxels];
            _materialBuffer.SetData(_materialClear);

            RenderTexture dummy = RenderTexture.GetTemporary(Grid, Grid, 0, RenderTextureFormat.R8);
            CommandBuffer cmd = new CommandBuffer { name = "BufferGI Voxelize" };
            cmd.SetRenderTarget(dummy);
            // We output clip space directly from the vertex shader, so neutralize the view-projection.
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.SetRandomWriteTarget(1, _materialBuffer);

            // Each field rasterizes its OWN volume's geometry into its slice (coarse = a separate,
            // scene-covering VoxelVolume with its own BakeRoot).
            VoxelizeFieldInto(cmd, fineRoot, GridOrigin, GridSize, VoxelSize, FineField * VoxelCount);
            Transform coarseRoot = _coarseVolume != null ? _coarseVolume.BakeRoot : null;
            if (coarseRoot != null) {
                VoxelizeFieldInto(cmd, coarseRoot, CoarseOrigin, CoarseSize, CoarseVoxelSize, CoarseField * VoxelCount);
            }

            cmd.ClearRandomWriteTargets();
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            RenderTexture.ReleaseTemporary(dummy);
            _materialBaked = true;
        }

        // Rasterize a volume's geometry into one field's slice. The voxelize shader reads the grid +
        // field offset as globals (BgiWorldToGrid / BgiSlot), so set them before the draws.
        void VoxelizeFieldInto(CommandBuffer cmd, Transform root, Vector3 origin, Vector3 size, Vector3 voxelSize, int fieldOffset) {
            MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            cmd.SetGlobalVector(s_gridOrigin, origin);
            cmd.SetGlobalVector(s_gridSize, size);
            cmd.SetGlobalVector(s_voxelSize, voxelSize);
            cmd.SetGlobalInt(s_fieldOffset, fieldOffset);

            for (int axis = 0; axis < 3; axis++) {
                cmd.SetGlobalInt(s_voxAxis, axis);
                foreach (MeshRenderer mr in renderers) {
                    if (mr == null || !mr.TryGetComponent(out MeshFilter mf)) continue;
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
            _computeShader.SetInt(s_raysPerFrame, Mathf.Max(1, _raysPerFrame));
            _computeShader.SetInt(s_maxSamples, Mathf.Max(1, _maxSamples));
            _computeShader.SetInt(s_raymarchMaxSteps, Mathf.Max(1, _raymarchMaxSteps));
            _computeShader.SetFloat(s_giFireflyClamp, _giFireflyClamp);
            _computeShader.SetVector(s_ambientFloor, (Vector4)_ambientFloor);
            SetDirectionalLightUniforms();
            Manager?.LocalLights?.ApplyToCompute(_computeShader);

            // Fine field every frame, then the coarse field every frame (when assigned).
            SolveField(GridOrigin, GridSize, VoxelSize, FineField * VoxelCount);
            if (HasCoarse) {
                SolveField(CoarseOrigin, CoarseSize, CoarseVoxelSize, CoarseField * VoxelCount);
            }

            if (_solveFrames < _maxSamples) _solveFrames++;
            if (_fineSolveFrames < _maxSamples) _fineSolveFrames++;
        }

        // Inject -> gather -> (optional) blur for one field's slice.
        void SolveField(Vector3 origin, Vector3 size, Vector3 voxelSize, int fieldOffset) {
            SetGridUniforms(origin, size, voxelSize);
            _computeShader.SetInt(s_fieldOffset, fieldOffset);

            // Inject: solid voxels emit/reflect. Reads _Material + last-frame _Irradiance, writes _Radiance.
            _computeShader.SetBuffer(_injectKernel, s_material, _materialBuffer);
            _computeShader.SetBuffer(_injectKernel, s_radiance, _radianceBuffer);
            _computeShader.SetBuffer(_injectKernel, s_irradiance, _irradianceBuffer);
            _computeShader.Dispatch(_injectKernel, Groups, 1, 1);

            // Gather: air voxels integrate 1 ray/frame off the fresh _Radiance, fold into _Irradiance.
            _computeShader.SetBuffer(_gatherKernel, s_material, _materialBuffer);
            _computeShader.SetBuffer(_gatherKernel, s_radiance, _radianceBuffer);
            _computeShader.SetBuffer(_gatherKernel, s_irradiance, _irradianceBuffer);
            _computeShader.Dispatch(_gatherKernel, Groups, 1, 1);

            // Blur: occupancy-gated spatial smoothing into the buffer the fragment read samples
            // (hides per-frame temporal shimmer). Optional, for A/B.
            if (_spatialBlur) {
                _computeShader.SetBuffer(_blurKernel, s_material, _materialBuffer);
                _computeShader.SetBuffer(_blurKernel, s_irradiance, _irradianceBuffer);
                _computeShader.SetBuffer(_blurKernel, s_irradianceBlur, _irradianceBlurBuffer);
                _computeShader.Dispatch(_blurKernel, Groups, 1, 1);
            }
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
            _materialBuffer = null;
            _radianceBuffer = null;
            _irradianceBuffer = null;
            _irradianceBlurBuffer = null;
            _materialBaked = false;
            _solveFrames = 0;
            _fineSolveFrames = 0;
            _resetFineField = false;
            _transitionFramesRemaining = 0;
        }
    }
}
