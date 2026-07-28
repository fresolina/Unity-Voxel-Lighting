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
        [Tooltip("Point lights baked into this volume's voxelization as emissive voxels. Filled by " +
                 "'Bake Voxelization To Disk' from the Baked and Mixed point lights inside the volume's " +
                 "bounds - re-bake after adding or moving one. Disable a listed light to switch it off " +
                 "at runtime; disable this component to switch the whole list off.")]
        [SerializeField] List<Light> _lights = new List<Light>();

        /// <summary>The baked lights of this volume, switched on or off. Read-only: membership is bake
        /// output (only the Editor can tell a Baked light from a realtime one), so it is rewritten by
        /// the bake button rather than edited at runtime. Entries can be null if a light was deleted
        /// since the bake - every reader skips those.</summary>
        public IReadOnlyList<Light> Lights => _lights;
    }
}
