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
        public const int VoxelCount = Grid * Grid * Grid; // 32768

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
        bool _hasLoggedMissingReferences;

        public ComputeBuffer MaterialBuffer => _materialBuffer;
        public ComputeBuffer RadianceBuffer => _radianceBuffer;
        public ComputeBuffer IrradianceBuffer => _irradianceBuffer;
        public VoxelVolume Volume => _volume;
        public Vector3 GridOrigin => _volume != null ? _volume.Bounds.min : Vector3.zero;
        public Vector3 GridSize => _volume != null ? _volume.Bounds.size : Vector3.one;
        // Per-axis voxel size: the 32^3 grid stretches to fill the (possibly non-cubic) bounds.
        public Vector3 VoxelSize => GridSize / Grid;

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
            // A geometry/threshold change invalidates the bake; redo it on the next Update.
            _materialBaked = false;
        }

        void Update() {
            VoxelVolume active = Manager != null ? Manager.Volume : null;
            if (active != _volume) {
                _volume = active;
                ReleaseBuffers();
                _hasLoggedMissingReferences = false;
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
            DispatchSolve();
            SetGlobals();
            SetGiBufferKeyword(true);
        }

        // Publish the buffers + grid mapping + confidence the lit shader's SampleBufferGI reads.
        void SetGlobals() {
            Shader.SetGlobalBuffer(s_material, _materialBuffer);
            // Fragment reads the blurred field when the blur is on, else the raw field (A/B).
            Shader.SetGlobalBuffer(s_irradiance, _spatialBlur ? _irradianceBlurBuffer : _irradianceBuffer);
            Shader.SetGlobalVector(s_gridOrigin, GridOrigin);
            Shader.SetGlobalVector(s_gridSize, GridSize);
            Shader.SetGlobalVector(s_voxelSize, VoxelSize);
            // Cold-start fade: displayed GI = buffer * confidence, so the first rays barely show and
            // we reach 100% only once the samples are complete. Ramps linearly over maxSamples.
            float confidence = Mathf.Clamp01(_solveFrames / (float)Mathf.Max(1, _maxSamples));
            Shader.SetGlobalFloat(s_confidence, confidence);
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

            // uint material, uint2 radiance/irradiance.
            _materialBuffer = new ComputeBuffer(VoxelCount, sizeof(uint));
            _radianceBuffer = new ComputeBuffer(VoxelCount, sizeof(uint) * 2);
            _irradianceBuffer = new ComputeBuffer(VoxelCount, sizeof(uint) * 2);
            _irradianceBlurBuffer = new ComputeBuffer(VoxelCount, sizeof(uint) * 2);
            _materialBaked = false;

            ClearDynamicFields();
        }

        void SetGridUniforms() {
            _computeShader.SetVector(s_gridOrigin, GridOrigin);
            _computeShader.SetVector(s_gridSize, GridSize);
            _computeShader.SetVector(s_voxelSize, VoxelSize);
        }

        static int Groups => Mathf.CeilToInt(VoxelCount / 64f);

        void ClearDynamicFields() {
            if (_clearKernel < 0) return;
            _computeShader.SetBuffer(_clearKernel, s_radiance, _radianceBuffer);
            _computeShader.SetBuffer(_clearKernel, s_irradiance, _irradianceBuffer);
            _computeShader.Dispatch(_clearKernel, Groups, 1, 1);
        }

        // GPU 3-axis rasterization of the volume's mesh geometry into the 32^3 material buffer.
        // One-shot (geometry is static): clears the buffer, then renders every submesh from the X, Y
        // and Z directions; each fragment writes its voxel's albedo+occupancy via a fragment UAV.
        public void Voxelize() {
            if (_voxelizeShader == null || _materialBuffer == null) return;
            Transform root = _volume.BakeRoot;
            if (root == null) { _materialBaked = true; return; }

            if (_voxelizeMaterial == null) {
                _voxelizeMaterial = new Material(_voxelizeShader) { hideFlags = HideFlags.HideAndDontSave };
            }

            // Rasterization only writes covered voxels, so clear the buffer to empty first.
            if (_materialClear == null) _materialClear = new uint[VoxelCount];
            _materialBuffer.SetData(_materialClear);

            // The voxelize shader reads these as globals (BgiWorldToGrid / BgiIndex).
            Shader.SetGlobalVector(s_gridOrigin, GridOrigin);
            Shader.SetGlobalVector(s_gridSize, GridSize);
            Shader.SetGlobalVector(s_voxelSize, VoxelSize);

            MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            RenderTexture dummy = RenderTexture.GetTemporary(Grid, Grid, 0, RenderTextureFormat.R8);
            CommandBuffer cmd = new CommandBuffer { name = "BufferGI Voxelize" };
            cmd.SetRenderTarget(dummy);
            // We output clip space directly from the vertex shader, so neutralize the view-projection.
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.SetRandomWriteTarget(1, _materialBuffer);

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

            cmd.ClearRandomWriteTargets();
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            RenderTexture.ReleaseTemporary(dummy);
            _materialBaked = true;
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

            SetGridUniforms();
            _computeShader.SetInt(s_frameCount, Time.frameCount);
            _computeShader.SetInt(s_raysPerFrame, Mathf.Max(1, _raysPerFrame));
            _computeShader.SetInt(s_maxSamples, Mathf.Max(1, _maxSamples));
            _computeShader.SetInt(s_raymarchMaxSteps, Mathf.Max(1, _raymarchMaxSteps));
            _computeShader.SetFloat(s_giFireflyClamp, _giFireflyClamp);
            _computeShader.SetVector(s_ambientFloor, (Vector4)_ambientFloor);
            SetDirectionalLightUniforms();
            Manager?.LocalLights?.ApplyToCompute(_computeShader);

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

            // Blur: occupancy-gated spatial smoothing of the noisy 1-ray field into the buffer the
            // fragment read samples (hides per-frame temporal shimmer). Optional, for A/B.
            if (_spatialBlur) {
                _computeShader.SetBuffer(_blurKernel, s_material, _materialBuffer);
                _computeShader.SetBuffer(_blurKernel, s_irradiance, _irradianceBuffer);
                _computeShader.SetBuffer(_blurKernel, s_irradianceBlur, _irradianceBlurBuffer);
                _computeShader.Dispatch(_blurKernel, Groups, 1, 1);
            }

            if (_solveFrames < _maxSamples) _solveFrames++;
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
        static void PackAmbientProbeSH(UnityEngine.Rendering.SphericalHarmonicsL2 sh, Vector4[] outCoeff) {
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
        }
    }
}
