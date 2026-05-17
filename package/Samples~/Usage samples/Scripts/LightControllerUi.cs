using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Unity.Properties;
using UnityEngine;
using UnityEngine.UIElements;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Lotec.Lighting.Samples {
    [RequireComponent(typeof(UIDocument))]
    public class LightControllerUi : MonoBehaviour, INotifyBindablePropertyChanged {
        const float OneSecondWindowDuration = 1f;
        const float TenSecondWindowDuration = 10f;
        const float SecondsToMilliseconds = 1000f;
        const string UnavailableFrameTimeText = "--.-- ms";

        struct FrameTimeSample {
            public readonly float Timestamp;
            public readonly float Milliseconds;

            public FrameTimeSample(float timestamp, float milliseconds) {
                Timestamp = timestamp;
                Milliseconds = milliseconds;
            }
        }

        static LightControllerUi s_instance;

        [SerializeField] LightController _lightController;
        [SerializeField] UIDocument _document;
        [SerializeField] PanelSettings _panelSettings;
        [SerializeField] VisualTreeAsset _visualTreeAsset;
        [SerializeField] int _sortingOrder = 1000;

        VisualElement _boundRoot;
        readonly Queue<FrameTimeSample> _frameTimeSamples = new Queue<FrameTimeSample>();
        Label _frameTimeLastTenSecondsLowLabel;
        Label _frameTimeLastTenSecondsHighLabel;
        Label _frameTimeLastTenSecondsAverageLabel;
        Label _frameTimeLastSecondLowLabel;
        Label _frameTimeLastSecondHighLabel;
        Label _frameTimeLastSecondAverageLabel;
        Label _frameTimeLastFrameLabel;
        bool _hasBindingSnapshot;
        string _frameTimeLastTenSecondsLowText = UnavailableFrameTimeText;
        string _frameTimeLastTenSecondsHighText = UnavailableFrameTimeText;
        string _frameTimeLastTenSecondsAverageText = UnavailableFrameTimeText;
        string _frameTimeLastSecondLowText = UnavailableFrameTimeText;
        string _frameTimeLastSecondHighText = UnavailableFrameTimeText;
        string _frameTimeLastSecondAverageText = UnavailableFrameTimeText;
        string _frameTimeLastFrameText = UnavailableFrameTimeText;
        GiFieldUpdater.LightingMethod _lastLightingMethod;
        float _lastEnabledLightIntensity;
        bool _lastFlashlightEnabled;
        bool _lastCandleEnabled;

        public static bool IsTextInputFocused => s_instance != null && s_instance.HasFocusedTextInput();

        public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;

#if UNITY_EDITOR
        void OnValidate() {
            EnsureLightController();
            EnsureDocument();
            EnsureDocumentAssets();
        }
#endif

        void OnEnable() {
            s_instance = this;
            EnsureLightController();
            EnsureDocument();
            ApplyDocumentAssets();
            ResetFrameTimeStats();
            BindUi();
            RefreshUi(false);
        }

        void OnDisable() {
            if (s_instance == this) {
                s_instance = null;
            }

            UnbindUi();
            ResetFrameTimeStats();
            _hasBindingSnapshot = false;
        }

        void Update() {
            UpdateFrameTimeStats();

            if (_boundRoot != _document.rootVisualElement) {
                EnsureDocument();
                ApplyDocumentAssets();
                BindUi();
            }

            RefreshUi(true);
        }

        void EnsureLightController() {
            if (_lightController == null) {
                _lightController = FindFirstObjectByType<LightController>();
            }
        }

        void EnsureDocument() {
            if (_document == null) {
                _document = GetComponent<UIDocument>();
            }

#if UNITY_EDITOR
            if (_document == null && !Application.isPlaying) {
                _document = Undo.AddComponent<UIDocument>(gameObject);
            }
#endif

            if (_document == null && Application.isPlaying) {
                _document = gameObject.AddComponent<UIDocument>();
            }
        }

#if UNITY_EDITOR
        void EnsureDocumentAssets() {
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

            PanelSettings panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>($"{sampleDirectory}/UI/LightControllerUiPanelSettings.asset");
            VisualTreeAsset visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"{sampleDirectory}/UI/LightControllerUi.uxml");

            bool wasChanged = false;
            if (_panelSettings != panelSettings) {
                _panelSettings = panelSettings;
                wasChanged = true;
            }

            if (_visualTreeAsset != visualTree) {
                _visualTreeAsset = visualTree;
                wasChanged = true;
            }

            ApplyDocumentAssets();

            if (wasChanged) {
                EditorUtility.SetDirty(this);
            }
        }
#endif

        void ApplyDocumentAssets() {
            bool wasChanged = false;
            if (_document.panelSettings != _panelSettings) {
                _document.panelSettings = _panelSettings;
                wasChanged = true;
            }

            if (_document.visualTreeAsset != _visualTreeAsset) {
                _document.visualTreeAsset = _visualTreeAsset;
                wasChanged = true;
            }

            if (_document.sortingOrder != _sortingOrder) {
                _document.sortingOrder = _sortingOrder;
                wasChanged = true;
            }

#if UNITY_EDITOR
            if (wasChanged) {
                EditorUtility.SetDirty(_document);
            }
#endif
        }

        void BindUi() {
            if (_document.rootVisualElement == null) {
                return;
            }

            VisualElement root = _document.rootVisualElement;
            if (_boundRoot == root) {
                return;
            }

            UnbindUi();

            EnumField giMethodField = root.Q<EnumField>("lighting-method-enum");
            FloatField enabledLightIntensityField = root.Q<FloatField>("enabled-light-intensity-field");
            Toggle flashlightToggle = root.Q<Toggle>("flashlight-toggle");
            Toggle candleToggle = root.Q<Toggle>("candle-toggle");

            if (giMethodField == null || enabledLightIntensityField == null || flashlightToggle == null || candleToggle == null || !TryCacheFrameTimeLabels(root)) {
                UnbindUi();
                return;
            }

            _boundRoot = root;
            _boundRoot.dataSource = this;
        }

        bool TryCacheFrameTimeLabels(VisualElement root) {
            _frameTimeLastTenSecondsLowLabel = root.Q<Label>("frame-time-last-10-seconds-low-value");
            _frameTimeLastTenSecondsHighLabel = root.Q<Label>("frame-time-last-10-seconds-high-value");
            _frameTimeLastTenSecondsAverageLabel = root.Q<Label>("frame-time-last-10-seconds-average-value");
            _frameTimeLastSecondLowLabel = root.Q<Label>("frame-time-last-second-low-value");
            _frameTimeLastSecondHighLabel = root.Q<Label>("frame-time-last-second-high-value");
            _frameTimeLastSecondAverageLabel = root.Q<Label>("frame-time-last-second-average-value");
            _frameTimeLastFrameLabel = root.Q<Label>("frame-time-last-frame-value");

            return _frameTimeLastTenSecondsLowLabel != null
                && _frameTimeLastTenSecondsHighLabel != null
                && _frameTimeLastTenSecondsAverageLabel != null
                && _frameTimeLastSecondLowLabel != null
                && _frameTimeLastSecondHighLabel != null
                && _frameTimeLastSecondAverageLabel != null
                && _frameTimeLastFrameLabel != null;
        }

        void UnbindUi() {
            if (_boundRoot != null) {
                _boundRoot.Q<EnumField>("lighting-method-enum")?.ClearBindings();
                _boundRoot.Q<FloatField>("enabled-light-intensity-field")?.ClearBindings();
                _boundRoot.Q<Toggle>("flashlight-toggle")?.ClearBindings();
                _boundRoot.Q<Toggle>("candle-toggle")?.ClearBindings();
                _boundRoot.dataSource = null;
            }

            _frameTimeLastTenSecondsLowLabel = null;
            _frameTimeLastTenSecondsHighLabel = null;
            _frameTimeLastTenSecondsAverageLabel = null;
            _frameTimeLastSecondLowLabel = null;
            _frameTimeLastSecondHighLabel = null;
            _frameTimeLastSecondAverageLabel = null;
            _frameTimeLastFrameLabel = null;
            _boundRoot = null;
        }

        bool HasFocusedTextInput() {
            Focusable focusedElement = _document.rootVisualElement?.panel?.focusController?.focusedElement;
            if (focusedElement is TextField || focusedElement is FloatField) {
                return true;
            }

            return focusedElement is VisualElement focusedVisualElement
                && focusedVisualElement.name == TextField.textInputUssName;
        }

        [CreateProperty]
        GiFieldUpdater.LightingMethod LightingMethod {
            get {
                LightingManager manager = LightingManager.Instance;
                if (manager == null || manager.GiUpdater == null) {
                    return GiFieldUpdater.LightingMethod.PathTracing;
                }

                return manager.LightingMethod;
            }
            set {
                LightingManager manager = LightingManager.Instance;
                if (manager == null || manager.GiUpdater == null) {
                    return;
                }

                if (!manager.GiUpdater.SetLightingMethod(value)) {
                    return;
                }

                RefreshUi(true);
            }
        }

        [CreateProperty]
        float EnabledLightIntensity {
            get => _lightController.EnabledLightIntensity;
            set {
                _lightController.SetEnabledLightIntensity(value);
                RefreshUi(true);
            }
        }

        [CreateProperty]
        bool FlashlightEnabled {
            get => _lightController.FlashlightEnabled;
            set {
                _lightController.SetFlashlightEnabled(value);
                RefreshUi(true);
            }
        }

        [CreateProperty]
        bool CandleEnabled {
            get => _lightController.CandleEnabled;
            set {
                _lightController.SetCandleEnabled(value);
                RefreshUi(true);
            }
        }

        void RefreshUi(bool notifyChanges) {
            UpdateBindingSnapshot(notifyChanges);
            RefreshFrameTimeLabels();
        }

        void ResetFrameTimeStats() {
            _frameTimeSamples.Clear();
            _frameTimeLastTenSecondsLowText = UnavailableFrameTimeText;
            _frameTimeLastTenSecondsHighText = UnavailableFrameTimeText;
            _frameTimeLastTenSecondsAverageText = UnavailableFrameTimeText;
            _frameTimeLastSecondLowText = UnavailableFrameTimeText;
            _frameTimeLastSecondHighText = UnavailableFrameTimeText;
            _frameTimeLastSecondAverageText = UnavailableFrameTimeText;
            _frameTimeLastFrameText = UnavailableFrameTimeText;
        }

        void UpdateFrameTimeStats() {
            float frameTimeMilliseconds = Time.unscaledDeltaTime * SecondsToMilliseconds;
            float now = Time.unscaledTime;

            _frameTimeSamples.Enqueue(new FrameTimeSample(now, frameTimeMilliseconds));
            TrimFrameTimeSamples(now);

            _frameTimeLastFrameText = FormatFrameTime(frameTimeMilliseconds);
            UpdateFrameTimeWindowTexts(now - OneSecondWindowDuration, out _frameTimeLastSecondLowText, out _frameTimeLastSecondHighText, out _frameTimeLastSecondAverageText);
            UpdateFrameTimeWindowTexts(now - TenSecondWindowDuration, out _frameTimeLastTenSecondsLowText, out _frameTimeLastTenSecondsHighText, out _frameTimeLastTenSecondsAverageText);
        }

        void TrimFrameTimeSamples(float now) {
            float cutoffTime = now - TenSecondWindowDuration;
            while (_frameTimeSamples.Count > 0 && _frameTimeSamples.Peek().Timestamp < cutoffTime) {
                _frameTimeSamples.Dequeue();
            }
        }

        void UpdateFrameTimeWindowTexts(float cutoffTime, out string lowText, out string highText, out string averageText) {
            if (!TryGetFrameTimeStats(cutoffTime, out float lowestMilliseconds, out float highestMilliseconds, out float averageMilliseconds)) {
                lowText = UnavailableFrameTimeText;
                highText = UnavailableFrameTimeText;
                averageText = UnavailableFrameTimeText;
                return;
            }

            lowText = FormatFrameTime(lowestMilliseconds);
            highText = FormatFrameTime(highestMilliseconds);
            averageText = FormatFrameTime(averageMilliseconds);
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
            UpdateLabelText(_frameTimeLastTenSecondsLowLabel, _frameTimeLastTenSecondsLowText);
            UpdateLabelText(_frameTimeLastTenSecondsHighLabel, _frameTimeLastTenSecondsHighText);
            UpdateLabelText(_frameTimeLastTenSecondsAverageLabel, _frameTimeLastTenSecondsAverageText);
            UpdateLabelText(_frameTimeLastSecondLowLabel, _frameTimeLastSecondLowText);
            UpdateLabelText(_frameTimeLastSecondHighLabel, _frameTimeLastSecondHighText);
            UpdateLabelText(_frameTimeLastSecondAverageLabel, _frameTimeLastSecondAverageText);
            UpdateLabelText(_frameTimeLastFrameLabel, _frameTimeLastFrameText);
        }

        void UpdateBindingSnapshot(bool notifyChanges) {
            GiFieldUpdater.LightingMethod lightingMethod = LightingMethod;
            float enabledLightIntensity = EnabledLightIntensity;
            bool flashlightEnabled = FlashlightEnabled;
            bool candleEnabled = CandleEnabled;

            if (!_hasBindingSnapshot) {
                _lastLightingMethod = lightingMethod;
                _lastEnabledLightIntensity = enabledLightIntensity;
                _lastFlashlightEnabled = flashlightEnabled;
                _lastCandleEnabled = candleEnabled;
                _hasBindingSnapshot = true;
                return;
            }

            UpdateLightingMethodSnapshot(ref _lastLightingMethod, lightingMethod, notifyChanges, nameof(LightingMethod));
            UpdateFloatSnapshot(ref _lastEnabledLightIntensity, enabledLightIntensity, notifyChanges, nameof(EnabledLightIntensity));
            UpdateBoolSnapshot(ref _lastFlashlightEnabled, flashlightEnabled, notifyChanges, nameof(FlashlightEnabled));
            UpdateBoolSnapshot(ref _lastCandleEnabled, candleEnabled, notifyChanges, nameof(CandleEnabled));
        }

        void UpdateLightingMethodSnapshot(ref GiFieldUpdater.LightingMethod currentValue, GiFieldUpdater.LightingMethod nextValue, bool notifyChanges, string propertyName) {
            if (currentValue == nextValue) {
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

        static string FormatFrameTime(float milliseconds) {
            return $"{milliseconds:0.00} ms";
        }

        void NotifyBindingChanged([CallerMemberName] string propertyName = "") {
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(propertyName));
        }
    }
}
