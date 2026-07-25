using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Inputs;

namespace Lotec.Demo {
    /// <summary>
    /// The bootstrap scene's spot and point lights, driven from the controllers: <b>A/X</b> toggles the
    /// spot light, <b>B/Y</b> the point light, and <b>trigger + stick up/down</b> sets the intensity of
    /// whichever light is lit (1-9, the same range the desktop LightController's digit shortcuts use).
    /// The VR counterpart of its F / G / 1-9 keys.
    ///
    /// Intensity lives on the trigger rather than on the toggle buttons so the two never fight: a press
    /// is only ever a toggle, and the dimmer only ever runs on a light you can already see. With both
    /// lights on, the trigger adjusts the one switched on last.
    ///
    /// Turning a light on re-parents the container they share ("Player relative") onto the controller that
    /// did it, so both lights ride that hand like a held torch. Last press wins: the two lights share one
    /// container, so it can only be on one hand at a time. It stays on that hand afterwards - there is
    /// nothing to see once the lights are off, so nothing is gained by putting it back. (The rig
    /// deactivates a controller it stops tracking, which takes the lights with it until it comes back;
    /// the press itself proves the hand was live, so in practice that's a controller going to sleep.)
    ///
    /// Nothing is bound to a specific hand: A/X, B/Y and either trigger all work, and the device that
    /// fired decides which hand gets the lights and whose stick is read.
    /// </summary>
    [AddComponentMenu("Lotec/Demo/VR Hand Lights")]
    public class VrHandLights : MonoBehaviour {
        [Tooltip("Transform holding the lights ('Player relative' in the bootstrap scene). Re-parented to " +
                 "whichever controller switches a light on.")]
        [SerializeField] Transform _lightContainer;
        [Tooltip("Spot light, toggled by A/X. Empty = the first spot light under the container.")]
        [SerializeField] Light _spotLight;
        [Tooltip("Point light, toggled by B/Y. Empty = the first point light under the container.")]
        [SerializeField] Light _pointLight;

        [Header("Input")]
        [Tooltip("Toggles the spot light. Default: A/X (either controller's primary button).")]
        [SerializeField] string _spotBinding = "<XRController>/{PrimaryButton}";
        [Tooltip("Toggles the point light. Default: B/Y (either controller's secondary button).")]
        [SerializeField] string _pointBinding = "<XRController>/{SecondaryButton}";
        [Tooltip("Held to turn the stick into an intensity dimmer. Default: either trigger.")]
        [SerializeField] string _triggerBinding = "<XRController>/{TriggerButton}";
        [Tooltip("Stick read while a LEFT-hand button or trigger is held.")]
        [SerializeField] string _leftStickBinding = "<XRController>{LeftHand}/{Primary2DAxis}";
        [Tooltip("Stick read while a RIGHT-hand button or trigger is held.")]
        [SerializeField] string _rightStickBinding = "<XRController>{RightHand}/{Primary2DAxis}";

        [Header("Intensity")]
        [Tooltip("Intensity limits. Matches the desktop LightController's 1-9 digit shortcuts.")]
        [SerializeField] Vector2 _intensityRange = new Vector2(1f, 9f);
        [Tooltip("Intensity units per second at full stick deflection: the whole 1-9 range in ~2s.")]
        [SerializeField] float _intensitySpeed = 4f;
        [Tooltip("Stick deflection below this doesn't count, so a trigger pull with a thumb resting on " +
                 "the stick doesn't drift the intensity.")]
        [Range(0f, 0.9f)][SerializeField] float _deadzone = 0.3f;
        [Tooltip("Stop the rig's stick locomotion while the dimmer is actually running, so the intensity " +
                 "push doesn't also walk or spin the player. Restored when the trigger is released.")]
        [SerializeField] bool _suppressLocomotion = true;

        [Header("Hands")]
        [Tooltip("Left controller transform. Empty = the rig's XRInputModalityManager tells us.")]
        [SerializeField] Transform _leftHand;
        [Tooltip("Right controller transform. Empty = the rig's XRInputModalityManager tells us.")]
        [SerializeField] Transform _rightHand;

        InputAction _spotToggle;
        InputAction _pointToggle;
        InputAction _trigger;
        InputAction _leftStick;
        InputAction _rightStick;
        // The light the dimmer aims at: the one switched on most recently. Cleared when it goes off, so
        // the trigger never adjusts something invisible.
        Light _target;
        bool _dimming;

        void Awake() {
            _spotToggle = new InputAction("Spot Light Toggle", InputActionType.Button, _spotBinding);
            _pointToggle = new InputAction("Point Light Toggle", InputActionType.Button, _pointBinding);
            _trigger = new InputAction("Light Intensity Hold", InputActionType.Button, _triggerBinding);
            _leftStick = new InputAction("Left Stick", InputActionType.Value, _leftStickBinding,
                expectedControlType: "Vector2");
            _rightStick = new InputAction("Right Stick", InputActionType.Value, _rightStickBinding,
                expectedControlType: "Vector2");
            ResolveLights();
        }

        void OnEnable() {
            _spotToggle.Enable();
            _pointToggle.Enable();
            _trigger.Enable();
            _leftStick.Enable();
            _rightStick.Enable();
        }

        void OnDisable() {
            _spotToggle.Disable();
            _pointToggle.Disable();
            _trigger.Disable();
            _leftStick.Disable();
            _rightStick.Disable();
            // Don't leave locomotion switched off because the component was disabled mid-gesture.
            StopDimming();
        }

        void OnDestroy() {
            _spotToggle?.Dispose();
            _pointToggle?.Dispose();
            _trigger?.Dispose();
            _leftStick?.Dispose();
            _rightStick?.Dispose();
        }

        void OnValidate() {
            ResolveLights();
        }

        void Update() {
            // activeControl is the control that actually fired, so this is the hand that pressed even
            // though the bindings cover both.
            if (_spotToggle.WasPressedThisFrame()) Toggle(_spotLight, _spotToggle.activeControl?.device);
            if (_pointToggle.WasPressedThisFrame()) Toggle(_pointLight, _pointToggle.activeControl?.device);

            UpdateDimmer();
        }

        void UpdateDimmer() {
            if (!_trigger.IsPressed()) {
                StopDimming();
                return;
            }

            Light target = ResolveTarget();
            if (target == null) return; // nothing lit - the trigger is just a trigger

            float push = ReadStickY(_trigger.activeControl?.device);
            if (Mathf.Abs(push) < _deadzone) return;

            // Claimed only once the stick is actually pushed: a plain trigger pull (grabbing, UI) must
            // not cost the player their locomotion.
            if (!_dimming && _suppressLocomotion) {
                _dimming = true;
                LocomotionSuppressor.Acquire();
            }

            target.intensity = Mathf.Clamp(target.intensity + push * _intensitySpeed * Time.deltaTime,
                _intensityRange.x, _intensityRange.y);
        }

        void StopDimming() {
            if (!_dimming) return;
            _dimming = false;
            LocomotionSuppressor.Release();
        }

        void Toggle(Light light, InputDevice hand) {
            if (light == null) return;

            if (light.enabled) {
                light.enabled = false;
                if (_target == light) _target = null;
                return;
            }

            light.enabled = true;
            // Clamped on the way in so the first stick push continues from a value inside the range
            // instead of jumping.
            light.intensity = Mathf.Clamp(light.intensity, _intensityRange.x, _intensityRange.y);
            _target = light;
            AttachContainer(hand);
        }

        // The dimmer's target: the light switched on last, or - for a light that was already on when the
        // scene loaded - whichever is lit.
        Light ResolveTarget() {
            if (_target != null && _target.enabled) return _target;
            if (_spotLight != null && _spotLight.enabled) return _spotLight;
            if (_pointLight != null && _pointLight.enabled) return _pointLight;
            return null;
        }

        // Move the lights onto the hand that switched them on, zeroing the local pose so the spot light
        // (which points down the container's +Z) shines wherever the controller does.
        void AttachContainer(InputDevice hand) {
            if (_lightContainer == null) return;
            Transform target = ResolveHand(hand);
            if (target == null || _lightContainer.parent == target) return;
            _lightContainer.SetParent(target, false);
            _lightContainer.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        }

        float ReadStickY(InputDevice hand) {
            if (hand == null) return 0f;
            InputAction stick = IsLeftHand(hand) ? _leftStick : _rightStick;
            return stick.ReadValue<Vector2>().y;
        }

        Transform ResolveHand(InputDevice hand) {
            bool left = IsLeftHand(hand);
            if (left && _leftHand != null) return _leftHand;
            if (!left && _rightHand != null) return _rightHand;

            // The rig's own answer to "where are the hands": the modality manager already tracks the
            // controller GameObjects it swaps between controllers and tracked hands.
            var modality = FindAnyObjectByType<XRInputModalityManager>();
            if (modality == null) return null;
            GameObject controller = left ? modality.leftController : modality.rightController;
            if (controller == null) return null;

            if (left) _leftHand = controller.transform;
            else _rightHand = controller.transform;
            return controller.transform;
        }

        static bool IsLeftHand(InputDevice device) {
            if (device == null) return false;
            foreach (var usage in device.usages) {
                if (usage == CommonUsages.LeftHand) return true;
            }

            return false;
        }

        // Both lights live under the container, so they don't need wiring by hand.
        void ResolveLights() {
            if (_lightContainer == null) return;
            if (_spotLight == null) _spotLight = FindLight(LightType.Spot);
            if (_pointLight == null) _pointLight = FindLight(LightType.Point);
        }

        Light FindLight(LightType type) {
            Light[] lights = _lightContainer.GetComponentsInChildren<Light>(true);
            for (int i = 0; i < lights.Length; i++) {
                if (lights[i].type == type) return lights[i];
            }

            return null;
        }
    }
}
