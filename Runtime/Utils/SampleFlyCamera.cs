using UnityEngine;
using UnityEngine.InputSystem;

namespace Lotec.Lighting.Samples {
    public class SampleFlyCamera : MonoBehaviour {
        [SerializeField] float _moveSpeed = 5f;
        [SerializeField] float _sprintMultiplier = 3f;
        [SerializeField] float _lookSensitivity = 2f;
        [SerializeField] float _pitchLimit = 85f;
        [SerializeField] bool _lookWhileRightMouseHeld = true;


        float _yaw;
        float _pitch;

        void OnEnable() {
            Vector3 eulerAngles = transform.rotation.eulerAngles;
            _yaw = eulerAngles.y;
            _pitch = NormalizePitch(eulerAngles.x);
            Debug.Log($"SampleFlyCamera: initial camera rotation: yaw={_yaw}, pitch={_pitch}");
        }

        void OnDisable() {
            UnlockCursor();
        }

        void Update() {
            bool isLooking = !_lookWhileRightMouseHeld || IsLookHeld();

            if (_lookWhileRightMouseHeld) {
                if (WasLookPressedThisFrame()) {
                    LockCursor();
                } else if (WasLookReleasedThisFrame()) {
                    UnlockCursor();
                }
            }

            if (isLooking) {
                Vector2 mouseDelta = ReadMouseDelta() * _lookSensitivity;
                float mouseX = mouseDelta.x;
                float mouseY = mouseDelta.y;

                _yaw += mouseX;
                _pitch = Mathf.Clamp(_pitch - mouseY, -_pitchLimit, _pitchLimit);

                transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }

            Vector3 input = new Vector3(
                GetHorizontalInput(),
                GetVerticalInput(),
                GetForwardInput());

            if (input.sqrMagnitude > 1f) {
                input.Normalize();
            }

            float speed = _moveSpeed;
            if (IsSprintHeld()) {
                speed *= _sprintMultiplier;
            }

            transform.position += transform.TransformDirection(input) * (speed * Time.deltaTime);
        }

        bool IsLookHeld() {
            return Mouse.current != null && Mouse.current.rightButton.isPressed;
        }

        bool WasLookPressedThisFrame() {
            return Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame;
        }

        bool WasLookReleasedThisFrame() {
            return Mouse.current != null && Mouse.current.rightButton.wasReleasedThisFrame;
        }

        Vector2 ReadMouseDelta() {
            if (Mouse.current == null) {
                return Vector2.zero;
            }

            return Mouse.current.delta.ReadValue() * 0.1f;
        }

        float GetHorizontalInput() {
            if (Keyboard.current == null) {
                return 0f;
            }

            float horizontal = 0f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) {
                horizontal -= 1f;
            }

            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) {
                horizontal += 1f;
            }

            return horizontal;
        }

        float GetForwardInput() {
            if (Keyboard.current == null) {
                return 0f;
            }

            float forward = 0f;
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) {
                forward += 1f;
            }

            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) {
                forward -= 1f;
            }

            return forward;
        }

        float GetVerticalInput() {
            if (Keyboard.current == null) {
                return 0f;
            }

            float vertical = 0f;

            if (Keyboard.current.eKey.isPressed) {
                vertical += 1f;
            }

            if (Keyboard.current.qKey.isPressed) {
                vertical -= 1f;
            }

            return vertical;
        }

        bool IsSprintHeld() {
            return Keyboard.current != null &&
                (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);
        }

        float NormalizePitch(float pitch) {
            if (pitch > 180f) {
                pitch -= 360f;
            }

            return pitch;
        }

        void LockCursor() {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        void UnlockCursor() {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
