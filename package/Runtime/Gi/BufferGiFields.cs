using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lotec.Lighting {
    /// <summary>
    /// Per-level Buffer GI field inputs, kept in the level scene so the persistent
    /// <see cref="BufferGiUpdater"/> (which lives once in the bootstrap scene) can pick them up when
    /// this level's volume becomes active. Carries the coarse (scene-covering) field, the detailed fine
    /// fields to bake, and the disk-baked voxelization assets - the data that is genuinely per-level and
    /// so can't be a cross-scene serialized reference on the singleton updater. The updater discovers
    /// this from the active volume's scene (see <see cref="Find"/>), mirroring how PlayerSpawner finds a
    /// level's SceneSpawnPoint.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Lotec/Voxel Lighting/Buffer GI Fields")]
    public class BufferGiFields : MonoBehaviour {
        [Tooltip("MeshBounds covering the whole scene, used as the coarse (low-detail, far) GI " +
                 "field - a 32^3 grid over its larger bounds (bigger voxels). Should be larger " +
                 "than the active fine volume. Leave null for fine only.")]
        [SerializeField] MeshBounds _coarseField;
        [Tooltip("Fine 'detailed' fields to bake to disk (the BufferGiUpdater's 'Bake Voxelization To " +
                 "Disk' button bakes the coarse field + each of these). Each should sit on a GameObject " +
                 "that also has a VoxelVolume (its padded bounds are the runtime fine grid). Reset " +
                 "auto-fills this with every MeshBounds in the scene except the coarse one.")]
        [SerializeField] List<MeshBounds> _detailedFields = new List<MeshBounds>();
        [Tooltip("Voxelizations baked to disk (one per field: coarse + each detailed field), set by " +
                 "the 'Bake Voxelization To Disk' button. At load the coarse asset + the asset matching " +
                 "the active fine volume are uploaded instead of re-rasterizing the scene meshes; on a " +
                 "mismatch (moved/rescaled bounds, changed normal source) it warns and falls back to " +
                 "runtime voxelization.")]
        [SerializeField] List<BufferGiBakeAsset> _bakeAssets = new List<BufferGiBakeAsset>();

        /// <summary>Coarse-field MeshBounds (null = fine only), for the editor bake button.</summary>
        public MeshBounds CoarseField => _coarseField;
        /// <summary>Fine fields the editor bake button voxelizes (in addition to the coarse field).</summary>
        public IReadOnlyList<MeshBounds> DetailedFields => _detailedFields;
        /// <summary>Disk bakes uploaded instead of runtime voxelization; the editor bake button rewrites this.</summary>
        public List<BufferGiBakeAsset> BakeAssets { get => _bakeAssets; set => _bakeAssets = value; }

        /// <summary>The Buffer GI field provider for a volume's level, resolved from that volume's scene.
        /// Returns null when the level has no provider (fine-only, runtime-voxelized). Mirrors
        /// PlayerSpawner.FindSpawnPoint - the runtime substitute for a cross-scene reference.</summary>
        public static BufferGiFields Find(VoxelVolume volume) {
            if (volume == null) return null;
            Scene scene = volume.gameObject.scene;
            if (!scene.IsValid()) return null;
            foreach (GameObject root in scene.GetRootGameObjects()) {
                BufferGiFields fields = root.GetComponentInChildren<BufferGiFields>(true);
                if (fields != null) return fields;
            }
            return null;
        }

        /// <summary>
        /// The volume to make active when nothing else has chosen one (see LightingManager's
        /// adoption path, used when auto-switch-to-closest is off).
        ///
        /// The COARSE field is deliberately never a candidate: it is a bare scene-covering
        /// <see cref="MeshBounds"/> with no <see cref="VoxelVolume"/> on it, so it never registers
        /// and can never be "the active volume". It does not need to be - the coarse field is reached
        /// THROUGH whichever volume is active, because that volume's scene is how the updater finds
        /// this component at all (see <see cref="Find"/>). Adopting any detailed field here is what
        /// makes the coarse one available.
        ///
        /// Prefers a field that actually has a disk bake, so a level that never switches loads its
        /// bake instead of silently voxelizing at runtime; falls back to the first detailed field
        /// with a volume when nothing is baked yet.
        /// </summary>
        public VoxelVolume FallbackVolume {
            get {
                VoxelVolume firstWithVolume = null;
                for (int i = 0; i < _detailedFields.Count; i++) {
                    MeshBounds field = _detailedFields[i];
                    if (field == null) continue;
                    // Every detailed field is authored to sit on a GameObject that also carries the
                    // VoxelVolume whose padded bounds are the runtime fine grid.
                    VoxelVolume volume = field.GetComponent<VoxelVolume>();
                    if (volume == null) continue;
                    if (firstWithVolume == null) firstWithVolume = volume;
                    if (HasBakeFor(volume)) return volume;
                }
                return firstWithVolume;
            }
        }

        /// <summary>Whether a non-coarse bake in this level was rasterized against this volume's grid.</summary>
        bool HasBakeFor(VoxelVolume volume) {
            Bounds bounds = volume.Bounds;
            for (int i = 0; i < _bakeAssets.Count; i++) {
                BufferGiBakeAsset asset = _bakeAssets[i];
                if (asset == null || asset.isCoarse) continue;
                if (asset.MatchesBounds(bounds.min, bounds.size)) return true;
            }
            return false;
        }

        /// <summary>The provider for whichever level is loaded, when no volume is active yet to resolve
        /// it from. Returns null with no level loaded.</summary>
        public static BufferGiFields FindAny() =>
            FindFirstObjectByType<BufferGiFields>(FindObjectsInactive.Include);

        // Editor: prefill the detailed-field list with every MeshBounds in the scene except the coarse
        // one, so a freshly added component already lists the fine fields to bake.
        void Reset() {
            _detailedFields.Clear();
            MeshBounds[] all = FindObjectsByType<MeshBounds>();
            foreach (MeshBounds mb in all) {
                if (mb != null && mb != _coarseField) _detailedFields.Add(mb);
            }
        }
    }
}
