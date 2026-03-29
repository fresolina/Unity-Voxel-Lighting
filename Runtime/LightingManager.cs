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
        public static LightingManager Instance { get; private set; }

        [Header("Source")]
        [SerializeField] LightingVolume _volume;
        [SerializeField] GiFieldUpdater _giUpdater;
        SdfShaderGlobals _sdfShaderGlobals; // TODO: Merge SdfShaderGlobals into this class.

        public LightingVolume Volume => _volume;
        public GiFieldUpdater GiUpdater => _giUpdater;

        void Awake() {
            Instance = this;
            Debug.Log($"LightingManager Awake: Instance set. Volume assigned? {_volume != null} ({_volume?.gameObject?.name ?? "null"})", this);
            EnsureFieldsAssigned();
        }

        void OnEnable() {
            EnsureFieldsAssigned();
        }

        // Update is called once per frame
        void Update() {
            EnsureFieldsAssigned();
        }

        void EnsureFieldsAssigned() {
            _sdfShaderGlobals = GetComponent<SdfShaderGlobals>();
            if (_sdfShaderGlobals != null) {
                _sdfShaderGlobals.Volume = _volume;
            }

            if (_giUpdater == null) {
                _giUpdater = GetComponent<GiFieldUpdater>();
                if (_giUpdater == null) return;
            }

            if (_giUpdater.Volume != Volume) {
                _giUpdater.Volume = Volume;
            }
#if UNITY_EDITOR
            if (!Application.isPlaying) {
                _giUpdater.Volume = Volume;
            }
#endif

            Debug.Log($"LightingManager.EnsureFieldsAssigned: VolumeAssigned={_volume != null}, VolumeName={_volume?.gameObject?.name ?? "null"}, materialAlbedoIntensityTexture={_volume?.materialAlbedoIntensityTexture?.name ?? "null"}, GiUpdaterPresent={_giUpdater != null}, GiUpdater.VolumeName={_giUpdater?.Volume?.gameObject?.name ?? "null"}", this);
        }
    }
}
