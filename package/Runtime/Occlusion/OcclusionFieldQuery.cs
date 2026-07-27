using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Sun-direction helpers shared by the occlusion-field and bitmask shadow paths: read the
    /// current sun direction and map it to the nearest baked Fibonacci direction. (The runtime
    /// publishing now lives on the per-feature binders, e.g. <see cref="VoxelOcclusionField"/>.)
    /// </summary>
    public static class OcclusionFieldQuery {
        /// <summary>Current sun direction (negated forward of the sun light, or down if none).</summary>
        public static Vector3 GetSunDirection() {
            return RenderSettings.sun != null ? -RenderSettings.sun.transform.forward : Vector3.down;
        }

        /// <summary>Index of the candidate most aligned with <paramref name="direction"/>.</summary>
        public static int FindNearestDirection(Vector3 direction, Vector3[] candidates, int count) {
            FindNearestTwoDirections(direction, candidates, count, out int bestIndex, out _, out _);
            return bestIndex;
        }

        /// <summary>
        /// The two candidates most aligned with <paramref name="direction"/> plus a blend weight
        /// toward the second, in 0..0.5.
        ///
        /// Snapping to the nearest alone leaves an angular error of half the direction spacing - 15
        /// degrees at 8 directions - which displaces a shadow by ~0.28x the occluder distance and pops
        /// as the sun crosses between cells.
        ///
        /// The weight uses a kernel that VANISHES at the THIRD nearest: w_i = dot_i - dot_3. That is
        /// what makes the sweep continuous. Weighting purely by the first two (say a/(a+b)) still pops,
        /// because the second-nearest itself changes discontinuously as the sun moves - the blend
        /// target teleports from one neighbour to another while carrying real weight. Anchoring on the
        /// third means a direction's weight is already zero at the moment it enters or leaves the pair,
        /// so every swap is silent.
        /// </summary>
        public static void FindNearestTwoDirections(
            Vector3 direction,
            Vector3[] candidates,
            int count,
            out int bestIndex,
            out int secondIndex,
            out float blendToSecond
        ) {
            bestIndex = 0;
            secondIndex = -1;
            blendToSecond = 0f;

            float d0 = -2f, d1 = -2f, d2 = -2f;
            for (int i = 0; i < count; i++) {
                float d = Vector3.Dot(direction, candidates[i]);
                if (d > d0) {
                    d2 = d1; d1 = d0; d0 = d;
                    secondIndex = bestIndex; bestIndex = i;
                } else if (d > d1) {
                    d2 = d1; d1 = d;
                    secondIndex = i;
                } else if (d > d2) {
                    d2 = d;
                }
            }

            if (count < 2) {
                secondIndex = -1;
                return;
            }
            // With only two candidates there is no third to anchor on; fall back to the far pole,
            // which keeps the weight well-defined and still bounded by 0.5.
            if (count < 3)
                d2 = -1f;

            float w0 = d0 - d2;
            float w1 = d1 - d2;
            if (w0 + w1 > 1e-6f)
                blendToSecond = w1 / (w0 + w1);
        }
    }
}
