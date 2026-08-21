using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Tracks WHICH <see cref="VoxelVolume"/> is active and publishes that volume's globals each
    /// frame - nothing else. Every other feature is its own component whose enabled state turns it
    /// on/off: GI method (<see cref="BufferGiUpdater"/>), local lights
    /// (<see cref="LocalLightsPublisher"/>), shadow source (the occlusion binder components on the
    /// volume) and SDF shadow tuning (<see cref="SdfShadow"/>).
    /// </summary>
    [DisallowMultipleComponent]
    // Run before the feature components (default order 0) so they see this frame's active volume.
    [DefaultExecutionOrder(-100)]
    [ExecuteInEditMode]
    [AddComponentMenu("Lotec/Voxel Lighting/Lighting Manager")]
    public class LightingManager : MonoBehaviour {
        public static LightingManager Instance { get; private set; }

        [Tooltip("Automatically activate the registered volume closest to the main camera.")]
        [SerializeField] bool _autoSwitchToClosestVolume;
        [SerializeField] bool _updateInEditor = true;

        VoxelVolume _volume;
        /// <summary>The currently active volume: the runtime override if set, else the serialized
        /// default.</summary>
        public VoxelVolume Volume => _volume;

        /// <summary>
        /// Switch the active lighting volume at runtime. Pass null to revert to the serialized
        /// default. The feature components react to the active-volume change on their own.
        /// </summary>
        public void SetActiveVolume(VoxelVolume volume) {
            if (_volume == volume) return;
            _volume = volume;
            PublishActiveVolume();
        }

        void Awake() {
            Instance = this;
        }

        void OnEnable() {
            Instance = this;
            PublishActiveVolume();
        }

        void Update() {
            // The manager is persistent (it lives in the bootstrap scene and outlives level scenes),
            // so the active volume comes and goes with the loaded level. Forget a volume whose level
            // unloaded (destroyed and deregistered) so we re-adopt the next one below.
            if (_volume != null && !VoxelVolume.IsRegistered(_volume))
                _volume = null;
            // Adopt a registered volume when we have none, and - when enabled - keep tracking the
            // closest (for a level that holds several volumes, e.g. adjacent rooms).
            if (_autoSwitchToClosestVolume)
                SwitchToClosestVolume();
            else if (_volume == null)
                AdoptFallbackVolume();
            if (Application.isPlaying || _updateInEditor)
                PublishActiveVolume();
        }

        // The active volume owns its core globals (bounds, SDF texture); the manager just decides
        // WHICH volume publishes, so the singular globals reflect the active volume only.
        void PublishActiveVolume() {
            if (Volume != null)
                Volume.ApplyShaderGlobals();
        }

        // With auto-switching OFF nothing else picks a volume: _volume is deliberately not
        // serialized (it is a runtime override), so it starts null and stays null until someone
        // calls SetActiveVolume. A null active volume does not just disable the fine field - it
        // takes the COARSE one with it, because BufferGiUpdater resolves its BufferGiFields FROM
        // the active volume (BufferGiFields.Find searches that volume's scene). No volume means no
        // fields, so HasCoarse is false and the level-wide GI is never set up at all.
        //
        // The level's BufferGiFields ("Level Settings") decides, because it is the only thing that
        // knows which fields exist and which of them are actually baked - so a level that never
        // switches loads its bake instead of silently voxelizing at runtime. Note the COARSE field
        // is never a candidate: it is a bare MeshBounds with no VoxelVolume, so it never registers
        // and cannot be "active". It does not need to be - it is reached THROUGH whichever volume is
        // active, since that volume's scene is how the updater finds the provider at all.
        //
        // Falls back to the volume registry when a level has no provider (fine-only, runtime
        // voxelized), which is the case this manager was already written for.
        void AdoptFallbackVolume() {
            BufferGiFields fields = BufferGiFields.FindAny();
            VoxelVolume preferred = fields != null ? fields.FallbackVolume : null;
            if (preferred != null) {
                SetActiveVolume(preferred);
                return;
            }
            var all = VoxelVolume.All;
            for (int i = 0; i < all.Count; i++) {
                if (all[i] == null) continue;
                SetActiveVolume(all[i]);
                return;
            }
        }

        void SwitchToClosestVolume() {
            var all = VoxelVolume.All;
            if (all.Count == 0) {
                SetActiveVolume(null); // no level loaded - drop the stale globals source
                return;
            }
            // A single registered volume is unambiguous: adopt it regardless of camera or SDF state
            // (a BufferGI-only volume may have no baked SDF; features that need it no-op when absent).
            if (all.Count == 1) {
                SetActiveVolume(all[0]);
                return;
            }

            Camera cam = Camera.main;
            if (cam == null) {
                if (_volume == null) SetActiveVolume(all[0]);
                return;
            }

            Vector3 camPos = cam.transform.position;
            VoxelVolume closest = null;
            float closestDist = float.MaxValue;

            for (int i = 0; i < all.Count; i++) {
                VoxelVolume vol = all[i];
                if (vol == null) continue;

                float dist = vol.Bounds.SqrDistance(camPos);
                if (dist < closestDist) {
                    closestDist = dist;
                    closest = vol;
                }
            }

            if (closest != null)
                SetActiveVolume(closest);
        }
    }
}
