using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lotec.Lighting.Editor {
    /// <summary>Adds Bake / Bake All Volumes buttons that drive every enabled baker on the GameObject.</summary>
    [CustomEditor(typeof(BakeHandler))]
    public class BakeHandlerEditor : UnityEditor.Editor {
        public override VisualElement CreateInspectorGUI() {
            var root = new VisualElement();
            InspectorElement.FillDefaultInspector(root, serializedObject, this);
            root.Add(new VisualElement { style = { height = 10 } });

            root.Add(new Button(OnBakeClicked) {
                text = "Bake",
                style = { height = 30, marginTop = 5, marginBottom = 5 }
            });
            root.Add(new Button(OnBakeAllClicked) {
                text = "Bake All Volumes",
                style = { height = 30, marginBottom = 5 }
            });
            return root;
        }

        void OnBakeClicked() {
            var handler = target as BakeHandler;
            if (handler == null) return;
            BakeAndSave(handler, handler.ResolveVolume());
        }

        void OnBakeAllClicked() {
            var handler = target as BakeHandler;
            if (handler == null) return;

            var volumes = Object.FindObjectsByType<VoxelVolume>();
            if (volumes.Length == 0) {
                Debug.LogWarning("No VoxelVolume components found in the scene.");
                return;
            }

            Debug.Log($"Baking all {volumes.Length} volume(s)...");
            foreach (var volume in volumes)
                BakeAndSave(handler, volume);
            Debug.Log($"Finished baking {volumes.Length} volume(s).");
        }

        static void BakeAndSave(BakeHandler handler, VoxelVolume volume) {
            if (volume == null) {
                Debug.LogError("BakeHandler: target volume is null.", handler);
                return;
            }
            if (!handler.Bake(volume))
                return;
            VoxelBakeEditorUtil.SaveBakedAssets(volume, handler);
        }
    }
}
