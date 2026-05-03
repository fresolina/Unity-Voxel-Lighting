using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    [DisallowMultipleComponent]
    [ExecuteInEditMode]
    public class GiFieldUpdater : MonoBehaviour {
        static bool s_loggedUnsupportedRuntimeGi;
        static bool s_loggedGiTextureFormatSelection;
        static readonly GraphicsFormat[] s_webGpuGiTextureFormatCandidates = {
            // WebGPU storage textures require strict format matching with RWTexture3D<float4>.
            GraphicsFormat.R32G32B32A32_SFloat,
        };
        static readonly GraphicsFormat[] s_giTextureFormatCandidates = {
            // rg11b10 is ideal for HDR GI, but not yet supported in some browser WebGPU backends.
            GraphicsFormat.B10G11R11_UFloatPack32,
            // rgba16float is the preferred WebGPU storage fallback for HDR lighting data.
            GraphicsFormat.R16G16B16A16_SFloat,
            // rgba8unorm is widely supported for storage textures in browsers, but clamps GI to LDR.
            GraphicsFormat.R8G8B8A8_UNorm,
            // Keep full float as a last resort because the 3D volume memory cost is high.
            GraphicsFormat.R32G32B32A32_SFloat,
        };
        bool _hasLoggedMissingReferences;

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

        public Texture3D MaterialFieldAlbedoIntensity => Volume.materialAlbedoIntensityTexture;
        public Texture3D SurfaceDistanceFieldHighRes => Volume.sdfHiresTexture;
        public Texture3D SurfaceDistanceFieldLowRes => Volume.sdfLowresTexture;
        public ComputeShader GiComputeShader { get => _giComputeShader; set => _giComputeShader = value; }
        public Texture2D BlueNoiseTexture { get => _blueNoiseTexture; set => _blueNoiseTexture = value; }
        public LightingMethod GiLightingMethod {
            get => _lightingMethod;
            set => SetLightingMethod(value);
        }
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
        int _clearVolumeKernel;
        Vector3 _radianceTextureResolution;
        int _irradianceFieldSampleCount;
        GraphicsFormat _giTextureFormat;
        LightSettings _prevLightSettings;
        int _currentPointLightCount;
        int _currentSpotLightCount;
        readonly Vector4[] _pointLightPositionRanges = new Vector4[LightingManager.MaxPointLights];
        readonly Vector4[] _pointLightColors = new Vector4[LightingManager.MaxPointLights];
        readonly Vector4[] _spotLightPositionRanges = new Vector4[LightingManager.MaxSpotLights];
        readonly Vector4[] _spotLightDirectionAngleScales = new Vector4[LightingManager.MaxSpotLights];
        readonly Vector4[] _spotLightColorAngleOffsets = new Vector4[LightingManager.MaxSpotLights];

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
        static readonly int s_clearVolumeTarget = Shader.PropertyToID("_ClearVolumeTarget");
        static readonly int s_radianceTextureSize = Shader.PropertyToID("_RadianceTextureSize");
        static readonly int s_materialAlbedoIntensity = Shader.PropertyToID("_MaterialAlbedoIntensity");
        static readonly int s_distanceField = Shader.PropertyToID("_DistanceField");
        static readonly int s_voxelSize = Shader.PropertyToID("_VoxelSize");
        static readonly int s_frameCount = Shader.PropertyToID("_FrameCount");
        static readonly int s_directLightDir = Shader.PropertyToID("_DirectLightDir");
        static readonly int s_directLightColor = Shader.PropertyToID("_DirectLightColor");
        static readonly int s_pointLightCount = Shader.PropertyToID("_PointLightCount");
        static readonly int s_pointLightPositionRange = Shader.PropertyToID("_PointLightPositionRange");
        static readonly int s_pointLightColor = Shader.PropertyToID("_PointLightColor");
        static readonly int s_spotLightCount = Shader.PropertyToID("_SpotLightCount");
        static readonly int s_spotLightPositionRange = Shader.PropertyToID("_SpotLightPositionRange");
        static readonly int s_spotLightDirectionAngleScale = Shader.PropertyToID("_SpotLightDirectionAngleScale");
        static readonly int s_spotLightColorAngleOffset = Shader.PropertyToID("_SpotLightColorAngleOffset");
        static readonly int s_skyColor = Shader.PropertyToID("_SkyColor");
        static readonly int s_blueNoiseTex = Shader.PropertyToID("_BlueNoiseTex");
        static readonly int s_injectLpvSky = Shader.PropertyToID("_InjectLpvSky");
        static readonly int s_lpvDecay = Shader.PropertyToID("_LpvDecay");
        // Property IDs Globals for Fragment Shaders
        static readonly int s_radianceFieldVoxelSize = Shader.PropertyToID("_RadianceFieldVoxelSize");
        #endregion

        public RenderTexture IrradianceFinal => _isEvenFrame ? _irradianceFieldA : _irradianceFieldB;
        public RenderTexture IrradianceBlurred => _irradianceFieldFinal;
        RenderTexture IrradianceRead => _isEvenFrame ? _irradianceFieldB : _irradianceFieldA;

        public LightingVolume Volume { get; set; }

        LightingManager Manager => LightingManager.Instance;

        Vector3 GetTextureVoxelSize(Texture3D texture) {
            if (texture == null) {
                return Vector3.one;
            }

            return new Vector3(
                Volume.Bounds.size.x / Mathf.Max(1, texture.width),
                Volume.Bounds.size.y / Mathf.Max(1, texture.height),
                Volume.Bounds.size.z / Mathf.Max(1, texture.depth));
        }

        bool TryGetCubicVoxelSize(Texture3D texture, out float voxelSize, out string reason) {
            Vector3 voxelSize3 = GetTextureVoxelSize(texture);
            float minVoxelSize = Mathf.Min(voxelSize3.x, Mathf.Min(voxelSize3.y, voxelSize3.z));
            float maxVoxelSize = Mathf.Max(voxelSize3.x, Mathf.Max(voxelSize3.y, voxelSize3.z));
            float tolerance = Mathf.Max(1e-5f, maxVoxelSize * 0.1f);

            if (maxVoxelSize - minVoxelSize > tolerance) {
                voxelSize = maxVoxelSize;
                reason = $"{texture.name} resolves to non-cubic voxels ({voxelSize3}). Runtime GI assumes cubic voxels; recompute bounds and rebake the SDF/GI textures.";
                return false;
            }

            voxelSize = maxVoxelSize;
            reason = null;
            return true;
        }

        float GetGiGridVoxelSize() {
            // GI centers, SDF distances, and runtime irradiance reads all live on the
            // low-res SDF / GI grid. The material field can be a different resolution,
            // but it is only sampled by normalized UVW, so its texel size must not drive
            // shell widths, start offsets, or runtime read offsets.
            TryGetCubicVoxelSize(SurfaceDistanceFieldLowRes, out float voxelSize, out _);
            return voxelSize;
        }

        public bool SetLightingMethod(LightingMethod lightingMethod) {
            if (_lightingMethod == lightingMethod) {
                return false;
            }

            _lightingMethod = lightingMethod;
            ResetLightingHistory();
            return true;
        }

        public LightingMethod ToggleLightingMethod() {
            SetLightingMethod(_lightingMethod == LightingMethod.PathTracing ? LightingMethod.LPV : LightingMethod.PathTracing);
            return _lightingMethod;
        }

        public void ResetLightingHistory() {
            _irradianceFieldSampleCount = 0;
            _prevLightSettings = default;
            _isEvenFrame = true;

            if (_radianceField == null && _irradianceFieldA == null && _irradianceFieldB == null && _irradianceFieldFinal == null) {
                return;
            }

            ClearVolume(_radianceField);
            ClearVolume(_irradianceFieldA);
            ClearVolume(_irradianceFieldB);
            ClearVolume(_irradianceFieldFinal);
        }

        void Start() {
            if (Volume == null && Manager != null)
                Volume = Manager.Volume;
            Debug.Log($"GiFieldUpdater.Start: Volume={(Volume != null ? Volume.gameObject.name : "null")}", this);
        }

        void Update() {
            if (!SupportsRuntimeGi(out string unsupportedReason)) {
                ReleaseBuffers();

                if (!s_loggedUnsupportedRuntimeGi) {
                    s_loggedUnsupportedRuntimeGi = true;
                    Debug.LogError($"Runtime GI requires WebGPU support: {unsupportedReason}", this);
                }

                enabled = false;
                return;
            }

            if (!IsRuntimeGiReady(out string missingReason)) {
                if (!_hasLoggedMissingReferences) {
                    _hasLoggedMissingReferences = true;
                    string volName = Volume != null ? Volume.gameObject.name : "null";
                    string matName = Volume != null && Volume.materialAlbedoIntensityTexture != null ? Volume.materialAlbedoIntensityTexture.name : "null";
                    string sdfHires = Volume != null && Volume.sdfHiresTexture != null ? Volume.sdfHiresTexture.name : "null";
                    string sdfLow = Volume != null && Volume.sdfLowresTexture != null ? Volume.sdfLowresTexture.name : "null";
                    string occlName = Volume != null && Volume.occlusionBitmaskTexture != null ? Volume.occlusionBitmaskTexture.name : "null";
                    Debug.LogWarning($"GI Field Updater is missing required references: {missingReason}. Volume={volName}, materialAlbedoIntensityTexture={matName}, sdfHiresTexture={sdfHires}, sdfLowresTexture={sdfLow}, occlusionBitmaskTexture={occlName}. Waiting for runtime GI initialization.", this);
                }
                return;
            }

            _hasLoggedMissingReferences = false;

            if (HasLightChanged()) {
                _irradianceFieldSampleCount = 0;
            }
            bool isStable = _irradianceFieldSampleCount > _maxSamples * 2;

            EnsureInitialized();
            if (!isStable || _continuousGi) {
                SetPointLightShaderUniforms();
                SetSpotLightShaderUniforms();
                SetDirectionalLightUniforms();
                DispatchGIUpdate();
                _irradianceFieldSampleCount++;
                _isEvenFrame = !_isEvenFrame;
                SetGlobalShaderVariables();
            }

            _prevLightSettings = new LightSettings {
                sunDirection = RenderSettings.sun != null ? -RenderSettings.sun.transform.forward : Vector3.down,
                sunColor = RenderSettings.sun != null ? (Vector4)RenderSettings.sun.color * RenderSettings.sun.intensity : Vector4.zero,
                skyColor = RenderSettings.ambientMode == AmbientMode.Flat ? (Vector4)RenderSettings.ambientLight : (Vector4)RenderSettings.ambientSkyColor,
                localLightHash = GetLocalLightStateHash()
            };
        }

        void OnDisable() {
            ReleaseBuffers();
        }

        bool IsRuntimeGiReady(out string reason) {
            if (Volume == null) {
                reason = "LightingVolume";
                return false;
            }

            if (_giComputeShader == null) {
                reason = "ComputeShader";
                return false;
            }

            if (MaterialFieldAlbedoIntensity == null) {
                reason = $"LightingVolume.materialAlbedoIntensityTexture";
                return false;
            }

            if (SurfaceDistanceFieldLowRes == null) {
                reason = "LightingVolume.sdfLowresTexture";
                return false;
            }

            if (!TryGetCubicVoxelSize(SurfaceDistanceFieldLowRes, out _, out reason)) {
                return false;
            }

            if (_lightingMethod == LightingMethod.PathTracing && _blueNoiseTexture == null) {
                reason = "BlueNoiseTexture";
                return false;
            }

            reason = null;
            return true;
        }

        bool SupportsRuntimeGi(out string reason) {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.WebGPU) {
                reason = $"the active graphics backend is '{SystemInfo.graphicsDeviceType}', but this package requires WebGPU on Web builds.";
                return false;
            }
#endif

            if (!SystemInfo.supportsComputeShaders) {
                reason = "compute shaders are not supported.";
                return false;
            }

            if (!SystemInfo.supports3DTextures) {
                reason = "3D textures are not supported.";
                return false;
            }

            if (!TryGetSupportedGiTextureFormat(out _, out string formatReason)) {
                reason = formatReason;
                return false;
            }

            reason = null;
            return true;
        }

        bool HasLightChanged() {
            Vector3 sunDir = RenderSettings.sun != null ? -RenderSettings.sun.transform.forward : Vector3.down;
            Vector4 sunCol = RenderSettings.sun != null ? (Vector4)RenderSettings.sun.color * RenderSettings.sun.intensity : Vector4.zero;
            Vector4 skyCol = RenderSettings.ambientMode == AmbientMode.Flat ? (Vector4)RenderSettings.ambientLight : (Vector4)RenderSettings.ambientSkyColor;
            int localLightHash = GetLocalLightStateHash();

            return sunDir != _prevLightSettings.sunDirection ||
                   sunCol != _prevLightSettings.sunColor ||
                   skyCol != _prevLightSettings.skyColor ||
                   localLightHash != _prevLightSettings.localLightHash;
        }

        int GetLocalLightStateHash() {
            if (Manager == null) {
                return 0;
            }

            unchecked {
                int hash = 17;
                int pointLightCount = 0;
                int spotLightCount = 0;
                var additionalLights = Manager.AdditionalLights;

                for (int i = 0; i < additionalLights.Count; i++) {
                    Light light = additionalLights[i];
                    if (IsSupportedPointLight(light) && pointLightCount < LightingManager.MaxPointLights) {
                        Vector3 position = light.transform.position;
                        Color color = light.color;
                        hash = (hash * 31) + ((int)light.type);
                        hash = (hash * 31) + position.x.GetHashCode();
                        hash = (hash * 31) + position.y.GetHashCode();
                        hash = (hash * 31) + position.z.GetHashCode();
                        hash = (hash * 31) + color.r.GetHashCode();
                        hash = (hash * 31) + color.g.GetHashCode();
                        hash = (hash * 31) + color.b.GetHashCode();
                        hash = (hash * 31) + light.range.GetHashCode();
                        hash = (hash * 31) + light.intensity.GetHashCode();
                        pointLightCount++;
                    } else if (IsSupportedSpotLight(light) && spotLightCount < LightingManager.MaxSpotLights) {
                        Vector3 position = light.transform.position;
                        Vector3 direction = light.transform.forward;
                        Color color = light.color;
                        hash = (hash * 31) + ((int)light.type);
                        hash = (hash * 31) + position.x.GetHashCode();
                        hash = (hash * 31) + position.y.GetHashCode();
                        hash = (hash * 31) + position.z.GetHashCode();
                        hash = (hash * 31) + direction.x.GetHashCode();
                        hash = (hash * 31) + direction.y.GetHashCode();
                        hash = (hash * 31) + direction.z.GetHashCode();
                        hash = (hash * 31) + color.r.GetHashCode();
                        hash = (hash * 31) + color.g.GetHashCode();
                        hash = (hash * 31) + color.b.GetHashCode();
                        hash = (hash * 31) + light.range.GetHashCode();
                        hash = (hash * 31) + light.intensity.GetHashCode();
                        hash = (hash * 31) + light.spotAngle.GetHashCode();
                        hash = (hash * 31) + light.innerSpotAngle.GetHashCode();
                        spotLightCount++;
                    }

                    if (pointLightCount >= LightingManager.MaxPointLights && spotLightCount >= LightingManager.MaxSpotLights) {
                        break;
                    }
                }

                hash = (hash * 31) + pointLightCount;
                return (hash * 31) + spotLightCount;
            }
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

            if (!TryGetSupportedGiTextureFormat(out _giTextureFormat, out string formatReason)) {
                Debug.LogError($"Failed to initialize GI field textures: {formatReason}", this);
                enabled = false;
                return;
            }

            _radianceKernel = _giComputeShader.FindKernel("CSComputeRadiance");
            _irradiancePathTracingKernel = _giComputeShader.FindKernel("CSComputeIrradiancePathTracing");
            _irradianceLpvKernel = _giComputeShader.FindKernel("CSComputeIrradianceLPV");
            _blurKernel = _giComputeShader.FindKernel("CSBlurIrradiance");
            _clearVolumeKernel = _giComputeShader.FindKernel("CSClearVolume");

            // Verify kernel validity on this platform/build. Some backends may fail to compile
            // or expose kernels differently, which causes Dispatch to throw "Kernel at index is invalid".
            Debug.Log($"GI compute shader: {_giComputeShader.name} kernel indices: Radiance={_radianceKernel}, IrradiancePathTracing={_irradiancePathTracingKernel}, IrradianceLPV={_irradianceLpvKernel}, Blur={_blurKernel}, Clear={_clearVolumeKernel}", this);
            bool kernelVerificationFailed = false;
            try {
                uint gx, gy, gz;
                try {
                    _giComputeShader.GetKernelThreadGroupSizes(_radianceKernel, out gx, out gy, out gz);
                    Debug.Log($"  Kernel CSComputeRadiance threadgroups: {gx},{gy},{gz}", this);
                } catch (System.Exception e) {
                    Debug.LogError($"  Kernel CSComputeRadiance verification failed: {e.Message}", this);
                    kernelVerificationFailed = true;
                }

                try {
                    _giComputeShader.GetKernelThreadGroupSizes(_irradiancePathTracingKernel, out gx, out gy, out gz);
                    Debug.Log($"  Kernel CSComputeIrradiancePathTracing threadgroups: {gx},{gy},{gz}", this);
                } catch (System.Exception e) {
                    Debug.LogError($"  Kernel CSComputeIrradiancePathTracing verification failed: {e.Message}", this);
                    kernelVerificationFailed = true;
                }

                try {
                    _giComputeShader.GetKernelThreadGroupSizes(_irradianceLpvKernel, out gx, out gy, out gz);
                    Debug.Log($"  Kernel CSComputeIrradianceLPV threadgroups: {gx},{gy},{gz}", this);
                } catch (System.Exception e) {
                    Debug.LogError($"  Kernel CSComputeIrradianceLPV verification failed: {e.Message}", this);
                    kernelVerificationFailed = true;
                }

                try {
                    _giComputeShader.GetKernelThreadGroupSizes(_blurKernel, out gx, out gy, out gz);
                    Debug.Log($"  Kernel CSBlurIrradiance threadgroups: {gx},{gy},{gz}", this);
                } catch (System.Exception e) {
                    Debug.LogError($"  Kernel CSBlurIrradiance verification failed: {e.Message}", this);
                    kernelVerificationFailed = true;
                }

                try {
                    _giComputeShader.GetKernelThreadGroupSizes(_clearVolumeKernel, out gx, out gy, out gz);
                    Debug.Log($"  Kernel CSClearVolume threadgroups: {gx},{gy},{gz}", this);
                } catch (System.Exception e) {
                    Debug.LogError($"  Kernel CSClearVolume verification failed: {e.Message}", this);
                    kernelVerificationFailed = true;
                }
            } catch (System.Exception ex) {
                Debug.LogError($"GI compute shader kernels failed verification (outer): {ex.Message}.", this);
                kernelVerificationFailed = true;
            }

            if (kernelVerificationFailed) {
                Debug.LogError("One or more GI compute shader kernels failed verification. Disabling runtime GI.", this);
                enabled = false;
                ReleaseBuffers();
                return;
            }

            // Prefer packed HDR when the backend supports UAV writes, otherwise fall back to a wider float format.
            _radianceField = CreateRadianceTexture(_giTextureFormat, "GI_Radiance_A");

            _irradianceFieldA = CreateRadianceTexture(_giTextureFormat, "GI_Irradiance_A");
            _irradianceFieldB = CreateRadianceTexture(_giTextureFormat, "GI_Irradiance_B");
            _irradianceFieldFinal = CreateRadianceTexture(_giTextureFormat, "GI_Irradiance_Final");
            ClearVolume(_radianceField);
            ClearVolume(_irradianceFieldA);
            ClearVolume(_irradianceFieldB);
            ClearVolume(_irradianceFieldFinal);

            // TODO: Control Field: Direction & Stability (ARGB32)
            // Standard 8-bit per channel is enough for direction packing.
            // RenderTextureFormat controlFormat = RenderTextureFormat.ARGB32;
        }

        public void ReleaseBuffers() {
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

        void DispatchGIUpdate() {
            // Shared parameters
            float voxelSize = GetGiGridVoxelSize();
            _giComputeShader.SetVector(s_voxelSize, voxelSize * Vector3.one);
            _giComputeShader.SetInt(s_frameCount, Time.frameCount);
            _giComputeShader.SetVector(s_radianceTextureSize, _radianceTextureResolution);
            _giComputeShader.SetInt(s_raysPerFrame, _raysPerFrame);
            _giComputeShader.SetInt(s_maxSamples, _maxSamples);
            _giComputeShader.SetInt(s_injectLpvSky, _lightingMethod == LightingMethod.LPV ? 1 : 0);
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
            if (_lightingMethod == LightingMethod.PathTracing) {
                _giComputeShader.SetTexture(_irradiancePathTracingKernel, s_blueNoiseTex, _blueNoiseTexture);
            }
            _giComputeShader.Dispatch(irradianceKernel, groupsX, groupsY, groupsZ);

            // Blur pass (skip in LPV mode to avoid cross-wall bleed from generic blur)
            if (_lightingMethod != LightingMethod.LPV) {
                _giComputeShader.SetTexture(_blurKernel, s_irradianceFieldInput, IrradianceFinal);
                _giComputeShader.SetTexture(_blurKernel, s_irradianceFieldFinal, _irradianceFieldFinal);
                _giComputeShader.SetTexture(_blurKernel, s_distanceField, SurfaceDistanceFieldLowRes);
                _giComputeShader.Dispatch(_blurKernel, groupsX, groupsY, groupsZ);
            }
        }

        RenderTexture CreateRadianceTexture(GraphicsFormat format, string name) {
            RenderTextureDescriptor desc = new RenderTextureDescriptor((int)_radianceTextureResolution.x, (int)_radianceTextureResolution.y) {
                graphicsFormat = format,
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

        static bool TryGetSupportedGiTextureFormat(out GraphicsFormat format, out string reason) {
            GraphicsFormat[] candidates = SystemInfo.graphicsDeviceType == GraphicsDeviceType.WebGPU
                ? s_webGpuGiTextureFormatCandidates
                : s_giTextureFormatCandidates;

            for (int i = 0; i < candidates.Length; i++) {
                GraphicsFormat candidate = candidates[i];
                if (!SystemInfo.IsFormatSupported(candidate, GraphicsFormatUsage.Sample)) {
                    continue;
                }

                if (!SystemInfo.IsFormatSupported(candidate, GraphicsFormatUsage.LoadStore)) {
                    continue;
                }

                format = candidate;
                if (!s_loggedGiTextureFormatSelection) {
                    s_loggedGiTextureFormatSelection = true;
                    Debug.Log($"GI field textures using graphics format {candidate}.");
                }

                reason = null;
                return true;
            }

            format = GraphicsFormat.None;
            reason = SystemInfo.graphicsDeviceType == GraphicsDeviceType.WebGPU
                ? "no supported WebGPU 3D storage format was found for RWTexture3D<float4> GI volumes. R32G32B32A32_SFloat is required for the current shader declarations."
                : "no supported 3D graphics format was found for both sampled reads and compute load/store writes.";
            return false;
        }

        void ClearVolume(RenderTexture rt) {
            if (rt == null) return;
            if (!rt.IsCreated()) rt.Create();

            int groupsX = Mathf.CeilToInt(_radianceTextureResolution.x / 8.0f);
            int groupsY = Mathf.CeilToInt(_radianceTextureResolution.y / 8.0f);
            int groupsZ = Mathf.CeilToInt(_radianceTextureResolution.z / 8.0f);
            _giComputeShader.SetVector(s_radianceTextureSize, _radianceTextureResolution);
            _giComputeShader.SetTexture(_clearVolumeKernel, s_clearVolumeTarget, rt);
            _giComputeShader.Dispatch(_clearVolumeKernel, groupsX, groupsY, groupsZ);
        }

        void SetDirectionalLightUniforms() {
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

        void SetPointLightShaderUniforms() {
            if (_giComputeShader == null) {
                return;
            }

            int pointLightCount = 0;
            var additionalLights = Manager != null ? Manager.AdditionalLights : null;
            if (additionalLights != null) {
                for (int i = 0; i < additionalLights.Count; i++) {
                    Light light = additionalLights[i];
                    if (!IsSupportedPointLight(light)) {
                        continue;
                    }

                    Vector3 position = light.transform.position;
                    _pointLightPositionRanges[pointLightCount] = new Vector4(position.x, position.y, position.z, light.range);
                    _pointLightColors[pointLightCount] = (Vector4)light.color * light.intensity;
                    pointLightCount++;

                    if (pointLightCount >= LightingManager.MaxPointLights) {
                        break;
                    }
                }
            }

            _currentPointLightCount = pointLightCount;
            _giComputeShader.SetInt(s_pointLightCount, pointLightCount);
            _giComputeShader.SetVectorArray(s_pointLightPositionRange, _pointLightPositionRanges);
            _giComputeShader.SetVectorArray(s_pointLightColor, _pointLightColors);
        }

        void SetSpotLightShaderUniforms() {
            if (_giComputeShader == null) {
                return;
            }

            int spotLightCount = 0;
            var additionalLights = Manager != null ? Manager.AdditionalLights : null;
            if (additionalLights != null) {
                for (int i = 0; i < additionalLights.Count; i++) {
                    Light light = additionalLights[i];
                    if (!IsSupportedSpotLight(light)) {
                        continue;
                    }

                    Vector3 position = light.transform.position;
                    Vector3 direction = light.transform.forward;
                    float outerCos = Mathf.Cos(light.spotAngle * Mathf.Deg2Rad * 0.5f);
                    float innerCos = Mathf.Cos(light.innerSpotAngle * Mathf.Deg2Rad * 0.5f);
                    float angleRange = Mathf.Max(innerCos - outerCos, 1e-4f);
                    float angleScale = 1f / angleRange;
                    float angleOffset = -outerCos * angleScale;

                    _spotLightPositionRanges[spotLightCount] = new Vector4(position.x, position.y, position.z, light.range);
                    _spotLightDirectionAngleScales[spotLightCount] = new Vector4(direction.x, direction.y, direction.z, angleScale);
                    _spotLightColorAngleOffsets[spotLightCount] = new Vector4(light.color.r * light.intensity, light.color.g * light.intensity, light.color.b * light.intensity, angleOffset);
                    spotLightCount++;

                    if (spotLightCount >= LightingManager.MaxSpotLights) {
                        break;
                    }
                }
            }

            _currentSpotLightCount = spotLightCount;
            _giComputeShader.SetInt(s_spotLightCount, spotLightCount);
            _giComputeShader.SetVectorArray(s_spotLightPositionRange, _spotLightPositionRanges);
            _giComputeShader.SetVectorArray(s_spotLightDirectionAngleScale, _spotLightDirectionAngleScales);
            _giComputeShader.SetVectorArray(s_spotLightColorAngleOffset, _spotLightColorAngleOffsets);
        }

        static bool IsSupportedPointLight(Light light) {
            return light != null &&
                   light.isActiveAndEnabled &&
                   light.type == LightType.Point &&
                   light.range > 0f &&
                   light.intensity > 0f;
        }

        static bool IsSupportedSpotLight(Light light) {
            return light != null &&
                   light.isActiveAndEnabled &&
                   light.type == LightType.Spot &&
                   light.range > 0f &&
                   light.intensity > 0f &&
                   light.spotAngle > 0f;
        }

        void SetGlobalShaderVariables() {
            // LPV uses the direct ping-pong output (no blur).
            // PathTracing uses the separate blurred irradiance texture.
            Shader.SetGlobalTexture(s_irradianceFieldFinal, _lightingMethod == LightingMethod.LPV ? IrradianceFinal : _irradianceFieldFinal);
            Shader.SetGlobalTexture(s_distanceField, SurfaceDistanceFieldLowRes);

            // Update these every frame in case the volume moves
            // The runtime GI include samples the irradiance volume, so use the GI grid
            // spacing here as well. A different material-field resolution should not
            // change the surface read offset in SampleVoxelGI.
            float voxelSize = GetGiGridVoxelSize();
            Shader.SetGlobalVector(s_radianceFieldVoxelSize, voxelSize * Vector3.one);

            // Visible local-light direct shading now runs in the fragment shader,
            // so publish the curated point/spot light arrays there as globals too.
            Shader.SetGlobalInt(s_pointLightCount, _currentPointLightCount);
            Shader.SetGlobalVectorArray(s_pointLightPositionRange, _pointLightPositionRanges);
            Shader.SetGlobalVectorArray(s_pointLightColor, _pointLightColors);
            Shader.SetGlobalInt(s_spotLightCount, _currentSpotLightCount);
            Shader.SetGlobalVectorArray(s_spotLightPositionRange, _spotLightPositionRanges);
            Shader.SetGlobalVectorArray(s_spotLightDirectionAngleScale, _spotLightDirectionAngleScales);
            Shader.SetGlobalVectorArray(s_spotLightColorAngleOffset, _spotLightColorAngleOffsets);
        }
    }

    struct LightSettings {
        public Vector3 sunDirection;
        public Vector4 sunColor;
        public Vector4 skyColor;
        public int localLightHash;
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
