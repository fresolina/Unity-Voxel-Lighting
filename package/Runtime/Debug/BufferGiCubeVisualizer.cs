using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    /// <summary>
    /// GPU buffer-GI debug viewer. Draws one cube per voxel via <see cref="Graphics.RenderPrimitives"/>,
    /// building the cube in the vertex shader and reading the voxel color straight from the GI
    /// StructuredBuffers on the GPU - no CPU readback, so it scales to the full grid. Based on
    /// <see cref="GpuVoxelCubeVisualizer"/> but retargeted from the directional 6-face textures to
    /// the single-value buffer fields owned by <see cref="BufferGiUpdater"/>.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Lotec/Voxel Lighting/Debug/Buffer GI Cube Visualizer")]
    public class BufferGiCubeVisualizer : MonoBehaviour {
        public enum Mode { Occupancy = 0, Irradiance = 1, Radiance = 2 }

        [Tooltip("Shader 'Hidden/Lotec/BufferGiCubeDebug'.")]
        [SerializeField] Shader _shader;
        [Tooltip("Which buffer to visualize.")]
        public Mode mode = Mode.Occupancy;
        [Tooltip("Draw every Nth voxel along each axis (1 = all).")]
        [Min(1)] public int stride = 1;
        [Range(0.1f, 1f)] public float cubeFill = 0.85f;
        [Min(0f)] public float exposure = 1f;
        [Min(0f)] public float minLuminance = 0.02f;

        Material _material;

        static readonly int s_material = Shader.PropertyToID("_DbgMaterial");
        static readonly int s_radiance = Shader.PropertyToID("_DbgRadiance");
        static readonly int s_irradiance = Shader.PropertyToID("_DbgIrradiance");
        static readonly int s_gridOrigin = Shader.PropertyToID("_BgiGridOrigin");
        static readonly int s_gridSize = Shader.PropertyToID("_BgiGridSize");
        static readonly int s_voxelSize = Shader.PropertyToID("_BgiVoxelSize");
        static readonly int s_gridDims = Shader.PropertyToID("_DbgGridDims");
        static readonly int s_stride = Shader.PropertyToID("_DbgStride");
        static readonly int s_cubeFill = Shader.PropertyToID("_DbgCubeFill");
        static readonly int s_exposure = Shader.PropertyToID("_DbgExposure");
        static readonly int s_minLum = Shader.PropertyToID("_DbgMinLum");
        static readonly int s_mode = Shader.PropertyToID("_DbgMode");

        void OnDisable() {
            if (_material != null) {
                if (Application.isPlaying) Destroy(_material); else DestroyImmediate(_material);
                _material = null;
            }
        }

        void Update() {
            if (_shader == null) return;

            BufferGiUpdater gi = BufferGiUpdater.Instance;
            if (gi == null || gi.Volume == null || gi.MaterialBuffer == null) return;

            if (_material == null) {
                _material = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            int s = Mathf.Max(1, stride);
            int g = Mathf.Max(1, BufferGiUpdater.Grid / s);
            int instanceCount = g * g * g;
            if (instanceCount <= 0) return;

            _material.SetBuffer(s_material, gi.MaterialBuffer);
            _material.SetBuffer(s_radiance, gi.RadianceBuffer);
            _material.SetBuffer(s_irradiance, gi.IrradianceBuffer);
            _material.SetVector(s_gridOrigin, gi.GridOrigin);
            _material.SetVector(s_gridSize, gi.GridSize);
            _material.SetVector(s_voxelSize, gi.VoxelSize);
            _material.SetVector(s_gridDims, new Vector4(g, g, g, 0f));
            _material.SetFloat(s_stride, s);
            _material.SetFloat(s_cubeFill, cubeFill);
            _material.SetFloat(s_exposure, exposure);
            _material.SetFloat(s_minLum, minLuminance);
            _material.SetFloat(s_mode, (int)mode);

            RenderParams rp = new RenderParams(_material) {
                worldBounds = gi.Volume.Bounds,
                receiveShadows = false,
                shadowCastingMode = ShadowCastingMode.Off
            };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 36, instanceCount);
        }
    }
}
