using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lotec.Lighting.Editor {
    /// <summary>
    /// Shared editor helpers for persisting baked voxel-lighting textures as assets.
    /// Used by both the per-baker editor and the BakeHandler editor so the save logic
    /// lives in one place regardless of which baker produced the textures.
    /// </summary>
    internal static class VoxelBakeEditorUtil {
        /// <summary>
        /// Save every non-null baked texture on the volume to the scene-adjacent bake
        /// folder, replacing existing assets in place. Safe to call after a single-baker
        /// bake (untouched textures are simply re-saved idempotently).
        /// </summary>
        public static void SaveBakedAssets(LightingVolume volume, Object context) {
            if (volume == null)
                return;

            string basePath = GetBakeFolder(volume);
            if (string.IsNullOrEmpty(basePath))
                return;

            Undo.RecordObject(volume, "Bake voxel lighting assets");

            if (volume.sdfHiresTexture != null) {
                string path = System.IO.Path.Combine(basePath, $"{volume.sdfHiresTexture.name}.asset");
                volume.sdfHiresTexture = SaveAsset(volume.sdfHiresTexture, path, "SDF");
            }
            if (volume.sdfLowresTexture != null) {
                string path = System.IO.Path.Combine(basePath, $"{volume.sdfLowresTexture.name}.asset");
                volume.sdfLowresTexture = SaveAsset(volume.sdfLowresTexture, path, "SDF");
            }
            // Bitmask texture lives on the VoxelOcclusionBitmask binder, not the volume.
            VoxelOcclusionBitmask bitmask = volume.GetComponent<VoxelOcclusionBitmask>();
            if (bitmask != null && bitmask.occlusionBitmaskTexture != null) {
                string path = System.IO.Path.Combine(basePath, $"{bitmask.occlusionBitmaskTexture.name}.asset");
                bitmask.occlusionBitmaskTexture = SaveAsset(bitmask.occlusionBitmaskTexture, path, "Occlusion Bitmask");
                EditorUtility.SetDirty(bitmask);
            }
            VoxelOcclusionField occField = volume.GetComponent<VoxelOcclusionField>();
            if (occField != null && occField.occlusionFieldTextures != null) {
                for (int i = 0; i < occField.occlusionFieldTextures.Length; i++) {
                    Texture3D tex = occField.occlusionFieldTextures[i];
                    if (tex == null) continue;
                    string path = System.IO.Path.Combine(basePath, $"{tex.name}.asset");
                    occField.occlusionFieldTextures[i] = SaveAsset(tex, path, "Occlusion Field");
                }
                EditorUtility.SetDirty(occField);
            }
            // Material field lives on the VoxelMaterialField component, not the volume.
            VoxelMaterialField material = volume.GetComponent<VoxelMaterialField>();
            if (material != null && material.materialAlbedoIntensityTexture != null) {
                string path = System.IO.Path.Combine(basePath, $"{material.materialAlbedoIntensityTexture.name}.asset");
                material.materialAlbedoIntensityTexture = SaveAsset(material.materialAlbedoIntensityTexture, path, "Material AlbedoIntensity");
                EditorUtility.SetDirty(material);
            }

            EditorUtility.SetDirty(volume);
            EditorSceneManager.MarkSceneDirty(volume.gameObject.scene);
            Debug.Log($"Voxel lighting bake assets saved for '{volume.gameObject.name}'.", context);
        }

        static string GetBakeFolder(LightingVolume volume) {
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

        static T SaveAsset<T>(T asset, string path, string assetType) where T : Object {
            if (asset == null || string.IsNullOrEmpty(path))
                return asset;

            // Only freshly baked (in-memory) textures need saving. Volume fields that
            // already point at on-disk assets (because this baker did not rebake them)
            // are persisted objects; trying to CopySerialized/DestroyImmediate those
            // would be redundant and trips "Destroying assets is not permitted".
            if (AssetDatabase.Contains(asset))
                return asset;

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

        static string NormalizeAssetPath(string path) {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
        }

        static string RemapHiddenSamplesFolder(string path) {
            string normalizedPath = NormalizeAssetPath(path);
            return normalizedPath.Contains("/Samples~/")
                ? normalizedPath.Replace("/Samples~/", "/_Samples/")
                : normalizedPath;
        }

        static bool TryGetReadonlyPackageFallback(string scenePath, string sceneName, out string fallbackPath, out string warning) {
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
