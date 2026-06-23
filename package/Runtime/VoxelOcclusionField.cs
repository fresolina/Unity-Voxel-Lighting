using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Runtime binder for occlusion-field shadows. Reads the baked occlusion field off the
    /// <see cref="LightingVolume"/> on the same GameObject and publishes the globals the
    /// shader needs (the per-direction field + the volume bounds), so the shadow term comes
    /// from the field with no SDF texture bound at runtime.
    ///
    /// Added automatically by <see cref="VoxelOcclusionFieldBaker"/> when it bakes. Its
    /// presence is what tells <see cref="LightingManager"/> to select the OCC_FIELD path.
    /// </summary>
    [ExecuteInEditMode]
    [RequireComponent(typeof(LightingVolume))]
    [AddComponentMenu("Lotec/Voxel Lighting/Binders/Voxel Occlusion Field")]
    public class VoxelOcclusionField : MonoBehaviour {
        static readonly int s_sdfBoundsMin = Shader.PropertyToID("_SdfBoundsMin");
        static readonly int s_sdfBoundsSize = Shader.PropertyToID("_SdfBoundsSize");
        static readonly int s_inverseVoxelSize = Shader.PropertyToID("_InverseVoxelSize");

        LightingVolume _volume;
        readonly OcclusionFieldQuery _query = new OcclusionFieldQuery();

        LightingVolume Volume {
            get {
                if (_volume == null) _volume = GetComponent<LightingVolume>();
                return _volume;
            }
        }

        /// <summary>True when there is baked occlusion-field data to publish.</summary>
        public bool HasData {
            get {
                LightingVolume v = Volume;
                return v != null
                    && v.occlusionFieldTextures != null && v.occlusionFieldTextures.Length > 0
                    && v.occlusionFieldDirections != null && v.occlusionFieldDirections.Length > 0;
            }
        }

        void Update() {
            // Shader globals are singular, so only the active volume's binder publishes.
            LightingManager manager = LightingManager.Instance;
            if (manager != null && manager.Volume != Volume) return;
            if (HasData) Bind();
        }

        /// <summary>Publish the occlusion-field globals plus the bounds the field sampling needs.</summary>
        public void Bind() {
            LightingVolume v = Volume;
            if (v == null) return;

            // Bounds are required by the field sampling (world -> uvw), independent of the SDF.
            Bounds bounds = v.Bounds;
            Shader.SetGlobalVector(s_sdfBoundsMin, bounds.min);
            Shader.SetGlobalVector(s_sdfBoundsSize, bounds.size);

            Vector3Int res = v.TrimmedMaxResolution;
            Vector3 voxelSize = new Vector3(
                bounds.size.x / Mathf.Max(1, res.x),
                bounds.size.y / Mathf.Max(1, res.y),
                bounds.size.z / Mathf.Max(1, res.z));
            Shader.SetGlobalVector(s_inverseVoxelSize, new Vector3(
                1f / Mathf.Max(1e-9f, voxelSize.x),
                1f / Mathf.Max(1e-9f, voxelSize.y),
                1f / Mathf.Max(1e-9f, voxelSize.z)));

            _query.Initialize(v.occlusionFieldDirections, v.occlusionFieldTextures);
            _query.ApplyShaderGlobals();
        }
    }
}
