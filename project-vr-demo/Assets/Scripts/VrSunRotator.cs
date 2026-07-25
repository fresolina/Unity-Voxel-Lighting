using UnityEngine;
using UnityEngine.InputSystem;

namespace Lotec.Demo {
    /// <summary>
    /// Steers the sun from a VR controller: hold the left grip and push the left thumbstick - X swings the
    /// sun around the horizon, Y raises and lowers it. This is the headset counterpart to the sample
    /// LightController's CTRL+mouse, which is unreachable in VR. While the grip is held, a
    /// <see cref="SunRotationIndicator"/> floats in front of the player showing where the sun now is.
    ///
    /// It only rotates <see cref="RenderSettings.sun"/>'s transform; the GI solver watches the sun's
    /// direction itself and wakes up when it moves, so there is nothing else to notify. The sun is
    /// resolved at runtime (and re-resolved after a level swap) because it lives in the additively loaded
    /// level scene, which the bootstrap scene can't reference at edit time - same reason
    /// <see cref="PlayerSpawner"/> resolves its spawn point late.
    ///
    /// Input actions are built in code from the binding paths below rather than pulled from an
    /// .inputactions asset, so dropping this component on a GameObject is the entire setup.
    /// </summary>
    [AddComponentMenu("Lotec/Demo/VR Sun Rotator")]
    public class VrSunRotator : MonoBehaviour {
        [Tooltip("Binding that must be held before the stick steers the sun. Default: left grip.")]
        [SerializeField] string _holdBinding = "<XRController>{LeftHand}/{GripButton}";
        [Tooltip("Stick that steers the sun while the hold binding is down. Default: left thumbstick.")]
        [SerializeField] string _stickBinding = "<XRController>{LeftHand}/{Primary2DAxis}";
        [Tooltip("Degrees per second the sun swings around the horizon at full stick deflection.")]
        [SerializeField] float _yawSpeed = 90f;
        [Tooltip("Degrees per second the sun rises or falls at full stick deflection.")]
        [SerializeField] float _pitchSpeed = 45f;
        [Tooltip("Stick deflection below this is ignored, so resting-thumb drift doesn't creep the sun.")]
        [Range(0f, 0.9f)][SerializeField] float _deadzone = 0.2f;
        [Tooltip("Elevation limits in degrees: 0 puts the sun on the horizon, 90 straight overhead, " +
                 "negative below the horizon (night).")]
        [SerializeField] Vector2 _pitchRange = new Vector2(-15f, 89f);
        [Tooltip("Stop the rig's stick locomotion while the sun is being steered, so the same stick " +
                 "doesn't also walk or spin the player. Restored on release.")]
        [SerializeField] bool _suppressLocomotion = true;
        [Tooltip("Light to rotate. Empty = the loaded level's sun (RenderSettings.sun), which is also the " +
                 "light the GI reads - leave it empty unless you know the level's sun isn't assigned.")]
        [SerializeField] Light _sunOverride;
        [Tooltip("Float a sphere with an arrow pointing at the sun in front of the player while steering.")]
        [SerializeField] bool _showIndicator = true;
        [Tooltip("Indicator to drive. Empty = one is created as a child on first use, so nothing needs " +
                 "wiring; assign one to tune its size, distance or colour in the inspector.")]
        [SerializeField] SunRotationIndicator _indicator;

        // Seconds between RenderSettings.sun lookups while it's null (no level loaded yet, or one is
        // being swapped). Cheap either way, but there's no reason to ask every frame.
        const float SunSearchInterval = 0.5f;

        InputAction _holdAction;
        InputAction _stickAction;
        Light _sun;
        float _nextSunSearch;
        // Yaw/pitch are integrated here rather than read back from the light every frame: euler angles
        // round-trip badly (the clamped pitch would fight the wrap at +-180), so the light's rotation is
        // written, never read, for as long as one hold lasts.
        float _yaw;
        float _pitch;
        bool _steering;
        bool _holdingLocomotion;

        void Awake() {
            _holdAction = new InputAction("Sun Rotate Hold", InputActionType.Button, _holdBinding);
            _stickAction = new InputAction("Sun Rotate", InputActionType.Value, _stickBinding,
                expectedControlType: "Vector2");
        }

        void OnEnable() {
            _holdAction.Enable();
            _stickAction.Enable();
        }

        void OnDisable() {
            _holdAction.Disable();
            _stickAction.Disable();
            // Never leave locomotion switched off because the component was disabled mid-hold.
            StopSteering();
        }

        void OnDestroy() {
            _holdAction?.Dispose();
            _stickAction?.Dispose();
        }

        void Update() {
            Light sun = ResolveSun();
            if (sun == null || !_holdAction.IsPressed()) {
                StopSteering();
                return;
            }

            // The hold alone claims the stick (locomotion off), so releasing to centre doesn't hand a
            // frame of movement back to the move provider mid-gesture.
            if (!_steering) StartSteering(sun);

            Vector2 stick = _stickAction.ReadValue<Vector2>();
            if (stick.sqrMagnitude >= _deadzone * _deadzone) {
                _yaw += stick.x * _yawSpeed * Time.deltaTime;
                _pitch = Mathf.Clamp(_pitch + stick.y * _pitchSpeed * Time.deltaTime,
                    _pitchRange.x, _pitchRange.y);
                sun.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }

            // Every frame the hold is down, not only while the stick is pushed: the arrow appearing is
            // what tells you the gesture took, before anything has moved.
            ShowIndicator(sun);
        }

        void ShowIndicator(Light sun) {
            if (!_showIndicator) return;
            if (_indicator == null) {
                var go = new GameObject("Sun Rotation Indicator");
                go.transform.SetParent(transform, false);
                _indicator = go.AddComponent<SunRotationIndicator>();
            }

            // -forward is the direction the sun is IN; forward is where its light goes.
            _indicator.Show(-sun.transform.forward, sun.color);
        }

        // The level's sun, cached. The cache self-heals on a level swap: the old light is destroyed, so
        // the reference compares null and we look it up again.
        Light ResolveSun() {
            if (_sunOverride != null) return _sunOverride;
            if (_sun != null) return _sun;
            if (Time.unscaledTime < _nextSunSearch) return null;
            _nextSunSearch = Time.unscaledTime + SunSearchInterval;
            _sun = RenderSettings.sun;
            return _sun;
        }

        // Pick up wherever the sun currently points, so a hold never snaps it (the level's own angle, or
        // one the desktop LightController set).
        void StartSteering(Light sun) {
            _steering = true;
            Vector3 euler = sun.transform.rotation.eulerAngles;
            _pitch = Mathf.Clamp(NormalizeAngle(euler.x), _pitchRange.x, _pitchRange.y);
            _yaw = NormalizeAngle(euler.y);
            // Tracked per hold, not read back from the flag: toggling the flag mid-gesture must not leave
            // the suppressor's count unbalanced.
            if (_suppressLocomotion) {
                _holdingLocomotion = true;
                LocomotionSuppressor.Acquire();
            }
        }

        void StopSteering() {
            if (!_steering) return;
            _steering = false;
            if (_holdingLocomotion) {
                _holdingLocomotion = false;
                LocomotionSuppressor.Release();
            }
            if (_indicator != null) _indicator.Hide();
        }

        static float NormalizeAngle(float angle) => angle > 180f ? angle - 360f : angle;
    }
}
