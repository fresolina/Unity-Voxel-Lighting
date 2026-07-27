using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Common contract for occlusion field bakes so alternative implementations (the SDF
    /// raymarch bake and the exact triangle-traversal bake) can be swapped for evaluation,
    /// mirroring <see cref="ISdfBake"/>.
    /// </summary>
    public interface IOcclusionFieldBake {
        /// <summary>
        /// Bakes one Texture3D per 4 directions, each channel storing the lit value
        /// (0 = shadow, 1 = lit) for its direction.
        /// </summary>
        bool TryBake(
            VoxelVolume sourceVolume,
            out Texture3D[] resultTextures,
            out Vector3[] bakedDirections,
            out string error
        );

        /// <summary>Upper-hemisphere-only direction set; read by the baker to warn on a stale bake.</summary>
        bool HemisphereOnly { get; }

        /// <summary>What the baked channels hold, so the binder can publish the matching decode.</summary>
        VoxelOcclusionField.ShadowEncoding Encoding { get; }

        /// <summary>Voxel range the channel spans under SignedDistance encoding; unused otherwise.</summary>
        float SdfRangeVoxels { get; }
    }
}
