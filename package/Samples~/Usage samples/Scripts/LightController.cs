using UnityEngine;
using UnityEngine.InputSystem;

namespace Lotec.Lighting.Samples {
    public class LightController : MonoBehaviour {
        [SerializeField] Light _flashlight;
        [SerializeField] Light _candle;
        [SerializeField] float _mouseRotationSpeed = 120f;

        public float EnabledLightIntensity => GetEnabledLightIntensity();
        public bool FlashlightEnabled => _flashlight.enabled;
        public bool CandleEnabled => _candle.enabled;

        Light _sunLight;
        Keyboard _keyboard;
        Mouse _mouse;
        float _xRotation;
        float _yRotation;

        void Awake() {
            InputSystem.onDeviceChange += HandleDeviceChange;
            RefreshInputDevices();
        }

        void OnValidate() {
            EnsureSerializedReferences();
        }

        void Start() {
            SyncRotationFromSunLight();
        }

        void OnDestroy() {
            InputSystem.onDeviceChange -= HandleDeviceChange;
        }

        void Update() {
            if (LightControllerUi.IsTextInputFocused) {
                return;
            }

            if (_keyboard.hKey.wasPressedThisFrame) {
                LightControllerUi.ToggleVisibility();
            }

            if (_keyboard.backquoteKey.wasPressedThisFrame) {
                ToggleLightingMethod();
            }

            if (_keyboard.fKey.wasPressedThisFrame) {
                ToggleFlashlight();
            }

            if (_keyboard.gKey.wasPressedThisFrame) {
                ToggleCandle();
            }

            if (TryReadIntensityShortcut(out float targetIntensity)) {
                SetEnabledLightIntensity(targetIntensity);
            }

            if (_sunLight == null) {
                _sunLight = RenderSettings.sun;
            }

            if (IsCtrlHeld() && _sunLight != null) {
                Vector2 mouseDelta = ReadMouseDelta();
                _xRotation += mouseDelta.x * _mouseRotationSpeed * Time.deltaTime;
                _yRotation += mouseDelta.y * _mouseRotationSpeed * Time.deltaTime;
                _sunLight.transform.rotation = Quaternion.Euler(_xRotation, _yRotation, 0f);
            }
        }

        void RefreshInputDevices() {
            _keyboard = Keyboard.current;
            _mouse = Mouse.current;
            enabled = _keyboard != null;
        }

        void HandleDeviceChange(InputDevice device, InputDeviceChange change) {
            if (device is not Keyboard && device is not Mouse) {
                return;
            }

            RefreshInputDevices();
        }

        void EnsureSerializedReferences() {
            if (_flashlight == null) {
                _flashlight = FindLight(LightType.Spot);
            }

            if (_candle == null) {
                _candle = FindLight(LightType.Point);
            }
        }

        public void ToggleLightingMethod() {
            GiFieldUpdater.Instance?.ToggleLightingMethod();
        }

        void ToggleFlashlight() {
            _flashlight.enabled = !_flashlight.enabled;
        }

        void ToggleCandle() {
            _candle.enabled = !_candle.enabled;
        }

        public void SetFlashlightEnabled(bool isEnabled) {
            _flashlight.enabled = isEnabled;
        }

        public void SetCandleEnabled(bool isEnabled) {
            _candle.enabled = isEnabled;
        }

        public void SetEnabledLightIntensity(float intensity) {
            float clampedIntensity = Mathf.Max(0f, intensity);

            if (_flashlight.enabled) {
                _flashlight.intensity = clampedIntensity;
            }

            if (_candle.enabled) {
                _candle.intensity = clampedIntensity;
            }
        }

        void SyncRotationFromSunLight() {
            if (_sunLight == null) {
                return;
            }

            Vector3 eulerAngles = _sunLight.transform.rotation.eulerAngles;
            _xRotation = NormalizeAngle(eulerAngles.x);
            _yRotation = NormalizeAngle(eulerAngles.y);
        }

        float GetEnabledLightIntensity() {
            if (_flashlight.enabled) {
                return _flashlight.intensity;
            }

            if (_candle.enabled) {
                return _candle.intensity;
            }

            return 0f;
        }

        bool TryReadIntensityShortcut(out float targetIntensity) {
            targetIntensity = 0f;
            for (int intensity = 1; intensity <= 9; intensity++) {
                Key digitKey = Key.Digit1 + (intensity - 1);
                Key numpadKey = Key.Numpad1 + (intensity - 1);
                if (_keyboard[digitKey].wasPressedThisFrame || _keyboard[numpadKey].wasPressedThisFrame) {
                    targetIntensity = intensity;
                    return true;
                }
            }

            return false;
        }

        bool IsCtrlHeld() {
            return _keyboard.leftCtrlKey.isPressed || _keyboard.rightCtrlKey.isPressed;
        }

        Vector2 ReadMouseDelta() {
            if (_mouse == null) {
                return Vector2.zero;
            }

            return _mouse.delta.ReadValue() * 0.1f;
        }

        float NormalizeAngle(float angle) {
            if (angle > 180f) {
                angle -= 360f;
            }

            return angle;
        }

        Light FindLight(LightType lightType) {
            Light[] sceneLights = FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < sceneLights.Length; i++) {
                Light candidate = sceneLights[i];
                if (candidate == null || candidate.type != lightType || candidate == _sunLight) {
                    continue;
                }

                return candidate;
            }

            return null;
        }

    }
}
