using System;
using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Convenience wrapper that bakes every enabled <see cref="VoxelBakerBase"/> on the
    /// same GameObject, in dependency order (SDF first). Individual bakers can also be
    /// baked on their own; this just drives all of them together.
    /// </summary>
    [AddComponentMenu("Lotec/Voxel Lighting/Bake Handler")]
    public class BakeHandler : MonoBehaviour {
        [Tooltip("Volume to bake into. If unset, falls back to the active LightingManager volume.")]
        [SerializeField] LightingVolume _targetVolume;

        public LightingVolume TargetVolume => _targetVolume;

        public LightingVolume ResolveVolume() {
            if (_targetVolume != null) return _targetVolume;
            LightingManager manager = LightingManager.Instance;
            return manager != null ? manager.Volume : null;
        }

        public bool Bake() => Bake(ResolveVolume());

        /// <summary>
        /// Bake all enabled baker components on this GameObject into the given volume,
        /// ordered by <see cref="VoxelBakerBase.BakeOrder"/> so the SDF is built first.
        /// Returns false if any stage fails.
        /// </summary>
        public bool Bake(LightingVolume volume) {
            if (volume == null) {
                Debug.LogError("BakeHandler: target volume is not assigned.", this);
                return false;
            }

            VoxelBakerBase[] bakers = GetComponents<VoxelBakerBase>();
            Array.Sort(bakers, (a, b) => a.BakeOrder.CompareTo(b.BakeOrder));

            bool anyEnabled = false;
            foreach (VoxelBakerBase baker in bakers) {
                if (!baker.enabled)
                    continue;
                anyEnabled = true;

                if (!baker.Bake(volume, out string error)) {
                    Debug.LogError($"BakeHandler: {baker.BakeLabel} bake failed: {error}", baker);
                    return false;
                }
                Debug.Log($"BakeHandler: {baker.BakeLabel} bake complete for '{volume.gameObject.name}'.", baker);
            }

            if (!anyEnabled)
                Debug.LogWarning("BakeHandler: no enabled VoxelBakerBase components found on this GameObject.", this);

            return anyEnabled;
        }

#if UNITY_EDITOR
        void Reset() {
            if (_targetVolume == null) {
                LightingManager manager = FindAnyObjectByType<LightingManager>();
                if (manager != null) _targetVolume = manager.Volume;
            }
        }
#endif
    }
}
