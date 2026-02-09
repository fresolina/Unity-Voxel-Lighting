using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    [System.Serializable]
    public class GiFieldUpdater {
        [SerializeField] ComputeShader _giComputeShader;
        [SerializeField] int _maxSamples = 2;
        [SerializeField] int _raysPerFrame = 1;
        public Texture3D MaterialFieldAlbedoRoughness { get; set; }
        public Texture3D MaterialFieldEmissionMetallic { get; set; }
        public Texture3D SurfaceDistanceFieldHighRes { get; set; }
        public Texture3D SurfaceDistanceFieldLowRes { get; set; }

        RenderTexture _radianceField;
        RenderTexture _irradianceFieldA;  // Ping
        RenderTexture _irradianceFieldB;  // Pong
        RenderTexture _controlFieldA; // Ping (Direction + Stability)
        RenderTexture _controlFieldB; // Pong
        bool _isEvenFrame = true;
        int _kernelIndex;
        int _irradianceKernelIndex;
        int _clearKernelIndex = -1;
        Vector3 _radianceTextureResolution;
        float _irradianceStability;
        int _irradianceFieldSampleCount;
        LightSettings prevLightSettings;

        #region Shader Property IDs
        // Property IDs local to gi update compute shader
        static readonly int s_radianceField = Shader.PropertyToID("_RadianceField");
        static readonly int s_maxSamples = Shader.PropertyToID("_MaxSamples");
        static readonly int s_raysPerFrame = Shader.PropertyToID("_RaysPerFrame");
        static readonly int s_radianceFieldWrite = Shader.PropertyToID("_RadianceFieldWrite");
        static readonly int s_irradianceFieldWrite = Shader.PropertyToID("_IrradianceFieldWrite");
        static readonly int s_irradianceField = Shader.PropertyToID("_IrradianceFieldHistory");
        static readonly int s_irradianceStability = Shader.PropertyToID("_IrradianceStability");
        static readonly int s_irradianceFieldSampleCount = Shader.PropertyToID("_IrradianceFieldSampleCount");
        static readonly int s_radianceTextureSize = Shader.PropertyToID("_RadianceTextureSize");
        static readonly int s_controlRead = Shader.PropertyToID("_ControlRead");
        static readonly int s_controlWrite = Shader.PropertyToID("_ControlWrite");
        static readonly int s_materialAlbedo = Shader.PropertyToID("_MaterialAlbedoRoughness");
        static readonly int s_materialEmission = Shader.PropertyToID("_MaterialEmissionMetallic");
        static readonly int s_distanceField = Shader.PropertyToID("_DistanceField");
        static readonly int s_voxelSize = Shader.PropertyToID("_VoxelSize");
        static readonly int s_frameCount = Shader.PropertyToID("_FrameCount");
        static readonly int s_directLightDir = Shader.PropertyToID("_DirectLightDir");
        static readonly int s_directLightColor = Shader.PropertyToID("_DirectLightColor");
        static readonly int s_skyColor = Shader.PropertyToID("_SkyColor");
        // Property IDs Globals for Fragment Shaders
        static readonly int s_radianceFieldVoxelSize = Shader.PropertyToID("_RadianceFieldVoxelSize");
        #endregion

        public RenderTexture IrradianceFinal => _isEvenFrame ? _irradianceFieldA : _irradianceFieldB;
        RenderTexture IrradianceRead => _isEvenFrame ? _irradianceFieldB : _irradianceFieldA;
        RenderTexture ControlRead => _isEvenFrame ? _controlFieldA : _controlFieldB;
        RenderTexture ControlWrite => _isEvenFrame ? _controlFieldB : _controlFieldA;

        public LightingVolume Volume { get; set; }

        public void Update() {
            if (Volume == null || _giComputeShader == null) {
                Debug.LogWarning("GI Field Updater is missing required references; skipping GI update.");
                return;
            }
            if (HasLightChanged()) {
                _irradianceFieldSampleCount = 0;
            }

            EnsureInitialized();
            SetGlobalShaderVariables();
            SetDirectLightParams();
            DispatchGIUpdate();
            _isEvenFrame = !_isEvenFrame;
            _irradianceFieldSampleCount++;
            if (_irradianceFieldSampleCount > _maxSamples)
                _irradianceFieldSampleCount = _maxSamples; // Clamp to max to avoid uint overflow in shader
            // _irradianceStability = 1f / (_irradianceFieldSampleCount + 1f);
            _irradianceStability = 1f / _maxSamples;
            prevLightSettings = new LightSettings {
                sunDirection = RenderSettings.sun != null ? -RenderSettings.sun.transform.forward : Vector3.down,
                sunColor = RenderSettings.sun != null ? (Vector4)RenderSettings.sun.color * RenderSettings.sun.intensity : Vector4.zero,
                skyColor = RenderSettings.ambientMode == AmbientMode.Flat ? (Vector4)RenderSettings.ambientLight : (Vector4)RenderSettings.ambientSkyColor
            };
        }

        bool HasLightChanged() {
            Vector3 sunDir = RenderSettings.sun != null ? -RenderSettings.sun.transform.forward : Vector3.down;
            Vector4 sunCol = RenderSettings.sun != null ? (Vector4)RenderSettings.sun.color * RenderSettings.sun.intensity : Vector4.zero;
            Vector4 skyCol = RenderSettings.ambientMode == AmbientMode.Flat ? (Vector4)RenderSettings.ambientLight : (Vector4)RenderSettings.ambientSkyColor;

            return sunDir != prevLightSettings.sunDirection ||
                   sunCol != prevLightSettings.sunColor ||
                   skyCol != prevLightSettings.skyColor;
        }

        void EnsureInitialized() {
            if (_radianceField == null || _irradianceFieldB == null || _irradianceFieldA == null || _controlFieldA == null || _controlFieldB == null) {
                InitializeBuffers();
            }
        }

        void InitializeBuffers() {
            ReleaseBuffers();
            _irradianceFieldSampleCount = 0;
            _radianceTextureResolution = new Vector3(SurfaceDistanceFieldLowRes.width, SurfaceDistanceFieldLowRes.height, SurfaceDistanceFieldLowRes.depth);

            _kernelIndex = _giComputeShader.FindKernel("CSMain");
            _irradianceKernelIndex = _giComputeShader.FindKernel("CSComputeIrradiance");

            // Radiance Field: HDR Color (R11G11B10)
            // High precision for light accumulation, no Alpha needed.
            RenderTextureFormat radianceFormat = RenderTextureFormat.RGB111110Float;
            _radianceField = CreateRadianceTexture(radianceFormat, "GI_Radiance_A");

            // Irradiance Field: HDR Color (R11G11B10)
            _irradianceFieldA = CreateRadianceTexture(radianceFormat, "GI_Irradiance_A");
            _irradianceFieldB = CreateRadianceTexture(radianceFormat, "GI_Irradiance_B");

            // Control Field: Direction & Stability (ARGB32)
            // Standard 8-bit per channel is enough for direction packing.
            RenderTextureFormat controlFormat = RenderTextureFormat.ARGB32;
            _controlFieldA = CreateRadianceTexture(controlFormat, "GI_Control_A");
            _controlFieldB = CreateRadianceTexture(controlFormat, "GI_Control_B");
        }

        public void ReleaseBuffers() {
            ClearRadianceField();
            if (_radianceField != null)
                _radianceField.Release();
            if (_irradianceFieldB != null)
                _irradianceFieldB.Release();
            if (_irradianceFieldA != null)
                _irradianceFieldA.Release();
            if (_controlFieldA != null)
                _controlFieldA.Release();
            if (_controlFieldB != null)
                _controlFieldB.Release();
            _radianceField = null;
            _irradianceFieldB = null;
            _irradianceFieldA = null;
            _controlFieldA = null;
            _controlFieldB = null;
        }
        void ClearRadianceField() {
            if (_giComputeShader == null) {
                Debug.LogWarning("GI compute shader is null; cannot clear volumes.");
                return;
            }

            if (_clearKernelIndex < 0) _clearKernelIndex = _giComputeShader.FindKernel("CSClearVolume");

            if (_radianceField != null) ClearVolumeWithCompute(_radianceField);
            if (_irradianceFieldB != null) ClearVolumeWithCompute(_irradianceFieldB);
            if (_irradianceFieldA != null) ClearVolumeWithCompute(_irradianceFieldA);
            if (_controlFieldA != null) ClearVolumeWithCompute(_controlFieldA);
            if (_controlFieldB != null) ClearVolumeWithCompute(_controlFieldB);
        }

        void ClearVolumeWithCompute(RenderTexture rt) {
            if (rt == null) return;
            if (!rt.IsCreated()) rt.Create();

            _giComputeShader.SetTexture(_clearKernelIndex, "_ClearTarget", rt);
            int groupsX = Mathf.CeilToInt(rt.width / 8.0f);
            int groupsY = Mathf.CeilToInt(rt.height / 8.0f);
            int groupsZ = Mathf.CeilToInt(rt.volumeDepth / 8.0f);

            _giComputeShader.Dispatch(_clearKernelIndex, groupsX, groupsY, groupsZ);

            // Unbind for cleanliness (Unity throws exception if we do this)
            // giComputeShader.SetTexture(_clearKernelIndex, "_ClearTarget", null);
        }

        void DispatchGIUpdate() {
            // Ping-Pong Logic
            // Bind Buffers
            _giComputeShader.SetTexture(_kernelIndex, s_radianceFieldWrite, _radianceField);

            // Bind previous-frame irradiance (read-only) so the main kernel can sample past surface irradiance
            _giComputeShader.SetTexture(_kernelIndex, s_irradianceField, IrradianceRead);
            _giComputeShader.SetTexture(_kernelIndex, s_controlRead, ControlRead);
            _giComputeShader.SetTexture(_kernelIndex, s_controlWrite, ControlWrite);

            // Bind External Inputs (Materials & SDF)
            // Ensure these textures are not null in the inspector!
            if (MaterialFieldAlbedoRoughness) _giComputeShader.SetTexture(_kernelIndex, s_materialAlbedo, MaterialFieldAlbedoRoughness);
            if (MaterialFieldEmissionMetallic) _giComputeShader.SetTexture(_kernelIndex, s_materialEmission, MaterialFieldEmissionMetallic);
            if (SurfaceDistanceFieldLowRes) _giComputeShader.SetTexture(_kernelIndex, s_distanceField, SurfaceDistanceFieldLowRes);

            // Set Parameters
            Vector3 resolution = MaterialFieldAlbedoRoughness.GetResolution();
            float voxelSize = (float)Volume.Bounds.size.x / resolution.x;
            _giComputeShader.SetVector(s_voxelSize, voxelSize * Vector4.one);
            _giComputeShader.SetInt(s_frameCount, Time.frameCount);
            _giComputeShader.SetVector(s_radianceTextureSize, _radianceTextureResolution);
            _giComputeShader.SetInt(s_raysPerFrame, _raysPerFrame);
            _giComputeShader.SetInt(s_maxSamples, _maxSamples);
            _giComputeShader.SetVector(s_voxelSize, voxelSize * Vector4.one);
            _giComputeShader.SetVector(s_radianceTextureSize, _radianceTextureResolution);
            _giComputeShader.SetFloat(s_irradianceStability, _irradianceStability);
            _giComputeShader.SetInt(s_irradianceFieldSampleCount, _irradianceFieldSampleCount);

            // Dispatch (8x8x8 threads per group)
            int groupsX = Mathf.CeilToInt(_radianceTextureResolution.x / 8.0f);
            int groupsY = Mathf.CeilToInt(_radianceTextureResolution.y / 8.0f);
            int groupsZ = Mathf.CeilToInt(_radianceTextureResolution.z / 8.0f);

            _giComputeShader.Dispatch(_kernelIndex, groupsX, groupsY, groupsZ);

            // Irradiance pass
            _giComputeShader.SetTexture(_irradianceKernelIndex, s_radianceField, _radianceField);
            _giComputeShader.SetTexture(_irradianceKernelIndex, s_irradianceFieldWrite, IrradianceFinal);
            _giComputeShader.SetTexture(_irradianceKernelIndex, s_irradianceField, IrradianceRead);
            _giComputeShader.SetTexture(_irradianceKernelIndex, s_distanceField, SurfaceDistanceFieldLowRes);
            _giComputeShader.Dispatch(_irradianceKernelIndex, groupsX, groupsY, groupsZ);
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
                // DelayedLogger.Log($"GI Updater: Using sun light '{sun.name}' with color {sun.color} and intensity {sun.intensity}.");
            } else {
                DelayedLogger.Log("GI Updater: No sun light found in the scene. GI will be computed with no direct lighting.");
                _giComputeShader.SetVector(s_directLightDir, Vector3.down);
                _giComputeShader.SetVector(s_directLightColor, Vector4.zero);
            }

            Color sky = RenderSettings.ambientMode == AmbientMode.Flat ? RenderSettings.ambientLight : RenderSettings.ambientSkyColor;
            _giComputeShader.SetVector(s_skyColor, (Vector4)sky);
        }

        void SetGlobalShaderVariables() {
            Shader.SetGlobalTexture(s_irradianceFieldWrite, IrradianceFinal);

            // Update these every frame in case the volume moves
            Vector3 resolution = MaterialFieldAlbedoRoughness.GetResolution();
            float voxelSize = MaterialFieldAlbedoRoughness.width / resolution.x;
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
