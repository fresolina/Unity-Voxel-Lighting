using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lotec.Lighting.Editor {
    /// <summary>
    /// Adds a standalone Bake button to any individual baker component, so each baker
    /// can be baked on its own without going through the BakeHandler.
    /// </summary>
    [CustomEditor(typeof(VoxelBakerBase), editorForChildClasses: true)]
    public class VoxelBakerEditor : UnityEditor.Editor {
        public override VisualElement CreateInspectorGUI() {
            var root = new VisualElement();
            InspectorElement.FillDefaultInspector(root, serializedObject, this);
            root.Add(new VisualElement { style = { height = 8 } });

            var baker = target as VoxelBakerBase;
            string label = baker != null ? $"Bake {baker.BakeLabel}" : "Bake";
            root.Add(new Button(OnBakeClicked) {
                text = label,
                style = { height = 28, marginTop = 4, marginBottom = 4 }
            });
            return root;
        }

        void OnBakeClicked() {
            var baker = target as VoxelBakerBase;
            if (baker == null) return;

            VoxelVolume volume = baker.ResolveVolume();
            if (volume == null) {
                Debug.LogError($"{baker.BakeLabel} baker: no target volume (assign one or add a LightingManager).", baker);
                return;
            }

            if (!baker.Bake(volume, out string error)) {
                Debug.LogError($"{baker.BakeLabel} bake failed: {error}", baker);
                return;
            }
            VoxelBakeEditorUtil.SaveBakedAssets(volume, baker);
        }
    }
}
