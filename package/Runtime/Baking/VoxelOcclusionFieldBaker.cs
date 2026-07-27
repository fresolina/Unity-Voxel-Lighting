using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Bakes the per-direction occlusion field used by the OcclusionField buffer-GI shadow mode
    /// (hardware-interpolated).
    ///
    /// <see cref="ExactOcclusionFieldBake"/> traces scene triangles directly, so this baker has no
    /// ordering dependency on <see cref="VoxelSdfBaker"/>.
    /// </summary>
    [AddComponentMenu("Lotec/Voxel Lighting/Bakers/Voxel Occlusion Field Baker")]
    public class VoxelOcclusionFieldBaker : VoxelBakerBase {
        [Header("Occlusion Field Baker")]
        [SerializeField] ExactOcclusionFieldBake _exactBake = new ExactOcclusionFieldBake();
        [HideInInspector][SerializeField] bool _hemisphereInitialized;
        [HideInInspector][SerializeField] bool _lastHemisphereOnly;

        public ExactOcclusionFieldBake ExactBaker => _exactBake;
        public override int BakeOrder => 10;
        public override string BakeLabel => "Occlusion Field";

        public override bool Bake(VoxelVolume volume, out string error) {
#if UNITY_EDITOR
            // Components serialized before the compute shader field existed have none assigned.
            // Resolving here (rather than in OnValidate) keeps the AssetDatabase lookup on an explicit
            // user action, where it is always safe to call.
            AssignMissingComputeShaders();
#endif
            if (!_exactBake.TryBake(volume, out Texture3D[] fieldTextures, out Vector3[] fieldDirections, out error))
                return false;

            // Store the baked field on its runtime binder (added if missing), so the field
            // data lives on the component that uses it and "just works" without manual wiring.
            if (!volume.TryGetComponent(out VoxelOcclusionField binder))
                binder = volume.gameObject.AddComponent<VoxelOcclusionField>();
            binder.occlusionFieldTextures = fieldTextures;
            // Hand the directions over inline; the editor's SaveBakedAssets moves them into an asset.
            // The old asset MUST be dropped in the same breath - the binder prefers the asset over the
            // inline array, so the previous bake's set would otherwise be read against fresh textures.
            // Dir 1 Sun makes this acute: its single direction is wherever the sun was last bake.
            binder.pendingDirections = fieldDirections;
            binder.directionSet = null;
            // The decode ramp is driven by these, so they must travel with the data - a field baked
            // as signed distance and read as visibility (or vice versa) is silently wrong, not broken.
            binder.shadowEncoding = _exactBake.shadowEncoding;
            binder.sdfRangeVoxels = _exactBake.sdfRangeVoxels;

            // Push the fresh bake to the buffer-GI driver so the OcclusionField ShadowMode reflects it in
            // edit mode right away (the holder is passive now - the updater publishes it).
            BufferGiUpdater.RefreshOcclusionSourcesFor(volume);
            return true;
        }

#if UNITY_EDITOR
        protected override void Reset() {
            base.Reset();
            AssignMissingComputeShaders();
        }

        void AssignMissingComputeShaders() {
            if (_exactBake.occlusionFieldTraceCompute == null) {
                ComputeShader cs = FindComputeShaderByExactName("OcclusionFieldTrace");
                if (cs != null) _exactBake.occlusionFieldTraceCompute = cs;
                else Debug.LogWarning("VoxelOcclusionFieldBaker: Could not find OcclusionFieldTrace compute shader. Assign it manually.", this);
            }
        }

        void OnValidate() {
            bool hemisphereOnly = _exactBake.hemisphereOnly;
            if (!_hemisphereInitialized) {
                _lastHemisphereOnly = hemisphereOnly;
                _hemisphereInitialized = true;
                return;
            }
            if (_lastHemisphereOnly != hemisphereOnly) {
                VoxelVolume volume = ResolveVolume();
                if (volume != null && volume.TryGetComponent(out VoxelOcclusionField binder)) {
                    bool hasBake = binder != null && binder.HasData;
                    if (hasBake) {
                        Debug.LogWarning("VoxelOcclusionFieldBaker: hemisphere-only changed; existing baked field no longer matches the runtime direction set. Rebake.", this);
                    }
                }
                _lastHemisphereOnly = hemisphereOnly;
            }
        }
#endif
    }
}
