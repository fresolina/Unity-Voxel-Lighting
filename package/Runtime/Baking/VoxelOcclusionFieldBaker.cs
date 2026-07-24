using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Bakes the per-direction occlusion field used by the OcclusionField buffer-GI shadow mode
    /// (hardware-interpolated). Reads the hi-res SDF, so it runs after
    /// <see cref="VoxelSdfBaker"/>.
    /// </summary>
    [AddComponentMenu("Lotec/Voxel Lighting/Bakers/Voxel Occlusion Field Baker")]
    public class VoxelOcclusionFieldBaker : VoxelBakerBase {
        [SerializeField] OcclusionFieldBake _baker = new OcclusionFieldBake();
        [HideInInspector][SerializeField] bool _hemisphereInitialized;
        [HideInInspector][SerializeField] bool _lastHemisphereOnly;

        public OcclusionFieldBake Baker => _baker;
        public override int BakeOrder => 10;
        public override string BakeLabel => "Occlusion Field";

        public override bool Bake(VoxelVolume volume, out string error) {
            if (!_baker.TryBake(volume, out Texture3D[] fieldTextures, out Vector3[] fieldDirections, out error))
                return false;

            // Store the baked field on its runtime binder (added if missing), so the field
            // data lives on the component that uses it and "just works" without manual wiring.
            if (!volume.TryGetComponent(out VoxelOcclusionField binder))
                binder = volume.gameObject.AddComponent<VoxelOcclusionField>();
            binder.occlusionFieldTextures = fieldTextures;
            binder.occlusionFieldDirections = fieldDirections;

            // Push the fresh bake to the buffer-GI driver so the OcclusionField ShadowMode reflects it in
            // edit mode right away (the holder is passive now - the updater publishes it).
            BufferGiUpdater.RefreshOcclusionSourcesFor(volume);
            return true;
        }

#if UNITY_EDITOR
        protected override void Reset() {
            base.Reset();
            if (_baker.occlusionFieldBakeCompute == null) {
                ComputeShader cs = FindComputeShaderByContains("OcclusionFieldBake");
                if (cs != null) _baker.occlusionFieldBakeCompute = cs;
                else Debug.LogWarning("VoxelOcclusionFieldBaker: Could not find OcclusionFieldBake compute shader. Assign it manually.", this);
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
                if (volume != null && volume.TryGetComponent(out VoxelOcclusionField binder)) {
                    bool hasBake = binder != null && binder.HasData;
                    if (hasBake) {
                        Debug.LogWarning("VoxelOcclusionFieldBaker: hemisphere-only changed; existing baked field no longer matches the runtime direction set. Rebake.", this);
                    }
                }
                _lastHemisphereOnly = _baker.hemisphereOnly;
            }
        }
#endif
    }
}
