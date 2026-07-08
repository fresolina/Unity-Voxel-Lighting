using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Unity.Profiling;
using Unity.Properties;
using UnityEngine;
using UnityEngine.UIElements;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Lotec.Lighting.Samples
{
    [RequireComponent(typeof(UIDocument))]
    public class LightControllerUi : MonoBehaviour, INotifyBindablePropertyChanged
    {
        const float OneSecondWindowDuration = 1f;
        const float TenSecondWindowDuration = 10f;
        const string UnavailableFrameTimeText = "--.-- ms";
        const string CpuTotalFrameTimeCounterName = "CPU Total Frame Time";

        struct FrameTimeSample
        {
            public readonly float Timestamp;
            public readonly float Milliseconds;

            public FrameTimeSample(float timestamp, float milliseconds)
            {
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
        readonly FrameTiming[] _frameTimings = new FrameTiming[1];
        readonly Queue<FrameTimeSample> _frameTimeSamples = new Queue<FrameTimeSample>();
        Label _frameTimeLastTenSecondsLowLabel;
        Label _frameTimeLastTenSecondsHighLabel;
        Label _frameTimeLastTenSecondsAverageLabel;
        Label _frameTimeLastSecondLowLabel;
        Label _frameTimeLastSecondHighLabel;
        Label _frameTimeLastSecondAverageLabel;
        ProfilerRecorder _frameTimingCollectionRecorder;
        bool _hasBindingSnapshot;
        string _frameTimeLastTenSecondsLowText = UnavailableFrameTimeText;
        string _frameTimeLastTenSecondsHighText = UnavailableFrameTimeText;
        string _frameTimeLastTenSecondsAverageText = UnavailableFrameTimeText;
        string _frameTimeLastSecondLowText = UnavailableFrameTimeText;
        string _frameTimeLastSecondHighText = UnavailableFrameTimeText;
        string _frameTimeLastSecondAverageText = UnavailableFrameTimeText;
        GiMethod _lastGiMethod;
        GiFieldUpdater.LightingMethod _lastLightingMethod;
        ShadowSourceMode _lastShadowMode;
        float _lastEnabledLightIntensity;
        bool _lastFlashlightEnabled;
        bool _lastCandleEnabled;
        int _lastSamplesPerFrame;

        public static bool IsTextInputFocused => s_instance != null && s_instance.HasFocusedTextInput();

        /// <summary>Fold or unfold this panel (the header bar stays visible). Driven by the H hotkey.</summary>
        public static void SetFolded(bool folded)
        {
            if (s_instance == null || s_instance._document == null || s_instance._document.rootVisualElement == null)
            {
                return;
            }

            FoldoutHeader.SetFolded(s_instance._document.rootVisualElement, folded);
        }

        public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;

#if UNITY_EDITOR
        void OnValidate() {
            EnsureLightController();
            EnsureDocument();
            EnsureDocumentAssets();
        }
#endif

        void Awake()
        {
            s_instance = this;
        }

        void Start()
        {
            EnsureLightController();
            ApplyDocumentAssets();
            _frameTimingCollectionRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, CpuTotalFrameTimeCounterName);
            BindUi();
            RefreshUi(false);
        }

        void OnEnable()
        {
            if (_document.rootVisualElement != null)
                _document.rootVisualElement.visible = true;
            ResetFrameTimeStats();
        }

        void OnDisable()
        {
            if (_document.rootVisualElement != null)
                _document.rootVisualElement.visible = false;
        }

        void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
            UnbindUi();
            DisposeRecorder(ref _frameTimingCollectionRecorder);
        }

        void Update()
        {
            UpdateFrameTimeStats();

            if (_boundRoot != _document.rootVisualElement)
            {
                ApplyDocumentAssets();
                BindUi();
            }

            RefreshUi(true);
        }

        void EnsureLightController()
        {
            if (_lightController == null)
            {
                _lightController = FindFirstObjectByType<LightController>();
            }
        }

        void EnsureDocument()
        {
            if (_document == null)
            {
                _document = GetComponent<UIDocument>();
            }

#if UNITY_EDITOR
            if (_document == null && !Application.isPlaying) {
                _document = Undo.AddComponent<UIDocument>(gameObject);
            }
#endif

            if (_document == null && Application.isPlaying)
            {
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

        void ApplyDocumentAssets()
        {
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
            if (wasChanged) {
                EditorUtility.SetDirty(_document);
            }
#endif
        }

        void BindUi()
        {
            if (_document.rootVisualElement == null)
            {
                return;
            }

            VisualElement root = _document.rootVisualElement;
            if (_boundRoot == root)
            {
                return;
            }

            UnbindUi();


            EnumField giField = root.Q<EnumField>("gi-enum");
            EnumField giMethodField = root.Q<EnumField>("lighting-method-enum");
            FloatField enabledLightIntensityField = root.Q<FloatField>("enabled-light-intensity-field");
            Toggle flashlightToggle = root.Q<Toggle>("flashlight-toggle");
            Toggle candleToggle = root.Q<Toggle>("candle-toggle");
            EnumField shadowModeField = root.Q<EnumField>("shadow-mode-enum");
            IntegerField samplesPerFrameField = root.Q<IntegerField>("samples-per-frame-field");

            if (giField == null || giMethodField == null || enabledLightIntensityField == null || flashlightToggle == null || candleToggle == null || shadowModeField == null || samplesPerFrameField == null || !TryCacheFrameTimeLabels(root))
            {
                UnbindUi();
                return;
            }

            _boundRoot = root;
            _boundRoot.dataSource = this;

            FoldoutHeader.Setup(root);

            // Apply stylesheet to panel root so it also covers dropdown popups.
            if (root.styleSheets.count > 0)
            {
                var panelRoot = root.panel.visualTree;
                var sheet = root.styleSheets[0];
                if (!panelRoot.styleSheets.Contains(sheet))
                {
                    panelRoot.styleSheets.Add(sheet);
                }
            }
        }

        bool TryCacheFrameTimeLabels(VisualElement root)
        {
            _frameTimeLastTenSecondsLowLabel = root.Q<Label>("frame-time-last-10-seconds-low-value");
            _frameTimeLastTenSecondsHighLabel = root.Q<Label>("frame-time-last-10-seconds-high-value");
            _frameTimeLastTenSecondsAverageLabel = root.Q<Label>("frame-time-last-10-seconds-average-value");
            _frameTimeLastSecondLowLabel = root.Q<Label>("frame-time-last-second-low-value");
            _frameTimeLastSecondHighLabel = root.Q<Label>("frame-time-last-second-high-value");
            _frameTimeLastSecondAverageLabel = root.Q<Label>("frame-time-last-second-average-value");

            return _frameTimeLastTenSecondsLowLabel != null
                && _frameTimeLastTenSecondsHighLabel != null
                && _frameTimeLastTenSecondsAverageLabel != null
                && _frameTimeLastSecondLowLabel != null
                && _frameTimeLastSecondHighLabel != null
                && _frameTimeLastSecondAverageLabel != null;
        }

        void UnbindUi()
        {
            if (_boundRoot != null)
            {
                _boundRoot.Q<EnumField>("gi-enum")?.ClearBindings();
                _boundRoot.Q<EnumField>("lighting-method-enum")?.ClearBindings();
                _boundRoot.Q<FloatField>("enabled-light-intensity-field")?.ClearBindings();
                _boundRoot.Q<Toggle>("flashlight-toggle")?.ClearBindings();
                _boundRoot.Q<Toggle>("candle-toggle")?.ClearBindings();
                _boundRoot.Q<EnumField>("shadow-mode-enum")?.ClearBindings();
                _boundRoot.Q<IntegerField>("samples-per-frame-field")?.ClearBindings();
                _boundRoot.dataSource = null;
            }

            _frameTimeLastTenSecondsLowLabel = null;
            _frameTimeLastTenSecondsHighLabel = null;
            _frameTimeLastTenSecondsAverageLabel = null;
            _frameTimeLastSecondLowLabel = null;
            _frameTimeLastSecondHighLabel = null;
            _frameTimeLastSecondAverageLabel = null;
            _boundRoot = null;
        }

        bool HasFocusedTextInput()
        {
            Focusable focusedElement = _document == null
                ? null
                : _document.rootVisualElement?.panel?.focusController?.focusedElement;
            return UiFocus.IsTextInput(focusedElement);
        }

        [CreateProperty]
        GiMethod Gi
        {
            get => GiMethodSelector.Get();
            set
            {
                GiMethodSelector.Set(value);
                RefreshUi(true);
            }
        }

        [CreateProperty]
        GiFieldUpdater.LightingMethod LightingMethod
        {
            get
            {
                GiFieldUpdater gi = GiFieldUpdater.Instance;
                return gi != null ? gi.GiLightingMethod : GiFieldUpdater.LightingMethod.PathTracing;
            }
            set
            {
                GiFieldUpdater gi = GiFieldUpdater.Instance;
                if (gi == null || !gi.SetLightingMethod(value))
                {
                    return;
                }

                RefreshUi(true);
            }
        }

        [CreateProperty]
        float EnabledLightIntensity
        {
            get => _lightController.EnabledLightIntensity;
            set
            {
                _lightController.SetEnabledLightIntensity(value);
                RefreshUi(true);
            }
        }

        [CreateProperty]
        bool FlashlightEnabled
        {
            get => _lightController.FlashlightEnabled;
            set
            {
                _lightController.SetFlashlightEnabled(value);
                RefreshUi(true);
            }
        }

        [CreateProperty]
        bool CandleEnabled
        {
            get => _lightController.CandleEnabled;
            set
            {
                _lightController.SetCandleEnabled(value);
                RefreshUi(true);
            }
        }

        [CreateProperty]
        int SamplesPerFrame
        {
            get
            {
                BufferGiUpdater gi = BufferGiUpdater.Instance;
                return gi != null ? gi.SamplesPerFrame : 1;
            }
            set
            {
                BufferGiUpdater gi = BufferGiUpdater.Instance;
                if (gi == null)
                {
                    return;
                }

                gi.SamplesPerFrame = value;
                RefreshUi(true);
            }
        }

        [CreateProperty]
        ShadowSourceMode ShadowMode
        {
            get => ShadowSourceSelector.Get();
            set
            {
                ShadowSourceSelector.Set(value);
                RefreshUi(true);
            }
        }

        void RefreshUi(bool notifyChanges)
        {
            UpdateBindingSnapshot(notifyChanges);
            RefreshFrameTimeLabels();
        }

        void ResetFrameTimeStats()
        {
            _frameTimeSamples.Clear();
            _frameTimeLastTenSecondsLowText = UnavailableFrameTimeText;
            _frameTimeLastTenSecondsHighText = UnavailableFrameTimeText;
            _frameTimeLastTenSecondsAverageText = UnavailableFrameTimeText;
            _frameTimeLastSecondLowText = UnavailableFrameTimeText;
            _frameTimeLastSecondHighText = UnavailableFrameTimeText;
            _frameTimeLastSecondAverageText = UnavailableFrameTimeText;
        }

        void UpdateFrameTimeStats()
        {
            float now = Time.unscaledTime;

            if (TryGetMeasuredFrameTimeMilliseconds(out float frameTimeMilliseconds))
            {
                _frameTimeSamples.Enqueue(new FrameTimeSample(now, frameTimeMilliseconds));
            }

            TrimFrameTimeSamples(now);
            UpdateFrameTimeWindowTexts(now - OneSecondWindowDuration, out _frameTimeLastSecondLowText, out _frameTimeLastSecondHighText, out _frameTimeLastSecondAverageText);
            UpdateFrameTimeWindowTexts(now - TenSecondWindowDuration, out _frameTimeLastTenSecondsLowText, out _frameTimeLastTenSecondsHighText, out _frameTimeLastTenSecondsAverageText);
        }

        bool TryGetMeasuredFrameTimeMilliseconds(out float frameTimeMilliseconds)
        {
            frameTimeMilliseconds = 0f;

            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings((uint)_frameTimings.Length, _frameTimings) == 0)
            {
                return false;
            }

            FrameTiming frameTiming = _frameTimings[0];
            double workTimeMilliseconds = frameTiming.cpuFrameTime - frameTiming.cpuMainThreadPresentWaitTime;
            if (workTimeMilliseconds <= 0d)
            {
                return false;
            }

            frameTimeMilliseconds = (float)workTimeMilliseconds;
            return true;
        }

        void TrimFrameTimeSamples(float now)
        {
            float cutoffTime = now - TenSecondWindowDuration;
            while (_frameTimeSamples.Count > 0 && _frameTimeSamples.Peek().Timestamp < cutoffTime)
            {
                _frameTimeSamples.Dequeue();
            }
        }

        void UpdateFrameTimeWindowTexts(float cutoffTime, out string lowText, out string highText, out string averageText)
        {
            if (!TryGetFrameTimeStats(cutoffTime, out float lowestMilliseconds, out float highestMilliseconds, out float averageMilliseconds))
            {
                lowText = UnavailableFrameTimeText;
                highText = UnavailableFrameTimeText;
                averageText = UnavailableFrameTimeText;
                return;
            }

            lowText = FormatFrameTime(lowestMilliseconds);
            highText = FormatFrameTime(highestMilliseconds);
            averageText = FormatFrameTime(averageMilliseconds);
        }

        bool TryGetFrameTimeStats(float cutoffTime, out float lowestMilliseconds, out float highestMilliseconds, out float averageMilliseconds)
        {
            lowestMilliseconds = 0f;
            highestMilliseconds = 0f;
            averageMilliseconds = 0f;

            float totalMilliseconds = 0f;
            int sampleCount = 0;
            foreach (FrameTimeSample sample in _frameTimeSamples)
            {
                if (sample.Timestamp < cutoffTime)
                {
                    continue;
                }

                if (sampleCount == 0)
                {
                    lowestMilliseconds = sample.Milliseconds;
                    highestMilliseconds = sample.Milliseconds;
                }
                else
                {
                    lowestMilliseconds = Mathf.Min(lowestMilliseconds, sample.Milliseconds);
                    highestMilliseconds = Mathf.Max(highestMilliseconds, sample.Milliseconds);
                }

                totalMilliseconds += sample.Milliseconds;
                sampleCount++;
            }

            if (sampleCount == 0)
            {
                return false;
            }

            averageMilliseconds = totalMilliseconds / sampleCount;
            return true;
        }

        void RefreshFrameTimeLabels()
        {
            UpdateLabelText(_frameTimeLastTenSecondsLowLabel, _frameTimeLastTenSecondsLowText);
            UpdateLabelText(_frameTimeLastTenSecondsHighLabel, _frameTimeLastTenSecondsHighText);
            UpdateLabelText(_frameTimeLastTenSecondsAverageLabel, _frameTimeLastTenSecondsAverageText);
            UpdateLabelText(_frameTimeLastSecondLowLabel, _frameTimeLastSecondLowText);
            UpdateLabelText(_frameTimeLastSecondHighLabel, _frameTimeLastSecondHighText);
            UpdateLabelText(_frameTimeLastSecondAverageLabel, _frameTimeLastSecondAverageText);
        }

        void UpdateBindingSnapshot(bool notifyChanges)
        {
            GiMethod giMethod = Gi;
            GiFieldUpdater.LightingMethod lightingMethod = LightingMethod;
            float enabledLightIntensity = EnabledLightIntensity;
            bool flashlightEnabled = FlashlightEnabled;
            bool candleEnabled = CandleEnabled;
            ShadowSourceMode shadowMode = ShadowMode;
            int samplesPerFrame = SamplesPerFrame;

            if (!_hasBindingSnapshot)
            {
                _lastGiMethod = giMethod;
                _lastLightingMethod = lightingMethod;
                _lastEnabledLightIntensity = enabledLightIntensity;
                _lastFlashlightEnabled = flashlightEnabled;
                _lastCandleEnabled = candleEnabled;
                _lastShadowMode = shadowMode;
                _lastSamplesPerFrame = samplesPerFrame;
                _hasBindingSnapshot = true;
                return;
            }

            UpdateEnumSnapshot(ref _lastGiMethod, giMethod, notifyChanges, nameof(Gi));
            UpdateEnumSnapshot(ref _lastLightingMethod, lightingMethod, notifyChanges, nameof(LightingMethod));
            UpdateFloatSnapshot(ref _lastEnabledLightIntensity, enabledLightIntensity, notifyChanges, nameof(EnabledLightIntensity));
            UpdateBoolSnapshot(ref _lastFlashlightEnabled, flashlightEnabled, notifyChanges, nameof(FlashlightEnabled));
            UpdateBoolSnapshot(ref _lastCandleEnabled, candleEnabled, notifyChanges, nameof(CandleEnabled));
            UpdateEnumSnapshot(ref _lastShadowMode, shadowMode, notifyChanges, nameof(ShadowMode));
            UpdateIntSnapshot(ref _lastSamplesPerFrame, samplesPerFrame, notifyChanges, nameof(SamplesPerFrame));
        }

        void UpdateEnumSnapshot<T>(ref T currentValue, T nextValue, bool notifyChanges, string propertyName) where T : struct, System.Enum
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(currentValue, nextValue))
            {
                return;
            }

            currentValue = nextValue;
            if (notifyChanges)
            {
                NotifyBindingChanged(propertyName);
            }
        }

        void UpdateFloatSnapshot(ref float currentValue, float nextValue, bool notifyChanges, string propertyName)
        {
            if (Mathf.Approximately(currentValue, nextValue))
            {
                return;
            }

            currentValue = nextValue;
            if (notifyChanges)
            {
                NotifyBindingChanged(propertyName);
            }
        }

        void UpdateBoolSnapshot(ref bool currentValue, bool nextValue, bool notifyChanges, string propertyName)
        {
            if (currentValue == nextValue)
            {
                return;
            }

            currentValue = nextValue;
            if (notifyChanges)
            {
                NotifyBindingChanged(propertyName);
            }
        }

        void UpdateIntSnapshot(ref int currentValue, int nextValue, bool notifyChanges, string propertyName)
        {
            if (currentValue == nextValue)
            {
                return;
            }

            currentValue = nextValue;
            if (notifyChanges)
            {
                NotifyBindingChanged(propertyName);
            }
        }

        static void UpdateLabelText(Label label, string text)
        {
            if (label == null || label.text == text)
            {
                return;
            }

            label.text = text;
        }

        static string FormatFrameTime(float milliseconds)
        {
            return $"{milliseconds:0.00} ms";
        }

        static void DisposeRecorder(ref ProfilerRecorder recorder)
        {
            if (!recorder.Valid)
            {
                return;
            }

            recorder.Dispose();
            recorder = default;
        }

        void NotifyBindingChanged([CallerMemberName] string propertyName = "")
        {
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(propertyName));
        }
    }
}
