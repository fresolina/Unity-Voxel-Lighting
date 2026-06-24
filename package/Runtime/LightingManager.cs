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

    // Run before GiFieldUpdater (default order 0): the manager collects the local lights and
    // publishes the shared LocalLightData each frame, which GI then reads for its compute solve.
    [DefaultExecutionOrder(-100)]
    [ExecuteInEditMode]
    public class LightingManager : MonoBehaviour {
        public static LightingManager Instance { get; private set; }

        static readonly int s_sdfTex = Shader.PropertyToID("_SdfTex");
        static readonly int s_sdfBoundsMin = Shader.PropertyToID("_SdfBoundsMin");
        static readonly int s_sdfBoundsSize = Shader.PropertyToID("_SdfBoundsSize");

        /// <summary>Runtime shadow-source selector (for UI). Derived from, and applied to, the
        /// binder components on the active volume - it is not serialized; the binders are the
        /// source of truth. Selecting a source whose binder is absent falls back to SDF.</summary>
        public enum ShadowSource { SDF, BitmaskPoint, Bitmask8Tap, OcclusionField }

        [Header("Source")]
        [SerializeField] LightingVolume _volume;
        [Tooltip("Automatically activate the volume closest to the main camera.")]
        [SerializeField] bool _autoSwitchToClosestVolume;

        readonly List<LightingVolume> _registeredVolumes = new List<LightingVolume>();
        LightingVolume _activeVolume;
        readonly LocalLightData _localLights = new LocalLightData();

        [Header("Additional Lights")]
        [Tooltip("Extra runtime GI lights. The first 4 supported point lights and the first 4 supported spot lights are injected.")]
        [SerializeField] List<Light> _additionalLights = new List<Light>();

        [Header("Shadows")]
        [SerializeField] SdfShadowConfig _sdfShadow = new SdfShadowConfig();

        [Header("Ambient Occlusion")]
        [SerializeField] SdfAoConfig _sdfAo = new SdfAoConfig();

        [SerializeField] bool _updateInEditor = true;

        public SdfShadowConfig SdfShadow => _sdfShadow;

        /// <summary>The active shadow source, read from / written to the volume's binder components.</summary>
        public ShadowSource ShadowMode {
            get {
                if (HasActiveOcclusionFieldBinder()) return ShadowSource.OcclusionField;
                if (HasActiveBitmaskBinder(out VoxelOcclusionBitmask bitmask))
                    return bitmask.sampling == VoxelOcclusionBitmask.Sampling.Point ? ShadowSource.BitmaskPoint : ShadowSource.Bitmask8Tap;
                return ShadowSource.SDF;
            }
            set => SetShadowMode(value);
        }
        /// <summary>The currently active volume. Returns the runtime override if set, otherwise the serialized default.</summary>
        public LightingVolume Volume => _activeVolume != null ? _activeVolume : _volume;
        /// <summary>All registered volumes in the scene.</summary>
        public IReadOnlyList<LightingVolume> Volumes => _registeredVolumes;
        public IReadOnlyList<Light> AdditionalLights => _additionalLights;
        /// <summary>The shared packed light data, collected once per frame for both fragment
        /// direct lighting (globals) and the GI compute solve.</summary>
        public LocalLightData LocalLights => _localLights;

        /// <summary>
        /// Switch the active lighting volume at runtime. Pass null to revert to the serialized
        /// default. Each volume's own GI/binder components react to the active-volume change.
        /// </summary>
        public void SetActiveVolume(LightingVolume volume) {
            if (_activeVolume == volume) return;
            _activeVolume = volume;
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

        void Awake() {
            Instance = this;
        }

        void OnEnable() {
            ApplyShaderGlobals();
        }

        private void ApplyShaderGlobals() {
            ApplyShadowModeKeywords();
            if (Volume != null) {
                PublishVolumeCore(Volume);
                _sdfShadow.ApplyShaderGlobals(Volume.VoxelSize);
            }
            _sdfAo.ApplyShaderGlobals();

            // Collect the local lights once per frame and publish them as globals for
            // fragment-shader direct lighting (works without a GiFieldUpdater present). The
            // GI updater reuses this same packed data for the compute solve (via LocalLights).
            _localLights.Collect(_additionalLights);
            _localLights.ApplyGlobals();
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

        // Update is called once per frame
        void Update() {
            if (_autoSwitchToClosestVolume) {
                SwitchToClosestVolume();
            }
            if (Application.isPlaying || _updateInEditor) {
                ApplyShaderGlobals();
            }
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
        // the OCC_FIELD path (it publishes the field + bounds, no SDF needed at runtime).
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

        // Switch the shadow source at runtime by toggling the binder components on the active
        // volume. A source whose binder is absent (or has no baked data) falls back to SDF.
        void SetShadowMode(ShadowSource source) {
            LightingVolume volume = Volume;
            if (volume == null) return;

            bool wantOcc = source == ShadowSource.OcclusionField;
            bool wantBitmask = source == ShadowSource.BitmaskPoint || source == ShadowSource.Bitmask8Tap;

            if (volume.TryGetComponent(out VoxelOcclusionField occ))
                occ.enabled = wantOcc;
            if (volume.TryGetComponent(out VoxelOcclusionBitmask bitmask)) {
                if (wantBitmask)
                    bitmask.sampling = source == ShadowSource.BitmaskPoint
                        ? VoxelOcclusionBitmask.Sampling.Point
                        : VoxelOcclusionBitmask.Sampling.TrilinearEightTap;
                bitmask.enabled = wantBitmask;
            }
            ApplyShadowModeKeywords();
        }

        // The shadow source is selected by which binder is present + enabled on the active
        // volume (presence = intent). Sources are mutually exclusive (occlusion field wins);
        // with no shadow binder, the default SDF path is used.
        void ApplyShadowModeKeywords() {
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
                if (point) { Shader.EnableKeyword("BITMASK_POINT"); Shader.DisableKeyword("BITMASK_8TAP"); } else { Shader.DisableKeyword("BITMASK_POINT"); Shader.EnableKeyword("BITMASK_8TAP"); }
                return;
            }
            // Default: SDF.
            Shader.EnableKeyword("SDF_ONLY");
            Shader.DisableKeyword("BITMASK_POINT");
            Shader.DisableKeyword("BITMASK_8TAP");
            Shader.DisableKeyword("OCC_FIELD");
        }

    }
}
