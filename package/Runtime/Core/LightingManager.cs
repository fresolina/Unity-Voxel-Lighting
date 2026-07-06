using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Tracks WHICH <see cref="VoxelVolume"/> is active and publishes that volume's globals each
    /// frame - nothing else. Every other feature is its own component whose enabled state turns it
    /// on/off: GI method (<see cref="GiFieldUpdater"/> / <see cref="BufferGiUpdater"/>), local
    /// lights (<see cref="LocalLightsPublisher"/>), shadow source (the occlusion binder components
    /// on the volume), SDF shadow tuning (<see cref="SdfShadow"/>) and ambient occlusion
    /// (<see cref="SdfAmbientOcclusion"/>).
    /// </summary>
    [DisallowMultipleComponent]
    // Run before the feature components (default order 0) so they see this frame's active volume.
    [DefaultExecutionOrder(-100)]
    [ExecuteInEditMode]
    [AddComponentMenu("Lotec/Voxel Lighting/Lighting Manager")]
    public class LightingManager : MonoBehaviour {
        public static LightingManager Instance { get; private set; }

        [Tooltip("Default active volume. A runtime override (SetActiveVolume / auto-switch) takes " +
                 "precedence while set.")]
        [SerializeField] VoxelVolume _volume;
        [Tooltip("Automatically activate the registered volume closest to the main camera.")]
        [SerializeField] bool _autoSwitchToClosestVolume;
        [SerializeField] bool _updateInEditor = true;

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
            if (_autoSwitchToClosestVolume)
                SwitchToClosestVolume();
            if (Application.isPlaying || _updateInEditor)
                PublishActiveVolume();
        }

        // The active volume owns its core globals (bounds, SDF texture); the manager just decides
        // WHICH volume publishes, so the singular globals reflect the active volume only.
        void PublishActiveVolume() {
            if (Volume != null)
                Volume.ApplyShaderGlobals();
        }

        void SwitchToClosestVolume() {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 camPos = cam.transform.position;
            VoxelVolume closest = null;
            float closestDist = float.MaxValue;

            for (int i = 0; i < VoxelVolume.All.Count; i++) {
                VoxelVolume vol = VoxelVolume.All[i];
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
    }
}
