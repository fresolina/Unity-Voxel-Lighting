using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Runtime binder for bitmask occlusion shadows. Reads the baked directional bitmask off
    /// the <see cref="VoxelVolume"/> on the same GameObject and publishes the globals the
    /// shader needs (the bitmask and the sun's nearest baked direction). The shared volume
    /// bounds it samples against are published by <see cref="LightingManager"/>.
    ///
    /// Added automatically by <see cref="VoxelOcclusionBitmaskBaker"/> when it bakes. Its
    /// presence (and the <see cref="Sampling"/> mode) tells <see cref="LightingManager"/> to
    /// select the BITMASK_POINT / BITMASK_8TAP path.
    /// </summary>
    [ExecuteInEditMode]
    [RequireComponent(typeof(VoxelVolume))]
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
        static readonly int s_voxelSize = Shader.PropertyToID("_VoxelSize");
        static readonly int s_voxelSizeInverse = Shader.PropertyToID("_VoxelSizeInverse");
        static readonly int s_bitmaskSunFibIndex = Shader.PropertyToID("_BitmaskSunFibIndex");
        static readonly int s_bitmaskDirCount = Shader.PropertyToID("_BitmaskDirCount");

        VoxelVolume _volume;
        VoxelVolume Volume {
            get {
                if (_volume == null) _volume = GetComponent<VoxelVolume>();
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

        /// <summary>Publish the bitmask globals + the occlusion-grid uniforms (resolution +
        /// inverse voxel size). The volume bounds it samples against are published by the
        /// active VoxelVolume.</summary>
        public void Bind() {
            VoxelVolume v = Volume;
            if (v == null) return;

            Vector3Int res = v.TrimmedMaxResolution;
            Shader.SetGlobalVector(s_voxelResolution, new Vector3(res.x, res.y, res.z));
            Shader.SetGlobalVector(s_voxelSize, Vector3.one * v.VoxelSize);
            Shader.SetGlobalVector(s_voxelSizeInverse, v.VoxelSizeInverse);

            Shader.SetGlobalTexture(s_bitmaskTex, occlusionBitmaskTexture);

            Vector3 sunDir = OcclusionFieldQuery.GetSunDirection();
            int bestIndex = OcclusionFieldQuery.FindNearestDirection(sunDir, occlusionBitmaskDirections, occlusionBitmaskDirections.Length);
            Shader.SetGlobalInt(s_bitmaskSunFibIndex, bestIndex);
            Shader.SetGlobalInt(s_bitmaskDirCount, occlusionBitmaskDirections.Length);
        }
    }
}
