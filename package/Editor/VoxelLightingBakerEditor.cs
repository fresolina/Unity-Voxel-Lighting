using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
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

            var bakeAllButton = new Button(OnBakeAllClicked) {
                text = "Bake All Volumes",
                style = {
                    height = 30,
                    marginBottom = 5
                }
            };
            root.Add(bakeAllButton);

            return root;
        }

        private void OnBakeClicked() {
            var baker = target as VoxelLightingBaker;
            if (baker == null)
                return;

            BakeAndSave(baker, baker.targetSdfVolume);
        }

        private void OnBakeAllClicked() {
            var baker = target as VoxelLightingBaker;
            if (baker == null)
                return;

            var volumes = Object.FindObjectsByType<LightingVolume>(FindObjectsSortMode.None);
            if (volumes.Length == 0) {
                Debug.LogWarning("No LightingVolume components found in the scene.");
                return;
            }

            Debug.Log($"Baking all {volumes.Length} volume(s)...");
            foreach (var volume in volumes) {
                BakeAndSave(baker, volume);
            }
            Debug.Log($"Finished baking {volumes.Length} volume(s).");
        }

        private void BakeAndSave(VoxelLightingBaker baker, LightingVolume volume) {
            baker.Bake(volume);
            string basePath = GetBakeFolder(volume);
            if (string.IsNullOrEmpty(basePath))
                return;

            Undo.RecordObject(volume, "Bake voxel lighting assets");

            Debug.Log($"VoxelLighting Baker bake completed for '{volume.gameObject.name}'.", baker);
            // Save baked SDF asset
            if (volume.sdfHiresTexture != null) {
                string sdfPath = System.IO.Path.Combine(basePath, $"{volume.sdfHiresTexture.name}.asset");
                volume.sdfHiresTexture = SaveAsset(volume.sdfHiresTexture, sdfPath, "SDF");
            }
            if (volume.sdfLowresTexture != null) {
                string sdfPath = System.IO.Path.Combine(basePath, $"{volume.sdfLowresTexture.name}.asset");
                volume.sdfLowresTexture = SaveAsset(volume.sdfLowresTexture, sdfPath, "SDF");
            }
            // Save baked Bitmask asset
            if (volume.occlusionBitmaskTexture != null) {
                string bitmaskPath = System.IO.Path.Combine(basePath, $"{volume.occlusionBitmaskTexture.name}.asset");
                volume.occlusionBitmaskTexture = SaveAsset(volume.occlusionBitmaskTexture, bitmaskPath, "Occlusion Bitmask");
            }
            // Save baked Occlusion Field textures
            if (volume.occlusionFieldTextures != null) {
                for (int i = 0; i < volume.occlusionFieldTextures.Length; i++) {
                    var tex = volume.occlusionFieldTextures[i];
                    if (tex == null) continue;
                    string fieldPath = System.IO.Path.Combine(basePath, $"{tex.name}.asset");
                    volume.occlusionFieldTextures[i] = SaveAsset(tex, fieldPath, "Occlusion Field");
                }
            }
            // Save baked packed material texture (albedo+emissionIntensity)
            if (volume.materialAlbedoIntensityTexture != null) {
                string matPath = System.IO.Path.Combine(basePath, $"{volume.materialAlbedoIntensityTexture.name}.asset");
                volume.materialAlbedoIntensityTexture = SaveAsset(volume.materialAlbedoIntensityTexture, matPath, "Material AlbedoIntensity");
            }

            EditorUtility.SetDirty(volume);
            EditorSceneManager.MarkSceneDirty(volume.gameObject.scene);
        }

        private static string GetBakeFolder(LightingVolume volume) {
            if (volume == null) {
                Debug.LogError("Target volume is null.");
                return null;
            }

            Scene scene = volume.gameObject.scene;
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) {
                Debug.LogError("Save the scene before baking voxel lighting assets.", volume);
                return null;
            }

            string scenePath = NormalizeAssetPath(scene.path);
            string sceneFolder = NormalizeAssetPath(System.IO.Path.GetDirectoryName(scenePath));
            string sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath);
            if (string.IsNullOrEmpty(sceneFolder) || string.IsNullOrEmpty(sceneName)) {
                Debug.LogError($"Could not determine bake folder from scene path '{scenePath}'.", volume);
                return null;
            }

            string bakeFolder = NormalizeAssetPath(System.IO.Path.Combine(sceneFolder, $"{sceneName}-VoxelLighting-{volume.gameObject.name}"));
            if (TryGetReadonlyPackageFallback(scenePath, sceneName, out string fallbackPath, out string warning)) {
                Debug.LogWarning(warning, volume);
                return fallbackPath;
            }

            return RemapHiddenSamplesFolder(bakeFolder);
        }

        private T SaveAsset<T>(T asset, string path, string assetType) where T : Object {
            if (asset == null || string.IsNullOrEmpty(path))
                return asset;

            // Ensure directory exists
            string dir = System.IO.Path.GetDirectoryName(path);
            string absoluteDir = string.IsNullOrEmpty(dir) ? null : System.IO.Path.GetFullPath(dir);
            if (!string.IsNullOrEmpty(absoluteDir) && !System.IO.Directory.Exists(absoluteDir)) {
                System.IO.Directory.CreateDirectory(absoluteDir);
            }

            T existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null) {
                EditorUtility.CopySerialized(asset, existing);
                Object.DestroyImmediate(asset);
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                Debug.Log($"{assetType} asset updated: {path}", existing);
                return existing;
            }

            AssetDatabase.CreateAsset(asset, path);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Debug.Log($"{assetType} asset written: {path}", asset);
            return asset;
        }

        private static string NormalizeAssetPath(string path) {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
        }

        private static string RemapHiddenSamplesFolder(string path) {
            string normalizedPath = NormalizeAssetPath(path);
            return normalizedPath.Contains("/Samples~/")
                ? normalizedPath.Replace("/Samples~/", "/_Samples/")
                : normalizedPath;
        }

        private static bool TryGetReadonlyPackageFallback(string scenePath, string sceneName, out string fallbackPath, out string warning) {
            fallbackPath = null;
            warning = null;

            string normalizedScenePath = NormalizeAssetPath(scenePath);
            if (!normalizedScenePath.StartsWith("Packages/"))
                return false;

            UnityEditor.PackageManager.PackageInfo packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(normalizedScenePath);
            if (packageInfo == null)
                return false;

            if (packageInfo.source == UnityEditor.PackageManager.PackageSource.Embedded || packageInfo.source == UnityEditor.PackageManager.PackageSource.Local)
                return false;

            fallbackPath = $"Assets/{sceneName}-VoxelLighting";
            warning = $"Scene '{normalizedScenePath}' is inside package '{packageInfo.name}' ({packageInfo.source}), which is likely read-only. Baking to '{fallbackPath}' instead.";
            return true;
        }
    }
}
