using System.Collections.Generic;
using UnityEngine;

namespace Lotec.Lighting {
    static class Fibonacci {
        /// <summary>
        /// Generates a deterministic set of sample directions using a Fibonacci sphere distribution.
        /// These directions are used by the bakers to cover lighting directions evenly without storing
        /// a hand-authored lookup table. When <paramref name="hemisphereOnly"/> is enabled, the set is
        /// restricted to the upper hemisphere so directional sunlight never samples below the horizon.
        /// </summary>
        public static Vector3[] GenerateFibonacciDirections(int count, bool hemisphereOnly) {
            if (!hemisphereOnly) {
                return GenerateFibonacciSphere(count);
            }

            // Generate double the amount, then remove lower hemisphere directions.
            Vector3[] all = GenerateFibonacciSphere(count * 2);
            var upper = new List<Vector3>(count);
            foreach (Vector3 direction in all) {
                if (direction.y >= 0f)
                    upper.Add(direction);
                if (upper.Count >= count)
                    break;
            }

            return upper.ToArray();
        }

        /// <summary>
        /// Places points on the unit sphere using the golden-angle spiral, which gives a cheap,
        /// roughly uniform distribution over the sphere. This avoids the pole clustering you get
        /// from latitude/longitude grids and is stable for any sample count.
        /// </summary>
        static Vector3[] GenerateFibonacciSphere(int count) {
            Vector3[] directions = new Vector3[count];
            float goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < count; i++) {
                float y = 1f - (i + 0.5f) / count * 2f;
                float radius = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float theta = goldenAngle * i;
                directions[i] = new Vector3(Mathf.Cos(theta) * radius, y, Mathf.Sin(theta) * radius);
            }
            return directions;
        }
    }
}
