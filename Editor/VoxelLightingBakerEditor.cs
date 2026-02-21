using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lotec.Lighting.Editor {
    // Add a bake button to the inspector
    [CustomEditor(typeof(VoxelLightingBaker))]
    public class VoxelLightingBakerEditor : UnityEditor.Editor {
        public override VisualElement CreateInspectorGUI() {
            var root = new VisualElement();
            InspectorElement.FillDefaultInspector(root, serializedObject, this);
            root.Add(new VisualElement { style = { height = 10 } });
            var bakeButton = new Button(OnBakeClicked) {
                text = "Bake",
                style = {
                    height = 30,
                    marginTop = 5,
                    marginBottom = 5
                }
            };
            root.Add(bakeButton);

            return root;
        }

        private void OnBakeClicked() {
            var baker = target as VoxelLightingBaker;
            if (baker == null)
                return;

            baker.Bake();

            Debug.Log("VoxelLighting Baker bake completed successfully.", baker);
            string basePath = baker.assetPath;
            // Save baked SDF asset
            if (baker.targetSdfVolume.sdfHiresTexture != null && !string.IsNullOrEmpty(basePath)) {
                string sdfPath = System.IO.Path.Combine(basePath, $"{baker.targetSdfVolume.sdfHiresTexture.name}.asset");
                SaveAsset(baker.targetSdfVolume.sdfHiresTexture, sdfPath, "SDF");
            }
            if (baker.targetSdfVolume.sdfLowresTexture != null && !string.IsNullOrEmpty(basePath)) {
                string sdfPath = System.IO.Path.Combine(basePath, $"{baker.targetSdfVolume.sdfLowresTexture.name}.asset");
                SaveAsset(baker.targetSdfVolume.sdfLowresTexture, sdfPath, "SDF");
            }
            // Save baked Bitmask asset
            if (baker.targetSdfVolume.occlusionBitmaskTexture != null && !string.IsNullOrEmpty(basePath)) {
                string bitmaskPath = System.IO.Path.Combine(basePath, $"{baker.targetSdfVolume.occlusionBitmaskTexture.name}.asset");
                SaveAsset(baker.targetSdfVolume.occlusionBitmaskTexture, bitmaskPath, "Occlusion Bitmask");
            }
            // Save baked packed material texture (albedo+emissionIntensity)
            if (baker.targetSdfVolume.materialAlbedoIntensityTexture != null && !string.IsNullOrEmpty(basePath)) {
                string matPath = System.IO.Path.Combine(basePath, $"{baker.targetSdfVolume.materialAlbedoIntensityTexture.name}.asset");
                SaveAsset(baker.targetSdfVolume.materialAlbedoIntensityTexture, matPath, "Material AlbedoIntensity");
            }
        }

        private void SaveAsset(Object asset, string path, string assetType) {
            if (asset == null || string.IsNullOrEmpty(path))
                return;

            // Ensure directory exists
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) {
                System.IO.Directory.CreateDirectory(dir);
            }

            // If asset exists, delete it first to avoid stale format/serialization issues,
            // then create a fresh asset from the provided object.
            Object existing = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (existing != null) {
                AssetDatabase.DeleteAsset(path);
            }

            AssetDatabase.CreateAsset(asset, path);
            Debug.Log($"{assetType} asset written: {path}", asset);

            // Ensure the new asset is marked dirty and reimported so Unity reloads the latest data
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }
    }
}
