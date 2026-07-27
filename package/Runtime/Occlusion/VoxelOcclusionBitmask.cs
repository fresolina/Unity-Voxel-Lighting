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
    /// and it can coexist with the occlusion-field holder. The shader read is a single point fetch:
    /// the bitmask is the cheap shadow source, and one bit per direction has nothing to interpolate.
    /// Soft or sub-voxel-sharp edges are the occlusion field's job, not this one's.
    /// </summary>
    [RequireComponent(typeof(VoxelVolume))]
    [AddComponentMenu("Lotec/Voxel Lighting/Binders/Voxel Occlusion Bitmask")]
    public class VoxelOcclusionBitmask : MonoBehaviour {
        [Tooltip("Baked directional occlusion bitmask (which directions are occluded per voxel). Written by VoxelOcclusionBitmaskBaker.")]
        public Texture3D occlusionBitmaskTexture;

        [Tooltip("The directions this bitmask was baked with, saved beside its texture. Written by " +
                 "the baker - it must match the texture or the bits decode to the wrong angles.")]
        public VoxelDirectionSet directionSet;

        /// <summary>
        /// Bake-to-save handoff only, never serialized. <see cref="VoxelOcclusionBitmaskBaker"/> runs in
        /// the runtime assembly and parks the freshly baked directions here; the editor's
        /// SaveBakedAssets picks them up and writes <see cref="directionSet"/>. Reading it in between
        /// is what lets a bake preview in edit mode before its assets are saved.
        /// </summary>
        [System.NonSerialized]
        public Vector3[] pendingDirections;

        [System.NonSerialized] bool _warnedMissingDirections;

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

        /// <summary>
        /// The baked directions: the saved asset, or the not-yet-saved bake still in memory.
        /// Null/empty means this binder has no usable bake.
        /// </summary>
        public Vector3[] Directions =>
            directionSet != null && directionSet.Count > 0
                ? directionSet.directions
                : pendingDirections;

        /// <summary>True when there is baked bitmask data to publish.</summary>
        public bool HasData {
            get {
                Vector3[] dirs = Directions;
                bool ok = occlusionBitmaskTexture != null && dirs != null && dirs.Length > 0;
                // A texture with no directions is the shape of a bake made before the directions moved
                // into an asset: the bits are fine but nothing says which angle each one means, so the
                // mode would just silently stop shadowing. Say so once rather than render wrong.
                if (!ok && occlusionBitmaskTexture != null && !_warnedMissingDirections) {
                    _warnedMissingDirections = true;
                    Debug.LogWarning(
                        $"VoxelOcclusionBitmask on '{name}' has a baked texture but no direction set. " +
                        "This bake predates VoxelDirectionSet - rebake the volume to migrate it. " +
                        "Until then the Bitmask shadow mode has no data and renders unshadowed.", this);
                }
                return ok;
            }
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

            Vector3[] dirs = Directions;
            if (dirs == null || dirs.Length == 0) return;

            Vector3 sunDir = OcclusionFieldQuery.GetSunDirection();
            int bestIndex = OcclusionFieldQuery.FindNearestDirection(sunDir, dirs, dirs.Length);
            Shader.SetGlobalInt(s_bitmaskSunFibIndex, bestIndex);
            Shader.SetGlobalInt(s_bitmaskDirCount, dirs.Length);
        }
    }
}
