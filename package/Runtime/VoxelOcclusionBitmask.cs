using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Runtime binder for bitmask occlusion shadows. Reads the baked directional bitmask off
    /// the <see cref="LightingVolume"/> on the same GameObject and publishes the globals the
    /// shader needs (the bitmask, the volume bounds, and the sun's nearest baked direction).
    ///
    /// Added automatically by <see cref="VoxelOcclusionBitmaskBaker"/> when it bakes. Its
    /// presence (and the <see cref="Sampling"/> mode) tells <see cref="LightingManager"/> to
    /// select the BITMASK_POINT / BITMASK_8TAP path.
    /// </summary>
    [ExecuteInEditMode]
    [RequireComponent(typeof(LightingVolume))]
    [AddComponentMenu("Lotec/Voxel Lighting/Binders/Voxel Occlusion Bitmask")]
    public class VoxelOcclusionBitmask : MonoBehaviour {
        public enum Sampling {
            Point,             // single nearest-direction bit (BITMASK_POINT)
            TrilinearEightTap, // smoother 2x2x2 trilinear blend (BITMASK_8TAP)
        }

        [Tooltip("Baked directional occlusion bitmask (which directions are occluded per voxel). Written by VoxelOcclusionBitmaskBaker.")]
        public Texture3D occlusionBitmaskTexture;
        [HideInInspector]
        public Vector3[] occlusionBitmaskDirections;
        [Tooltip("Point = single nearest-direction bit (cheaper). Trilinear 8-tap = smoother 2x2x2 blend.")]
        public Sampling sampling = Sampling.Point;

        static readonly int s_bitmaskTex = Shader.PropertyToID("_BitmaskTex");
        static readonly int s_voxelResolution = Shader.PropertyToID("_VoxelResolution");
        static readonly int s_sdfBoundsMin = Shader.PropertyToID("_SdfBoundsMin");
        static readonly int s_sdfBoundsSize = Shader.PropertyToID("_SdfBoundsSize");
        static readonly int s_inverseVoxelSize = Shader.PropertyToID("_InverseVoxelSize");
        static readonly int s_bitmaskSunFibIndex = Shader.PropertyToID("_BitmaskSunFibIndex");
        static readonly int s_bitmaskDirCount = Shader.PropertyToID("_BitmaskDirCount");

        LightingVolume _volume;
        LightingVolume Volume {
            get {
                if (_volume == null) _volume = GetComponent<LightingVolume>();
                return _volume;
            }
        }

        /// <summary>True when there is baked bitmask data to publish.</summary>
        public bool HasData =>
            occlusionBitmaskTexture != null
            && occlusionBitmaskDirections != null && occlusionBitmaskDirections.Length > 0;

        void Update() {
            // Shader globals are singular, so only the active volume's binder publishes.
            LightingManager manager = LightingManager.Instance;
            if (manager != null && manager.Volume != Volume) return;
            if (HasData) Bind();
        }

        /// <summary>Publish the bitmask globals plus the bounds the sampling needs.</summary>
        public void Bind() {
            LightingVolume v = Volume;
            if (v == null) return;

            Bounds bounds = v.Bounds;
            Shader.SetGlobalVector(s_sdfBoundsMin, bounds.min);
            Shader.SetGlobalVector(s_sdfBoundsSize, bounds.size);

            Vector3Int res = v.TrimmedMaxResolution;
            Shader.SetGlobalVector(s_voxelResolution, new Vector3(res.x, res.y, res.z));

            Vector3 voxelSize = new Vector3(
                bounds.size.x / Mathf.Max(1, res.x),
                bounds.size.y / Mathf.Max(1, res.y),
                bounds.size.z / Mathf.Max(1, res.z));
            Shader.SetGlobalVector(s_inverseVoxelSize, new Vector3(
                1f / Mathf.Max(1e-9f, voxelSize.x),
                1f / Mathf.Max(1e-9f, voxelSize.y),
                1f / Mathf.Max(1e-9f, voxelSize.z)));

            Shader.SetGlobalTexture(s_bitmaskTex, occlusionBitmaskTexture);

            Vector3 sunDir = OcclusionFieldQuery.GetSunDirection();
            int bestIndex = OcclusionFieldQuery.FindNearestDirection(sunDir, occlusionBitmaskDirections, occlusionBitmaskDirections.Length);
            Shader.SetGlobalInt(s_bitmaskSunFibIndex, bestIndex);
            Shader.SetGlobalInt(s_bitmaskDirCount, occlusionBitmaskDirections.Length);
        }
    }
}
