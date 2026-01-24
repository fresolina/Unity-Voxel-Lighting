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
        [SerializeField] MaterialBaker _materialBaker = new MaterialBaker();

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
            volume.sdfTexture = bakedSdf;

            if (!_occlusionBitmaskBaker.TryBake(volume, out Texture3D bakedBitmask, out error)) {
                return false;
            }
            volume.occlusionBitmaskTexture = bakedBitmask;

            // Material baker produces two lower-res material textures (albedo+roughness, emission+metallic)
            if (_materialBaker.materialBakeCompute == null) {
                error = "Material Bake Compute is not assigned to MaterialBaker.";
                return false;
            }
            if (!_materialBaker.TryBake(volume, out Texture3D bakedAlbedoRoughness, out Texture3D bakedEmissionMetallic, out error)) {
                error = "MaterialBaker failed: " + error;
                return false;
            }
            volume.materialAlbedoRoughnessTexture = bakedAlbedoRoughness;
            volume.materialEmissionMetallicTexture = bakedEmissionMetallic;

            return true;
        }
    }
}
