using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace Lotec.Lighting {
    /// <summary>
    /// GPU buffer-GI debug viewer. Draws one cube per voxel via <see cref="Graphics.RenderPrimitives"/>,
    /// building the cube in the vertex shader and reading the voxel color straight from the GI
    /// StructuredBuffers on the GPU - no CPU readback, so it scales to the full grid.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Lotec/Voxel Lighting/Debug/Buffer GI Debug")]
    public class BufferGiDebug : MonoBehaviour {
        public enum Mode { Occupancy = 0, Irradiance = 1, Radiance = 2 }
        public enum Field { Fine = 0, Coarse = 1 }

        [Tooltip("Shader 'Hidden/Lotec/BufferGiCubeDebug'.")]
        [SerializeField] Shader _shader;
        [Tooltip("Which buffer to visualize.")]
        public Mode mode = Mode.Occupancy;
        [Tooltip("Which field to visualize: the fine volume or the coarse (large) box.")]
        public Field field = Field.Fine;
        [Tooltip("Draw the fine + coarse field bounds as wireframe boxes.")]
        public bool showWireframe = true;
        [Tooltip("Draw every Nth voxel along each axis (1 = all).")]
        [Min(1)] public int stride = 1;
        [Range(0.1f, 1f)] public float cubeFill = 0.85f;
        [Tooltip("Linear display multiplier for the irradiance/radiance modes (1 = neutral). " +
                 "Occupancy mode ignores it.")]
        [Min(0f)][FormerlySerializedAs("exposure")] public float intensity = 1f;
        [Min(0f)] public float minLuminance = 0.02f;

        Material _material;
        MaterialPropertyBlock _wireProps;

        static readonly int s_material = Shader.PropertyToID("_DbgMaterial");
        static readonly int s_radiance = Shader.PropertyToID("_DbgRadiance");
        static readonly int s_irradiance = Shader.PropertyToID("_DbgIrradiance");
        static readonly int s_gridOrigin = Shader.PropertyToID("_BgiGridOrigin");
        static readonly int s_gridSize = Shader.PropertyToID("_BgiGridSize");
        static readonly int s_voxelSize = Shader.PropertyToID("_BgiVoxelSize");
        static readonly int s_gridDims = Shader.PropertyToID("_DbgGridDims");
        static readonly int s_stride = Shader.PropertyToID("_DbgStride");
        static readonly int s_cubeFill = Shader.PropertyToID("_DbgCubeFill");
        static readonly int s_intensity = Shader.PropertyToID("_DbgIntensity");
        static readonly int s_minLum = Shader.PropertyToID("_DbgMinLum");
        static readonly int s_mode = Shader.PropertyToID("_DbgMode");
        static readonly int s_fieldOffset = Shader.PropertyToID("_DbgFieldOffset");
        static readonly int s_wire = Shader.PropertyToID("_DbgWire");
        static readonly int s_wireOrigin0 = Shader.PropertyToID("_WireOrigin0");
        static readonly int s_wireSize0 = Shader.PropertyToID("_WireSize0");
        static readonly int s_wireOrigin1 = Shader.PropertyToID("_WireOrigin1");
        static readonly int s_wireSize1 = Shader.PropertyToID("_WireSize1");

        void Reset() {
            // Auto-set the default shader if not already assigned
            if (_shader == null) {
                _shader = Shader.Find("Hidden/Lotec/BufferGiCubeDebug");
            }
        }

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

            // Field-specific grid + slice. Coarse is the same 32^3 grid with larger voxels.
            bool coarse = field == Field.Coarse;
            Vector3 origin = coarse ? gi.CoarseOrigin : gi.GridOrigin;
            Vector3 size = coarse ? gi.CoarseSize : gi.GridSize;
            Vector3 voxelSize = coarse ? gi.CoarseVoxelSize : gi.VoxelSize;
            int fieldOffset = (coarse ? BufferGiUpdater.CoarseField : BufferGiUpdater.FineField) * BufferGiUpdater.VoxelCount;

            _material.SetBuffer(s_material, gi.MaterialBuffer);
            _material.SetBuffer(s_radiance, gi.RadianceBuffer);
            _material.SetBuffer(s_irradiance, gi.IrradianceBuffer);
            _material.SetVector(s_gridOrigin, origin);
            _material.SetVector(s_gridSize, size);
            _material.SetVector(s_voxelSize, voxelSize);
            _material.SetVector(s_gridDims, new Vector4(g, g, g, 0f));
            _material.SetFloat(s_stride, s);
            _material.SetFloat(s_cubeFill, cubeFill);
            _material.SetFloat(s_intensity, intensity);
            _material.SetFloat(s_minLum, minLuminance);
            _material.SetFloat(s_mode, (int)mode);
            _material.SetFloat(s_fieldOffset, fieldOffset);
            _material.SetFloat(s_wire, 0f);

            // Bounds enclosing both fields, so neither the cubes nor the wireframe get culled.
            Bounds worldBounds = gi.Volume.Bounds;
            worldBounds.Encapsulate(new Bounds(gi.CoarseOrigin + gi.CoarseSize * 0.5f, gi.CoarseSize));

            RenderParams rp = new RenderParams(_material) {
                worldBounds = worldBounds,
                receiveShadows = false,
                shadowCastingMode = ShadowCastingMode.Off
            };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 36, instanceCount);

            if (showWireframe) {
                if (_wireProps == null) _wireProps = new MaterialPropertyBlock();
                _wireProps.SetFloat(s_wire, 1f);
                _wireProps.SetVector(s_wireOrigin0, gi.GridOrigin);
                _wireProps.SetVector(s_wireSize0, gi.GridSize);
                _wireProps.SetVector(s_wireOrigin1, gi.CoarseOrigin);
                _wireProps.SetVector(s_wireSize1, gi.CoarseSize);

                RenderParams wrp = new RenderParams(_material) {
                    worldBounds = worldBounds,
                    receiveShadows = false,
                    shadowCastingMode = ShadowCastingMode.Off,
                    matProps = _wireProps
                };
                // 24 line-list verts (12 edges) per box: fine always, coarse only when assigned.
                int boxes = gi.HasCoarse ? 2 : 1;
                Graphics.RenderPrimitives(wrp, MeshTopology.Lines, 24, boxes);
            }
        }
    }
}
