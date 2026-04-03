using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    public static class Texture3DReadback {
        static readonly Dictionary<string, Material> s_materialCache = new Dictionary<string, Material>();

        /// <summary>
        /// Read back RGBA data from a 3D texture (Texture3D or RenderTexture) asynchronously.
        /// Uses the default path with no conversion shader.
        /// </summary>
        /// <param name="tex">Source texture (Texture3D or RenderTexture).</param>
        /// <param name="onComplete">Callback with success flag, pixel array, width, height, depth.</param>
        public static void ReadbackRGBAAsync(Texture tex, Action<bool, Color[], int, int, int> onComplete) {
            ReadbackRGBAAsync(tex, null, onComplete);
        }

        /// <summary>
        /// Read back RGBA data from a 3D texture (Texture3D or RenderTexture) asynchronously.
        /// If a conversion shader name is provided, each slice is rendered through that shader
        /// (e.g., for HDR packed formats like R11G11B10) and then read back as RGBAFloat.
        /// </summary>
        /// <param name="tex">Source texture (Texture3D or RenderTexture).</param>
        /// <param name="conversionShaderName">Optional shader name (e.g., "Hidden/Unpack3D").</param>
        /// <param name="onComplete">Callback with success flag, pixel array, width, height, depth.</param>
        public static void ReadbackRGBAAsync(Texture tex, string conversionShaderName, Action<bool, Color[], int, int, int> onComplete) {
            if (tex == null) {
                onComplete(false, null, 0, 0, 0);
                return;
            }

            if (tex is RenderTexture rt) {
                ReadbackRenderTexture(rt, conversionShaderName, onComplete);
                return;
            }

            if (tex is Texture3D t3) {
                if (!t3.isReadable) {
                    onComplete(false, null, 0, 0, 0);
                    return;
                }
                try {
                    Color[] pixels = t3.GetPixels();
                    onComplete(true, pixels, t3.width, t3.height, t3.depth);
                } catch {
                    onComplete(false, null, 0, 0, 0);
                }
                return;
            }

            onComplete(false, null, 0, 0, 0);
        }

        /// <summary>
        /// Read back RGBA data from a RenderTexture (3D) using optional conversion shader.
        /// </summary>
        /// <param name="rt">Source RenderTexture (3D).</param>
        /// <param name="conversionShaderName">Optional conversion shader name.</param>
        /// <param name="onComplete">Callback with success flag, pixel array, width, height, depth.</param>
        static void ReadbackRenderTexture(RenderTexture rt, string conversionShaderName, Action<bool, Color[], int, int, int> onComplete) {
            if (rt == null) { onComplete(false, null, 0, 0, 0); return; }
            if (!rt.IsCreated()) rt.Create();

            int w = rt.width, h = rt.height, d = rt.volumeDepth;
            Color[] pixels = new Color[w * h * d];
            int remaining = d;

            if (string.IsNullOrEmpty(conversionShaderName)) {
                // Fallback: synchronous readback per slice
                try {
                    ReadbackRenderTextureSync(rt, pixels, w, h, d);
                    onComplete(true, pixels, w, h, d);
                } catch {
                    onComplete(false, null, 0, 0, 0);
                }
                return;
            }

            Material mat = GetOrCreateMaterial(conversionShaderName);
            if (mat == null) { onComplete(false, null, 0, 0, 0); return; }

            for (int zi = 0; zi < d; ++zi) {
                int z = zi; // capture
                RenderTexture tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                tmp.wrapMode = TextureWrapMode.Clamp;
                tmp.filterMode = FilterMode.Bilinear;

                mat.SetTexture("_VolumeTex", rt);
                mat.SetFloat("_SliceZ", (z + 0.5f) / (float)d);
                Graphics.Blit(null, tmp, mat);

                AsyncGPUReadback.Request(tmp, 0, TextureFormat.RGBAFloat, req => {
                    if (!req.hasError) {
                        try {
                            var arr = req.GetData<Color>();
                            int baseIdx = z * w * h;
                            for (int i = 0; i < arr.Length && (baseIdx + i) < pixels.Length; ++i) {
                                pixels[baseIdx + i] = arr[i];
                            }
                        } catch {
                            // ignore
                        }
                    }

                    RenderTexture.ReleaseTemporary(tmp);
                    remaining--;
                    if (remaining == 0) {
                        onComplete(true, pixels, w, h, d);
                    }
                });
            }
        }

        /// <summary>
        /// Synchronous per-slice readback for RenderTexture (3D) into an existing pixel array.
        /// Used as a fallback when no conversion shader is provided.
        /// </summary>
        /// <param name="rt">Source RenderTexture (3D).</param>
        /// <param name="pixels">Destination pixel array (w*h*d).</param>
        /// <param name="w">Width.</param>
        /// <param name="h">Height.</param>
        /// <param name="d">Depth (slices).</param>
        static void ReadbackRenderTextureSync(RenderTexture rt, Color[] pixels, int w, int h, int d) {
            RenderTexture prev = RenderTexture.active;
            Texture2D slice = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            for (int z = 0; z < d; ++z) {
                Graphics.SetRenderTarget(rt, 0, CubemapFace.Unknown, z);
                slice.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                slice.Apply(false, false);
                Color[] c = slice.GetPixels();
                int baseIdx = z * w * h;
                for (int i = 0; i < c.Length; ++i) {
                    pixels[baseIdx + i] = c[i];
                }
            }
            RenderTexture.active = prev;
            UnityEngine.Object.DestroyImmediate(slice);
        }

        /// <summary>
        /// Find or create a cached material for the given shader name.
        /// </summary>
        /// <param name="shaderName">Shader name used for conversion.</param>
        /// <returns>A cached Material instance, or null if shader not found.</returns>
        static Material GetOrCreateMaterial(string shaderName) {
            if (s_materialCache.TryGetValue(shaderName, out var mat) && mat != null) return mat;
            Shader s = Shader.Find(shaderName);
            if (s == null) return null;
            mat = new Material(s) { hideFlags = HideFlags.DontSave };
            s_materialCache[shaderName] = mat;
            return mat;
        }
    }
}
