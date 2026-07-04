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
        /// <summary>GI method (VoxelLit: multi_compile GI_OFF GI_VOXEL_TEXTURE GI_VOXEL_BUFFER).
        /// Owned by whichever GI updater component is enabled; GI_OFF when none.</summary>
        public static readonly ShaderKeywordGroup Gi = new ShaderKeywordGroup("GI_OFF", "GI_VOXEL_TEXTURE", "GI_VOXEL_BUFFER");
        public const string GiOff = "GI_OFF";
        public const string GiTexture = "GI_VOXEL_TEXTURE";
        public const string GiBuffer = "GI_VOXEL_BUFFER";

        static object s_giOwner;
        static string s_giKeyword = GiOff;

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

        /// <summary>Local-shadow source (VoxelLit: multi_compile __ BITMASK_POINT BITMASK_8TAP
        /// OCC_FIELD). Bare default = the SDF path. Owned by the occlusion binder components.</summary>
        public static readonly ShaderKeywordGroup ShadowSource = new ShaderKeywordGroup(null, "BITMASK_POINT", "BITMASK_8TAP", "OCC_FIELD");
        public const string ShadowBitmaskPoint = "BITMASK_POINT";
        public const string ShadowBitmask8Tap = "BITMASK_8TAP";
        public const string ShadowOcclusionField = "OCC_FIELD";

        static object s_shadowOwner;
        static string s_shadowKeyword;

        /// <summary>Claim the shadow-source group for a binder and activate its keyword. Change-only
        /// and ownership-aware, safe to call every frame from the active binder.</summary>
        public static void ClaimShadow(object owner, string keyword) {
            if (s_shadowOwner == owner && s_shadowKeyword == keyword) return;
            s_shadowOwner = owner;
            s_shadowKeyword = keyword;
            ShadowSource.Set(keyword);
        }

        /// <summary>Release the shadow-source group back to the SDF default - only if the caller
        /// still owns it, so an old binder can't clobber the keyword after a source switch.</summary>
        public static void ReleaseShadow(object owner) {
            if (s_shadowOwner != owner) return;
            s_shadowOwner = null;
            s_shadowKeyword = null;
            ShadowSource.Set(null);
        }

        /// <summary>SDF ambient-occlusion quality (VoxelLit: multi_compile __ SDF_AO_LQ SDF_AO_HQ).
        /// Bare default = off (no keyword). Owned by the SdfAmbientOcclusion component.</summary>
        public static readonly ShaderKeywordGroup SdfAo = new ShaderKeywordGroup(null, "SDF_AO_LQ", "SDF_AO_HQ");
        public const string SdfAoLow = "SDF_AO_LQ";
        public const string SdfAoHigh = "SDF_AO_HQ";
    }
}
