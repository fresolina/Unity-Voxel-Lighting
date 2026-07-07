using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lotec.Lighting.Editor {
    /// <summary>
    /// Adds a "Bake Voxelization To Disk" button: captures the buffer GI's voxelized material/surface
    /// slices - the coarse field plus every detailed field - into one BufferGiBakeAsset per field,
    /// each named for its field and saved in the scene-adjacent bake folder (same convention as the
    /// texture bakes). The assets are assigned back to the component, so load uploads the coarse asset
    /// + the asset matching the active fine volume instead of re-rasterizing the scene meshes.
    /// </summary>
    [CustomEditor(typeof(BufferGiUpdater))]
    public class BufferGiUpdaterEditor : UnityEditor.Editor {
        const string AssetSuffix = "-BufferGiVoxelization";

        public override VisualElement CreateInspectorGUI() {
            var root = new VisualElement();
            InspectorElement.FillDefaultInspector(root, serializedObject, this);
            root.Add(new VisualElement { style = { height = 10 } });

            root.Add(new Button(OnBakeClicked) {
                text = "Bake Voxelization To Disk",
                style = { height = 30, marginTop = 5, marginBottom = 5 }
            });
            return root;
        }

        void OnBakeClicked() {
            var gi = target as BufferGiUpdater;
            if (gi == null) return;

            // Bake folder is scene-adjacent; derive it from any volume in the scene.
            VoxelVolume folderVolume = gi.Volume != null ? gi.Volume : FindAnyDetailedVolume(gi);
            if (folderVolume == null) {
                Debug.LogError("Buffer GI bake: need at least one VoxelVolume (the active volume or a detailed field with a VoxelVolume sibling) to resolve the save folder.", gi);
                return;
            }
            string folder = VoxelBakeEditorUtil.GetSceneBakeFolder(folderVolume.gameObject, "-VoxelLighting");

            if (string.IsNullOrEmpty(folder)) return;

            var assets = new List<BufferGiBakeAsset>();

            // Coarse field (one asset, named for the coarse MeshBounds).
            if (gi.CoarseBounds != null && gi.CoarseBounds.Root != null) {
                var coarse = ScriptableObject.CreateInstance<BufferGiBakeAsset>();
                coarse.name = gi.CoarseBounds.name + AssetSuffix;
                if (gi.CaptureFieldToAsset(coarse, true, gi.CoarseBounds.Root, gi.CoarseOrigin, gi.CoarseSize))
                    assets.Add(Save(coarse, folder));
                else
                    Object.DestroyImmediate(coarse);
            }

            // Each detailed (fine) field: its runtime grid is its sibling VoxelVolume's padded bounds.
            foreach (MeshBounds field in gi.DetailedFields) {
                if (field == null) continue;
                if (!gi.TryGetDetailedFieldGrid(field, out Transform root, out Vector3 origin, out Vector3 size))
                    continue;
                var fine = ScriptableObject.CreateInstance<BufferGiBakeAsset>();
                fine.name = field.name + AssetSuffix;
                if (gi.CaptureFieldToAsset(fine, false, root, origin, size))
                    assets.Add(Save(fine, folder));
                else
                    Object.DestroyImmediate(fine);
            }

            if (assets.Count == 0) {
                Debug.LogWarning("Buffer GI bake: nothing baked (no coarse field and no detailed fields with a VoxelVolume sibling).", gi);
                return;
            }

            // Assign the freshly baked set (replacing the old list) so load uploads them.
            serializedObject.Update();
            SerializedProperty list = serializedObject.FindProperty("_bakeAssets");
            list.ClearArray();
            for (int i = 0; i < assets.Count; i++) {
                list.InsertArrayElementAtIndex(i);
                list.GetArrayElementAtIndex(i).objectReferenceValue = assets[i];
            }
            serializedObject.ApplyModifiedProperties();
            Debug.Log($"Buffer GI: baked {assets.Count} field voxelization asset(s) to '{folder}'.", gi);
        }

        // Save (or overwrite in place) one field asset in the bake folder.
        static BufferGiBakeAsset Save(BufferGiBakeAsset asset, string folder) {
            string path = System.IO.Path.Combine(folder, $"{asset.name}.asset");
            return VoxelBakeEditorUtil.SaveAsset(asset, path, "Buffer GI Voxelization");
        }

        static VoxelVolume FindAnyDetailedVolume(BufferGiUpdater gi) {
            foreach (MeshBounds field in gi.DetailedFields) {
                if (field == null) continue;
                VoxelVolume vv = field.GetComponent<VoxelVolume>();
                if (vv != null) return vv;
            }
            return null;
        }
    }
}
