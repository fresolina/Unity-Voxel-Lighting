using UnityEngine;
using UnityEngine.Serialization;

namespace Lotec.Lighting {
    /// <summary>
    /// Bakes the signed distance field for a volume. This is the foundational bake:
    /// shadows and AO read the hi-res SDF directly, and the occlusion/material bakers
    /// read it too, so this baker runs first (BakeOrder 0).
    /// </summary>
    [AddComponentMenu("Lotec/Voxel Lighting/Bakers/Voxel SDF Baker")]
    public class VoxelSdfBaker : VoxelBakerBase {
        public enum SdfBakeMode {
            Exact, // ExactSdfBake: uniform-grid exact nearest-triangle search
            Jfa,   // JfaSdfBake: approximate jump-flooding (for evaluation)
        }

        [Header("SDF Baker")]
        [Tooltip("Which SDF bake to use. Exact is the reference; JFA is an approximate alternative kept for evaluation.")]
        [FormerlySerializedAs("sdfBakerMode")]
        public SdfBakeMode sdfBakeMode = SdfBakeMode.Exact;

        [Tooltip("Also bake a low-res SDF used by GI. Disable for direct-lighting-only setups that never run GI.")]
        public bool bakeLowres = true;

        [Tooltip("Downscale factor to produce the low-res SDF used by GI.")]
        [Range(1, 6)]
        public int LowresDownscaleFactor = 2;

        [FormerlySerializedAs("_sdfBaker")]
        [SerializeField] ExactSdfBake _exactBake = new ExactSdfBake();
        [FormerlySerializedAs("_sdfBakerJfa")]
        [SerializeField] JfaSdfBake _jfaBake = new JfaSdfBake();

        public override int BakeOrder => 0;
        public override string BakeLabel => "SDF";

        public override bool Bake(VoxelVolume volume, out string error) {
            error = null;
            if (volume == null) {
                error = "Target volume is not assigned.";
                return false;
            }

            // The SDF baker owns bounds/resolution: it must run before any baker that
            // depends on them, so recompute here rather than relying on a prior bake.
            volume.RecomputeBoundsAndResolution();

            ISdfBake bake = sdfBakeMode == SdfBakeMode.Jfa ? (ISdfBake)_jfaBake : _exactBake;

            if (!bake.TryBake(volume, volume.TrimmedMaxResolution, volume.BakeRoot.name, out Texture3D bakedSdf, out error))
                return false;
            volume.sdfHiresTexture = bakedSdf;

            if (bakeLowres) {
                if (!bake.TryBake(volume, volume.TrimmedMaxResolution / LowresDownscaleFactor, volume.BakeRoot.name + "_Lowres", out Texture3D bakedLowres, out error))
                    return false;
                volume.sdfLowresTexture = bakedLowres;
            }

            return true;
        }

#if UNITY_EDITOR
        protected override void Reset() {
            base.Reset();
            if (_exactBake.sdfBakeCompute == null) {
                ComputeShader cs = FindComputeShaderByExactName("SdfBake");
                if (cs != null) _exactBake.sdfBakeCompute = cs;
                else Debug.LogWarning("VoxelSdfBaker: Could not find SdfBake compute shader. Assign it manually.", this);
            }
            if (_jfaBake.sdfBakeJfaCompute == null) {
                ComputeShader cs = FindComputeShaderByExactName("SdfBakeJfa");
                if (cs != null) _jfaBake.sdfBakeJfaCompute = cs;
                else Debug.LogWarning("VoxelSdfBaker: Could not find SdfBakeJfa compute shader. Assign it manually.", this);
            }
        }
#endif
    }
}
