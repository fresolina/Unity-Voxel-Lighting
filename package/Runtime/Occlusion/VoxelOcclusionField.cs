using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Passive holder for the baked occlusion field. Stores the per-direction field textures off the
    /// <see cref="VoxelVolume"/> on the same GameObject and, on demand, publishes the field globals
    /// (<see cref="Bind"/>) so the shadow term comes from the field with no SDF texture bound at runtime.
    /// The shared volume bounds it samples against are published by <see cref="LightingManager"/>.
    ///
    /// Added automatically by the occlusion-field baker when it bakes. This component does NOT run its
    /// own update loop or claim any keyword: <see cref="BufferGiUpdater"/> is the sole driver - it calls
    /// <see cref="Bind"/> for the field whose ShadowMode is OcclusionField. So there is no idle Update
    /// cost, and it can coexist with the bitmask holder (each publishes disjoint globals).
    /// </summary>
    [RequireComponent(typeof(VoxelVolume))]
    [AddComponentMenu("Lotec/Voxel Lighting/Binders/Voxel Occlusion Field")]
    public class VoxelOcclusionField : MonoBehaviour {
        /// <summary>What the baked channels actually hold. Set by the baker; drives the decode ramp.</summary>
        public enum ShadowEncoding {
            Visibility,     // the channel IS the lit value - decoded as a pass-through
            SignedDistance, // signed distance to the shadow boundary, in voxels, so the fragment
                            // reconstructs a sharp edge and the penumbra becomes a runtime knob
        }

        [Tooltip("RGBA32 textures storing per-direction lit values. 4 directions per texture. Written by VoxelOcclusionFieldBaker.")]
        public Texture3D[] occlusionFieldTextures;
        [HideInInspector]
        public Vector3[] occlusionFieldDirections;

        [Tooltip("What the baked channels hold. Written by the baker - do not set by hand, it must " +
                 "match how the data was baked or the shadow decode is wrong.")]
        public ShadowEncoding shadowEncoding = ShadowEncoding.Visibility;

        [Tooltip("Signed Distance encoding only: the +/- voxel range the baked channel spans. " +
                 "Written by the baker.")]
        public float sdfRangeVoxels = 4f;

        [Tooltip("Signed Distance encoding only: shadow edge width in voxels. Runtime-tunable - no " +
                 "rebake needed. Small values give sharp contact shadows; the field can resolve well " +
                 "below one voxel because the boundary is reconstructed, not interpolated.")]
        [Range(0.05f, 4f)]
        public float penumbraVoxels = 1f;

        static readonly int s_voxelSize = Shader.PropertyToID("_VoxelSize");
        static readonly int s_voxelSizeInverse = Shader.PropertyToID("_VoxelSizeInverse");
        static readonly int s_occFieldSunDir = Shader.PropertyToID("_OccFieldSunDir");
        static readonly int s_occFieldSunChannel = Shader.PropertyToID("_OccFieldSunChannel");
        static readonly int s_occFieldTex = Shader.PropertyToID("_OccFieldTex");
        static readonly int s_occFieldDecode = Shader.PropertyToID("_OccFieldDecode");

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

        /// <summary>Publish the occlusion-field globals + the occlusion-grid inverse voxel size
        /// (the volume bounds it samples against are published by the active VoxelVolume).</summary>
        public void Bind() {
            VoxelVolume v = Volume;
            if (v == null) return;
            Shader.SetGlobalVector(s_voxelSize, Vector3.one * v.VoxelSize);
            Shader.SetGlobalVector(s_voxelSizeInverse, v.VoxelSizeInverse);
            Shader.SetGlobalFloat(s_occFieldDecode, DecodeScale);
            PublishSunField();
        }

        /// <summary>
        /// Slope of the linear ramp the fragment applies to the stored channel. One formula covers
        /// both encodings, so the shader needs no branch and no keyword: a Visibility field passes
        /// straight through at 1.0, while a SignedDistance field turns into a soft edge whose width
        /// is <see cref="penumbraVoxels"/>.
        ///
        /// stored - 0.5 == d / (2 * range), and we want saturate(d / (2 * penumbra) + 0.5),
        /// so the scale is range / penumbra.
        /// </summary>
        public float DecodeScale =>
            shadowEncoding == ShadowEncoding.SignedDistance
                ? Mathf.Max(sdfRangeVoxels, 1e-3f) / Mathf.Max(penumbraVoxels, 1e-3f)
                : 1f;

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
