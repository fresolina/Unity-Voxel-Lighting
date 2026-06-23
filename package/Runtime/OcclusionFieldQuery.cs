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
            int bestIndex = 0;
            float bestDot = -2f;
            for (int i = 0; i < count; i++) {
                float d = Vector3.Dot(direction, candidates[i]);
                if (d > bestDot) {
                    bestDot = d;
                    bestIndex = i;
                }
            }
            return bestIndex;
        }
    }
}
