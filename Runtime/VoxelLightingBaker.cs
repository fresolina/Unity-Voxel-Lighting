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
        public int LowresDownscaleFactor = 2;
        [Header("Bakers")]
        [SerializeField] SdfBaker _sdfBaker = new SdfBaker();
        [SerializeField] OcclusionBitmaskBaker _occlusionBitmaskBaker = new OcclusionBitmaskBaker();
        [SerializeField] MaterialBaker _materialBaker = new MaterialBaker();
        [Tooltip("Where to save the baked Texture3D asset(s) (must be under Assets/).")]
        public string assetPath = "Assets/VoxelLighting";
        public LightingVolume targetSdfVolume => _lightingManager.Volume;

        LightingManager _lightingManager;

#if UNITY_EDITOR
        void OnValidate() {
            if (_lightingManager == null)
                _lightingManager = FindAnyObjectByType<LightingManager>();
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

        public void Bake() {
            LightingVolume volume = _lightingManager.Volume;
            string error;
            if (volume == null) {
                Debug.LogError("Target SdfVolume is not assigned.", _lightingManager);
                return;
            }

            volume.RecomputeBoundsAndResolution();

            // Bake SDF fields.
            if (!_sdfBaker.TryBake(volume, volume.TrimmedMaxResolution, volume.BakeRoot.name, out Texture3D bakedSdf, out error)) {
                Debug.LogError("SDF Bake failed: " + error, _lightingManager);
                return;
            }
            volume.sdfHiresTexture = bakedSdf;
            if (!_sdfBaker.TryBake(volume, volume.TrimmedMaxResolution / LowresDownscaleFactor, volume.BakeRoot.name + "_Lowres", out bakedSdf, out error)) {
                Debug.LogError("SDF Bake failed: " + error, _lightingManager);
                return;
            }
            volume.sdfLowresTexture = bakedSdf;

            // Bake occlusion bitmask field. TODO: Make this optional.
            if (!_occlusionBitmaskBaker.TryBake(volume, out Texture3D bakedBitmask, out error)) {
                return;
            }
            volume.occlusionBitmaskTexture = bakedBitmask;

            // Material baker produces one lower-res packed material texture (albedo+emissionIntensity)
            if (_materialBaker.MaterialBakeCompute == null) {
                error = "Material Bake Compute is not assigned to MaterialBaker.";
                return;
            }
            string matErr = _materialBaker.Bake(volume, out Texture3D bakedAlbedoIntensity, LowresDownscaleFactor);
            if (!string.IsNullOrEmpty(matErr)) {
                error = "MaterialBaker failed: " + matErr;
                Debug.LogError(error, _lightingManager);
                return;
            }
            volume.materialAlbedoIntensityTexture = bakedAlbedoIntensity;
        }
    }
}
