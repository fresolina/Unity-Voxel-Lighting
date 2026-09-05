using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Lotec.Lighting.Samples {
    /// <summary>
    /// Keyboard/mouse control of the actual scene lights: CTRL+mouse rotates the sun, F/G toggle the
    /// flashlight/candle, and digits 1-9 set the enabled light's intensity. This is input only and has
    /// no UI - the GI / <see cref="LightingManager"/> settings (and their panel) live in
    /// <see cref="LightingController"/>.
    ///
    /// The lights are resolved at RUNTIME rather than being pure serialized references. This controller
    /// lives in the bootstrap scene, and a light that belongs to a LEVEL scene (the Playground's candle)
    /// cannot be referenced from here - Unity has no cross-scene references, so the field would just
    /// serialize as null. It asks <see cref="LocalLights"/> instead. The serialized fields
    /// are therefore optional overrides for same-scene lights, and anything still missing is looked up
    /// once the level is loaded, and looked up again if that level is swapped for another.
    /// </summary>
    public class LightController : MonoBehaviour {
        [Tooltip("Optional. Leave empty to resolve at runtime from the published local lights - " +
                 "required for a light that lives in a level scene rather than this one.")]
        [SerializeField] Light _flashlight;
        [Tooltip("Optional. Leave empty to resolve at runtime from the published local lights - " +
                 "required for a light that lives in a level scene rather than this one.")]
        [SerializeField] Light _candle;
        [SerializeField] float _mouseRotationSpeed = 120f;

        // How often to retry while a light is still missing. The lookup only runs while something is
        // unresolved, but its fallback scans the scene, so it must not run every frame during the wait.
        const float ResolveRetryInterval = 0.25f;

        static readonly List<Light> s_publishedLights = new List<Light>();

        Light _sunLight;
        Keyboard _keyboard;
        Mouse _mouse;
        float _xRotation;
        float _yRotation;
        float _nextResolveTime;

        void Awake() {
            InputSystem.onDeviceChange += HandleDeviceChange;
            RefreshInputDevices();
        }

        void Start() {
            SyncRotationFromSunLight();
        }

        void OnDestroy() {
            InputSystem.onDeviceChange -= HandleDeviceChange;
        }

        void Update() {
            if (LightingController.IsTextInputFocused || BufferGiDebugUi.IsTextInputFocused) {
                return;
            }

            EnsureLightsResolved();

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

        // Fill in whichever light isn't resolved yet. A destroyed light compares equal to null, so a
        // level swap re-resolves by itself: the old candle dies with its scene and the next lookup finds
        // the new one. Costs nothing once both are found.
        void EnsureLightsResolved() {
            if (_flashlight != null && _candle != null) {
                return;
            }

            if (Time.unscaledTime < _nextResolveTime) {
                return;
            }

            _nextResolveTime = Time.unscaledTime + ResolveRetryInterval;

            // Ask the lighting system what it is actually PUBLISHING - every loaded level's
            // realtime point/spot light. That is the set these keys are meant to drive, and unlike a raw scene
            // scan it cannot pick up a baked light (a fireplace is a point light too, but it is
            // voxelized into the GI and has no runtime switch).
            LocalLights.Gather(s_publishedLights);
            if (_flashlight == null) {
                _flashlight = FirstOfType(s_publishedLights, LightType.Spot);
            }

            if (_candle == null) {
                _candle = FirstOfType(s_publishedLights, LightType.Point);
            }

            // Nothing published (a sample scene with no providers): fall back to a scene scan.
            if (_flashlight == null) {
                _flashlight = FindLight(LightType.Spot);
            }

            if (_candle == null) {
                _candle = FindLight(LightType.Point);
            }
        }

        void ToggleFlashlight() {
            if (_flashlight == null) {
                return;
            }

            _flashlight.enabled = !_flashlight.enabled;
        }

        void ToggleCandle() {
            if (_candle == null) {
                return;
            }

            _candle.enabled = !_candle.enabled;
        }

        void SetEnabledLightIntensity(float intensity) {
            float clampedIntensity = Mathf.Max(0f, intensity);

            if (_flashlight != null && _flashlight.enabled) {
                _flashlight.intensity = clampedIntensity;
            }

            if (_candle != null && _candle.enabled) {
                _candle.intensity = clampedIntensity;
            }
        }

        // The published lights include switched-OFF ones (a provider lists them whether they burn or
        // not), which is exactly right here: the candle starts disabled and G is what turns it on.
        static Light FirstOfType(List<Light> lights, LightType lightType) {
            for (int i = 0; i < lights.Count; i++) {
                if (lights[i] != null && lights[i].type == lightType) {
                    return lights[i];
                }
            }

            return null;
        }

        void SyncRotationFromSunLight() {
            if (_sunLight == null) {
                return;
            }

            Vector3 eulerAngles = _sunLight.transform.rotation.eulerAngles;
            _xRotation = NormalizeAngle(eulerAngles.x);
            _yRotation = NormalizeAngle(eulerAngles.y);
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

        // Last-resort scan, used only when no provider lists the light. Includes inactive
        // objects because the light this is looking for may well be switched off - that is the state F/G
        // exist to change. Skips the sun, which CTRL+mouse owns.
        Light FindLight(LightType lightType) {
            Light[] sceneLights = FindObjectsByType<Light>(FindObjectsInactive.Include);
            for (int i = 0; i < sceneLights.Length; i++) {
                Light candidate = sceneLights[i];
                if (candidate == null || candidate.type != lightType) {
                    continue;
                }

                return candidate;
            }

            return null;
        }

    }
}
