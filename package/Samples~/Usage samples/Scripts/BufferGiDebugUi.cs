using System;
using System.Collections.Generic;
using System.IO;
using Unity.Properties;
using UnityEngine;
using UnityEngine.UIElements;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Lotec.Lighting.Samples {
    /// <summary>
    /// Runtime UI Toolkit panel that mirrors the <see cref="BufferGiDebug"/> inspector: every field
    /// the inspector exposes is a live, in-game control here (mode/field enums, the toggles, stride,
    /// cube fill, normal-line length, intensity, min luminance). Uses the same data-binding pattern
    /// as <see cref="LightControllerUi"/> - <see cref="CreateProperty"/> getters/setters over the
    /// component plus a per-frame snapshot so external (inspector) edits reflect back into the UI.
    /// </summary>
    [RequireComponent(typeof(PanelRenderer))]
    public class BufferGiDebugUi : MonoBehaviour, INotifyBindablePropertyChanged {
        static BufferGiDebugUi s_instance;

        [SerializeField] BufferGiDebug _debug;
        [SerializeField] PanelRenderer _panel;
        [SerializeField] PanelSettings _panelSettings;
        [SerializeField] VisualTreeAsset _visualTreeAsset;
        [SerializeField] int _sortingOrder = 1001; // above LightControllerUi (1000) so popups layer on top

        // PanelRenderer has no synchronous rootVisualElement; the root arrives via OnUiReload and is
        // cached here for the lifetime of the loaded tree. _boundRoot tracks the last root we bound.
        VisualElement _root;
        VisualElement _boundRoot;
        bool _hasBindingSnapshot;
        bool _lastDebugEnabled;
        BufferGiDebug.Mode _lastMode;
        BufferGiDebug.Field _lastField;
        bool _lastShowWireframe;
        int _lastStride;
        float _lastCubeFill;
        float _lastNormalLineLength;
        float _lastIntensity;
        float _lastMinLuminance;

        public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;

        /// <summary>True while a text/number input in this panel has focus, so game hotkeys can bail.</summary>
        public static bool IsTextInputFocused => s_instance != null && s_instance.HasFocusedTextInput();

        /// <summary>Fold or unfold this panel (the header bar stays visible). Driven by the H hotkey.</summary>
        public static void SetFolded(bool folded) {
            if (s_instance == null || s_instance._root == null)
                return;
            FoldoutHeader.SetFolded(s_instance._root, folded);
        }

#if UNITY_EDITOR
        void OnValidate() {
            EnsureDebug();
            EnsurePanel();
            EnsurePanelAssets();
        }
#endif

        void Awake() => s_instance = this;

        void Start() {
            // Panel + reload callback are set up in OnEnable; re-ensure the debug target in case
            // OnEnable ran first, and re-apply assets idempotently. Binding happens in OnUiReload.
            EnsureDebug();
            ApplyPanelAssets();
            RefreshUi(false);
        }

        void OnEnable() {
            EnsureDebug();
            EnsurePanel();
            if (_panel != null) {
                // Register before assigning the tree so we receive the initial load's root here.
                _panel.RegisterUIReloadCallback(OnUiReload);
                ApplyPanelAssets();
            }

            if (_root != null)
                _root.visible = true;
        }

        void OnDisable() {
            if (_panel != null)
                _panel.UnregisterUIReloadCallback(OnUiReload);
            // PanelRenderer keeps its content in memory while disabled, so the cached root stays valid.
            if (_root != null)
                _root.visible = false;
        }

        void OnDestroy() {
            if (s_instance == this)
                s_instance = null;
            UnbindUi();
        }

        void Update() {
            RefreshUi(true);
        }

        // PanelRenderer hands us the freshly loaded root here - on initial setup and whenever the
        // visual tree reloads (asset swap / live reload) - replacing UIDocument's rootVisualElement poll.
        void OnUiReload(PanelRenderer panel, VisualElement root) {
            _root = root;
            BindUi();
            RefreshUi(false);
        }

        void EnsureDebug() {
            if (_debug == null)
                _debug = FindAnyObjectByType<BufferGiDebug>();
        }

        void EnsurePanel() {
            if (_panel == null)
                _panel = GetComponent<PanelRenderer>();

#if UNITY_EDITOR
            if (_panel == null && !Application.isPlaying)
                _panel = Undo.AddComponent<PanelRenderer>(gameObject);
#endif
            if (_panel == null && Application.isPlaying)
                _panel = gameObject.AddComponent<PanelRenderer>();
        }

#if UNITY_EDITOR
        void EnsurePanelAssets() {
            MonoScript monoScript = MonoScript.FromMonoBehaviour(this);
            string scriptPath = AssetDatabase.GetAssetPath(monoScript);
            string scriptDirectory = Path.GetDirectoryName(scriptPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(scriptDirectory))
                return;

            string sampleDirectory = Path.GetDirectoryName(scriptDirectory)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(sampleDirectory))
                return;

            // Reuse the LightControllerUi panel settings (shared panel, layered by sorting order).
            PanelSettings panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>($"{sampleDirectory}/UI/LightControllerUiPanelSettings.asset");
            VisualTreeAsset visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"{sampleDirectory}/UI/BufferGiDebugUi.uxml");

            bool wasChanged = false;
            if (_panelSettings != panelSettings) {
                _panelSettings = panelSettings;
                wasChanged = true;
            }
            if (_visualTreeAsset != visualTree) {
                _visualTreeAsset = visualTree;
                wasChanged = true;
            }

            ApplyPanelAssets();

            if (wasChanged)
                EditorUtility.SetDirty(this);
        }
#endif

        void ApplyPanelAssets() {
            if (_panel == null)
                return;

            bool wasChanged = false;
            if (_panel.panelSettings != _panelSettings) {
                _panel.panelSettings = _panelSettings;
                wasChanged = true;
            }
            if (_panel.visualTreeAsset != _visualTreeAsset) {
                _panel.visualTreeAsset = _visualTreeAsset;
                wasChanged = true;
            }
            // sortingOrder is inherited from Renderer (int) - layers this panel above LightControllerUi.
            if (_panel.sortingOrder != _sortingOrder) {
                _panel.sortingOrder = _sortingOrder;
                wasChanged = true;
            }

#if UNITY_EDITOR
            if (wasChanged)
                EditorUtility.SetDirty(_panel);
#endif
        }

        void BindUi() {
            if (_root == null)
                return;

            VisualElement root = _root;
            if (_boundRoot == root)
                return;

            UnbindUi();
            _boundRoot = root;
            _boundRoot.dataSource = this;

            FoldoutHeader.Setup(root);

            // Apply stylesheet to panel root so it also covers dropdown popups.
            if (root.styleSheets.count > 0 && root.panel != null) {
                VisualElement panelRoot = root.panel.visualTree;
                StyleSheet sheet = root.styleSheets[0];
                if (!panelRoot.styleSheets.Contains(sheet))
                    panelRoot.styleSheets.Add(sheet);
            }
        }

        void UnbindUi() {
            if (_boundRoot != null) {
                _boundRoot.Q<Toggle>("debug-enabled-toggle")?.ClearBindings();
                _boundRoot.Q<EnumField>("mode-enum")?.ClearBindings();
                _boundRoot.Q<EnumField>("field-enum")?.ClearBindings();
                _boundRoot.Q<Toggle>("wireframe-toggle")?.ClearBindings();
                _boundRoot.Q<IntegerField>("stride-field")?.ClearBindings();
                _boundRoot.Q<Slider>("cube-fill-slider")?.ClearBindings();
                _boundRoot.Q<Slider>("normal-line-length-slider")?.ClearBindings();
                _boundRoot.Q<FloatField>("intensity-field")?.ClearBindings();
                _boundRoot.Q<FloatField>("min-luminance-field")?.ClearBindings();
                _boundRoot.dataSource = null;
            }
            _boundRoot = null;
        }

        bool HasFocusedTextInput() {
            Focusable focused = _root?.panel?.focusController?.focusedElement;
            if (focused is TextField || focused is FloatField || focused is IntegerField)
                return true;
            return focused is VisualElement ve && ve.name == TextField.textInputUssName;
        }

        // ---- Bound properties (getters/setters over the BufferGiDebug component) -------------------

        [CreateProperty]
        bool DebugEnabled {
            get => _debug != null && _debug.enabled;
            set { if (_debug != null) { _debug.enabled = value; RefreshUi(true); } }
        }

        [CreateProperty]
        BufferGiDebug.Mode Mode {
            get => _debug != null ? _debug.mode : BufferGiDebug.Mode.Occupancy;
            set { if (_debug != null) { _debug.mode = value; RefreshUi(true); } }
        }

        [CreateProperty]
        BufferGiDebug.Field Field {
            get => _debug != null ? _debug.field : BufferGiDebug.Field.Fine;
            set { if (_debug != null) { _debug.field = value; RefreshUi(true); } }
        }

        [CreateProperty]
        bool ShowWireframe {
            get => _debug != null && _debug.showWireframe;
            set { if (_debug != null) { _debug.showWireframe = value; RefreshUi(true); } }
        }

        [CreateProperty]
        int Stride {
            get => _debug != null ? _debug.stride : 1;
            set { if (_debug != null) { _debug.stride = Mathf.Max(1, value); RefreshUi(true); } }
        }

        [CreateProperty]
        float CubeFill {
            get => _debug != null ? _debug.cubeFill : 0.85f;
            set { if (_debug != null) { _debug.cubeFill = Mathf.Clamp(value, 0.1f, 1f); RefreshUi(true); } }
        }

        [CreateProperty]
        float NormalLineLength {
            get => _debug != null ? _debug.normalLineLength : 1.5f;
            set { if (_debug != null) { _debug.normalLineLength = Mathf.Clamp(value, 0.5f, 4f); RefreshUi(true); } }
        }

        [CreateProperty]
        float Intensity {
            get => _debug != null ? _debug.intensity : 1f;
            set { if (_debug != null) { _debug.intensity = Mathf.Max(0f, value); RefreshUi(true); } }
        }

        [CreateProperty]
        float MinLuminance {
            get => _debug != null ? _debug.minLuminance : 0.02f;
            set { if (_debug != null) { _debug.minLuminance = Mathf.Max(0f, value); RefreshUi(true); } }
        }

        // ---- Snapshot / change notification --------------------------------------------------------

        void RefreshUi(bool notifyChanges) {
            bool debugEnabled = DebugEnabled;
            BufferGiDebug.Mode mode = Mode;
            BufferGiDebug.Field field = Field;
            bool showWireframe = ShowWireframe;
            int stride = Stride;
            float cubeFill = CubeFill;
            float normalLineLength = NormalLineLength;
            float intensity = Intensity;
            float minLuminance = MinLuminance;

            if (!_hasBindingSnapshot) {
                _lastDebugEnabled = debugEnabled;
                _lastMode = mode;
                _lastField = field;
                _lastShowWireframe = showWireframe;
                _lastStride = stride;
                _lastCubeFill = cubeFill;
                _lastNormalLineLength = normalLineLength;
                _lastIntensity = intensity;
                _lastMinLuminance = minLuminance;
                _hasBindingSnapshot = true;
                return;
            }

            UpdateBoolSnapshot(ref _lastDebugEnabled, debugEnabled, notifyChanges, nameof(DebugEnabled));
            UpdateEnumSnapshot(ref _lastMode, mode, notifyChanges, nameof(Mode));
            UpdateEnumSnapshot(ref _lastField, field, notifyChanges, nameof(Field));
            UpdateBoolSnapshot(ref _lastShowWireframe, showWireframe, notifyChanges, nameof(ShowWireframe));
            UpdateIntSnapshot(ref _lastStride, stride, notifyChanges, nameof(Stride));
            UpdateFloatSnapshot(ref _lastCubeFill, cubeFill, notifyChanges, nameof(CubeFill));
            UpdateFloatSnapshot(ref _lastNormalLineLength, normalLineLength, notifyChanges, nameof(NormalLineLength));
            UpdateFloatSnapshot(ref _lastIntensity, intensity, notifyChanges, nameof(Intensity));
            UpdateFloatSnapshot(ref _lastMinLuminance, minLuminance, notifyChanges, nameof(MinLuminance));
        }

        void UpdateEnumSnapshot<T>(ref T current, T next, bool notify, string propertyName) where T : struct, Enum {
            if (EqualityComparer<T>.Default.Equals(current, next))
                return;
            current = next;
            if (notify) NotifyBindingChanged(propertyName);
        }

        void UpdateBoolSnapshot(ref bool current, bool next, bool notify, string propertyName) {
            if (current == next)
                return;
            current = next;
            if (notify) NotifyBindingChanged(propertyName);
        }

        void UpdateIntSnapshot(ref int current, int next, bool notify, string propertyName) {
            if (current == next)
                return;
            current = next;
            if (notify) NotifyBindingChanged(propertyName);
        }

        void UpdateFloatSnapshot(ref float current, float next, bool notify, string propertyName) {
            if (Mathf.Approximately(current, next))
                return;
            current = next;
            if (notify) NotifyBindingChanged(propertyName);
        }

        void NotifyBindingChanged(string propertyName) {
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(propertyName));
        }
    }
}
