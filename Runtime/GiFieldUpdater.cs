using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    [System.Serializable]
    public class GiFieldUpdater {
        [SerializeField] ComputeShader _giComputeShader;
        public Texture3D MaterialFieldAlbedoRoughness { get; set; }
        public Texture3D MaterialFieldEmissionMetallic { get; set; }
        public Texture3D SurfaceDistanceFieldHighRes { get; set; }
        public Texture3D SurfaceDistanceFieldLowRes { get; set; }

        RenderTexture _radianceFieldA; // Ping
        RenderTexture _radianceFieldB; // Pong
        RenderTexture _controlFieldA; // Ping (Direction + Stability)
        RenderTexture _controlFieldB; // Pong
        bool _isEvenFrame = true;
        int _kernelIndex;
        int _clearKernelIndex = -1;
        Vector3 _radianceTextureResolution;

        #region Shader Property IDs
        // Property IDs local to gi update compute shader
        static readonly int s_giFieldRead = Shader.PropertyToID("_GiFieldRead");
        static readonly int s_giFieldWrite = Shader.PropertyToID("_GiFieldWrite");
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
        static readonly int s_globalVoxelRadiance = Shader.PropertyToID("_RadianceFieldTexture");
        static readonly int s_radianceFieldVoxelSize = Shader.PropertyToID("_RadianceFieldVoxelSize");
        #endregion

        RenderTexture RadianceRead => _isEvenFrame ? _radianceFieldA : _radianceFieldB;
        RenderTexture RadianceWrite => _isEvenFrame ? _radianceFieldB : _radianceFieldA;
        RenderTexture ControlRead => _isEvenFrame ? _controlFieldA : _controlFieldB;
        RenderTexture ControlWrite => _isEvenFrame ? _controlFieldB : _controlFieldA;

        public LightingVolume Volume { get; set; }

        public void Update() {
            if (Volume == null || _giComputeShader == null) {
                Debug.LogWarning("GI Field Updater is missing required references; skipping GI update.");
                return;
            }

            EnsureInitialized();
            DispatchGIUpdate();
            SetGlobalShaderVariables();
            _isEvenFrame = !_isEvenFrame;
        }

        void EnsureInitialized() {
            if (_radianceFieldA == null || _radianceFieldB == null || _controlFieldA == null || _controlFieldB == null) {
                InitializeBuffers();
            }
        }

        void InitializeBuffers() {
            ReleaseBuffers();

            _radianceTextureResolution = new Vector3(SurfaceDistanceFieldLowRes.width, SurfaceDistanceFieldLowRes.height, SurfaceDistanceFieldLowRes.depth);

            _kernelIndex = _giComputeShader.FindKernel("CSMain");

            // 1. Radiance Field: HDR Color (R11G11B10)
            // High precision for light accumulation, no Alpha needed.
            RenderTextureFormat radianceFormat = RenderTextureFormat.RGB111110Float;
            _radianceFieldA = CreateRadianceTexture(radianceFormat, "GI_Radiance_A");
            _radianceFieldB = CreateRadianceTexture(radianceFormat, "GI_Radiance_B");

            // 2. Control Field: Direction & Stability (ARGB32)
            // Standard 8-bit per channel is enough for direction packing.
            RenderTextureFormat controlFormat = RenderTextureFormat.ARGB32;
            _controlFieldA = CreateRadianceTexture(controlFormat, "GI_Control_A");
            _controlFieldB = CreateRadianceTexture(controlFormat, "GI_Control_B");
        }

        public void ReleaseBuffers() {
            ClearRadianceField();
            if (_radianceFieldA != null)
                _radianceFieldA.Release();
            if (_radianceFieldB != null)
                _radianceFieldB.Release();
            if (_controlFieldA != null)
                _controlFieldA.Release();
            if (_controlFieldB != null)
                _controlFieldB.Release();
            _radianceFieldA = null;
            _radianceFieldB = null;
            _controlFieldA = null;
            _controlFieldB = null;
        }
        void ClearRadianceField() {
            if (_giComputeShader == null) {
                Debug.LogWarning("GI compute shader is null; cannot clear volumes.");
                return;
            }

            if (_clearKernelIndex < 0) _clearKernelIndex = _giComputeShader.FindKernel("CSClearVolume");

            if (_radianceFieldA != null) ClearVolumeWithCompute(_radianceFieldA);
            if (_radianceFieldB != null) ClearVolumeWithCompute(_radianceFieldB);
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
            _giComputeShader.SetTexture(_kernelIndex, s_giFieldRead, RadianceRead);
            _giComputeShader.SetTexture(_kernelIndex, s_giFieldWrite, RadianceWrite);

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
            SetDirectLightParams();

            // Dispatch (8x8x8 threads per group)
            int groupsX = Mathf.CeilToInt(_radianceTextureResolution.x / 8.0f);
            int groupsY = Mathf.CeilToInt(_radianceTextureResolution.y / 8.0f);
            int groupsZ = Mathf.CeilToInt(_radianceTextureResolution.z / 8.0f);

            _giComputeShader.Dispatch(_kernelIndex, groupsX, groupsY, groupsZ);
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
                Vector3 dir = -sun.transform.forward; // light direction towards the scene
                _giComputeShader.SetVector(s_directLightDir, dir);
                _giComputeShader.SetVector(s_directLightColor, (Vector4)sun.color * sun.intensity); // TODO: Maybe put intensity in alpha channel.
            } else {
                _giComputeShader.SetVector(s_directLightDir, Vector3.down);
                _giComputeShader.SetVector(s_directLightColor, Vector4.zero);
            }

            Color sky = RenderSettings.ambientMode == AmbientMode.Flat ? RenderSettings.ambientLight : RenderSettings.ambientSkyColor;
            _giComputeShader.SetVector(s_skyColor, (Vector4)sky);
        }

        void SetGlobalShaderVariables() {
            Shader.SetGlobalTexture(s_globalVoxelRadiance, RadianceRead);

            // Update these every frame in case the volume moves
            Vector3 resolution = MaterialFieldAlbedoRoughness.GetResolution();
            float voxelSize = MaterialFieldAlbedoRoughness.width / resolution.x;
            Shader.SetGlobalVector(s_radianceFieldVoxelSize, voxelSize * Vector4.one);
        }
    }
}
