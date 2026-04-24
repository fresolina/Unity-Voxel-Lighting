using System;
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
        static LightControllerUi s_instance;

        [SerializeField] LightController _lightController;
        [SerializeField] UIDocument _document;
        [SerializeField] PanelSettings _panelSettings;
        [SerializeField] VisualTreeAsset _visualTreeAsset;
        [SerializeField] int _sortingOrder = 1000;

        VisualElement _boundRoot;
        bool _hasBindingSnapshot;
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
            BindUi();
            RefreshUi(false);
        }

        void OnDisable() {
            if (s_instance == this) {
                s_instance = null;
            }

            UnbindUi();
            _hasBindingSnapshot = false;
        }

        void Update() {
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

            if (giMethodField == null || enabledLightIntensityField == null || flashlightToggle == null || candleToggle == null) {
                UnbindUi();
                return;
            }

            _boundRoot = root;
            _boundRoot.dataSource = this;
        }

        void UnbindUi() {
            if (_boundRoot != null) {
                _boundRoot.Q<EnumField>("lighting-method-enum")?.ClearBindings();
                _boundRoot.Q<FloatField>("enabled-light-intensity-field")?.ClearBindings();
                _boundRoot.Q<Toggle>("flashlight-toggle")?.ClearBindings();
                _boundRoot.Q<Toggle>("candle-toggle")?.ClearBindings();
                _boundRoot.dataSource = null;
            }

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

        void NotifyBindingChanged([CallerMemberName] string propertyName = "") {
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(propertyName));
        }
    }
}
