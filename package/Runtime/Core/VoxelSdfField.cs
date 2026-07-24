using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Per-volume baked signed distance field (hi-res, world-unit distances). Holds the data on
    /// the component that owns it - written by <see cref="VoxelSdfBaker"/>, read by the active
    /// volume: <see cref="VoxelVolume.ApplyShaderGlobals"/> publishes this texture as the
    /// <c>_SdfHires</c> global for the SDF shadow + AO fragment paths. Extracted off VoxelVolume so
    /// the volume no longer owns baked field data (mirrors the other per-volume field binders); a
    /// data holder, so it publishes nothing itself.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(VoxelVolume))]
    [AddComponentMenu("Lotec/Voxel Lighting/Binders/Voxel SDF Field")]
    public class VoxelSdfField : MonoBehaviour {
        [Tooltip("Hi-res signed distance field (world-unit distances). Written by VoxelSdfBaker, " +
                 "published as _SdfHires by the active VoxelVolume for the SDF shadow + AO paths.")]
        public Texture3D sdfTexture;
    }
}
