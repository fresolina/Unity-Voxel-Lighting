using UnityEngine;

namespace Lotec.Lighting.Samples
{
    /// <summary>
    /// WebGL only: stop Unity from capturing all page keyboard input. With captureAllKeyboardInput on
    /// (the default), Unity grabs keyboard at the document level, and a UI Toolkit text field loses
    /// focus the instant it enters edit mode - focus jumps straight to null - so you cannot type into
    /// the runtime UI in the browser build (it works in the editor, which doesn't route through this).
    /// Runs automatically before the first scene loads; no scene wiring needed.
    /// </summary>
    static class WebGlInputBootstrap
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void DisableKeyboardCapture()
        {
            WebGLInput.captureAllKeyboardInput = false;
            Debug.Log($"[WebGlInput] captureAllKeyboardInput -> false; TouchScreenKeyboard.isSupported={TouchScreenKeyboard.isSupported}");
        }
#endif
    }
}
