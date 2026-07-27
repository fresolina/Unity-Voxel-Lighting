using UnityEngine;
using UnityEngine.Serialization;

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

        [Tooltip("Signed Distance encoding only: shadow edge width in voxels. This is the softness " +
                 "knob for a Signed Distance field - it is runtime-tunable and needs no rebake, " +
                 "unlike the baker's cone settings. Small values give sharp contact shadows; the " +
                 "field resolves well below one voxel because the boundary is reconstructed rather " +
                 "than interpolated.")]
        [FormerlySerializedAs("penumbraVoxels")]
        [Range(0.05f, 4f)]
        public float shadowEdgeVoxels = 1f;

        [Tooltip("Blend between the two baked directions nearest the sun instead of snapping to one. " +
                 "Costs a second texture tap, and removes the pop as the sun crosses between " +
                 "directions plus most of the shadow displacement from direction quantisation. " +
                 "Pointless for a single-direction (Dir 1 Sun) bake, which is already exact.")]
        public bool blendDirections = true;

        static readonly int s_voxelSize = Shader.PropertyToID("_VoxelSize");
        static readonly int s_voxelSizeInverse = Shader.PropertyToID("_VoxelSizeInverse");
        static readonly int s_occFieldSunMask = Shader.PropertyToID("_OccFieldSunMask");
        static readonly int s_occFieldSunMaskB = Shader.PropertyToID("_OccFieldSunMaskB");
        static readonly int s_occFieldTex = Shader.PropertyToID("_OccFieldTex");
        static readonly int s_occFieldTexB = Shader.PropertyToID("_OccFieldTexB");
        static readonly int s_occFieldBlend = Shader.PropertyToID("_OccFieldBlend");
        static readonly int s_occFieldDecode = Shader.PropertyToID("_OccFieldDecode");

        static readonly Vector4[] s_channelMasks = {
            new Vector4(1, 0, 0, 0), new Vector4(0, 1, 0, 0),
            new Vector4(0, 0, 1, 0), new Vector4(0, 0, 0, 1),
        };

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
        /// is <see cref="shadowEdgeVoxels"/>.
        ///
        /// stored - 0.5 == d / (2 * range), and we want saturate(d / (2 * edge) + 0.5),
        /// so the scale is range / edge.
        /// </summary>
        public float DecodeScale =>
            shadowEncoding == ShadowEncoding.SignedDistance
                ? Mathf.Max(sdfRangeVoxels, 1e-3f) / Mathf.Max(shadowEdgeVoxels, 1e-3f)
                : 1f;

        // Map the current sun direction onto the baked direction set and bind the texture(s) +
        // channel mask(s) the shader samples. With blending on, the two nearest directions are bound
        // and the shader slides between them; otherwise it snaps to the nearest, as before.
        void PublishSunField() {
            // Publish the disabled state FIRST. Shader globals persist across volumes, so bailing out
            // without writing this would leave a previous binder's blend weight live and the shader
            // would tap a second texture that no longer belongs to this field.
            Shader.SetGlobalFloat(s_occFieldBlend, 0f);

            if (occlusionFieldDirections == null || occlusionFieldDirections.Length == 0 || occlusionFieldTextures == null)
                return;

            Vector3 sunDir = OcclusionFieldQuery.GetSunDirection();
            OcclusionFieldQuery.FindNearestTwoDirections(
                sunDir, occlusionFieldDirections, occlusionFieldDirections.Length,
                out int bestIndex, out int secondIndex, out float weight);

            Shader.SetGlobalVector(s_occFieldSunMask, s_channelMasks[bestIndex & 3]);
            BindFieldTexture(s_occFieldTex, bestIndex);

            // Blending needs a genuine second direction AND a texture holding it. A Dir 1 Sun bake has
            // neither (secondIndex is -1), and a partially written binder can have directions whose
            // texture is missing - in both cases fall back to the single-tap path rather than blend
            // against whatever texture happened to be bound last.
            float blend = 0f;
            if (blendDirections && secondIndex >= 0 && BindFieldTexture(s_occFieldTexB, secondIndex)) {
                blend = weight;
                Shader.SetGlobalVector(s_occFieldSunMaskB, s_channelMasks[secondIndex & 3]);
            } else {
                // Keep the second slot pointing at valid data even when unused - the shader skips the
                // fetch, but the global should never be left dangling at a stale texture.
                Shader.SetGlobalVector(s_occFieldSunMaskB, s_channelMasks[bestIndex & 3]);
                BindFieldTexture(s_occFieldTexB, bestIndex);
            }
            Shader.SetGlobalFloat(s_occFieldBlend, blend);
        }

        bool BindFieldTexture(int propertyId, int directionIndex) {
            int texIndex = directionIndex / 4;
            if (texIndex < 0 || texIndex >= occlusionFieldTextures.Length || occlusionFieldTextures[texIndex] == null)
                return false;
            Shader.SetGlobalTexture(propertyId, occlusionFieldTextures[texIndex]);
            return true;
        }
    }
}
