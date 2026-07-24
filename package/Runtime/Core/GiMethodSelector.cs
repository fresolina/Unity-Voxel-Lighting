using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>Which GI system is active. The <see cref="BufferGiUpdater"/> component is the source of
    /// truth for the voxel method (its enabled state selects it). <see cref="UnityBuiltin"/> and
    /// <see cref="Off"/> have no updater - they are distinguished by the active shader GI keyword.</summary>
    public enum GiMethod { Off, BufferGi, UnityBuiltin }

    /// <summary>
    /// Convenience get/set for the GI method (e.g. for a settings UI). For the voxel method it
    /// delegates to the <see cref="BufferGiUpdater"/> component's enabled state; selecting BufferGi
    /// when the component is absent leaves GI off. <see cref="GiMethod.UnityBuiltin"/> and
    /// <see cref="GiMethod.Off"/> have no updater, so the selector drives the shader GI keyword
    /// directly: UnityBuiltin picks the engine's built-in indirect (SampleSH) as an A/B performance
    /// baseline against the voxel GI; Off is direct only.
    /// </summary>
    public static class GiMethodSelector {
        // The GI keyword is normally owned by the enabled updater. For the component-less options the
        // selector claims it on their behalf under this stable token (so a disabled updater's release
        // can't clobber it, and switching back to the voxel method hands ownership to the updater).
        static readonly object s_owner = new object();

        public static GiMethod Get() {
            BufferGiUpdater buffer = Object.FindAnyObjectByType<BufferGiUpdater>();
            if (buffer != null && buffer.enabled) return GiMethod.BufferGi;
            // No updater enabled: the active keyword tells Unity built-in apart from plain Off.
            return LightingKeywords.ActiveGiKeyword == LightingKeywords.GiUnity
                ? GiMethod.UnityBuiltin
                : GiMethod.Off;
        }

        public static void Set(GiMethod method) {
            BufferGiUpdater buffer = Object.FindAnyObjectByType<BufferGiUpdater>();
            if (buffer != null) buffer.enabled = method == GiMethod.BufferGi;

            // Drive the GI keyword for the component-less options; hand it back to the updater for the
            // voxel method. BufferGi with no component falls through to GI_OFF.
            switch (method) {
                case GiMethod.UnityBuiltin:
                    LightingKeywords.ClaimGi(s_owner, LightingKeywords.GiUnity);
                    break;
                case GiMethod.BufferGi when buffer != null:
                    LightingKeywords.ReleaseGi(s_owner);
                    break;
                default:
                    LightingKeywords.ClaimGi(s_owner, LightingKeywords.GiOff);
                    break;
            }
        }
    }
}
