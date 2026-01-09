using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// MonoBehaviour for coordinating baking.
    /// </summary>
    public class VoxelLightingBaker : MonoBehaviour {
        [Header("Target Volume")]
        [Tooltip("SdfVolume that provides bake settings (root, resolution, bounds) and receives baked textures.")]
        public SdfVolume targetSdfVolume;

        [Header("Bakers")]
        [SerializeField] SdfBaker _sdfBaker = new SdfBaker();
        [SerializeField] OcclusionBitmaskBaker _occlusionBitmaskBaker = new OcclusionBitmaskBaker();

        [Tooltip("Where to save the baked Texture3D asset(s) (must be under Assets/).")]
        public string assetPath = "Assets/VoxelLighting";

        public bool TryBake(out string error) {
            SdfVolume volume = targetSdfVolume;
            if (volume == null) {
                error = "Target SdfVolume is not assigned.";
                return false;
            }

            // Set up SDF baker
            if (_sdfBaker.sdfBakeCompute == null) {
                error = "SDF Bake Compute is not assigned to SdfBaker.";
                return false;
            }

            // Execute the bake
            if (!_sdfBaker.TryBake(volume, out Texture3D bakedSdf, out error)) {
                return false;
            }

            if (!_occlusionBitmaskBaker.TryBake(volume, out Texture3D bakedBitmask, out error)) {
                return false;
            }

            // Apply results to volume
            volume.sdfTexture = bakedSdf;
            volume.occlusionBitmaskTexture = bakedBitmask;

            return true;
        }
    }
}
