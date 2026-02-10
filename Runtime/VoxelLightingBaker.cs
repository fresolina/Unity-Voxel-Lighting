using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

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

#if UNITY_EDITOR
        void OnValidate() {
            if (_sdfShaderGlobals == null)
                _sdfShaderGlobals = FindAnyObjectByType<SdfShaderGlobals>();
        }
        void Reset() {
            // Editor fallback: search the project for a matching compute shader asset by name
            if (_occlusionBitmaskBaker.bitmaskBakeCompute == null) {
                string[] guids = AssetDatabase.FindAssets("OcclusionBitmaskBake t:ComputeShader");
                if (guids.Length > 0) {
                    string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    _occlusionBitmaskBaker.bitmaskBakeCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                    EditorUtility.SetDirty(this);
                } else {
                    Debug.LogWarning("Could not find OcclusionBitmaskBake compute shader in project. Please assign it manually to the VoxelLightingBaker.");
                }
            }
            if (_materialBaker.MaterialBakeCompute == null) {
                string[] guids = AssetDatabase.FindAssets("MaterialBake t:ComputeShader");
                if (guids.Length > 0) {
                    string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    _materialBaker.MaterialBakeCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                    EditorUtility.SetDirty(this);
                } else {
                    Debug.LogWarning("Could not find MaterialBake compute shader in project. Please assign it manually to the VoxelLightingBaker.");
                }
            }
            if (_sdfBaker.sdfBakeCompute == null) {
                string[] guids = AssetDatabase.FindAssets("SdfBake t:ComputeShader");
                if (guids.Length > 0) {
                    string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    _sdfBaker.sdfBakeCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                    EditorUtility.SetDirty(this);
                } else {
                    Debug.LogWarning("Could not find SdfBake compute shader in project. Please assign it manually to the VoxelLightingBaker.");
                }
            }
        }
#endif

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
