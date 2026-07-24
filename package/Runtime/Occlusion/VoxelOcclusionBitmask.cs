using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Passive holder for the baked directional occlusion bitmask. Stores the bitmask texture off
    /// the <see cref="VoxelVolume"/> on the same GameObject and, on demand, publishes the globals the
    /// shader needs (<see cref="Bind"/>: the bitmask and the sun's nearest baked direction). The shared
    /// volume bounds it samples against are published by <see cref="LightingManager"/>.
    ///
    /// Added automatically by the bitmask baker when it bakes. This component does NOT run its own
    /// update loop or claim any keyword: <see cref="BufferGiUpdater"/> is the sole driver - it calls
    /// <see cref="Bind"/> for the field whose ShadowMode is Bitmask. So there is no idle Update cost,
    /// and it can coexist with the occlusion-field holder. The shader read is always the trilinear
    /// 8-tap (the old per-voxel Point variant was a keyword path that buffer GI no longer selects).
    /// </summary>
    [RequireComponent(typeof(VoxelVolume))]
    [AddComponentMenu("Lotec/Voxel Lighting/Binders/Voxel Occlusion Bitmask")]
    public class VoxelOcclusionBitmask : MonoBehaviour {
        [Tooltip("Baked directional occlusion bitmask (which directions are occluded per voxel). Written by VoxelOcclusionBitmaskBaker.")]
        public Texture3D occlusionBitmaskTexture;
        [HideInInspector]
        public Vector3[] occlusionBitmaskDirections;

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
