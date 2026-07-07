using UnityEngine;

namespace Lotec.Demo {
    /// <summary>
    /// Minimal on-screen menu for <see cref="SceneLoader"/>. Draws a "Remote Scenes" button;
    /// toggling it opens a panel that lists the loader's scenes with Load buttons, a download
    /// progress bar, and an Unload button for whatever is currently loaded.
    ///
    /// Uses IMGUI so it needs zero Canvas/prefab wiring - just drop this + SceneLoader on one
    /// GameObject. Note: IMGUI is screen-space, so it shows on a flat display but not inside a VR
    /// headset; swap for a world-space uGUI/UIToolkit panel if you need it in-headset.
    /// </summary>
    [RequireComponent(typeof(SceneLoader))]
    public class RemoteSceneMenu : MonoBehaviour {
        [SerializeField] SceneLoader _loader;
        [Tooltip("On-screen scale for the menu (bump up for high-DPI / headset-mirror displays).")]
        [SerializeField] float _uiScale = 1.5f;

        bool _open;

        void Reset() => _loader = GetComponent<SceneLoader>();
        void Awake() { if (_loader == null) _loader = GetComponent<SceneLoader>(); }

        void OnGUI() {
            Matrix4x4 prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * _uiScale);

            const float pad = 10f;
            const float width = 300f;
            const float buttonWidth = 160f;

            // Anchor to the top-center. Coordinates are in the pre-scale space, so center on
            // Screen.width / _uiScale.
            float screenWidth = Screen.width / _uiScale;

            if (GUI.Button(new Rect((screenWidth - buttonWidth) * 0.5f, pad, buttonWidth, 28), _open ? "Remote Scenes  ▲" : "Remote Scenes  ▼"))
                _open = !_open;

            if (_open) {
                GUILayout.BeginArea(new Rect((screenWidth - width) * 0.5f, pad + 32, width, Screen.height / _uiScale - pad * 2 - 32), GUI.skin.box);
                DrawPanel();
                GUILayout.EndArea();
            }

            GUI.matrix = prevMatrix;
        }

        void DrawPanel() {
            GUILayout.Label("<b>Remote Scenes</b>", RichLabel);

            if (_loader.Scenes.Count == 0)
                GUILayout.Label("No scenes configured on the SceneLoader.", RichLabel);

            GUI.enabled = !_loader.IsBusy;
            foreach (RemoteSceneEntry entry in _loader.Scenes) {
                if (entry == null) continue;
                bool isLoaded = _loader.LoadedEntry == entry;
                string label = isLoaded ? $"✓ {entry.displayName}" : entry.displayName;
                if (GUILayout.Button(label, GUILayout.Height(26)))
                    _loader.Load(entry);
            }
            GUI.enabled = true;

            GUILayout.Space(6);

            // Progress bar while downloading.
            if (_loader.State == RemoteSceneState.Downloading) {
                Rect bar = GUILayoutUtility.GetRect(1, 18, GUILayout.ExpandWidth(true));
                GUI.Box(bar, GUIContent.none);
                Rect fill = new(bar.x, bar.y, bar.width * Mathf.Clamp01(_loader.Progress), bar.height);
                GUI.color = new Color(0.3f, 0.7f, 1f, 0.8f);
                GUI.Box(fill, GUIContent.none);
                GUI.color = Color.white;
            }

            if (!string.IsNullOrEmpty(_loader.StatusMessage))
                GUILayout.Label(_loader.StatusMessage, RichLabel);

            GUILayout.FlexibleSpace();

            GUI.enabled = _loader.LoadedEntry != null && !_loader.IsBusy;
            if (GUILayout.Button("Unload Current", GUILayout.Height(26)))
                _loader.Unload();
            GUI.enabled = true;
        }

        static GUIStyle _richLabel;
        static GUIStyle RichLabel => _richLabel ??= new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
    }
}
