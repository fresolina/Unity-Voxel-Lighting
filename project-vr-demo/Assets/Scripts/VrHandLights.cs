using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Inputs;

namespace Lotec.Demo {
    /// <summary>
    /// The bootstrap scene's spot and point lights, driven from the controllers: <b>tap A/X</b> toggles the
    /// spot light, <b>tap B/Y</b> the point light, and <b>holding</b> either while pushing that hand's
    /// stick up or down sets that light's intensity (1-9, the same range the desktop LightController's
    /// digit shortcuts use). The VR counterpart of its F / G / 1-9 keys.
    ///
    /// Tap versus hold is what keeps the two gestures out of each other's way: the toggle fires on
    /// RELEASE and only if the stick was never pushed during the hold, so dimming a lit light doesn't
    /// switch it off on the way out. Pushing the stick also turns the light on if it was off - otherwise
    /// the gesture would silently adjust something invisible.
    ///
    /// Turning a light on re-parents the container they share ("Player relative") onto the controller that
    /// did it, so both lights ride that hand like a held torch. Last press wins: the two lights share one
    /// container, so it can only be on one hand at a time. It stays on that hand afterwards - there is
    /// nothing to see once the lights are off, so nothing is gained by putting it back. (The rig
    /// deactivates a controller it stops tracking, which takes the lights with it until it comes back;
    /// the press itself proves the hand was live, so in practice that's a controller going to sleep.)
    ///
    /// The buttons are bound without a hand qualifier, so A/X and B/Y both work and the pressing device
    /// decides which hand gets the lights and whose stick is read.
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
        [Tooltip("Stick read while a LEFT-hand button is held.")]
        [SerializeField] string _leftStickBinding = "<XRController>{LeftHand}/{Primary2DAxis}";
        [Tooltip("Stick read while a RIGHT-hand button is held.")]
        [SerializeField] string _rightStickBinding = "<XRController>{RightHand}/{Primary2DAxis}";

        [Header("Intensity")]
        [Tooltip("Intensity limits. Matches the desktop LightController's 1-9 digit shortcuts.")]
        [SerializeField] Vector2 _intensityRange = new Vector2(1f, 9f);
        [Tooltip("Intensity units per second at full stick deflection: the whole 1-9 range in ~2s.")]
        [SerializeField] float _intensitySpeed = 4f;
        [Tooltip("Stick deflection below this doesn't count as an intensity push, so a tap stays a tap " +
                 "even with a thumb resting on the stick.")]
        [Range(0f, 0.9f)][SerializeField] float _deadzone = 0.3f;
        [Tooltip("Stop the rig's stick locomotion while a light button is held, so the intensity push " +
                 "doesn't also walk or spin the player. Restored on release.")]
        [SerializeField] bool _suppressLocomotion = true;

        [Header("Hands")]
        [Tooltip("Left controller transform. Empty = the rig's XRInputModalityManager tells us.")]
        [SerializeField] Transform _leftHand;
        [Tooltip("Right controller transform. Empty = the rig's XRInputModalityManager tells us.")]
        [SerializeField] Transform _rightHand;

        // One button's worth of gesture state. Two instances, so the tap/hold logic is written once.
        class Gesture {
            public string name;
            public InputAction toggle;
            public Light light;
            public InputDevice hand;      // controller that started the press
            public bool pushedStick;      // stick used during this hold -> release must not toggle
            public bool holdsLocomotion;
        }

        readonly Gesture _spot = new Gesture { name = "Spot Light Toggle" };
        readonly Gesture _point = new Gesture { name = "Point Light Toggle" };
        InputAction _leftStick;
        InputAction _rightStick;

        void Awake() {
            _spot.toggle = new InputAction(_spot.name, InputActionType.Button, _spotBinding);
            _point.toggle = new InputAction(_point.name, InputActionType.Button, _pointBinding);
            _leftStick = new InputAction("Left Stick", InputActionType.Value, _leftStickBinding,
                expectedControlType: "Vector2");
            _rightStick = new InputAction("Right Stick", InputActionType.Value, _rightStickBinding,
                expectedControlType: "Vector2");
            ResolveLights();
        }

        void OnEnable() {
            _spot.toggle.Enable();
            _point.toggle.Enable();
            _leftStick.Enable();
            _rightStick.Enable();
        }

        void OnDisable() {
            _spot.toggle.Disable();
            _point.toggle.Disable();
            _leftStick.Disable();
            _rightStick.Disable();
            // Don't leave locomotion switched off because the component was disabled mid-hold.
            EndHold(_spot);
            EndHold(_point);
        }

        void OnDestroy() {
            _spot.toggle?.Dispose();
            _point.toggle?.Dispose();
            _leftStick?.Dispose();
            _rightStick?.Dispose();
        }

        void OnValidate() {
            ResolveLights();
        }

        void Update() {
            UpdateGesture(_spot);
            UpdateGesture(_point);
        }

        void UpdateGesture(Gesture gesture) {
            if (gesture.light == null) return;

            if (gesture.toggle.WasPressedThisFrame()) {
                // activeControl is the control that actually fired, so this is the hand that pressed even
                // though the binding covers both.
                gesture.hand = gesture.toggle.activeControl?.device;
                gesture.pushedStick = false;
                if (_suppressLocomotion) {
                    gesture.holdsLocomotion = true;
                    LocomotionSuppressor.Acquire();
                }
            }

            if (gesture.toggle.IsPressed()) {
                float push = ReadStickY(gesture.hand);
                if (Mathf.Abs(push) >= _deadzone) {
                    if (!gesture.pushedStick) {
                        gesture.pushedStick = true;
                        // A dimmer for an unlit light is no feedback at all, so the push implies "on".
                        if (!gesture.light.enabled) SwitchOn(gesture);
                    }

                    gesture.light.intensity = Mathf.Clamp(
                        gesture.light.intensity + push * _intensitySpeed * Time.deltaTime,
                        _intensityRange.x, _intensityRange.y);
                }
            }

            if (gesture.toggle.WasReleasedThisFrame()) {
                if (!gesture.pushedStick) {
                    if (gesture.light.enabled) gesture.light.enabled = false;
                    else SwitchOn(gesture);
                }

                EndHold(gesture);
            }
        }

        void SwitchOn(Gesture gesture) {
            gesture.light.enabled = true;
            // Clamped on the way in so the first stick push continues from a value inside the range
            // instead of jumping (the scene ships the spot light at 9 and the point light at 2).
            gesture.light.intensity =
                Mathf.Clamp(gesture.light.intensity, _intensityRange.x, _intensityRange.y);
            AttachContainer(gesture.hand);
        }

        void EndHold(Gesture gesture) {
            gesture.hand = null;
            gesture.pushedStick = false;
            if (!gesture.holdsLocomotion) return;
            gesture.holdsLocomotion = false;
            LocomotionSuppressor.Release();
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
            _spot.light = _spotLight;
            _point.light = _pointLight;
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
