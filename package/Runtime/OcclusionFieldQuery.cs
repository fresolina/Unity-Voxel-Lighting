using System;
using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Runtime query helper for the occlusion field.
    /// Finds the sun direction, maps it to the nearest Fibonacci direction index,
    /// and sets shader globals for the fragment shader to sample the correct textures.
    /// </summary>
    [Serializable]
    public class OcclusionFieldQuery {
        static readonly int s_sunDirection = Shader.PropertyToID("_OccFieldSunDir");
        static readonly int s_sunChannel = Shader.PropertyToID("_OccFieldSunChannel");
        static readonly int s_activeTex = Shader.PropertyToID("_OccFieldTex");

        Vector3[] _directions;
        int _directionCount;
        Texture3D[] _textures;

        /// <summary>
        /// Returns the current sun direction (negated forward of the sun light, or Vector3.down if none).
        /// </summary>
        public static Vector3 GetSunDirection() {
            return RenderSettings.sun != null ? -RenderSettings.sun.transform.forward : Vector3.down;
        }

        /// <summary>
        /// Finds the index of the direction in <paramref name="candidates"/> most aligned with <paramref name="direction"/>.
        /// </summary>
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

        /// <summary>
        /// Initialize with the baked direction set. Must be called once after baking or loading.
        /// </summary>
        public void Initialize(Vector3[] bakedDirections, Texture3D[] textures) {
            _directions = bakedDirections;
            _directionCount = bakedDirections.Length;
            _textures = textures;
        }

        /// <summary>
        /// Call once per frame to update shader globals with the current sun direction.
        /// </summary>
        public void ApplyShaderGlobals() {
            if (_directions == null || _directionCount == 0 || _textures == null) return;

            Vector3 sunDirection = GetSunDirection();
            int bestIndex = FindNearestDirection(sunDirection, _directions, _directionCount);
            int texIndex = bestIndex / 4;
            int channel = bestIndex % 4;

            Shader.SetGlobalVector(s_sunDirection, sunDirection);
            Shader.SetGlobalInt(s_sunChannel, channel);
            if (texIndex >= 0 && texIndex < _textures.Length && _textures[texIndex] != null)
                Shader.SetGlobalTexture(s_activeTex, _textures[texIndex]);
        }
    }
}
