using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Unity.Properties;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Lotec.Lighting.Samples {
    /// <summary>
    /// Runtime UI Toolkit panel for the GI / <see cref="LightingManager"/> settings (GI method,
    /// samples/frame, shadow mode, AO, confidence, radiance directions, tonemap) plus the FPS /
    /// frame-time readout, and the GI-related hotkeys (backquote cycles the lighting method; H - or
    /// the VR left-controller Menu button - folds the panels). The actual scene lights (sun,
    /// flashlight, candle) live in <see cref="LightController"/> and have no UI.
    /// <para>VR: this renders as a screen Overlay today. To make it VR-accessible with no VR package,
    /// switch the shared PanelSettings render mode to World Space and position the "Lighting UI" root
    /// in the scene - the auto-generated panel collider then receives an XR ray interactor's pointer
    /// events, no code change required.</para>
    /// </summary>
    [RequireComponent(typeof(PanelRenderer))]
    public class LightingController : MonoBehaviour, INotifyBindablePropertyChanged {
        const float OneSecondWindowDuration = 1f;
        const string UnavailableFrameTimeText = "--.-- ms";
        const string UnavailableFpsText = "-- fps";
        // Seconds between readout re-formats. The numbers are already averaged over a 1s window, so
        // rebuilding their text every frame produced ~300 string allocations/second for no extra
        // information - a steady drip that fed periodic GC spikes on device.
        const float ReadoutRefreshInterval = 0.25f;
        // Highest FPS with a pre-built string. Above this the label formats fresh (never in practice).
        const int MaxCachedFps = 300;

        struct FrameTimeSample {
            public readonly float Timestamp;
            public readonly float Milliseconds;

            public FrameTimeSample(float timestamp, float milliseconds) {
                Timestamp = timestamp;
                Milliseconds = milliseconds;
            }
        }

        /// <summary>The single "Shadow Mode" dropdown, combining the BufferGI baked voxel sun-shadow
        /// (first / default entry) with the volume shadow-source options. Selecting Baked turns on the
        /// fine field's baked shadow; any other entry turns it off and selects that shadow source.</summary>
        // Mirrors BufferGiUpdater.ShadowMode (the buffer-GI fine field is the sole shadow authority now).
        // Bitmask has a single read path under buffer GI (a point fetch), so the old Point/8-tap split
        // is collapsed to one entry.
        public enum ShadowUiMode { Off, Baked, SDF, OcclusionField, Bitmask }

        static LightingController s_instance;

        [SerializeField] PanelRenderer _panel;
        [SerializeField] PanelSettings _panelSettings;
        [SerializeField] VisualTreeAsset _visualTreeAsset;
        [SerializeField] int _sortingOrder = 1000;

        // PanelRenderer has no synchronous rootVisualElement; the root arrives via OnUiReload and is
        // cached here for the lifetime of the loaded tree. _boundRoot tracks the last root we bound.
        VisualElement _root;
        VisualElement _boundRoot;
        readonly Queue<FrameTimeSample> _frameTimeSamples = new Queue<FrameTimeSample>();
        Label _frameTimeLastSecondLowLabel;
        Label _frameTimeLastSecondHighLabel;
        Label _frameTimeLastSecondAverageLabel;
        Label _fpsLabel;
        VisualElement _panelBody;
        bool _hasBindingSnapshot;
        string _frameTimeLastSecondLowText = UnavailableFrameTimeText;
        string _frameTimeLastSecondHighText = UnavailableFrameTimeText;
        string _frameTimeLastSecondAverageText = UnavailableFrameTimeText;
        string _fpsText = UnavailableFpsText;
        float _nextReadoutTime;
        // Reusable formatting buffer + memo tables, so a repeated value costs no allocation at all
        // (frame times hover over the same handful of hundredths, and an FPS number even more so).
        static readonly StringBuilder s_textBuilder = new StringBuilder(16);
        static readonly Dictionary<int, string> s_frameTimeTextCache = new Dictionary<int, string>(256);
        static readonly string[] s_fpsTextCache = new string[MaxCachedFps + 1];
        GiMethod _lastGiMethod;
        ShadowUiMode _lastShadowMode;
        int _lastSamplesPerFrame;
        float _lastConfidenceCurve;
        float _lastAoStrength;
        BufferGiUpdater.RadianceDirections _lastRadianceDirections;
        AutoExposure.TonemapMode _lastTonemap;
        bool _lastAutoExposure;
        Keyboard _keyboard;
#if LOTEC_XR
        bool _menuHeldLastFrame;
#endif

        public static bool IsTextInputFocused => s_instance != null && s_instance.HasFocusedTextInput();

        /// <summary>Fold or unfold this panel (the header bar stays visible). Driven by the H hotkey.</summary>
        public static void SetFolded(bool folded) {
            if (s_instance == null || s_instance._root == null) {
                return;
            }

            FoldoutHeader.SetFolded(s_instance._root, folded);
        }

        public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;

#if UNITY_EDITOR
        void OnValidate() {
            EnsurePanel();
            EnsurePanelAssets();
        }
#endif

        void Awake() {
            s_instance = this;
        }

        void Start() {
            // Panel + reload callback are set up in OnEnable; re-apply assets idempotently here.
            // Binding happens in OnUiReload.
            ApplyPanelAssets();
            RefreshUi(false);
        }

        void OnEnable() {
            EnsurePanel();
            if (_panel != null) {
                // Register before assigning the tree so we receive the initial load's root here.
                _panel.RegisterUIReloadCallback(OnUiReload);
                ApplyPanelAssets();
            }

            if (_root != null)
                _root.visible = true;
            ResetFrameTimeStats();
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
            UpdateFrameTimeStats();
            RefreshUi(true);
            HandleHotkeys();
#if LOTEC_XR
            HandleVrMenuButton();
#endif
        }

        // GI-panel hotkeys: H folds both runtime panels, backquote cycles the GI lighting method.
        // Guarded so typing in a field (this panel or the debug panel) doesn't trigger them.
        // Both panels follow THIS panel's current fold state, so the key/button always does the visible
        // thing even after the header was clicked directly (which is what a stored flag got wrong).
        void ToggleBothPanels() {
            bool folded = !IsFolded;
            SetFolded(folded);
            BufferGiDebugUi.SetFolded(folded);
        }

        void HandleHotkeys() {
            _keyboard = Keyboard.current;
            if (_keyboard == null) {
                return;
            }

            if (IsTextInputFocused || BufferGiDebugUi.IsTextInputFocused) {
                return;
            }

            if (_keyboard.hKey.wasPressedThisFrame) {
                ToggleBothPanels();
            }
        }

        // VR: the LEFT Touch controller's Menu button toggles both panels (same fold as the H hotkey),
        // so the UI is reachable in-headset. Polled through the XR input subsystem (OpenXR feeds it) -
        // no action asset or scene wiring, matching the keyboard poll above; on desktop (no XR device)
        // GetDeviceAtXRNode returns an invalid device and this is a harmless no-op. XR types are fully
        // qualified to avoid the InputSystem/XR CommonUsages + InputDevice name clash. Not gated by the
        // text-focus guard - the button is not a character, so it should always toggle.
        //
        // Gated by LOTEC_XR so this shared sample compiles in the non-XR project-demo, where the
        // UnityEngine.XR module isn't present. The VR project defines LOTEC_XR (project-vr-demo/Assets/csc.rsp).
#if LOTEC_XR
        void HandleVrMenuButton() {
            UnityEngine.XR.InputDevice leftHand =
                UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);
            bool pressed = leftHand.isValid
                && leftHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.menuButton, out bool held)
                && held;

            // Rising-edge only, so holding the button doesn't flip the panels every frame.
            if (pressed && !_menuHeldLastFrame) {
                ToggleBothPanels();
            }
            _menuHeldLastFrame = pressed;
        }
#endif

        // PanelRenderer hands us the freshly loaded root here - on initial setup and whenever the
        // visual tree reloads (asset swap / live reload) - replacing UIDocument's rootVisualElement poll.
        void OnUiReload(PanelRenderer panel, VisualElement root) {
            _root = root;
            BindUi();
            RefreshUi(false);
        }

        void EnsurePanel() {
            if (_panel == null) {
                _panel = GetComponent<PanelRenderer>();
            }

#if UNITY_EDITOR
            if (_panel == null && !Application.isPlaying) {
                _panel = Undo.AddComponent<PanelRenderer>(gameObject);
            }
#endif

            if (_panel == null && Application.isPlaying) {
                _panel = gameObject.AddComponent<PanelRenderer>();
            }
        }

#if UNITY_EDITOR
        void EnsurePanelAssets() {
            MonoScript monoScript = MonoScript.FromMonoBehaviour(this);
            string scriptPath = AssetDatabase.GetAssetPath(monoScript);
            string scriptDirectory = Path.GetDirectoryName(scriptPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(scriptDirectory)) {
                return;
            }

            string sampleDirectory = Path.GetDirectoryName(scriptDirectory)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(sampleDirectory)) {
                return;
            }

            VisualTreeAsset visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"{sampleDirectory}/UI/LightingController.uxml");

            bool wasChanged = false;

            if (_visualTreeAsset != visualTree) {
                _visualTreeAsset = visualTree;
                wasChanged = true;
            }

            ApplyPanelAssets();

            if (wasChanged) {
                EditorUtility.SetDirty(this);
            }
        }
#endif

        void ApplyPanelAssets() {
            if (_panel == null) {
                return;
            }

            bool wasChanged = false;
            if (_panel.panelSettings != _panelSettings) {
                _panel.panelSettings = _panelSettings;
                wasChanged = true;
            }

            if (_panel.visualTreeAsset != _visualTreeAsset) {
                _panel.visualTreeAsset = _visualTreeAsset;
                wasChanged = true;
            }

            // sortingOrder is inherited from Renderer (int) - it layers this panel against the other
            // shared-PanelSettings panels (e.g. BufferGiDebugUi at 1001).
            if (_panel.sortingOrder != _sortingOrder) {
                _panel.sortingOrder = _sortingOrder;
                wasChanged = true;
            }

#if UNITY_EDITOR
            if (wasChanged) {
                EditorUtility.SetDirty(_panel);
            }
#endif
        }

        void BindUi() {
            if (_root == null) {
                return;
            }

            VisualElement root = _root;
            if (_boundRoot == root) {
                return;
            }

            UnbindUi();


            EnumField giField = root.Q<EnumField>("gi-enum");
            EnumField shadowModeField = root.Q<EnumField>("shadow-mode-enum");
            SliderInt samplesPerFrameSlider = root.Q<SliderInt>("samples-per-frame-slider");
            Slider confidenceCurveSlider = root.Q<Slider>("confidence-curve-slider");
            Slider aoStrengthSlider = root.Q<Slider>("ao-strength-slider");
            EnumField radianceDirectionsField = root.Q<EnumField>("radiance-directions-enum");
            EnumField tonemapField = root.Q<EnumField>("tonemap-enum");
            Toggle autoExposureToggle = root.Q<Toggle>("auto-exposure-toggle");

            if (giField == null || shadowModeField == null || samplesPerFrameSlider == null || confidenceCurveSlider == null || aoStrengthSlider == null || radianceDirectionsField == null || tonemapField == null || autoExposureToggle == null || !TryCacheFrameTimeLabels(root)) {
                UnbindUi();
                return;
            }

            _boundRoot = root;
            _boundRoot.dataSource = this;

            // Set the enum type from code: the UXML `type` string resolves ShadowUiMode (same
            // Assembly-CSharp as this UXML) but not TonemapMode / RadianceDirections (in the
            // Lotec.Lighting package assembly) at runtime, leaving the field empty. Init here
            // guarantees the choices.
            tonemapField.Init(Tonemap);
            radianceDirectionsField.Init(RadianceDirections);

            FoldoutHeader.Setup(root);

            // Apply stylesheet to panel root so it also covers dropdown popups.
            if (root.styleSheets.count > 0) {
                var panelRoot = root.panel.visualTree;
                var sheet = root.styleSheets[0];
                if (!panelRoot.styleSheets.Contains(sheet)) {
                    panelRoot.styleSheets.Add(sheet);
                }
            }
        }

        bool TryCacheFrameTimeLabels(VisualElement root) {
            _frameTimeLastSecondLowLabel = root.Q<Label>("frame-time-last-second-low-value");
            _frameTimeLastSecondHighLabel = root.Q<Label>("frame-time-last-second-high-value");
            _frameTimeLastSecondAverageLabel = root.Q<Label>("frame-time-last-second-average-value");
            _fpsLabel = root.Q<Label>("fps-value");
            // The foldable body, cached so the readout can ask whether it is actually on screen. Its
            // display style is the SINGLE source of truth for the fold - clicking the header goes
            // through FoldoutHeader and never touches this component's state.
            _panelBody = root.Q<VisualElement>("panel-body");

            return _frameTimeLastSecondLowLabel != null
                && _frameTimeLastSecondHighLabel != null
                && _frameTimeLastSecondAverageLabel != null
                && _fpsLabel != null;
        }

        void UnbindUi() {
            if (_boundRoot != null) {
                _boundRoot.Q<EnumField>("gi-enum")?.ClearBindings();
                _boundRoot.Q<EnumField>("shadow-mode-enum")?.ClearBindings();
                _boundRoot.Q<SliderInt>("samples-per-frame-slider")?.ClearBindings();
                _boundRoot.Q<Slider>("confidence-curve-slider")?.ClearBindings();
                _boundRoot.Q<Slider>("ao-strength-slider")?.ClearBindings();
                _boundRoot.Q<EnumField>("radiance-directions-enum")?.ClearBindings();
                _boundRoot.Q<EnumField>("tonemap-enum")?.ClearBindings();
                _boundRoot.Q<Toggle>("auto-exposure-toggle")?.ClearBindings();
                _boundRoot.dataSource = null;
            }

            _frameTimeLastSecondLowLabel = null;
            _frameTimeLastSecondHighLabel = null;
            _frameTimeLastSecondAverageLabel = null;
            _fpsLabel = null;
            _panelBody = null;
            _boundRoot = null;
        }

        bool HasFocusedTextInput() {
            Focusable focusedElement = _root?.panel?.focusController?.focusedElement;
            if (focusedElement is TextField || focusedElement is FloatField) {
                return true;
            }

            return focusedElement is VisualElement focusedVisualElement
                && focusedVisualElement.name == TextField.textInputUssName;
        }

        [CreateProperty]
        GiMethod Gi {
            get => GiMethodSelector.Get();
            set {
                GiMethodSelector.Set(value);
                RefreshUi(true);
            }
        }

        [CreateProperty]
        int SamplesPerFrame {
            get {
                BufferGiUpdater gi = BufferGiUpdater.Instance;
                return gi != null ? gi.SamplesPerFrame : 1;
            }
            set {
                BufferGiUpdater gi = BufferGiUpdater.Instance;
                if (gi == null) {
                    return;
                }

                gi.SamplesPerFrame = value;
                RefreshUi(true);
            }
        }

        [CreateProperty]
        ShadowUiMode ShadowMode {
            get {
                BufferGiUpdater gi = BufferGiUpdater.Instance;
                if (gi == null) {
                    return ShadowUiMode.Off;
                }

                return gi.FineShadow switch {
                    BufferGiUpdater.ShadowMode.Baked => ShadowUiMode.Baked,
                    BufferGiUpdater.ShadowMode.Sdf => ShadowUiMode.SDF,
                    BufferGiUpdater.ShadowMode.OcclusionField => ShadowUiMode.OcclusionField,
                    BufferGiUpdater.ShadowMode.Bitmask => ShadowUiMode.Bitmask,
                    _ => ShadowUiMode.Off,
                };
            }
            set {
                BufferGiUpdater gi = BufferGiUpdater.Instance;
                if (gi == null) {
                    return;
                }

                // The buffer-GI fine field resolves every shadow source now, so the UI drives FineShadow
                // directly (no separate keyword-based shadow selector).
                gi.FineShadow = value switch {
                    ShadowUiMode.Baked => BufferGiUpdater.ShadowMode.Baked,
                    ShadowUiMode.SDF => BufferGiUpdater.ShadowMode.Sdf,
                    ShadowUiMode.OcclusionField => BufferGiUpdater.ShadowMode.OcclusionField,
                    ShadowUiMode.Bitmask => BufferGiUpdater.ShadowMode.Bitmask,
                    _ => BufferGiUpdater.ShadowMode.Off,
                };

                RefreshUi(true);
            }
        }

        [CreateProperty]
        float ConfidenceCurve {
            get {
                BufferGiUpdater gi = BufferGiUpdater.Instance;
                return gi != null ? gi.ConfidenceCurve : 3f;
            }
            set {
                BufferGiUpdater gi = BufferGiUpdater.Instance;
                if (gi == null) {
                    return;
                }

                gi.ConfidenceCurve = value;
                RefreshUi(true);
            }
        }

        [CreateProperty]
        float AoStrength {
            get {
                BufferGiUpdater gi = BufferGiUpdater.Instance;
                return gi != null ? gi.AoStrength : 0f;
            }
            set {
                BufferGiUpdater gi = BufferGiUpdater.Instance;
                if (gi == null) {
                    return;
                }

                gi.AoStrength = value;
                RefreshUi(true);
            }
        }

        // Named to match the UXML data-source-path; the property TYPE is the package enum of the same
        // name, which is why every use here is fully qualified.
        [CreateProperty]
        BufferGiUpdater.RadianceDirections RadianceDirections {
            get {
                BufferGiUpdater gi = BufferGiUpdater.Instance;
                return gi != null ? gi.Directions : BufferGiUpdater.RadianceDirections.Single;
            }
            set {
                BufferGiUpdater gi = BufferGiUpdater.Instance;
                if (gi == null) {
                    return;
                }

                // Reallocates the radiance/irradiance buffers and restarts the progressive average.
                gi.Directions = value;
                RefreshUi(true);
            }
        }

        [CreateProperty]
        AutoExposure.TonemapMode Tonemap {
            get {
                BufferGiUpdater buffer = BufferGiUpdater.Instance;
                return buffer != null ? buffer.ExposureControl.Tonemap : AutoExposure.TonemapMode.Reinhard;
            }
            set {
                // The buffer GI owns the shared tonemap keyword group while it is the active GI method.
                BufferGiUpdater buffer = BufferGiUpdater.Instance;
                if (buffer != null) {
                    buffer.ExposureControl.Tonemap = value;
                }

                RefreshUi(true);
            }
        }

        // Named ...Enabled, not AutoExposure: a member called AutoExposure would shadow the package
        // type of that name and break `AutoExposure.TonemapMode` above.
        [CreateProperty]
        bool AutoExposureEnabled {
            get {
                BufferGiUpdater buffer = BufferGiUpdater.Instance;
                return buffer != null && buffer.ExposureControl.Auto;
            }
            set {
                BufferGiUpdater buffer = BufferGiUpdater.Instance;
                if (buffer != null) {
                    buffer.ExposureControl.Auto = value;
                }

                RefreshUi(true);
            }
        }

        void RefreshUi(bool notifyChanges) {
            UpdateBindingSnapshot(notifyChanges);
            RefreshFrameTimeLabels();
        }

        void ResetFrameTimeStats() {
            _frameTimeSamples.Clear();
            _frameTimeLastSecondLowText = UnavailableFrameTimeText;
            _frameTimeLastSecondHighText = UnavailableFrameTimeText;
            _frameTimeLastSecondAverageText = UnavailableFrameTimeText;
            _fpsText = UnavailableFpsText;
        }

        void UpdateFrameTimeStats() {
            float now = Time.unscaledTime;

            // Wall-clock time since the last frame - the actual displayed frame interval (includes
            // any present/vsync wait), which is what FPS reflects. Skip the first frame's 0 delta.
            float frameTimeMilliseconds = Time.unscaledDeltaTime * 1000f;
            if (frameTimeMilliseconds > 0f) {
                _frameTimeSamples.Enqueue(new FrameTimeSample(now, frameTimeMilliseconds));
            }

            // Sampling stays per-frame (that's what the min/max are for); only the TEXT is throttled,
            // and skipped outright when nothing is on screen to read it - Update runs even while the
            // panel is hidden or folded, so this was burning CPU invisibly.
            TrimFrameTimeSamples(now);
            if (now < _nextReadoutTime || !IsReadoutVisible()) {
                return;
            }

            _nextReadoutTime = now + ReadoutRefreshInterval;
            UpdateFrameTimeWindowTexts(now - OneSecondWindowDuration, out _frameTimeLastSecondLowText, out _frameTimeLastSecondHighText, out _frameTimeLastSecondAverageText, out _fpsText);
        }

        /// <summary>Whether this panel's body is collapsed. Derived from the panel itself rather than
        /// cached in a field: the header's own click handler (FoldoutHeader) folds it without going
        /// through this component, so any stored copy goes stale the first time the user clicks the
        /// header - which froze the frame-time readout and made the H hotkey need two presses.
        /// Reports folded when nothing is bound yet, so the first toggle unfolds (the UXML ships
        /// folded).</summary>
        bool IsFolded => _panelBody == null || _panelBody.resolvedStyle.display == DisplayStyle.None;

        // Fails open: with no body to consult, refresh rather than silently stop updating.
        bool IsReadoutVisible() => _root != null && _root.visible && (_panelBody == null || !IsFolded);

        void TrimFrameTimeSamples(float now) {
            float cutoffTime = now - OneSecondWindowDuration;
            while (_frameTimeSamples.Count > 0 && _frameTimeSamples.Peek().Timestamp < cutoffTime) {
                _frameTimeSamples.Dequeue();
            }
        }

        void UpdateFrameTimeWindowTexts(float cutoffTime, out string lowText, out string highText, out string averageText, out string fpsText) {
            if (!TryGetFrameTimeStats(cutoffTime, out float lowestMilliseconds, out float highestMilliseconds, out float averageMilliseconds)) {
                lowText = UnavailableFrameTimeText;
                highText = UnavailableFrameTimeText;
                averageText = UnavailableFrameTimeText;
                fpsText = UnavailableFpsText;
                return;
            }

            lowText = FormatFrameTime(lowestMilliseconds);
            highText = FormatFrameTime(highestMilliseconds);
            averageText = FormatFrameTime(averageMilliseconds);
            fpsText = FormatFps(averageMilliseconds);
        }

        bool TryGetFrameTimeStats(float cutoffTime, out float lowestMilliseconds, out float highestMilliseconds, out float averageMilliseconds) {
            lowestMilliseconds = 0f;
            highestMilliseconds = 0f;
            averageMilliseconds = 0f;

            float totalMilliseconds = 0f;
            int sampleCount = 0;
            foreach (FrameTimeSample sample in _frameTimeSamples) {
                if (sample.Timestamp < cutoffTime) {
                    continue;
                }

                if (sampleCount == 0) {
                    lowestMilliseconds = sample.Milliseconds;
                    highestMilliseconds = sample.Milliseconds;
                } else {
                    lowestMilliseconds = Mathf.Min(lowestMilliseconds, sample.Milliseconds);
                    highestMilliseconds = Mathf.Max(highestMilliseconds, sample.Milliseconds);
                }

                totalMilliseconds += sample.Milliseconds;
                sampleCount++;
            }

            if (sampleCount == 0) {
                return false;
            }

            averageMilliseconds = totalMilliseconds / sampleCount;
            return true;
        }

        void RefreshFrameTimeLabels() {
            UpdateLabelText(_frameTimeLastSecondLowLabel, _frameTimeLastSecondLowText);
            UpdateLabelText(_frameTimeLastSecondHighLabel, _frameTimeLastSecondHighText);
            UpdateLabelText(_frameTimeLastSecondAverageLabel, _frameTimeLastSecondAverageText);
            UpdateLabelText(_fpsLabel, _fpsText);
        }

        void UpdateBindingSnapshot(bool notifyChanges) {
            GiMethod giMethod = Gi;
            ShadowUiMode shadowMode = ShadowMode;
            int samplesPerFrame = SamplesPerFrame;
            float confidenceCurve = ConfidenceCurve;
            float aoStrength = AoStrength;
            BufferGiUpdater.RadianceDirections radianceDirections = RadianceDirections;
            AutoExposure.TonemapMode tonemap = Tonemap;
            bool autoExposure = AutoExposureEnabled;

            if (!_hasBindingSnapshot) {
                _lastGiMethod = giMethod;
                _lastShadowMode = shadowMode;
                _lastSamplesPerFrame = samplesPerFrame;
                _lastConfidenceCurve = confidenceCurve;
                _lastAoStrength = aoStrength;
                _lastRadianceDirections = radianceDirections;
                _lastTonemap = tonemap;
                _lastAutoExposure = autoExposure;
                _hasBindingSnapshot = true;
                return;
            }

            UpdateEnumSnapshot(ref _lastGiMethod, giMethod, notifyChanges, nameof(Gi));
            UpdateEnumSnapshot(ref _lastShadowMode, shadowMode, notifyChanges, nameof(ShadowMode));
            UpdateIntSnapshot(ref _lastSamplesPerFrame, samplesPerFrame, notifyChanges, nameof(SamplesPerFrame));
            UpdateFloatSnapshot(ref _lastConfidenceCurve, confidenceCurve, notifyChanges, nameof(ConfidenceCurve));
            UpdateFloatSnapshot(ref _lastAoStrength, aoStrength, notifyChanges, nameof(AoStrength));
            UpdateEnumSnapshot(ref _lastRadianceDirections, radianceDirections, notifyChanges, nameof(RadianceDirections));
            UpdateEnumSnapshot(ref _lastTonemap, tonemap, notifyChanges, nameof(Tonemap));
            UpdateBoolSnapshot(ref _lastAutoExposure, autoExposure, notifyChanges, nameof(AutoExposureEnabled));
        }

        void UpdateEnumSnapshot<T>(ref T currentValue, T nextValue, bool notifyChanges, string propertyName) where T : struct, System.Enum {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(currentValue, nextValue)) {
                return;
            }

            currentValue = nextValue;
            if (notifyChanges) {
                NotifyBindingChanged(propertyName);
            }
        }

        void UpdateFloatSnapshot(ref float currentValue, float nextValue, bool notifyChanges, string propertyName) {
            if (Mathf.Approximately(currentValue, nextValue)) {
                return;
            }

            currentValue = nextValue;
            if (notifyChanges) {
                NotifyBindingChanged(propertyName);
            }
        }

        void UpdateIntSnapshot(ref int currentValue, int nextValue, bool notifyChanges, string propertyName) {
            if (currentValue == nextValue) {
                return;
            }

            currentValue = nextValue;
            if (notifyChanges) {
                NotifyBindingChanged(propertyName);
            }
        }

        void UpdateBoolSnapshot(ref bool currentValue, bool nextValue, bool notifyChanges, string propertyName) {
            if (currentValue == nextValue) {
                return;
            }

            currentValue = nextValue;
            if (notifyChanges) {
                NotifyBindingChanged(propertyName);
            }
        }

        static void UpdateLabelText(Label label, string text) {
            if (label == null || label.text == text) {
                return;
            }

            label.text = text;
        }

        // Allocation-free in the steady state: the value is quantised to hundredths of a millisecond and
        // the resulting text memoised, so a value that has been shown before costs a dictionary lookup.
        // ($"{ms:0.00} ms" allocated a string AND boxed the float on every single call.)
        static string FormatFrameTime(float milliseconds) {
            int hundredths = Mathf.Clamp(Mathf.RoundToInt(milliseconds * 100f), 0, 999999);
            if (s_frameTimeTextCache.TryGetValue(hundredths, out string cached)) {
                return cached;
            }

            s_textBuilder.Clear();
            s_textBuilder.Append(hundredths / 100).Append('.');
            int fraction = hundredths % 100;
            if (fraction < 10) {
                s_textBuilder.Append('0');
            }

            string text = s_textBuilder.Append(fraction).Append(" ms").ToString();
            // Bounded: a pathological frame-time sweep can't grow this without limit.
            if (s_frameTimeTextCache.Count > 4096) {
                s_frameTimeTextCache.Clear();
            }

            s_frameTimeTextCache[hundredths] = text;
            return text;
        }

        static string FormatFps(float averageMilliseconds) {
            if (averageMilliseconds <= 0f) {
                return UnavailableFpsText;
            }

            int fps = Mathf.RoundToInt(1000f / averageMilliseconds);
            if (fps < 0 || fps > MaxCachedFps) {
                return UnavailableFpsText;
            }

            // Whole numbers, so the cache saturates after the first few seconds and never allocates again.
            string cached = s_fpsTextCache[fps];
            if (cached == null) {
                s_textBuilder.Clear();
                cached = s_textBuilder.Append(fps).Append(" fps").ToString();
                s_fpsTextCache[fps] = cached;
            }

            return cached;
        }

        void NotifyBindingChanged([CallerMemberName] string propertyName = "") {
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(propertyName));
        }
    }
}
