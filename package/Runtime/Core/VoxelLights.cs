using System.Collections.Generic;
using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Per-volume list of the Unity <see cref="Light"/>s baked into the buffer-GI voxelization as
    /// emissive voxels (see <see cref="LightEmissionBake"/>). Holds the data on the component that owns
    /// it, like the other per-volume binders - written by the BufferGiUpdater inspector's "Bake
    /// Voxelization To Disk" button from the Baked and Mixed POINT lights inside this volume's bounds,
    /// read by BufferGiUpdater when it stamps them into a field.
    ///
    /// This list is the reason baked lights work in a PLAYER at all: <c>Light.lightmapBakeType</c> - the
    /// property that says whether a light is Baked - exists only in the Editor, so which lights qualify
    /// has to be decided while authoring and serialized. Everything downstream of the list is runtime
    /// code, which is what also makes a baked light SWITCHABLE: the emission of its voxel is re-stamped
    /// from the lights that are currently on, while the albedo always comes from every listed light, so
    /// occupancy (and the fields derived from it) never moves and a switch costs one small dispatch
    /// instead of a re-voxelization.
    ///
    /// A listed light is switched by enabling/disabling the Light itself. Disabling THIS component
    /// switches the whole list off at once, which makes it a group switch for a volume's baked lighting
    /// (a room's lamps, a fireplace that burns out) - hence the lights stay listed while off, rather
    /// than being removed from the list.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(VoxelVolume))]
    [AddComponentMenu("Lotec/Voxel Lighting/Binders/Voxel Lights")]
    public class VoxelLights : MonoBehaviour {
        [Tooltip("Point lights baked into this volume's voxelization as emissive voxels: the Baked and " +
                 "Mixed point lights inside the volume's bounds. Filled when the component is added, " +
                 "by 'Refresh Lights From Scene' in this component's context menu, and by 'Bake " +
                 "Voxelization To Disk' - refresh after adding, moving or retyping a light. Disable a " +
                 "listed light to switch it off at runtime; disable this component to switch the whole " +
                 "list off.")]
        [SerializeField] List<Light> _lights = new List<Light>();

        /// <summary>The baked lights of this volume, switched on or off. Membership is derived (only the
        /// Editor can tell a Baked light from a realtime one), so it is rewritten wholesale by a refresh
        /// rather than edited entry by entry. Entries can be null if a light was deleted since the last
        /// one - every reader skips those.</summary>
        public IReadOnlyList<Light> Lights => _lights;

#if UNITY_EDITOR
        // Unity calls Reset when the component is added and when Reset is chosen, which is exactly when
        // "fill this in for me" is the right default - including the bake button's Undo.AddComponent.
        void Reset() {
            LightEmissionBake.FillFromScene(this);
        }

        // Deliberately a manual action rather than a poll. Which lights qualify depends on their Mode
        // and position, and nothing raises an event for either, so the only automatic option would be to
        // scan continuously - which rewrites the list under the cursor and reverts hand edits. Refresh
        // is one click, and the bake button refreshes too, so the list cannot ship stale.
        [ContextMenu("Refresh Lights From Scene")]
        void RefreshFromScene() {
            if (LightEmissionBake.FillFromScene(this))
                Debug.Log($"Voxel Lights: refreshed '{name}' from the scene - {_lights.Count} baked light(s).", this);
            else
                Debug.Log($"Voxel Lights: '{name}' is already up to date - {_lights.Count} baked light(s).", this);
        }
#endif
    }
}
