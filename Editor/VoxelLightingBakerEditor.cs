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
                EditorUtility.DisplayDialog("Bake Failed", $"SDF Baker bake failed:\n{error}", "OK");
                Debug.LogError($"SDF Baker bake failed: {error}", baker);
                return;
            }

            Debug.Log("SDF Baker bake completed successfully.", baker);

            string basePath = baker.assetPath;
            // Save baked SDF asset
            if (baker.targetSdfVolume.sdfTexture != null && !string.IsNullOrEmpty(basePath)) {
                string sdfPath = System.IO.Path.Combine(basePath, $"{baker.targetSdfVolume.sdfTexture.name}.asset");
                SaveAsset(baker.targetSdfVolume.sdfTexture, sdfPath, "SDF");
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

            // Check if asset already exists
            Object existing = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (existing != null) {
                // Update existing asset
                EditorUtility.CopySerialized(asset, existing);
                EditorUtility.SetDirty(existing);
                Debug.Log($"{assetType} asset updated: {path}", existing);
            } else {
                // Create new asset
                AssetDatabase.CreateAsset(asset, path);
                Debug.Log($"{assetType} asset created: {path}", asset);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }
}
