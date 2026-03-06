using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    [DisallowMultipleComponent]
    [ExecuteInEditMode]
    public class GiFieldUpdater : MonoBehaviour {
        [SerializeField] ComputeShader _giComputeShader;
        [SerializeField] bool _continuousGi;
        [Tooltip("Consider GI stable after this many samples. Stops GI updates until a lighting change is detected or continuousGI is enabled.")]
        [SerializeField] int _maxSamples = 90;
        [SerializeField] int _raysPerFrame = 1;
        [SerializeField] Texture2D _blueNoiseTexture;

        [Header("Lighting")]
        [Tooltip("Select GI solve method.")]
        [SerializeField] LightingMethod _lightingMethod = LightingMethod.PathTracing;
        [Range(0f, 1f)]
        [Tooltip("LPV light retention per iteration. Injection uses (1 - decay).")]
        [SerializeField] float _lpvDecay = 0.97f;

        public enum LightingMethod {
            PathTracing = 0,
            LPV = 1,
        }

        public Texture3D MaterialFieldAlbedoIntensity { get; set; }
        public Texture3D SurfaceDistanceFieldHighRes { get; set; }
        public Texture3D SurfaceDistanceFieldLowRes { get; set; }
        public ComputeShader GiComputeShader { get => _giComputeShader; set => _giComputeShader = value; }
        public Texture2D BlueNoiseTexture { get => _blueNoiseTexture; set => _blueNoiseTexture = value; }
        public LightingMethod GiLightingMethod { get => _lightingMethod; set => _lightingMethod = value; }
        public float LpvDecay { get => _lpvDecay; set => _lpvDecay = Mathf.Clamp01(value); }

        RenderTexture _radianceField;
        RenderTexture _irradianceFieldA;  // Ping
        RenderTexture _irradianceFieldB;  // Pong
        RenderTexture _irradianceFieldFinal; // Blurred final
        bool _isEvenFrame = true;
        int _radianceKernel;
        int _irradiancePathTracingKernel;
        int _irradianceLpvKernel;
        int _blurKernel;
        int _clearKernel = -1;
        Vector3 _radianceTextureResolution;
        int _irradianceFieldSampleCount;
        LightSettings _prevLightSettings;

        #region Shader Property IDs
        // Property IDs local to gi update compute shader
        static readonly int s_radianceField = Shader.PropertyToID("_RadianceField");
        static readonly int s_radianceFieldWrite = Shader.PropertyToID("_RadianceFieldWrite");
        static readonly int s_maxSamples = Shader.PropertyToID("_MaxSamples");
        static readonly int s_raysPerFrame = Shader.PropertyToID("_RaysPerFrame");
        static readonly int s_irradianceFieldWrite = Shader.PropertyToID("_IrradianceFieldWrite");
        static readonly int s_irradianceFieldInput = Shader.PropertyToID("_IrradianceFieldInput");
        static readonly int s_irradianceFieldFinal = Shader.PropertyToID("_IrradianceFieldFinal");
        static readonly int s_irradianceField = Shader.PropertyToID("_IrradianceFieldHistory");
        static readonly int s_radianceTextureSize = Shader.PropertyToID("_RadianceTextureSize");
        static readonly int s_materialAlbedoIntensity = Shader.PropertyToID("_MaterialAlbedoIntensity");
        static readonly int s_distanceField = Shader.PropertyToID("_DistanceField");
        static readonly int s_voxelSize = Shader.PropertyToID("_VoxelSize");
        static readonly int s_frameCount = Shader.PropertyToID("_FrameCount");
        static readonly int s_directLightDir = Shader.PropertyToID("_DirectLightDir");
        static readonly int s_directLightColor = Shader.PropertyToID("_DirectLightColor");
        static readonly int s_skyColor = Shader.PropertyToID("_SkyColor");
        static readonly int s_blueNoiseTex = Shader.PropertyToID("_BlueNoiseTex");
        static readonly int s_lpvDecay = Shader.PropertyToID("_LpvDecay");
        // Property IDs Globals for Fragment Shaders
        static readonly int s_radianceFieldVoxelSize = Shader.PropertyToID("_RadianceFieldVoxelSize");
        #endregion

        public RenderTexture IrradianceFinal => _isEvenFrame ? _irradianceFieldA : _irradianceFieldB;
        public RenderTexture IrradianceBlurred => _irradianceFieldFinal;
        RenderTexture IrradianceRead => _isEvenFrame ? _irradianceFieldB : _irradianceFieldA;

        public LightingVolume Volume { get; set; }

        void Update() {
            if (Volume == null || _giComputeShader == null) {
                Debug.LogWarning("GI Field Updater is missing required references; skipping GI update.");
                return;
            }

            if (Volume == null) return;

            if (MaterialFieldAlbedoIntensity == null) {
                MaterialFieldAlbedoIntensity = Volume.materialAlbedoIntensityTexture;
            }
            if (SurfaceDistanceFieldHighRes == null) {
                SurfaceDistanceFieldHighRes = Volume.sdfHiresTexture;
            }
            if (SurfaceDistanceFieldLowRes == null) {
                SurfaceDistanceFieldLowRes = Volume.sdfLowresTexture;
            }

            if (HasLightChanged()) {
                _irradianceFieldSampleCount = 0;
            }
            bool isStable = _irradianceFieldSampleCount > _maxSamples * 2;

            EnsureInitialized();
            if (!isStable || _continuousGi) {
                SetDirectLightParams();
                DispatchGIUpdate();
                _irradianceFieldSampleCount++;
            }

            _prevLightSettings = new LightSettings {
                sunDirection = RenderSettings.sun != null ? -RenderSettings.sun.transform.forward : Vector3.down,
                sunColor = RenderSettings.sun != null ? (Vector4)RenderSettings.sun.color * RenderSettings.sun.intensity : Vector4.zero,
                skyColor = RenderSettings.ambientMode == AmbientMode.Flat ? (Vector4)RenderSettings.ambientLight : (Vector4)RenderSettings.ambientSkyColor
            };
            _isEvenFrame = !_isEvenFrame;
            SetGlobalShaderVariables();
        }

        void OnDisable() {
            ReleaseBuffers();
        }

        bool HasLightChanged() {
            Vector3 sunDir = RenderSettings.sun != null ? -RenderSettings.sun.transform.forward : Vector3.down;
            Vector4 sunCol = RenderSettings.sun != null ? (Vector4)RenderSettings.sun.color * RenderSettings.sun.intensity : Vector4.zero;
            Vector4 skyCol = RenderSettings.ambientMode == AmbientMode.Flat ? (Vector4)RenderSettings.ambientLight : (Vector4)RenderSettings.ambientSkyColor;

            return sunDir != _prevLightSettings.sunDirection ||
                   sunCol != _prevLightSettings.sunColor ||
                   skyCol != _prevLightSettings.skyColor;
        }

        void EnsureInitialized() {
            if (_radianceField == null || _irradianceFieldB == null || _irradianceFieldA == null || _irradianceFieldFinal == null) {
                InitializeBuffers();
            }
        }

        void InitializeBuffers() {
            ReleaseBuffers();
            _irradianceFieldSampleCount = 0;
            _radianceTextureResolution = new Vector3(SurfaceDistanceFieldLowRes.width, SurfaceDistanceFieldLowRes.height, SurfaceDistanceFieldLowRes.depth);

            _radianceKernel = _giComputeShader.FindKernel("CSComputeRadiance");
            _irradiancePathTracingKernel = _giComputeShader.FindKernel("CSComputeIrradiancePathTracing");
            _irradianceLpvKernel = _giComputeShader.FindKernel("CSComputeIrradianceLPV");
            _blurKernel = _giComputeShader.FindKernel("CSBlurIrradiance");

            // Radiance Field: HDR Color (R11G11B10)
            _radianceField = CreateRadianceTexture(RenderTextureFormat.RGB111110Float, "GI_Radiance_A");

            // Irradiance Field: HDR Color (R11G11B10)
            RenderTextureFormat irradianceFormat = RenderTextureFormat.RGB111110Float;
            _irradianceFieldA = CreateRadianceTexture(irradianceFormat, "GI_Irradiance_A");
            _irradianceFieldB = CreateRadianceTexture(irradianceFormat, "GI_Irradiance_B");
            _irradianceFieldFinal = CreateRadianceTexture(irradianceFormat, "GI_Irradiance_Final");

            // TODO: Control Field: Direction & Stability (ARGB32)
            // Standard 8-bit per channel is enough for direction packing.
            // RenderTextureFormat controlFormat = RenderTextureFormat.ARGB32;
        }

        public void ReleaseBuffers() {
            ClearRadianceField();
            if (_radianceField != null)
                _radianceField.Release();
            if (_irradianceFieldB != null)
                _irradianceFieldB.Release();
            if (_irradianceFieldA != null)
                _irradianceFieldA.Release();
            if (_irradianceFieldFinal != null)
                _irradianceFieldFinal.Release();
            _radianceField = null;
            _irradianceFieldB = null;
            _irradianceFieldA = null;
            _irradianceFieldFinal = null;
        }
        void ClearRadianceField() {
            if (_giComputeShader == null) {
                Debug.LogWarning("GI compute shader is null; cannot clear volumes.");
                return;
            }

            if (_clearKernel < 0) _clearKernel = _giComputeShader.FindKernel("CSClearVolume");

            if (_radianceField != null) ClearVolumeWithCompute(_radianceField);
            if (_irradianceFieldB != null) ClearVolumeWithCompute(_irradianceFieldB);
            if (_irradianceFieldA != null) ClearVolumeWithCompute(_irradianceFieldA);
            if (_irradianceFieldFinal != null) ClearVolumeWithCompute(_irradianceFieldFinal);
        }

        void ClearVolumeWithCompute(RenderTexture rt) {
            if (rt == null) return;
            if (!rt.IsCreated()) rt.Create();

            _giComputeShader.SetTexture(_clearKernel, "_ClearTarget", rt);
            int groupsX = Mathf.CeilToInt(rt.width / 8.0f);
            int groupsY = Mathf.CeilToInt(rt.height / 8.0f);
            int groupsZ = Mathf.CeilToInt(rt.volumeDepth / 8.0f);

            _giComputeShader.Dispatch(_clearKernel, groupsX, groupsY, groupsZ);

            // Unbind for cleanliness (Unity throws exception if we do this)
            // giComputeShader.SetTexture(_clearKernelIndex, "_ClearTarget", null);
        }

        void DispatchGIUpdate() {
            // Shared parameters
            Vector3 resolution = MaterialFieldAlbedoIntensity.GetResolution();
            float voxelSize = (float)Volume.Bounds.size.x / resolution.x;
            _giComputeShader.SetVector(s_voxelSize, voxelSize * Vector4.one);
            _giComputeShader.SetInt(s_frameCount, Time.frameCount);
            _giComputeShader.SetVector(s_radianceTextureSize, _radianceTextureResolution);
            _giComputeShader.SetInt(s_raysPerFrame, _raysPerFrame);
            _giComputeShader.SetInt(s_maxSamples, _maxSamples);
            _giComputeShader.SetFloat(s_lpvDecay, _lpvDecay);

            // Dispatch (8x8x8 threads per group)
            int groupsX = Mathf.CeilToInt(_radianceTextureResolution.x / 8.0f);
            int groupsY = Mathf.CeilToInt(_radianceTextureResolution.y / 8.0f);
            int groupsZ = Mathf.CeilToInt(_radianceTextureResolution.z / 8.0f);

            // Radiance pass
            _giComputeShader.SetTexture(_radianceKernel, s_radianceFieldWrite, _radianceField);
            _giComputeShader.SetTexture(_radianceKernel, s_irradianceField, IrradianceRead);
            _giComputeShader.SetTexture(_radianceKernel, s_distanceField, SurfaceDistanceFieldLowRes);
            _giComputeShader.SetTexture(_radianceKernel, s_materialAlbedoIntensity, MaterialFieldAlbedoIntensity);
            _giComputeShader.Dispatch(_radianceKernel, groupsX, groupsY, groupsZ);

            // Irradiance pass
            // PathTracing = stochastic raymarch gather.
            // LPV = iterative neighbor propagation in the voxel grid.
            int irradianceKernel = (_lightingMethod == LightingMethod.LPV) ? _irradianceLpvKernel : _irradiancePathTracingKernel;
            _giComputeShader.SetTexture(irradianceKernel, s_radianceField, _radianceField);
            _giComputeShader.SetTexture(irradianceKernel, s_irradianceFieldWrite, IrradianceFinal);
            _giComputeShader.SetTexture(irradianceKernel, s_irradianceField, IrradianceRead);
            _giComputeShader.SetTexture(irradianceKernel, s_distanceField, SurfaceDistanceFieldLowRes);
            _giComputeShader.SetTexture(irradianceKernel, s_materialAlbedoIntensity, MaterialFieldAlbedoIntensity);
            _giComputeShader.SetTexture(irradianceKernel, s_blueNoiseTex, _blueNoiseTexture);
            _giComputeShader.Dispatch(irradianceKernel, groupsX, groupsY, groupsZ);

            // Blur pass (skip in LPV mode to avoid cross-wall bleed from generic blur)
            if (_lightingMethod != LightingMethod.LPV) {
                _giComputeShader.SetTexture(_blurKernel, s_irradianceFieldInput, IrradianceFinal);
                _giComputeShader.SetTexture(_blurKernel, s_irradianceFieldFinal, _irradianceFieldFinal);
                _giComputeShader.SetTexture(_blurKernel, s_distanceField, SurfaceDistanceFieldLowRes);
                _giComputeShader.Dispatch(_blurKernel, groupsX, groupsY, groupsZ);
            }
        }

        RenderTexture CreateRadianceTexture(RenderTextureFormat format, string name) {
            RenderTextureDescriptor desc = new RenderTextureDescriptor((int)_radianceTextureResolution.x, (int)_radianceTextureResolution.y, format, 0) {
                dimension = TextureDimension.Tex3D,
                volumeDepth = (int)_radianceTextureResolution.z,
                enableRandomWrite = true,
                msaaSamples = 1
            };

            RenderTexture rt = new RenderTexture(desc) {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = name
            };
            rt.Create();
            return rt;
        }

        void SetDirectLightParams() {
            Light sun = RenderSettings.sun;

            if (sun != null) {
                // light direction towards the scene
                Vector3 dir = -sun.transform.forward;
                _giComputeShader.SetVector(s_directLightDir, dir);
                _giComputeShader.SetVector(s_directLightColor, (Vector4)sun.color * sun.intensity); // TODO: Maybe put intensity in alpha channel.
            } else {
                DelayedLogger.Log("GI Updater: No sun light found in the scene. GI will be computed with no direct lighting.");
                _giComputeShader.SetVector(s_directLightDir, Vector3.down);
                _giComputeShader.SetVector(s_directLightColor, Vector4.zero);
            }

            Color sky = RenderSettings.ambientMode == AmbientMode.Flat ? RenderSettings.ambientLight : RenderSettings.ambientSkyColor;
            _giComputeShader.SetVector(s_skyColor, (Vector4)sky);
        }

        void SetGlobalShaderVariables() {
            // LPV uses the direct ping-pong output (no blur).
            // PathTracing uses the separate blurred irradiance texture.
            Shader.SetGlobalTexture(s_irradianceFieldFinal, _lightingMethod == LightingMethod.LPV ? IrradianceFinal : _irradianceFieldFinal);

            // Update these every frame in case the volume moves
            Vector3 resolution = MaterialFieldAlbedoIntensity.GetResolution();
            float voxelSize = MaterialFieldAlbedoIntensity.width / resolution.x;
            Shader.SetGlobalVector(s_radianceFieldVoxelSize, voxelSize * Vector4.one);
        }
    }

    struct LightSettings {
        public Vector3 sunDirection;
        public Vector4 sunColor;
        public Vector4 skyColor;
    }

    internal class DelayedLogger {
        static float _lastLogTime = 0f;
        static float _logDelay = 0.5f; // seconds
        public static void Log(string message) {
            if (Time.realtimeSinceStartup - _lastLogTime < _logDelay)
                return;
            _lastLogTime = Time.realtimeSinceStartup;
            Debug.Log(message);
        }
    }
}
