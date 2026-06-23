using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Per-volume baked material field (albedo.rgb + emissionIntensity.a) consumed by the GI
    /// solve. Holds the data on the component that owns it: written by VoxelMaterialBaker,
    /// read by GiFieldUpdater. GI binds it directly to the compute shader, so - unlike the
    /// shadow binders - this component publishes no fragment globals; it is a data holder.
    /// </summary>
    [RequireComponent(typeof(LightingVolume))]
    [AddComponentMenu("Lotec/Voxel Lighting/Binders/Voxel Material")]
    public class VoxelMaterial : MonoBehaviour {
        [Tooltip("Lower-resolution packed material: albedo.rgb + emissionIntensity (a). Written by VoxelMaterialBaker, consumed by GI.")]
        public Texture3D materialAlbedoIntensityTexture;
    }
}
