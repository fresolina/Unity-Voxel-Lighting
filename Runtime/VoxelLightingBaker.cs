using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// MonoBehaviour for coordinating baking.
    /// </summary>
    public class VoxelLightingBaker : MonoBehaviour {
        [Tooltip("Downscale factor to produce lower-res voxel field for material and GI")]
        [Range(1, 6)]
        public int LowresDownscaleFactor = 4;
        [Header("Bakers")]
        [SerializeField] SdfBaker _sdfBaker = new SdfBaker();
        [SerializeField] OcclusionBitmaskBaker _occlusionBitmaskBaker = new OcclusionBitmaskBaker();
        [SerializeField] MaterialBaker _materialBaker = new MaterialBaker();
        [Tooltip("Where to save the baked Texture3D asset(s) (must be under Assets/).")]
        public string assetPath = "Assets/VoxelLighting";
        public LightingVolume targetSdfVolume => _sdfShaderGlobals.volume;

        SdfShaderGlobals _sdfShaderGlobals;

        void OnValidate() {
            if (_sdfShaderGlobals == null)
                _sdfShaderGlobals = FindAnyObjectByType<SdfShaderGlobals>();
        }

        public bool TryBake(out string error) {
            LightingVolume volume = _sdfShaderGlobals.volume;
            if (volume == null) {
                error = "Target SdfVolume is not assigned.";
                return false;
            }

            // Bake SDF fields.
            if (_sdfBaker.sdfBakeCompute == null) {
                error = "SDF Bake Compute is not assigned to SdfBaker.";
                return false;
            }
            if (!_sdfBaker.TryBake(volume, volume.TrimmedMaxResolution, volume.BakeRoot.name, out Texture3D bakedSdf, out error)) {
                return false;
            }
            volume.sdfHiresTexture = bakedSdf;
            if (!_sdfBaker.TryBake(volume, volume.TrimmedMaxResolution / LowresDownscaleFactor, volume.BakeRoot.name + "_Lowres", out bakedSdf, out error)) {
                return false;
            }
            volume.sdfLowresTexture = bakedSdf;

            // Bake occlusion bitmask field. TODO: Make this optional.
            if (!_occlusionBitmaskBaker.TryBake(volume, out Texture3D bakedBitmask, out error)) {
                return false;
            }
            volume.occlusionBitmaskTexture = bakedBitmask;

            // Material baker produces two lower-res material textures (albedo+roughness, emission+metallic)
            if (_materialBaker.MaterialBakeCompute == null) {
                error = "Material Bake Compute is not assigned to MaterialBaker.";
                return false;
            }
            string matErr = _materialBaker.Bake(volume, out Texture3D bakedAlbedoRoughness, out Texture3D bakedEmissionMetallic, LowresDownscaleFactor);
            if (!string.IsNullOrEmpty(matErr)) {
                error = "MaterialBaker failed: " + matErr;
                return false;
            }
            volume.materialAlbedoRoughnessTexture = bakedAlbedoRoughness;
            volume.materialEmissionMetallicTexture = bakedEmissionMetallic;

            return true;
        }
    }
}
