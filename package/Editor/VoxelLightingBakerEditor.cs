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

            return root;
        }

        private void OnBakeClicked() {
            var baker = target as VoxelLightingBaker;
            if (baker == null)
                return;

            baker.Bake();
            string basePath = GetBakeFolder(baker);
            if (string.IsNullOrEmpty(basePath))
                return;

            Undo.RecordObject(baker.targetSdfVolume, "Bake voxel lighting assets");

            Debug.Log("VoxelLighting Baker bake completed successfully.", baker);
            // Save baked SDF asset
            if (baker.targetSdfVolume.sdfHiresTexture != null && !string.IsNullOrEmpty(basePath)) {
                string sdfPath = System.IO.Path.Combine(basePath, $"{baker.targetSdfVolume.sdfHiresTexture.name}.asset");
                baker.targetSdfVolume.sdfHiresTexture = SaveAsset(baker.targetSdfVolume.sdfHiresTexture, sdfPath, "SDF");
            }
            if (baker.targetSdfVolume.sdfLowresTexture != null && !string.IsNullOrEmpty(basePath)) {
                string sdfPath = System.IO.Path.Combine(basePath, $"{baker.targetSdfVolume.sdfLowresTexture.name}.asset");
                baker.targetSdfVolume.sdfLowresTexture = SaveAsset(baker.targetSdfVolume.sdfLowresTexture, sdfPath, "SDF");
            }
            // Save baked Bitmask asset
            if (baker.targetSdfVolume.occlusionBitmaskTexture != null && !string.IsNullOrEmpty(basePath)) {
                string bitmaskPath = System.IO.Path.Combine(basePath, $"{baker.targetSdfVolume.occlusionBitmaskTexture.name}.asset");
                baker.targetSdfVolume.occlusionBitmaskTexture = SaveAsset(baker.targetSdfVolume.occlusionBitmaskTexture, bitmaskPath, "Occlusion Bitmask");
            }
            // Save baked packed material texture (albedo+emissionIntensity)
            if (baker.targetSdfVolume.materialAlbedoIntensityTexture != null && !string.IsNullOrEmpty(basePath)) {
                string matPath = System.IO.Path.Combine(basePath, $"{baker.targetSdfVolume.materialAlbedoIntensityTexture.name}.asset");
                baker.targetSdfVolume.materialAlbedoIntensityTexture = SaveAsset(baker.targetSdfVolume.materialAlbedoIntensityTexture, matPath, "Material AlbedoIntensity");
            }

            EditorUtility.SetDirty(baker.targetSdfVolume);
            EditorSceneManager.MarkSceneDirty(baker.targetSdfVolume.gameObject.scene);
        }

        private static string GetBakeFolder(VoxelLightingBaker baker) {
            LightingVolume volume = baker.targetSdfVolume;
            if (volume == null) {
                Debug.LogError("Target SdfVolume is not assigned.", baker);
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

            string bakeFolder = NormalizeAssetPath(System.IO.Path.Combine(sceneFolder, $"{sceneName}-VoxelLighting"));
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
