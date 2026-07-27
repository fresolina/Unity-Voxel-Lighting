using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Bakes the per-direction occlusion field used by the OcclusionField buffer-GI shadow mode
    /// (hardware-interpolated).
    ///
    /// Two implementations, selected by <see cref="bakeMode"/>: the default
    /// <see cref="ExactOcclusionFieldBake"/> traces scene triangles and needs no SDF, while the
    /// legacy <see cref="OcclusionFieldBake"/> sphere-traces the hi-res SDF and therefore still
    /// runs after <see cref="VoxelSdfBaker"/>. Both emit the same format, so they can be A/B'd.
    /// </summary>
    [AddComponentMenu("Lotec/Voxel Lighting/Bakers/Voxel Occlusion Field Baker")]
    public class VoxelOcclusionFieldBaker : VoxelBakerBase {
        public enum OcclusionFieldBakeMode {
            ExactTriangle, // ExactOcclusionFieldBake: exact ray-triangle traversal, no SDF needed
            SdfRaymarch,   // OcclusionFieldBake: sphere-traces the hi-res SDF (leaks on thin geometry)
        }

        [Header("Occlusion Field Baker")]
        [Tooltip("Exact Triangle traces the scene triangles directly - no SDF dependency and no " +
                 "leaking through sub-voxel-thick walls. Sdf Raymarch is the older SDF sphere-trace, " +
                 "kept for comparison.")]
        public OcclusionFieldBakeMode bakeMode = OcclusionFieldBakeMode.ExactTriangle;

        [SerializeField] ExactOcclusionFieldBake _exactBake = new ExactOcclusionFieldBake();
        [SerializeField] OcclusionFieldBake _baker = new OcclusionFieldBake();
        [HideInInspector][SerializeField] bool _hemisphereInitialized;
        [HideInInspector][SerializeField] bool _lastHemisphereOnly;

        public OcclusionFieldBake Baker => _baker;
        public ExactOcclusionFieldBake ExactBaker => _exactBake;
        public override int BakeOrder => 10;
        public override string BakeLabel => "Occlusion Field";

        IOcclusionFieldBake ActiveBake =>
            bakeMode == OcclusionFieldBakeMode.SdfRaymarch ? (IOcclusionFieldBake)_baker : _exactBake;

        public override bool Bake(VoxelVolume volume, out string error) {
#if UNITY_EDITOR
            // Components serialized before a given bake mode existed have no compute shader assigned.
            // Resolving here (rather than in OnValidate) keeps the AssetDatabase lookup on an explicit
            // user action, where it is always safe to call.
            AssignMissingComputeShaders();
#endif
            if (!ActiveBake.TryBake(volume, out Texture3D[] fieldTextures, out Vector3[] fieldDirections, out error))
                return false;

            // Store the baked field on its runtime binder (added if missing), so the field
            // data lives on the component that uses it and "just works" without manual wiring.
            if (!volume.TryGetComponent(out VoxelOcclusionField binder))
                binder = volume.gameObject.AddComponent<VoxelOcclusionField>();
            binder.occlusionFieldTextures = fieldTextures;
            binder.occlusionFieldDirections = fieldDirections;
            // The decode ramp is driven by these, so they must travel with the data - a field baked
            // as signed distance and read as visibility (or vice versa) is silently wrong, not broken.
            binder.shadowEncoding = ActiveBake.Encoding;
            if (ActiveBake.SdfRangeVoxels > 0f)
                binder.sdfRangeVoxels = ActiveBake.SdfRangeVoxels;

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
            if (_baker.occlusionFieldBakeCompute == null) {
                ComputeShader cs = FindComputeShaderByExactName("OcclusionFieldBake");
                if (cs != null) _baker.occlusionFieldBakeCompute = cs;
                else Debug.LogWarning("VoxelOcclusionFieldBaker: Could not find OcclusionFieldBake compute shader. Assign it manually.", this);
            }
            if (_exactBake.occlusionFieldTraceCompute == null) {
                ComputeShader cs = FindComputeShaderByExactName("OcclusionFieldTrace");
                if (cs != null) _exactBake.occlusionFieldTraceCompute = cs;
                else Debug.LogWarning("VoxelOcclusionFieldBaker: Could not find OcclusionFieldTrace compute shader. Assign it manually.", this);
            }
        }

        void OnValidate() {
            bool hemisphereOnly = ActiveBake.HemisphereOnly;
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
