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
    public class LightingManager : MonoBehaviour {
        public static LightingManager Instance { get; private set; }

        static readonly int s_sdfTex = Shader.PropertyToID("_SdfTex");
        static readonly int s_sdfBoundsMin = Shader.PropertyToID("_SdfBoundsMin");
        static readonly int s_sdfBoundsSize = Shader.PropertyToID("_SdfBoundsSize");

        // Keep in sync with MAX_POINT_LIGHTS and MAX_SPOT_LIGHTS in VoxelGiUpdate.compute.
        internal const int MaxPointLights = 4;
        internal const int MaxSpotLights = 4;
        public enum ShadowModeType { SDF = 0, BitmaskPoint = 1, Bitmask8Tap = 4, OcclusionField = 5 }

        [Header("Source")]
        [SerializeField] LightingVolume _volume;
        [Tooltip("Automatically activate the volume closest to the main camera.")]
        [SerializeField] bool _autoSwitchToClosestVolume;
        [SerializeField] GiFieldUpdater _giUpdater;

        readonly List<LightingVolume> _registeredVolumes = new List<LightingVolume>();
        LightingVolume _activeVolume;
        readonly LocalLightArrays _localLights = new LocalLightArrays();

        [Header("Additional Lights")]
        [Tooltip("Extra runtime GI lights. The first 4 supported point lights and the first 4 supported spot lights are injected.")]
        [SerializeField] List<Light> _additionalLights = new List<Light>();

        [Header("Shadows")]
        [SerializeField] ShadowModeType _shadowMode = ShadowModeType.SDF;
        [SerializeField] SdfShadowConfig _sdfShadow = new SdfShadowConfig();

        [Header("Ambient Occlusion")]
        [SerializeField] SdfAoConfig _sdfAo = new SdfAoConfig();

        [SerializeField] bool _updateInEditor = true;

        public ShadowModeType ShadowMode {
            get => _shadowMode;
            set {
                if (_shadowMode != value) {
                    _shadowMode = value;
                    ApplyShadowModeKeywords();
                }
            }
        }
        public SdfShadowConfig SdfShadow => _sdfShadow;
        /// <summary>The currently active volume. Returns the runtime override if set, otherwise the serialized default.</summary>
        public LightingVolume Volume => _activeVolume != null ? _activeVolume : _volume;
        public GiFieldUpdater GiUpdater => _giUpdater;
        /// <summary>All registered volumes in the scene.</summary>
        public IReadOnlyList<LightingVolume> Volumes => _registeredVolumes;
        public IReadOnlyList<Light> AdditionalLights => _additionalLights;
        public GiFieldUpdater.LightingMethod LightingMethod => _giUpdater != null ? _giUpdater.GiLightingMethod : GiFieldUpdater.LightingMethod.PathTracing;

        void OnValidate() {
            EnsureFieldsAssigned();
        }

        /// <summary>
        /// Switch the active lighting volume at runtime. Pass null to revert to the serialized default.
        /// Releases GI buffers and resets lighting history for a clean transition.
        /// </summary>
        public void SetActiveVolume(LightingVolume volume) {
            if (_activeVolume == volume) return;
            _activeVolume = volume;
            EnsureFieldsAssigned();
            ApplyShaderGlobals();
        }

        internal void RegisterVolume(LightingVolume volume) {
            if (volume != null && !_registeredVolumes.Contains(volume))
                _registeredVolumes.Add(volume);
        }

        internal void UnregisterVolume(LightingVolume volume) {
            _registeredVolumes.Remove(volume);
            if (_activeVolume == volume)
                _activeVolume = null;
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
        }

        void OnEnable() {
            EnsureFieldsAssigned();
            ApplyShaderGlobals();
        }

        private void ApplyShaderGlobals() {
            ApplyShadowModeKeywords();
            if (Volume != null) {
                PublishVolumeCore(Volume);
                _sdfShadow.ApplyShaderGlobals(Volume.VoxelSize);
            }
            _sdfAo.ApplyShaderGlobals();

            // Publish point/spot light globals for fragment-shader direct lighting here,
            // independent of GI, so direct lights work without a GiFieldUpdater present.
            _localLights.Collect(_additionalLights);
            _localLights.ApplyGlobals();
            ApplyGiKeyword();
        }

        // Publish the volume's core shader globals: the SDF texture (when baked) and the
        // bounds. Bounds are published unconditionally because the occlusion-field/bitmask
        // paths map world->uvw with them even when no SDF is bound at runtime.
        void PublishVolumeCore(LightingVolume volume) {
            Shader.SetGlobalVector(s_sdfBoundsMin, volume.Bounds.min);
            Shader.SetGlobalVector(s_sdfBoundsSize, volume.Bounds.size);
            if (volume.sdfHiresTexture != null)
                Shader.SetGlobalTexture(s_sdfTex, volume.sdfHiresTexture);
        }

        // GI_ON when an active GI updater is driving the irradiance field; GI_OFF lets the
        // shader compile out the GI/AO path for direct-lighting-only setups.
        void ApplyGiKeyword() {
            bool giOn = _giUpdater != null && _giUpdater.isActiveAndEnabled;
            if (giOn) {
                Shader.EnableKeyword("GI_ON");
                Shader.DisableKeyword("GI_OFF");
            } else {
                Shader.DisableKeyword("GI_ON");
                Shader.EnableKeyword("GI_OFF");
            }
        }

        // Update is called once per frame
        void Update() {
            EnsureFieldsAssigned();
            if (_autoSwitchToClosestVolume) {
                SwitchToClosestVolume();
            }
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

        void SwitchToClosestVolume() {
            Camera cam = Camera.main;
            if (cam == null || _registeredVolumes.Count == 0) return;

            Vector3 camPos = cam.transform.position;
            LightingVolume closest = null;
            float closestDist = float.MaxValue;

            for (int i = 0; i < _registeredVolumes.Count; i++) {
                LightingVolume vol = _registeredVolumes[i];
                if (vol == null || vol.sdfHiresTexture == null) continue;

                float dist = vol.Bounds.SqrDistance(camPos);
                if (dist < closestDist) {
                    closestDist = dist;
                    closest = vol;
                }
            }

            if (closest != null)
                SetActiveVolume(closest);
        }

        // An enabled VoxelOcclusionField binder with baked data on the active volume drives
        // the OCC_FIELD path (it publishes the field + bounds, no SDF needed at runtime),
        // overriding the serialized ShadowMode. This is the "presence = intent" migration.
        bool HasActiveOcclusionFieldBinder() {
            return Volume != null
                && Volume.TryGetComponent(out VoxelOcclusionField binder)
                && binder.enabled
                && binder.HasData;
        }

        bool HasActiveBitmaskBinder(out VoxelOcclusionBitmask binder) {
            binder = null;
            return Volume != null
                && Volume.TryGetComponent(out binder)
                && binder.enabled
                && binder.HasData;
        }

        void ApplyShadowModeKeywords() {
            // Binders on the active volume take precedence over the serialized ShadowMode
            // (presence = intent). Shadow sources are mutually exclusive; occlusion field wins.
            if (HasActiveOcclusionFieldBinder()) {
                Shader.DisableKeyword("SDF_ONLY");
                Shader.DisableKeyword("BITMASK_POINT");
                Shader.DisableKeyword("BITMASK_8TAP");
                Shader.EnableKeyword("OCC_FIELD");
                return;
            }
            if (HasActiveBitmaskBinder(out VoxelOcclusionBitmask bitmask)) {
                Shader.DisableKeyword("SDF_ONLY");
                Shader.DisableKeyword("OCC_FIELD");
                bool point = bitmask.sampling == VoxelOcclusionBitmask.Sampling.Point;
                if (point) { Shader.EnableKeyword("BITMASK_POINT"); Shader.DisableKeyword("BITMASK_8TAP"); }
                else { Shader.DisableKeyword("BITMASK_POINT"); Shader.EnableKeyword("BITMASK_8TAP"); }
                return;
            }
            switch (_shadowMode) {
                case ShadowModeType.SDF:
                    Shader.EnableKeyword("SDF_ONLY");
                    Shader.DisableKeyword("BITMASK_POINT");
                    Shader.DisableKeyword("BITMASK_8TAP");
                    Shader.DisableKeyword("OCC_FIELD");
                    break;
                case ShadowModeType.BitmaskPoint:
                    Shader.DisableKeyword("SDF_ONLY");
                    Shader.EnableKeyword("BITMASK_POINT");
                    Shader.DisableKeyword("BITMASK_8TAP");
                    Shader.DisableKeyword("OCC_FIELD");
                    break;
                case ShadowModeType.Bitmask8Tap:
                    Shader.DisableKeyword("SDF_ONLY");
                    Shader.DisableKeyword("BITMASK_POINT");
                    Shader.EnableKeyword("BITMASK_8TAP");
                    Shader.DisableKeyword("OCC_FIELD");
                    break;
                case ShadowModeType.OcclusionField:
                    Shader.DisableKeyword("SDF_ONLY");
                    Shader.DisableKeyword("BITMASK_POINT");
                    Shader.DisableKeyword("BITMASK_8TAP");
                    Shader.EnableKeyword("OCC_FIELD");
                    break;
            }
        }

    }
}
