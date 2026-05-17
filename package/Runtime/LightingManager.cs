using System.Collections.Generic;
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
    [RequireComponent(typeof(GiFieldUpdater))]
    public class LightingManager : MonoBehaviour {
        public static LightingManager Instance { get; private set; }

        // Keep in sync with MAX_POINT_LIGHTS and MAX_SPOT_LIGHTS in VoxelGiUpdate.compute.
        internal const int MaxPointLights = 4;
        internal const int MaxSpotLights = 4;
        public enum ShadowMode { SDF = 0, BitmaskPoint = 1, Bitmask8Tap = 4, OcclusionField = 5 }

        [Header("Source")]
        [SerializeField] LightingVolume _volume;
        [SerializeField] GiFieldUpdater _giUpdater;

        [Header("Additional Lights")]
        [Tooltip("Extra runtime GI lights. The first 4 supported point lights and the first 4 supported spot lights are injected.")]
        [SerializeField] List<Light> _additionalLights = new List<Light>();

        [Header("Shadows")]
        [SerializeField] ShadowMode _shadowMode = ShadowMode.SDF;
        [SerializeField] SdfShadowConfig _sdfShadow = new SdfShadowConfig();

        [Header("Ambient Occlusion")]
        [SerializeField] SdfAoConfig _sdfAo = new SdfAoConfig();

        [SerializeField] bool _updateInEditor = true;


        public LightingVolume Volume => _volume;
        public GiFieldUpdater GiUpdater => _giUpdater;
        public IReadOnlyList<Light> AdditionalLights => _additionalLights;
        public GiFieldUpdater.LightingMethod LightingMethod => _giUpdater != null ? _giUpdater.GiLightingMethod : GiFieldUpdater.LightingMethod.PathTracing;

        void OnValidate() {
            EnsureFieldsAssigned();
        }

        public bool ToggleLightingMethod() {
            EnsureFieldsAssigned();
            if (_giUpdater == null) {
                return false;
            }

            _giUpdater.ToggleLightingMethod();
            return true;
        }

        void Awake() {
            Instance = this;
            Debug.Log($"LightingManager Awake: Instance set. Volume assigned? {_volume != null} ({_volume?.gameObject?.name ?? "null"})", this);
            EnsureFieldsAssigned();
            ApplyShaderGlobals();
        }

        void OnEnable() {
            EnsureFieldsAssigned();
            ApplyShaderGlobals();
        }

        private void ApplyShaderGlobals() {
            ApplyShadowModeKeywords();
            if (_volume != null) {
                _volume.ApplyShaderGlobals();
            }
            _sdfShadow.ApplyShaderGlobals();
            _sdfAo.ApplyShaderGlobals();
        }

        // Update is called once per frame
        void Update() {
            EnsureFieldsAssigned();
            if (Application.isPlaying || _updateInEditor) {
                ApplyShaderGlobals();
            }
        }

        void EnsureFieldsAssigned() {
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
        }

        void ApplyShadowModeKeywords() {
            switch (_shadowMode) {
                case ShadowMode.SDF:
                    Shader.EnableKeyword("SDF_ONLY");
                    Shader.DisableKeyword("BITMASK_POINT");
                    Shader.DisableKeyword("BITMASK_8TAP");
                    Shader.DisableKeyword("OCC_FIELD");
                    break;
                case ShadowMode.BitmaskPoint:
                    Shader.DisableKeyword("SDF_ONLY");
                    Shader.EnableKeyword("BITMASK_POINT");
                    Shader.DisableKeyword("BITMASK_8TAP");
                    Shader.DisableKeyword("OCC_FIELD");
                    break;
                case ShadowMode.Bitmask8Tap:
                    Shader.DisableKeyword("SDF_ONLY");
                    Shader.DisableKeyword("BITMASK_POINT");
                    Shader.EnableKeyword("BITMASK_8TAP");
                    Shader.DisableKeyword("OCC_FIELD");
                    break;
                case ShadowMode.OcclusionField:
                    Shader.DisableKeyword("SDF_ONLY");
                    Shader.DisableKeyword("BITMASK_POINT");
                    Shader.DisableKeyword("BITMASK_8TAP");
                    Shader.EnableKeyword("OCC_FIELD");
                    break;
            }
        }

    }
}
