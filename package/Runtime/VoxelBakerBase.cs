using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Base class for self-contained voxel bakers. Each concrete baker is its own
    /// MonoBehaviour and can bake on its own (writing its result onto the target
    /// <see cref="LightingVolume"/>), or be driven together with sibling bakers by a
    /// <see cref="BakeHandler"/> on the same GameObject.
    /// </summary>
    public abstract class VoxelBakerBase : MonoBehaviour {
        [Tooltip("Volume to bake into. If unset, falls back to the active LightingManager volume.")]
        [SerializeField] protected LightingVolume _targetVolume;

        /// <summary>
        /// Bakers with a lower order run first. The SDF baker must run before bakers
        /// that read the baked SDF (occlusion, material), so it uses order 0 while the
        /// dependent bakers use a higher value.
        /// </summary>
        public virtual int BakeOrder => 100;

        /// <summary>Short name used in logs and the inspector bake button.</summary>
        public abstract string BakeLabel { get; }

        /// <summary>The serialized target volume, if any (BakeHandler may override it).</summary>
        public LightingVolume TargetVolume => _targetVolume;

        /// <summary>Resolve the volume to bake into: explicit target, else the active manager volume.</summary>
        public LightingVolume ResolveVolume() {
            if (_targetVolume != null) return _targetVolume;
            LightingManager manager = LightingManager.Instance;
            return manager != null ? manager.Volume : null;
        }

        /// <summary>
        /// Run this baker, writing its result onto <paramref name="volume"/>.
        /// Returns false with a populated <paramref name="error"/> on failure.
        /// </summary>
        public abstract bool Bake(LightingVolume volume, out string error);

#if UNITY_EDITOR
        protected virtual void Reset() {
            if (_targetVolume == null) {
                LightingManager manager = FindAnyObjectByType<LightingManager>();
                if (manager != null) _targetVolume = manager.Volume;
            }
        }

        // FindAssets does substring matching, so "SdfBake" also returns "SdfBakeJfa".
        // Resolve to the asset whose file name (without extension) matches exactly.
        protected static ComputeShader FindComputeShaderByExactName(string exactName) {
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets($"{exactName} t:ComputeShader")) {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) == exactName)
                    return UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            }
            return null;
        }

        // Loose match: returns the first compute shader whose name contains the fragment.
        protected static ComputeShader FindComputeShaderByContains(string nameFragment) {
            string[] guids = UnityEditor.AssetDatabase.FindAssets($"{nameFragment} t:ComputeShader");
            if (guids.Length > 0)
                return UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]));
            return null;
        }
#endif
    }
}
