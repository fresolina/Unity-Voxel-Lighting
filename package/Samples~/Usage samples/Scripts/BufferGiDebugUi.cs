using System;
using System.Collections.Generic;
using System.IO;
using Unity.Properties;
using UnityEngine;
using UnityEngine.UIElements;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Lotec.Lighting.Samples
{
    /// <summary>
    /// Runtime UI Toolkit panel that mirrors the <see cref="BufferGiDebug"/> inspector: every field
    /// the inspector exposes is a live, in-game control here (mode/field enums, the toggles, stride,
    /// cube fill, normal-line length, intensity, min luminance). Uses the same data-binding pattern
    /// as <see cref="LightControllerUi"/> - <see cref="CreateProperty"/> getters/setters over the
    /// component plus a per-frame snapshot so external (inspector) edits reflect back into the UI.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class BufferGiDebugUi : MonoBehaviour, INotifyBindablePropertyChanged
    {
        static BufferGiDebugUi s_instance;

        [SerializeField] BufferGiDebug _debug;
        [SerializeField] UIDocument _document;
        [SerializeField] PanelSettings _panelSettings;
        [SerializeField] VisualTreeAsset _visualTreeAsset;
        [SerializeField] int _sortingOrder = 1001; // above LightControllerUi (1000) so popups layer on top

        VisualElement _boundRoot;
        Label _focusDebugLabel; // TEMP diagnostic: live focus state, to confirm the input-focus gate in the build
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
        public static void SetFolded(bool folded)
        {
            if (s_instance == null || s_instance._document == null || s_instance._document.rootVisualElement == null)
                return;
            FoldoutHeader.SetFolded(s_instance._document.rootVisualElement, folded);
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            EnsureDebug();
            EnsureDocument();
            EnsureDocumentAssets();
        }
#endif

        void Awake() => s_instance = this;

        void Start()
        {
            EnsureDebug();
            ApplyDocumentAssets();
            BindUi();
            RefreshUi(false);
        }

        void OnEnable()
        {
            if (_document != null && _document.rootVisualElement != null)
                _document.rootVisualElement.visible = true;
        }

        void OnDisable()
        {
            if (_document != null && _document.rootVisualElement != null)
                _document.rootVisualElement.visible = false;
        }

        void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
            UnbindUi();
        }

        void Update()
        {
            if (_document == null)
                return;

            if (_boundRoot != _document.rootVisualElement)
            {
                ApplyDocumentAssets();
                BindUi();
            }

            RefreshUi(true);
            UpdateFocusDebug();
        }

        // TEMP diagnostic: shows the panel's currently focused element and whether the input-focus gate
        // catches it. If a field reads "IsTextInput: True" while focused, the gate works and any remaining
        // typing trouble is elsewhere; if it flips to False/<none> the moment you click a field, focus is
        // being lost (canvas refocus), not mis-detected. Remove once the WebGL typing issue is confirmed fixed.
        void UpdateFocusDebug()
        {
            if (_focusDebugLabel == null)
                return;

            Focusable focused = _document == null
                ? null
                : _document.rootVisualElement?.panel?.focusController?.focusedElement;

            string what = focused is VisualElement ve
                ? (string.IsNullOrEmpty(ve.name) ? ve.GetType().Name : $"{ve.GetType().Name}#{ve.name}")
                : "<none>";
            _focusDebugLabel.text = $"focus: {what}  |  IsTextInput: {UiFocus.IsTextInput(focused)}";
        }

        void EnsureDebug()
        {
            if (_debug == null)
                _debug = FindFirstObjectByType<BufferGiDebug>();
        }

        void EnsureDocument()
        {
            if (_document == null)
                _document = GetComponent<UIDocument>();

#if UNITY_EDITOR
            if (_document == null && !Application.isPlaying)
                _document = Undo.AddComponent<UIDocument>(gameObject);
#endif
            if (_document == null && Application.isPlaying)
                _document = gameObject.AddComponent<UIDocument>();
        }

#if UNITY_EDITOR
        void EnsureDocumentAssets()
        {
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
            if (_panelSettings != panelSettings)
            {
                _panelSettings = panelSettings;
                wasChanged = true;
            }
            if (_visualTreeAsset != visualTree)
            {
                _visualTreeAsset = visualTree;
                wasChanged = true;
            }

            ApplyDocumentAssets();

            if (wasChanged)
                EditorUtility.SetDirty(this);
        }
#endif

        void ApplyDocumentAssets()
        {
            if (_document == null)
                return;

            bool wasChanged = false;
            if (_document.panelSettings != _panelSettings)
            {
                _document.panelSettings = _panelSettings;
                wasChanged = true;
            }
            if (_document.visualTreeAsset != _visualTreeAsset)
            {
                _document.visualTreeAsset = _visualTreeAsset;
                wasChanged = true;
            }
            if (_document.sortingOrder != _sortingOrder)
            {
                _document.sortingOrder = _sortingOrder;
                wasChanged = true;
            }

#if UNITY_EDITOR
            if (wasChanged)
                EditorUtility.SetDirty(_document);
#endif
        }

        void BindUi()
        {
            if (_document == null || _document.rootVisualElement == null)
                return;

            VisualElement root = _document.rootVisualElement;
            if (_boundRoot == root)
                return;

            UnbindUi();
            _boundRoot = root;
            _boundRoot.dataSource = this;

            FoldoutHeader.Setup(root);
            _focusDebugLabel = root.Q<Label>("focus-debug-label");

            // Apply stylesheet to panel root so it also covers dropdown popups.
            if (root.styleSheets.count > 0 && root.panel != null)
            {
                VisualElement panelRoot = root.panel.visualTree;
                StyleSheet sheet = root.styleSheets[0];
                if (!panelRoot.styleSheets.Contains(sheet))
                    panelRoot.styleSheets.Add(sheet);
            }
        }

        void UnbindUi()
        {
            if (_boundRoot != null)
            {
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
            _focusDebugLabel = null;
            _boundRoot = null;
        }

        bool HasFocusedTextInput()
        {
            Focusable focused = _document == null
                ? null
                : _document.rootVisualElement?.panel?.focusController?.focusedElement;
            return UiFocus.IsTextInput(focused);
        }

        // ---- Bound properties (getters/setters over the BufferGiDebug component) -------------------

        [CreateProperty]
        bool DebugEnabled
        {
            get => _debug != null && _debug.enabled;
            set { if (_debug != null) { _debug.enabled = value; RefreshUi(true); } }
        }

        [CreateProperty]
        BufferGiDebug.Mode Mode
        {
            get => _debug != null ? _debug.mode : BufferGiDebug.Mode.Occupancy;
            set { if (_debug != null) { _debug.mode = value; RefreshUi(true); } }
        }

        [CreateProperty]
        BufferGiDebug.Field Field
        {
            get => _debug != null ? _debug.field : BufferGiDebug.Field.Fine;
            set { if (_debug != null) { _debug.field = value; RefreshUi(true); } }
        }

        [CreateProperty]
        bool ShowWireframe
        {
            get => _debug != null && _debug.showWireframe;
            set { if (_debug != null) { _debug.showWireframe = value; RefreshUi(true); } }
        }

        [CreateProperty]
        int Stride
        {
            get => _debug != null ? _debug.stride : 1;
            set { if (_debug != null) { _debug.stride = Mathf.Max(1, value); RefreshUi(true); } }
        }

        [CreateProperty]
        float CubeFill
        {
            get => _debug != null ? _debug.cubeFill : 0.85f;
            set { if (_debug != null) { _debug.cubeFill = Mathf.Clamp(value, 0.1f, 1f); RefreshUi(true); } }
        }

        [CreateProperty]
        float NormalLineLength
        {
            get => _debug != null ? _debug.normalLineLength : 1.5f;
            set { if (_debug != null) { _debug.normalLineLength = Mathf.Clamp(value, 0.5f, 4f); RefreshUi(true); } }
        }

        [CreateProperty]
        float Intensity
        {
            get => _debug != null ? _debug.intensity : 1f;
            set { if (_debug != null) { _debug.intensity = Mathf.Max(0f, value); RefreshUi(true); } }
        }

        [CreateProperty]
        float MinLuminance
        {
            get => _debug != null ? _debug.minLuminance : 0.02f;
            set { if (_debug != null) { _debug.minLuminance = Mathf.Max(0f, value); RefreshUi(true); } }
        }

        // ---- Snapshot / change notification --------------------------------------------------------

        void RefreshUi(bool notifyChanges)
        {
            bool debugEnabled = DebugEnabled;
            BufferGiDebug.Mode mode = Mode;
            BufferGiDebug.Field field = Field;
            bool showWireframe = ShowWireframe;
            int stride = Stride;
            float cubeFill = CubeFill;
            float normalLineLength = NormalLineLength;
            float intensity = Intensity;
            float minLuminance = MinLuminance;

            if (!_hasBindingSnapshot)
            {
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

        void UpdateEnumSnapshot<T>(ref T current, T next, bool notify, string propertyName) where T : struct, Enum
        {
            if (EqualityComparer<T>.Default.Equals(current, next))
                return;
            current = next;
            if (notify) NotifyBindingChanged(propertyName);
        }

        void UpdateBoolSnapshot(ref bool current, bool next, bool notify, string propertyName)
        {
            if (current == next)
                return;
            current = next;
            if (notify) NotifyBindingChanged(propertyName);
        }

        void UpdateIntSnapshot(ref int current, int next, bool notify, string propertyName)
        {
            if (current == next)
                return;
            current = next;
            if (notify) NotifyBindingChanged(propertyName);
        }

        void UpdateFloatSnapshot(ref float current, float next, bool notify, string propertyName)
        {
            if (Mathf.Approximately(current, next))
                return;
            current = next;
            if (notify) NotifyBindingChanged(propertyName);
        }

        void NotifyBindingChanged(string propertyName)
        {
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(propertyName));
        }
    }
}
