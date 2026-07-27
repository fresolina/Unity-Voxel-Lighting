using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Bakes the directional occlusion bitmask used by the Bitmask buffer-GI shadow mode.
    ///
    /// <see cref="ExactOcclusionBitmaskBake"/> traces scene triangles directly, so this baker has no
    /// ordering dependency on <see cref="VoxelSdfBaker"/>.
    /// </summary>
    [AddComponentMenu("Lotec/Voxel Lighting/Bakers/Voxel Occlusion Bitmask Baker")]
    public class VoxelOcclusionBitmaskBaker : VoxelBakerBase {
        [Header("Occlusion Bitmask Baker")]
        [SerializeField] ExactOcclusionBitmaskBake _exactBake = new ExactOcclusionBitmaskBake();
        [HideInInspector][SerializeField] bool _hemisphereInitialized;
        [HideInInspector][SerializeField] bool _lastHemisphereOnly;

        public ExactOcclusionBitmaskBake ExactBaker => _exactBake;
        public override int BakeOrder => 10;
        public override string BakeLabel => "Occlusion Bitmask";

        public override bool Bake(VoxelVolume volume, out string error) {
#if UNITY_EDITOR
            // Components serialized before the compute shader field existed have none assigned.
            // Resolving here (rather than in OnValidate) keeps the AssetDatabase lookup on an explicit
            // user action, where it is always safe to call.
            AssignMissingComputeShaders();
#endif
            if (!_exactBake.TryBake(volume, out Texture3D baked, out Vector3[] directions, out error))
                return false;

            // Store the baked bitmask on its runtime binder (added if missing).
            if (!volume.TryGetComponent(out VoxelOcclusionBitmask binder))
                binder = volume.gameObject.AddComponent<VoxelOcclusionBitmask>();
            binder.occlusionBitmaskTexture = baked;
            // Hand the directions over inline; the editor's SaveBakedAssets moves them into an asset.
            // The old asset MUST be dropped in the same breath - the binder prefers the asset over the
            // inline array, so a Dir64 set left behind by the previous bake would otherwise be read
            // against a fresh Dir8 texture, decoding every bit to the wrong angle with no error.
            binder.pendingDirections = directions;
            binder.directionSet = null;

            // Push the fresh bake to the buffer-GI driver so the Bitmask ShadowMode reflects it in edit
            // mode right away (the holder is passive now - the updater publishes it).
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
                else Debug.LogWarning("VoxelOcclusionBitmaskBaker: Could not find OcclusionFieldTrace compute shader. Assign it manually.", this);
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
                if (volume != null && volume.TryGetComponent(out VoxelOcclusionBitmask binder) && binder.HasData) {
                    Debug.LogWarning("VoxelOcclusionBitmaskBaker: hemisphere-only changed; existing baked bitmask no longer matches the runtime direction set. Rebake.", this);
                }
                _lastHemisphereOnly = hemisphereOnly;
            }
        }
#endif
    }
}
