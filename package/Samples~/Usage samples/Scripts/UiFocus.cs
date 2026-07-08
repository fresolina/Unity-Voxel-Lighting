using UnityEngine.UIElements;

namespace Lotec.Lighting.Samples
{
    /// <summary>
    /// Shared "is the user typing in a UI field?" test, so game hotkeys (WASD, 1-9, F/G/H, mouse-look)
    /// stand down while a text/number field is being edited. In a built player the focused element is
    /// the field's inner "unity-text-input", not the field itself, so we walk ancestors rather than
    /// match one exact type/name - that mismatch is why editing worked in the editor but leaked
    /// keystrokes into the game in the WebGL build.
    /// </summary>
    static class UiFocus
    {
        public static bool IsTextInput(Focusable focused)
        {
            if (focused is not VisualElement ve)
                return false;

            return ve is TextField or FloatField or IntegerField
                || ve.GetFirstAncestorOfType<TextField>() != null
                || ve.GetFirstAncestorOfType<FloatField>() != null
                || ve.GetFirstAncestorOfType<IntegerField>() != null;
        }
    }
}
