using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Bakes the directional occlusion bitmask used by the BITMASK_* shadow modes.
    /// Reads the hi-res SDF, so it runs after <see cref="VoxelSdfBaker"/>.
    /// </summary>
    [AddComponentMenu("Lotec/Voxel Lighting/Bakers/Voxel Occlusion Bitmask Baker")]
    public class VoxelOcclusionBitmaskBaker : VoxelBakerBase {
        [SerializeField] OcclusionBitmaskBake _baker = new OcclusionBitmaskBake();
        [HideInInspector][SerializeField] bool _hemisphereInitialized;
        [HideInInspector][SerializeField] bool _lastHemisphereOnly;

        public OcclusionBitmaskBake Baker => _baker;
        public override int BakeOrder => 10;
        public override string BakeLabel => "Occlusion Bitmask";

        public override bool Bake(VoxelVolume volume, out string error) {
            if (!_baker.TryBake(volume, out Texture3D baked, out Vector3[] directions, out error))
                return false;

            // Store the baked bitmask on its runtime binder (added if missing).
            if (!volume.TryGetComponent(out VoxelOcclusionBitmask binder))
                binder = volume.gameObject.AddComponent<VoxelOcclusionBitmask>();
            binder.occlusionBitmaskTexture = baked;
            binder.occlusionBitmaskDirections = directions;
            return true;
        }

#if UNITY_EDITOR
        protected override void Reset() {
            base.Reset();
            if (_baker.bitmaskBakeCompute == null) {
                ComputeShader cs = FindComputeShaderByContains("OcclusionBitmaskBake");
                if (cs != null) _baker.bitmaskBakeCompute = cs;
                else Debug.LogWarning("VoxelOcclusionBitmaskBaker: Could not find OcclusionBitmaskBake compute shader. Assign it manually.", this);
            }
        }

        void OnValidate() {
            if (!_hemisphereInitialized) {
                _lastHemisphereOnly = _baker.hemisphereOnly;
                _hemisphereInitialized = true;
                return;
            }
            if (_lastHemisphereOnly != _baker.hemisphereOnly) {
                VoxelVolume volume = ResolveVolume();
                if (volume != null && volume.TryGetComponent(out VoxelOcclusionBitmask binder) && binder.HasData) {
                    Debug.LogWarning("VoxelOcclusionBitmaskBaker: hemisphere-only changed; existing baked bitmask no longer matches the runtime direction set. Rebake.", this);
                }
                _lastHemisphereOnly = _baker.hemisphereOnly;
            }
        }
#endif
    }
}
