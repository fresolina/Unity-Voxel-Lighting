using UnityEngine;
using UnityEngine.InputSystem;

namespace Lotec.Lighting.Samples {
    public class LightController : MonoBehaviour {
        [SerializeField] Light _targetLight;
        [SerializeField] float _mouseRotationSpeed = 120f;

        float _xRotation;
        float _yRotation;

        void OnValidate() {
            if (_targetLight == null) {
                _targetLight = RenderSettings.sun;
            }
        }

        void OnEnable() {
            if (_targetLight == null) {
                return;
            }

            Vector3 eulerAngles = _targetLight.transform.rotation.eulerAngles;
            _xRotation = NormalizeAngle(eulerAngles.x);
            _yRotation = NormalizeAngle(eulerAngles.y);
        }

        void Update() {
            if (_targetLight == null) {
                return;
            }

            if (IsCtrlHeld()) {
                Vector2 mouseDelta = ReadMouseDelta();
                _xRotation += mouseDelta.x * _mouseRotationSpeed * Time.deltaTime;
                _yRotation += mouseDelta.y * _mouseRotationSpeed * Time.deltaTime;
            }

            _targetLight.transform.rotation = Quaternion.Euler(_xRotation, _yRotation, 0f);
        }

        bool IsCtrlHeld() {
            return Keyboard.current != null &&
                (Keyboard.current.leftCtrlKey.isPressed || Keyboard.current.rightCtrlKey.isPressed);
        }

        Vector2 ReadMouseDelta() {
            if (Mouse.current == null) {
                return Vector2.zero;
            }

            return Mouse.current.delta.ReadValue() * 0.1f;
        }

        float NormalizeAngle(float angle) {
            if (angle > 180f) {
                angle -= 360f;
            }

            return angle;
        }

    }
}
