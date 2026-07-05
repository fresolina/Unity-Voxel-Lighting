using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>Which GI system is active. The GI updater components are the source of truth
    /// (their enabled state selects the method); Off when neither is enabled.</summary>
    public enum GiMethod { Off, VoxelGi, BufferGi }

    /// <summary>
    /// Convenience get/set for the GI method (e.g. for a settings UI). Purely delegates to the GI
    /// updater components' enabled state - enabling one disables the other (the updaters enforce
    /// that themselves). Selecting a method whose component is absent leaves GI off.
    /// </summary>
    public static class GiMethodSelector {
        public static GiMethod Get() {
            BufferGiUpdater buffer = Object.FindAnyObjectByType<BufferGiUpdater>();
            if (buffer != null && buffer.enabled) return GiMethod.BufferGi;
            GiFieldUpdater texture = Object.FindAnyObjectByType<GiFieldUpdater>();
            if (texture != null && texture.enabled) return GiMethod.VoxelGi;
            return GiMethod.Off;
        }

        public static void Set(GiMethod method) {
            BufferGiUpdater buffer = Object.FindAnyObjectByType<BufferGiUpdater>();
            GiFieldUpdater texture = Object.FindAnyObjectByType<GiFieldUpdater>();
            // Disable first, then enable, so the updaters' own OnEnable mutual exclusion can't
            // disable the one we just chose.
            if (texture != null && method != GiMethod.VoxelGi) texture.enabled = false;
            if (buffer != null && method != GiMethod.BufferGi) buffer.enabled = false;
            if (texture != null && method == GiMethod.VoxelGi) texture.enabled = true;
            if (buffer != null && method == GiMethod.BufferGi) buffer.enabled = true;
        }
    }
}
