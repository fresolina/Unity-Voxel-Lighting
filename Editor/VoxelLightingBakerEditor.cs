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

            if (!baker.TryBake(out string error)) {
                EditorUtility.DisplayDialog("Bake Failed", $"VoxelLighting Baker bake failed:\n{error}", "OK");
                Debug.LogError($"VoxelLighting Baker bake failed: {error}", baker);
                return;
            }

            Debug.Log("VoxelLighting Baker bake completed successfully.", baker);
            string basePath = baker.assetPath;
            // Save baked SDF asset
            if (baker.targetSdfVolume.sdfTexture != null && !string.IsNullOrEmpty(basePath)) {
                string sdfPath = System.IO.Path.Combine(basePath, $"{baker.targetSdfVolume.sdfTexture.name}.asset");
                SaveAsset(baker.targetSdfVolume.sdfTexture, sdfPath, "SDF");
            }
            // Save baked Bitmask asset
            if (baker.targetSdfVolume.occlusionBitmaskTexture != null && !string.IsNullOrEmpty(basePath)) {
                string bitmaskPath = System.IO.Path.Combine(basePath, $"{baker.targetSdfVolume.occlusionBitmaskTexture.name}.asset");
                SaveAsset(baker.targetSdfVolume.occlusionBitmaskTexture, bitmaskPath, "Occlusion Bitmask");
            }
            // Save baked Material textures (albedo+roughness and emission+metallic)
            if (baker.targetSdfVolume.materialAlbedoRoughnessTexture != null && !string.IsNullOrEmpty(basePath)) {
                string matAPath = System.IO.Path.Combine(basePath, $"{baker.targetSdfVolume.materialAlbedoRoughnessTexture.name}.asset");
                SaveAsset(baker.targetSdfVolume.materialAlbedoRoughnessTexture, matAPath, "Material AlbedoRoughness");
            }
            if (baker.targetSdfVolume.materialEmissionMetallicTexture != null && !string.IsNullOrEmpty(basePath)) {
                string matBPath = System.IO.Path.Combine(basePath, $"{baker.targetSdfVolume.materialEmissionMetallicTexture.name}.asset");
                SaveAsset(baker.targetSdfVolume.materialEmissionMetallicTexture, matBPath, "Material EmissionMetallic");
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
                AssetDatabase.Refresh();
                AssetDatabase.CreateAsset(asset, path);
                Debug.Log($"{assetType} asset replaced: {path}", asset);
            } else {
                AssetDatabase.CreateAsset(asset, path);
                Debug.Log($"{assetType} asset created: {path}", asset);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }
}
