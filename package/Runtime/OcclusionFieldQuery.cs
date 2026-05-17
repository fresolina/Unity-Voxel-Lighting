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
        /// Initialize with the baked direction set. Must be called once after baking or loading.
        /// </summary>
        public void Initialize(Vector3[] bakedDirections, Texture3D[] textures) {
            _directions = bakedDirections;
            _directionCount = bakedDirections.Length;
            _textures = textures;
        }

        /// <summary>
        /// Find the nearest Fibonacci direction index to a given direction.
        /// Also computes which texture and channel (0-3) the direction maps to.
        /// </summary>
        public int FindNearest(Vector3 direction, out int textureIndex, out int channel) {
            int bestIndex = 0;
            float bestDot = -2f;
            for (int i = 0; i < _directionCount; i++) {
                float d = Vector3.Dot(direction, _directions[i]);
                if (d > bestDot) {
                    bestDot = d;
                    bestIndex = i;
                }
            }
            textureIndex = bestIndex / 4;
            channel = bestIndex % 4;
            return bestIndex;
        }

        /// <summary>
        /// Call once per frame to update shader globals with the current sun direction.
        /// </summary>
        public void ApplyShaderGlobals() {
            if (_directions == null || _directionCount == 0 || _textures == null) return;

            Vector3 sunDirection = RenderSettings.sun != null ? -RenderSettings.sun.transform.forward : Vector3.down;

            FindNearest(sunDirection, out int texIndex, out int channel);

            Shader.SetGlobalVector(s_sunDirection, sunDirection);
            Shader.SetGlobalInt(s_sunChannel, channel);
            if (texIndex >= 0 && texIndex < _textures.Length && _textures[texIndex] != null)
                Shader.SetGlobalTexture(s_activeTex, _textures[texIndex]);
        }
    }
}
