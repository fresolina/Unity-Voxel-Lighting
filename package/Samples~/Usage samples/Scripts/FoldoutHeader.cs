using UnityEngine.UIElements;

namespace Lotec.Lighting.Samples
{
    /// <summary>
    /// Makes a panel header collapse/expand its body on click, and exposes the same fold as a
    /// programmatic call (so a hotkey can fold every panel at once). A UXML panel opts in by giving
    /// its clickable header row the name "panel-header", the foldable content a wrapper named
    /// "panel-body", and (optionally) a "fold-arrow" label whose glyph flips ▾/▸. Shared by
    /// <see cref="LightControllerUi"/> and <see cref="BufferGiDebugUi"/>.
    /// </summary>
    static class FoldoutHeader
    {
        public static void Setup(VisualElement root)
        {
            VisualElement header = root.Q<VisualElement>("panel-header");
            if (header == null || root.Q<VisualElement>("panel-body") == null)
                return;

            header.RegisterCallback<ClickEvent>(_ => Toggle(root));
        }

        /// <summary>Flip the fold state of the panel under <paramref name="root"/>.</summary>
        public static void Toggle(VisualElement root)
        {
            VisualElement body = root.Q<VisualElement>("panel-body");
            if (body == null)
                return;
            SetFolded(root, body.resolvedStyle.display != DisplayStyle.None);
        }

        /// <summary>Fold (hide body) or unfold the panel, updating the ▾/▸ arrow.</summary>
        public static void SetFolded(VisualElement root, bool folded)
        {
            VisualElement body = root.Q<VisualElement>("panel-body");
            if (body == null)
                return;

            body.style.display = folded ? DisplayStyle.None : DisplayStyle.Flex;
            Label arrow = root.Q<Label>("fold-arrow");
            if (arrow != null)
                arrow.text = folded ? "▸" : "▾";
        }
    }
}
