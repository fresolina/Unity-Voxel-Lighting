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
        [SerializeField] OcclusionFieldBaker _occlusionFieldBaker = new OcclusionFieldBaker();
        [SerializeField] MaterialBaker _materialBaker = new MaterialBaker();
        [HideInInspector][SerializeField] bool _hemisphereFlagsInitialized;
        [HideInInspector][SerializeField] bool _lastBitmaskHemisphereOnly;
        [HideInInspector][SerializeField] bool _lastOcclusionFieldHemisphereOnly;
        public LightingVolume targetSdfVolume => _lightingManager.Volume;

        LightingManager _lightingManager;

#if UNITY_EDITOR
        void OnValidate() {
            if (_lightingManager == null)
                _lightingManager = FindAnyObjectByType<LightingManager>();

            if (!_hemisphereFlagsInitialized) {
                _lastBitmaskHemisphereOnly = _occlusionBitmaskBaker.hemisphereOnly;
                _lastOcclusionFieldHemisphereOnly = _occlusionFieldBaker.hemisphereOnly;
                _hemisphereFlagsInitialized = true;
                return;
            }

            bool bitmaskChanged = _lastBitmaskHemisphereOnly != _occlusionBitmaskBaker.hemisphereOnly;
            bool fieldChanged = _lastOcclusionFieldHemisphereOnly != _occlusionFieldBaker.hemisphereOnly;
            if (bitmaskChanged || fieldChanged) {
                var volume = _lightingManager != null ? _lightingManager.Volume : null;
                bool hasBitmaskBake = volume != null && volume.occlusionBitmaskTexture != null;
                bool hasFieldBake = volume != null && volume.occlusionFieldTextures != null && volume.occlusionFieldTextures.Length > 0;

                if ((bitmaskChanged && hasBitmaskBake) || (fieldChanged && hasFieldBake)) {
                    string targets = bitmaskChanged && fieldChanged
                        ? "bitmask and occlusion field"
                        : bitmaskChanged ? "bitmask" : "occlusion field";
                    Debug.LogWarning($"VoxelLightingBaker: Changed hemisphere-only directions for the {targets}. Existing baked data no longer matches the runtime direction set; rebake the field.", this);
                }

                _lastBitmaskHemisphereOnly = _occlusionBitmaskBaker.hemisphereOnly;
                _lastOcclusionFieldHemisphereOnly = _occlusionFieldBaker.hemisphereOnly;
            }
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
            if (_occlusionFieldBaker.occlusionFieldBakeCompute == null) {
                string[] guids = AssetDatabase.FindAssets("OcclusionFieldBake t:ComputeShader");
                if (guids.Length > 0) {
                    string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    _occlusionFieldBaker.occlusionFieldBakeCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                    EditorUtility.SetDirty(this);
                } else {
                    Debug.LogWarning("Could not find OcclusionFieldBake compute shader in project. Please assign it manually to the VoxelLightingBaker.");
                }
            }
        }
#endif

        public void Bake() {
            if (_lightingManager == null)
                _lightingManager = FindAnyObjectByType<LightingManager>();
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
            if (!_occlusionBitmaskBaker.TryBake(volume, out Texture3D bakedBitmask, out Vector3[] bitmaskDirections, out error)) {
                return;
            }
            volume.occlusionBitmaskTexture = bakedBitmask;
            volume.occlusionBitmaskDirections = bitmaskDirections;

            // Bake occlusion field (per-direction lit values for hardware-interpolated shadows).
            if (_occlusionFieldBaker.occlusionFieldBakeCompute != null) {
                if (!_occlusionFieldBaker.TryBake(volume, out Texture3D[] fieldTextures, out Vector3[] fieldDirections, out error)) {
                    Debug.LogError("Occlusion Field Bake failed: " + error, _lightingManager);
                    return;
                }
                volume.occlusionFieldTextures = fieldTextures;
                volume.occlusionFieldDirections = fieldDirections;
            }

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
