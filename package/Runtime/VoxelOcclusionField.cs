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
        [Tooltip("RGBA32 textures storing per-direction lit values. 4 directions per texture. Written by VoxelOcclusionFieldBaker.")]
        public Texture3D[] occlusionFieldTextures;
        [HideInInspector]
        public Vector3[] occlusionFieldDirections;

        static readonly int s_sdfBoundsMin = Shader.PropertyToID("_SdfBoundsMin");
        static readonly int s_sdfBoundsSize = Shader.PropertyToID("_SdfBoundsSize");
        static readonly int s_inverseVoxelSize = Shader.PropertyToID("_InverseVoxelSize");
        static readonly int s_occFieldSunDir = Shader.PropertyToID("_OccFieldSunDir");
        static readonly int s_occFieldSunChannel = Shader.PropertyToID("_OccFieldSunChannel");
        static readonly int s_occFieldTex = Shader.PropertyToID("_OccFieldTex");

        LightingVolume _volume;

        LightingVolume Volume {
            get {
                if (_volume == null) _volume = GetComponent<LightingVolume>();
                return _volume;
            }
        }

        /// <summary>True when there is baked occlusion-field data to publish.</summary>
        public bool HasData =>
            occlusionFieldTextures != null && occlusionFieldTextures.Length > 0
            && occlusionFieldDirections != null && occlusionFieldDirections.Length > 0;

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

            PublishSunField();
        }

        // Map the current sun direction to the nearest baked direction, then bind that
        // direction's texture + RGBA channel for the shader to sample.
        void PublishSunField() {
            if (occlusionFieldDirections == null || occlusionFieldDirections.Length == 0 || occlusionFieldTextures == null)
                return;

            Vector3 sunDir = OcclusionFieldQuery.GetSunDirection();
            int bestIndex = OcclusionFieldQuery.FindNearestDirection(sunDir, occlusionFieldDirections, occlusionFieldDirections.Length);
            int texIndex = bestIndex / 4;
            int channel = bestIndex % 4;

            Shader.SetGlobalVector(s_occFieldSunDir, sunDir);
            Shader.SetGlobalInt(s_occFieldSunChannel, channel);
            if (texIndex >= 0 && texIndex < occlusionFieldTextures.Length && occlusionFieldTextures[texIndex] != null)
                Shader.SetGlobalTexture(s_occFieldTex, occlusionFieldTextures[texIndex]);
        }
    }
}
