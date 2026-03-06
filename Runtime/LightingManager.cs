using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Lotec.Lighting {
    public static class TextureExtensions {
        public static Vector3 GetResolution(this Texture3D t) {
            if (t == null) return Vector3.zero;
            return new Vector3(t.width, t.height, t.depth);
        }
        public static Vector3Int GetResolutionInt(this Texture3D t) {
            if (t == null) return Vector3Int.zero;
            return new Vector3Int(t.width, t.height, t.depth);
        }
    }

    [ExecuteInEditMode]
    [RequireComponent(typeof(SdfShaderGlobals))]
    [RequireComponent(typeof(GiFieldUpdater))]
    public class LightingManager : MonoBehaviour {
        [SerializeField] GiFieldUpdater _giUpdater;
        SdfShaderGlobals _sdfShaderGlobals; // TODO: Merge SdfShaderGlobals into this class.

        public LightingVolume Volume => _sdfShaderGlobals.volume;
        public GiFieldUpdater GiUpdater => _giUpdater;

#if UNITY_EDITOR
        void Reset() {
            _giUpdater = GetComponent<GiFieldUpdater>();
        }
#endif

        void Awake() {
            _sdfShaderGlobals = GetComponent<SdfShaderGlobals>();
        }

        // Update is called once per frame
        void Update() {
            EnsureFieldsAssigned();
        }

        void EnsureFieldsAssigned() {
            if (_giUpdater == null) {
                _giUpdater = GetComponent<GiFieldUpdater>();
                if (_giUpdater == null) return;
            }

            if (_sdfShaderGlobals == null) {
                _sdfShaderGlobals = GetComponent<SdfShaderGlobals>();
            }
            if (_giUpdater.Volume == null) {
                _giUpdater.Volume = Volume;
            }
        }
    }
}
