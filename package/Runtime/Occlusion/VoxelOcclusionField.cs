using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Runtime binder for occlusion-field shadows. Reads the baked occlusion field off the
    /// <see cref="VoxelVolume"/> on the same GameObject and publishes the per-direction field
    /// globals, so the shadow term comes from the field with no SDF texture bound at runtime.
    /// The shared volume bounds it samples against are published by <see cref="LightingManager"/>.
    ///
    /// Added automatically by the occlusion-field baker when it bakes. The ENABLED binder on the
    /// active volume selects the OCC_FIELD shadow path (it owns the shadow keyword group); with no
    /// enabled binder the SDF default is used. Mutually exclusive with the bitmask binder.
    /// </summary>
    [ExecuteInEditMode]
    [RequireComponent(typeof(VoxelVolume))]
    [AddComponentMenu("Lotec/Voxel Lighting/Binders/Voxel Occlusion Field")]
    public class VoxelOcclusionField : MonoBehaviour {
        [Tooltip("RGBA32 textures storing per-direction lit values. 4 directions per texture. Written by VoxelOcclusionFieldBaker.")]
        public Texture3D[] occlusionFieldTextures;
        [HideInInspector]
        public Vector3[] occlusionFieldDirections;

        static readonly int s_voxelSize = Shader.PropertyToID("_VoxelSize");
        static readonly int s_voxelSizeInverse = Shader.PropertyToID("_VoxelSizeInverse");
        static readonly int s_occFieldSunDir = Shader.PropertyToID("_OccFieldSunDir");
        static readonly int s_occFieldSunChannel = Shader.PropertyToID("_OccFieldSunChannel");
        static readonly int s_occFieldTex = Shader.PropertyToID("_OccFieldTex");

        VoxelVolume _volume;

        VoxelVolume Volume {
            get {
                if (_volume == null) _volume = GetComponent<VoxelVolume>();
                return _volume;
            }
        }

        /// <summary>True when there is baked occlusion-field data to publish.</summary>
        public bool HasData =>
            occlusionFieldTextures != null && occlusionFieldTextures.Length > 0
            && occlusionFieldDirections != null && occlusionFieldDirections.Length > 0;

        void OnEnable() {
            // Shadow sources are mutually exclusive: enabling this binder turns the bitmask off.
            if (TryGetComponent(out VoxelOcclusionBitmask bitmask) && bitmask.enabled)
                bitmask.enabled = false;
        }

        void OnDisable() {
            LightingKeywords.ReleaseShadow(this);
        }

        void Update() {
            // Shader globals are singular, so only the active volume's binder publishes (and only
            // the publishing binder holds the shadow-keyword claim).
            LightingManager manager = LightingManager.Instance;
            bool active = (manager == null || manager.Volume == Volume) && HasData;
            if (!active) {
                LightingKeywords.ReleaseShadow(this);
                return;
            }
            LightingKeywords.ClaimShadow(this, LightingKeywords.ShadowOcclusionField);
            Bind();
        }

        /// <summary>Publish the occlusion-field globals + the occlusion-grid inverse voxel size
        /// (the volume bounds it samples against are published by the active VoxelVolume).</summary>
        public void Bind() {
            VoxelVolume v = Volume;
            if (v == null) return;
            Shader.SetGlobalVector(s_voxelSize, Vector3.one * v.VoxelSize);
            Shader.SetGlobalVector(s_voxelSizeInverse, v.VoxelSizeInverse);
            PublishSunField();
        }

        // Map the current sun direction to the nearest baked direction, then bind that
        // direction's texture + RGBA channel for the shader to sample.
        void PublishSunField() {
            if (occlusionFieldDirections == null || occlusionFieldDirections.Length == 0 || occlusionFieldTextures == null)
                return;

            Vector3 sunDir = OcclusionFieldQuery.GetSunDirection();
            int bestIndex = OcclusionFieldQuery.FindNearestDirection(sunDir, occlusionFieldDirections, occlusionFieldDirections.Length);
            int texIndex = bestIndex / 4;
            int channel = bestIndex % 4;

            Shader.SetGlobalVector(s_occFieldSunDir, sunDir);
            Shader.SetGlobalInt(s_occFieldSunChannel, channel);
            if (texIndex >= 0 && texIndex < occlusionFieldTextures.Length && occlusionFieldTextures[texIndex] != null)
                Shader.SetGlobalTexture(s_occFieldTex, occlusionFieldTextures[texIndex]);
        }
    }
}
