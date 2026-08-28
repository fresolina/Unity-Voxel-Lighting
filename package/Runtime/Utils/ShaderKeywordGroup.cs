using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// A mutually-exclusive set of global shader keywords (one multi_compile group). Setting one
    /// keyword disables every sibling - <see cref="Shader.EnableKeyword(string)"/> alone does NOT,
    /// which repeatedly caused "two variants of one group active" bugs. The first entry is the
    /// group's default variant; use null for a bare default (multi_compile __ ...).
    /// </summary>
    public sealed class ShaderKeywordGroup {
        readonly string[] _keywords;

        public ShaderKeywordGroup(params string[] keywords) {
            _keywords = keywords;
        }

        /// <summary>Enable exactly this keyword and disable every sibling. Pass null (or a keyword
        /// not in the group, e.g. a bare-default marker) to disable the whole group.</summary>
        public void Set(string active) {
            for (int i = 0; i < _keywords.Length; i++) {
                string keyword = _keywords[i];
                if (string.IsNullOrEmpty(keyword)) continue;
                if (keyword == active) Shader.EnableKeyword(keyword);
                else Shader.DisableKeyword(keyword);
            }
        }

        /// <summary>Reset the group to its default (first) variant.</summary>
        public void Reset() => Set(_keywords.Length > 0 ? _keywords[0] : null);
    }

    /// <summary>
    /// The global keyword groups this package drives, in one place so every feature toggles its
    /// variants consistently (and so it is discoverable which keywords the package owns).
    /// </summary>
    public static class LightingKeywords {
        /// <summary>GI method (VoxelLit: multi_compile GI_OFF GI_VOXEL_BUFFER GI_UNITY). Owned by the
        /// buffer GI updater component when enabled; the component-less options (GI_OFF / GI_UNITY) are
        /// driven by GiMethodSelector. GI_UNITY selects Unity's built-in indirect (SampleSH) as an A/B
        /// performance baseline against the voxel GI.</summary>
        public static readonly ShaderKeywordGroup Gi = new ShaderKeywordGroup("GI_OFF", "GI_VOXEL_BUFFER", "GI_UNITY");
        public const string GiOff = "GI_OFF";
        public const string GiBuffer = "GI_VOXEL_BUFFER";
        public const string GiUnity = "GI_UNITY";

        /// <summary>Display-transform operator (VoxelLit: multi_compile TONEMAP_OFF TONEMAP_REINHARD
        /// TONEMAP_AGX TONEMAP_ACES), driven by <see cref="AutoExposure"/>. Compile-time rather than a
        /// uniform branch because a fragment kernel's register allocation spans EVERY path it contains:
        /// with all operators in one kernel it was sized for AgX's worst case, which capped occupancy on
        /// this latency-bound shader and cost ~1ms/frame even while the cheapest operator ran.</summary>
        public static readonly ShaderKeywordGroup Tonemap = new ShaderKeywordGroup(
            "TONEMAP_OFF", "TONEMAP_REINHARD", "TONEMAP_AGX", "TONEMAP_ACES");
        public const string TonemapOff = "TONEMAP_OFF";
        public const string TonemapReinhard = "TONEMAP_REINHARD";
        public const string TonemapAgx = "TONEMAP_AGX";
        public const string TonemapAces = "TONEMAP_ACES";

        /// <summary>Single-mode irradiance tap filter (VoxelLit: multi_compile_fragment __
        /// BGI_TAP_AXIS_SNAPPED), driven by <see cref="BufferGiUpdater.SingleTapFilter"/>. Bare default
        /// (null) = the Fast one-tap read; the keyword selects the axis-snapped n^2-weighted taps. A
        /// keyword rather than a uniform for the same register-allocation reason as
        /// <see cref="Tonemap"/> - the Fast variant is the package's cheapest read and must not carry
        /// the other path's registers. Fragment-only, so it doubles fragment variants but not vertex.</summary>
        public static readonly ShaderKeywordGroup BgiTap = new ShaderKeywordGroup(null, "BGI_TAP_AXIS_SNAPPED");
        public const string BgiTapAxisSnapped = "BGI_TAP_AXIS_SNAPPED";

        /// <summary>Contaminated-axis snap in the irradiance tap (VoxelLit: multi_compile_fragment __
        /// BGI_TAP_SNAP_INPLANE), driven by <see cref="BufferGiUpdater.InPlaneSnap"/>. Where the
        /// trilinear kernel spans a one-voxel wall IN THE SURFACE PLANE, the tap is snapped to a cell
        /// centre on that axis alone. Its own group rather than a third state of <see cref="BgiTap"/>
        /// because it composes with both filters and with Cube - it clears the layer BESIDE the
        /// surface, where the axis-snapped filter clears the one BEHIND it. A keyword because the gate
        /// costs a texture Load on every shaded pixel, which is exactly the per-pixel cost the Fast tap
        /// exists to avoid carrying.</summary>
        public static readonly ShaderKeywordGroup BgiSnap = new ShaderKeywordGroup(null, "BGI_TAP_SNAP_INPLANE");
        public const string BgiTapSnapInPlane = "BGI_TAP_SNAP_INPLANE";

        /// <summary>ANALYSIS views in the lit shader (VoxelLit: multi_compile_fragment __
        /// BGI_DEBUG_VIEWS), driven by <see cref="BufferGiUpdater.DebugView"/>. A keyword rather than
        /// a plain branch on the uniform because the solid-weight view costs up to eight occupancy
        /// reads and a 2x2x2 walk: as a dynamic branch that code stays resident and eats registers on
        /// EVERY shaded pixel, which is exactly the occupancy hit the Fast tap exists to avoid. Off by
        /// default, so a shipping build never compiles it in.</summary>
        public static readonly ShaderKeywordGroup BgiDebug = new ShaderKeywordGroup(null, "BGI_DEBUG_VIEWS");
        public const string BgiDebugViews = "BGI_DEBUG_VIEWS";

        static object s_giOwner;
        static string s_giKeyword = GiOff;

        /// <summary>The GI keyword currently active (whatever the last ClaimGi/ReleaseGi set). Lets a
        /// caller tell the two component-less options apart - GI_OFF vs GI_UNITY both mean "no updater
        /// enabled".</summary>
        public static string ActiveGiKeyword => s_giKeyword;

        /// <summary>Claim the GI group for a GI updater and activate its keyword. Change-only (safe
        /// to call every frame), and ownership-aware: a later claim simply takes over.</summary>
        public static void ClaimGi(object owner, string keyword) {
            if (s_giOwner == owner && s_giKeyword == keyword) return;
            s_giOwner = owner;
            s_giKeyword = keyword;
            Gi.Set(keyword);
        }

        /// <summary>Release the GI group back to GI_OFF - but only if the caller still owns it, so
        /// disabling the OLD updater while switching methods can't clobber the new owner's keyword.</summary>
        public static void ReleaseGi(object owner) {
            if (s_giOwner != owner) return;
            s_giOwner = null;
            s_giKeyword = GiOff;
            Gi.Set(GiOff);
        }
    }
}
