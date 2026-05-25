using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    /// <summary>
    /// Manages the Surface-Based Spatial Hash Voxel GI buffers and dispatch.
    /// Uses a flat StructuredBuffer + spatial hash grid instead of Texture3D.
    /// Optimized for mobile (Adreno 740 / Quest 3).
    /// </summary>
    class SpatialHashGi : System.IDisposable {
        const int HashGridSize = 64;
        const int HashGridTotal = HashGridSize * HashGridSize * HashGridSize; // 262,144

        readonly ComputeShader _computeShader;
        readonly int _maxVoxelCount;

        ComputeBuffer _voxelDataBuffer;
        ComputeBuffer _hashGridBuffer;
        ComputeBuffer _counterBuffer;

        int _activeVoxelCount;
        bool _isVoxelized;
        bool _counterReadbackPending;

        int _clearHashGridKernel;
        int _voxelizeSurfacesKernel;
        int _injectLightKernel;

        #region Shader Property IDs
        static readonly int s_voxelDataBuffer = Shader.PropertyToID("_VoxelDataBuffer");
        static readonly int s_voxelHashGrid = Shader.PropertyToID("_VoxelHashGrid");
        static readonly int s_voxelCounter = Shader.PropertyToID("_VoxelCounter");
        static readonly int s_spatialHashVoxelSize = Shader.PropertyToID("_SpatialHashVoxelSize");
        static readonly int s_maxVoxelCount = Shader.PropertyToID("_MaxVoxelCount");
        static readonly int s_activeVoxelCount = Shader.PropertyToID("_ActiveVoxelCount");
        static readonly int s_distanceField = Shader.PropertyToID("_DistanceField");
        static readonly int s_materialAlbedoIntensity = Shader.PropertyToID("_MaterialAlbedoIntensity");
        static readonly int s_voxelSize = Shader.PropertyToID("_VoxelSize");
        static readonly int s_directLightDir = Shader.PropertyToID("_DirectLightDir");
        static readonly int s_directLightColor = Shader.PropertyToID("_DirectLightColor");
        static readonly int s_skyColor = Shader.PropertyToID("_SkyColor");
        static readonly int s_pointLightCount = Shader.PropertyToID("_PointLightCount");
        static readonly int s_pointLightPositionRange = Shader.PropertyToID("_PointLightPositionRange");
        static readonly int s_pointLightColor = Shader.PropertyToID("_PointLightColor");
        static readonly int s_spotLightCount = Shader.PropertyToID("_SpotLightCount");
        static readonly int s_spotLightPositionRange = Shader.PropertyToID("_SpotLightPositionRange");
        static readonly int s_spotLightDirectionAngleScale = Shader.PropertyToID("_SpotLightDirectionAngleScale");
        static readonly int s_spotLightColorAngleOffset = Shader.PropertyToID("_SpotLightColorAngleOffset");
        static readonly int s_raymarchMaxSteps = Shader.PropertyToID("_RaymarchMaxSteps");
        static readonly int s_raymarchSoftness = Shader.PropertyToID("_RaymarchSoftness");

        // Globals for surface shaders
        static readonly int s_globalVoxelData = Shader.PropertyToID("_SpatialHashVoxelData");
        static readonly int s_globalHashGrid = Shader.PropertyToID("_SpatialHashGrid");
        static readonly int s_globalSpatialHashVoxelSize = Shader.PropertyToID("_SpatialHashVoxelSize");
        static readonly int s_globalSpatialHashOneOverVoxelSize = Shader.PropertyToID("_SpatialHashOneOverVoxelSize");
        #endregion

        public int ActiveVoxelCount => _activeVoxelCount;
        public bool IsVoxelized => _isVoxelized;
        public ComputeBuffer VoxelDataBuffer => _voxelDataBuffer;
        public ComputeBuffer HashGridBuffer => _hashGridBuffer;

        public SpatialHashGi(ComputeShader computeShader, int maxVoxelCount) {
            _computeShader = computeShader;
            _maxVoxelCount = Mathf.Clamp(maxVoxelCount, 1024, 65536);

            _clearHashGridKernel = _computeShader.FindKernel("CSClearHashGrid");
            _voxelizeSurfacesKernel = _computeShader.FindKernel("CSVoxelizeSurfaces");
            _injectLightKernel = _computeShader.FindKernel("CSInjectLight");

            CreateBuffers();
        }

        void CreateBuffers() {
            // VoxelGI: 3 uints = 12 bytes per element
            _voxelDataBuffer = new ComputeBuffer(_maxVoxelCount, 12, ComputeBufferType.Structured);
            // Hash grid: 1 int per cell
            _hashGridBuffer = new ComputeBuffer(HashGridTotal, sizeof(int), ComputeBufferType.Structured);
            // Counter: single uint
            _counterBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);
        }

        /// <summary>
        /// Populate the hash grid from the SDF. Call once at scene load or when geometry changes.
        /// </summary>
        public void Voxelize(LightingVolume volume, float voxelSize) {
            _isVoxelized = false;
            _activeVoxelCount = 0;

            float spatialHashVoxelSize = volume.Bounds.size.x / HashGridSize;

            // Clear counter
            _counterBuffer.SetData(new uint[] { 0 });

            // Set shared uniforms
            _computeShader.SetVector(s_voxelSize, Vector3.one * voxelSize);
            _computeShader.SetFloat(s_spatialHashVoxelSize, spatialHashVoxelSize);
            _computeShader.SetInt(s_maxVoxelCount, _maxVoxelCount);

            // Clear hash grid (fill with -1)
            _computeShader.SetBuffer(_clearHashGridKernel, s_voxelHashGrid, _hashGridBuffer);
            int clearGroups = Mathf.CeilToInt(HashGridTotal / 64f);
            _computeShader.Dispatch(_clearHashGridKernel, clearGroups, 1, 1);

            // Voxelize surfaces
            _computeShader.SetTexture(_voxelizeSurfacesKernel, s_distanceField, volume.sdfLowresTexture);
            _computeShader.SetBuffer(_voxelizeSurfacesKernel, s_voxelDataBuffer, _voxelDataBuffer);
            _computeShader.SetBuffer(_voxelizeSurfacesKernel, s_voxelHashGrid, _hashGridBuffer);
            _computeShader.SetBuffer(_voxelizeSurfacesKernel, s_voxelCounter, _counterBuffer);

            // Dispatch 64^3 / 4^3 = 4096 groups
            int voxelizeGroups = HashGridSize / 4;
            _computeShader.Dispatch(_voxelizeSurfacesKernel, voxelizeGroups, voxelizeGroups, voxelizeGroups);

            // Async readback for active voxel count
            _counterReadbackPending = true;
            AsyncGPUReadback.Request(_counterBuffer, OnCounterReadback);
        }

        void OnCounterReadback(AsyncGPUReadbackRequest request) {
            _counterReadbackPending = false;
            if (request.hasError) {
                Debug.LogError("SpatialHashGi: failed to read back voxel counter.");
                return;
            }

            var data = request.GetData<uint>();
            _activeVoxelCount = (int)Mathf.Min(data[0], _maxVoxelCount);
            _isVoxelized = true;
            Debug.Log($"SpatialHashGi: voxelized {_activeVoxelCount} surface voxels (max {_maxVoxelCount}).");
        }

        /// <summary>
        /// Compute lighting for all active surface voxels. Call every frame.
        /// </summary>
        public void InjectLight(LightingVolume volume, float voxelSize, int raymarchMaxSteps, float raymarchSoftness) {
            if (!_isVoxelized || _activeVoxelCount == 0) return;

            float spatialHashVoxelSize = volume.Bounds.size.x / HashGridSize;

            // Set uniforms
            _computeShader.SetVector(s_voxelSize, Vector3.one * voxelSize);
            _computeShader.SetFloat(s_spatialHashVoxelSize, spatialHashVoxelSize);
            _computeShader.SetInt(s_activeVoxelCount, _activeVoxelCount);
            _computeShader.SetInt(s_raymarchMaxSteps, raymarchMaxSteps);
            _computeShader.SetFloat(s_raymarchSoftness, raymarchSoftness);

            // Bind buffers
            _computeShader.SetTexture(_injectLightKernel, s_distanceField, volume.sdfLowresTexture);
            _computeShader.SetTexture(_injectLightKernel, s_materialAlbedoIntensity, volume.materialAlbedoIntensityTexture);
            _computeShader.SetBuffer(_injectLightKernel, s_voxelDataBuffer, _voxelDataBuffer);
            _computeShader.SetBuffer(_injectLightKernel, s_voxelHashGrid, _hashGridBuffer);

            // Dispatch one thread per active voxel
            int groups = Mathf.CeilToInt(_activeVoxelCount / 64f);
            _computeShader.Dispatch(_injectLightKernel, groups, 1, 1);
        }

        /// <summary>
        /// Set directional light uniforms on the spatial hash compute shader.
        /// </summary>
        public void SetDirectionalLight(Vector3 direction, Vector4 color) {
            _computeShader.SetVector(s_directLightDir, direction);
            _computeShader.SetVector(s_directLightColor, color);
        }

        /// <summary>
        /// Set sky color uniform.
        /// </summary>
        public void SetSkyColor(Vector4 skyColor) {
            _computeShader.SetVector(s_skyColor, skyColor);
        }

        /// <summary>
        /// Set point light uniforms on the spatial hash compute shader.
        /// </summary>
        public void SetPointLights(int count, Vector4[] positionRanges, Vector4[] colors) {
            _computeShader.SetInt(s_pointLightCount, count);
            _computeShader.SetVectorArray(s_pointLightPositionRange, positionRanges);
            _computeShader.SetVectorArray(s_pointLightColor, colors);
        }

        /// <summary>
        /// Set spot light uniforms on the spatial hash compute shader.
        /// </summary>
        public void SetSpotLights(int count, Vector4[] positionRanges, Vector4[] dirAngleScales, Vector4[] colorAngleOffsets) {
            _computeShader.SetInt(s_spotLightCount, count);
            _computeShader.SetVectorArray(s_spotLightPositionRange, positionRanges);
            _computeShader.SetVectorArray(s_spotLightDirectionAngleScale, dirAngleScales);
            _computeShader.SetVectorArray(s_spotLightColorAngleOffset, colorAngleOffsets);
        }

        /// <summary>
        /// Publish the hash grid and voxel data to global shader properties
        /// so surface shaders can sample via SampleSpatialHashGi().
        /// </summary>
        public void SetShaderGlobals(LightingVolume volume) {
            if (_voxelDataBuffer == null || _hashGridBuffer == null) return;

            float spatialHashVoxelSize = volume.Bounds.size.x / HashGridSize;

            Shader.SetGlobalBuffer(s_globalVoxelData, _voxelDataBuffer);
            Shader.SetGlobalBuffer(s_globalHashGrid, _hashGridBuffer);
            Shader.SetGlobalFloat(s_globalSpatialHashVoxelSize, spatialHashVoxelSize);
            Shader.SetGlobalFloat(s_globalSpatialHashOneOverVoxelSize, 1f / Mathf.Max(spatialHashVoxelSize, 1e-9f));
        }

        public void Dispose() {
            _voxelDataBuffer?.Release();
            _hashGridBuffer?.Release();
            _counterBuffer?.Release();
            _voxelDataBuffer = null;
            _hashGridBuffer = null;
            _counterBuffer = null;
            _isVoxelized = false;
            _activeVoxelCount = 0;
        }
    }
}
