using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>Runtime shadow-source selector values. The binder components on the active volume
    /// are the source of truth (their enabled state selects the source); SDF is the default when
    /// no binder is enabled or has data.</summary>
    public enum ShadowSourceMode { SDF, BitmaskPoint, Bitmask8Tap, OcclusionField }

    /// <summary>
    /// Convenience get/set for the shadow source (e.g. for a settings UI). Purely delegates to the
    /// binder components on the active volume - it holds no state of its own. Selecting a source
    /// whose binder is absent (or has no baked data) falls back to SDF.
    /// </summary>
    public static class ShadowSourceSelector {
        public static ShadowSourceMode Get() {
            VoxelVolume volume = LightingManager.Instance != null ? LightingManager.Instance.Volume : null;
            if (volume == null) return ShadowSourceMode.SDF;

            if (volume.TryGetComponent(out VoxelOcclusionField occ) && occ.enabled && occ.HasData)
                return ShadowSourceMode.OcclusionField;
            if (volume.TryGetComponent(out VoxelOcclusionBitmask bitmask) && bitmask.enabled && bitmask.HasData)
                return bitmask.sampling == VoxelOcclusionBitmask.Sampling.Point
                    ? ShadowSourceMode.BitmaskPoint
                    : ShadowSourceMode.Bitmask8Tap;
            return ShadowSourceMode.SDF;
        }

        public static void Set(ShadowSourceMode mode) {
            VoxelVolume volume = LightingManager.Instance != null ? LightingManager.Instance.Volume : null;
            if (volume == null) return;

            bool wantOcc = mode == ShadowSourceMode.OcclusionField;
            bool wantBitmask = mode == ShadowSourceMode.BitmaskPoint || mode == ShadowSourceMode.Bitmask8Tap;

            // Order matters: disable first, then enable, so the binders' own OnEnable mutual
            // exclusion can't disable the one we just chose.
            if (volume.TryGetComponent(out VoxelOcclusionField occ) && !wantOcc) occ.enabled = false;
            if (volume.TryGetComponent(out VoxelOcclusionBitmask bitmask)) {
                if (wantBitmask) {
                    bitmask.sampling = mode == ShadowSourceMode.BitmaskPoint
                        ? VoxelOcclusionBitmask.Sampling.Point
                        : VoxelOcclusionBitmask.Sampling.TrilinearEightTap;
                    bitmask.enabled = true;
                } else {
                    bitmask.enabled = false;
                }
            }
            if (occ != null && wantOcc) occ.enabled = true;
        }
    }
}
