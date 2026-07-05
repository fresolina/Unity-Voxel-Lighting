using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Common contract for SDF bakers so alternative implementations (e.g. the exact
    /// grid baker and the JFA baker) can be swapped for evaluation.
    /// </summary>
    public interface ISdfBake {
        bool TryBake(
            VoxelVolume volume,
            Vector3Int resolution,
            string textureName,
            out Texture3D bakedSdf,
            out string error
        );
    }
}
