using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Bakes the packed material field (albedo.rgb + emissionIntensity.a) consumed by
    /// GI. Reads the hi-res SDF resolution, so it runs after <see cref="VoxelSdfBaker"/>.
    /// </summary>
    [AddComponentMenu("Lotec/Voxel Lighting/Bakers/Voxel Material Baker")]
    public class VoxelMaterialBaker : VoxelBakerBase {
        [Tooltip("Downscale factor for the baked material field relative to the hi-res SDF.")]
        [Range(1, 6)]
        public int LowresDownscaleFactor = 2;

        [SerializeField] MaterialBake _baker = new MaterialBake();

        public MaterialBake Baker => _baker;
        public override int BakeOrder => 10;
        public override string BakeLabel => "Material";

        public override bool Bake(VoxelVolume volume, out string error) {
            error = _baker.Bake(volume, out Texture3D bakedAlbedoIntensity, LowresDownscaleFactor);
            if (!string.IsNullOrEmpty(error))
                return false;

            // Store the baked material field on its component (added if missing).
            if (!volume.TryGetComponent(out VoxelMaterialField material))
                material = volume.gameObject.AddComponent<VoxelMaterialField>();
            material.materialAlbedoIntensityTexture = bakedAlbedoIntensity;
            return true;
        }

#if UNITY_EDITOR
        protected override void Reset() {
            base.Reset();
            if (_baker.MaterialBakeCompute == null) {
                ComputeShader cs = FindComputeShaderByContains("MaterialBake");
                if (cs != null) _baker.MaterialBakeCompute = cs;
                else Debug.LogWarning("VoxelMaterialBaker: Could not find MaterialBake compute shader. Assign it manually.", this);
            }
        }
#endif
    }
}
