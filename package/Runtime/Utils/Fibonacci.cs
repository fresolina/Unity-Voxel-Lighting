using UnityEngine;

namespace Lotec.Lighting {
    static class Fibonacci {
        /// <summary>
        /// Deterministic set of sample directions, used by the bakers to cover lighting directions
        /// evenly without storing a hand-authored lookup table. When <paramref name="hemisphereOnly"/>
        /// is enabled the set is restricted to the upper hemisphere so directional sunlight never
        /// samples below the horizon.
        ///
        /// Index 0 is pinned to straight up and the rest spiral outwards from it. A plain Fibonacci
        /// spiral puts its first point ~20 degrees off the pole at 8 directions, so a sun near the
        /// zenith - where it spends most of its time, and where a tilted shadow is most obvious -
        /// always snapped to a slanted direction. Pinning the pole makes overhead sun exact.
        ///
        /// Callers must not assume this set is stable across versions: the bakers store the
        /// directions they baked with on the binder, and the runtime reads that stored array, so
        /// changing this formula never invalidates existing baked data.
        /// </summary>
        public static Vector3[] GenerateFibonacciDirections(int count, bool hemisphereOnly) {
            if (count <= 0)
                return new Vector3[0];

            var directions = new Vector3[count];
            directions[0] = Vector3.up;
            if (count == 1)
                return directions;

            // Equal-area spacing: the surface area of a sphere between two heights is proportional to
            // their difference, so stepping y linearly spreads the directions evenly by solid angle.
            // Pinning index 0 to the pole puts it at the TOP of its band rather than the centre - a
            // deliberate trade of a little uniformity for an exact overhead direction.
            float span = hemisphereOnly ? 1f : 2f;
            float goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 1; i < count; i++) {
                float y = 1f - span * i / count;
                float radius = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float theta = goldenAngle * i;
                directions[i] = new Vector3(Mathf.Cos(theta) * radius, y, Mathf.Sin(theta) * radius);
            }
            return directions;
        }
    }
}
