using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lotec.VoxelLighting.Editor {
    /// <summary>
    /// Generates a tileable blue-noise texture using a lightweight
    /// swap-optimization pass over random values.
    /// </summary>
    public static class BlueNoiseGenerator {
        const int DefaultSize = 128; // Should match Math.hlsl BLUE_NOISE_SIZE
        const int DefaultIterations = 8;
        const int DefaultBlurRadius = 2;
        const int DefaultSeed = 1337;

        [MenuItem("Tools/Unity Voxel Lighting/Generate Blue Noise Texture")]
        public static void GenerateMenu() {
            GenerateTexture($"VoxelLighting/BlueNoise_{DefaultSize}.png", DefaultSize, DefaultIterations, DefaultBlurRadius, DefaultSeed);
        }

        public static void GenerateTexture(string relativePath, int size, int iterations, int blurRadius, int seed) {
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
            if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));
            if (blurRadius <= 0) throw new ArgumentOutOfRangeException(nameof(blurRadius));

            int pixelCount = size * size;
            float[] values = new float[pixelCount];
            float[] blurred = new float[pixelCount];
            float[] error = new float[pixelCount];

            System.Random rng = new System.Random(seed);
            for (int i = 0; i < pixelCount; i++) {
                values[i] = (float)rng.NextDouble();
            }

            float[] kernel = BuildGaussianKernel(blurRadius, out int kernelSize);
            int swapBatch = Mathf.Max(16, pixelCount / 8);

            for (int iter = 0; iter < iterations; iter++) {
                ConvolveWrapped(values, blurred, size, kernel, kernelSize, blurRadius);
                for (int i = 0; i < pixelCount; i++) {
                    error[i] = values[i] - blurred[i];
                }

                int[] maxIndices = FindExtremeIndices(error, swapBatch, true);
                int[] minIndices = FindExtremeIndices(error, swapBatch, false);

                int swapCount = Mathf.Min(maxIndices.Length, minIndices.Length);
                for (int i = 0; i < swapCount; i++) {
                    int a = maxIndices[i];
                    int b = minIndices[i];
                    float tmp = values[a];
                    values[a] = values[b];
                    values[b] = tmp;
                }
            }

            // Rank-normalize so the final distribution spans 0..1 evenly.
            int[] order = BuildRankOrder(values);
            float invCount = 1f / Mathf.Max(1, pixelCount - 1);
            for (int rank = 0; rank < order.Length; rank++) {
                values[order[rank]] = rank * invCount;
            }

            Texture2D tex = new Texture2D(size, size, TextureFormat.R8, false) {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                name = $"BlueNoise_{size}"
            };

            Color[] pixels = new Color[pixelCount];
            for (int i = 0; i < pixelCount; i++) {
                float v = values[i];
                pixels[i] = new Color(v, v, v, 1f);
            }
            tex.SetPixels(pixels);
            tex.Apply();

            string fullPath = Path.Join(Application.dataPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Application.dataPath);
            File.WriteAllBytes(fullPath, tex.EncodeToPNG());

            string assetPath = Path.Combine("Assets", relativePath).Replace("\\", "/");
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null) {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Point;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.isReadable = false;
                importer.mipmapEnabled = false;
                importer.SaveAndReimport();
            } else {
                Debug.LogError("Failed to get TextureImporter for " + assetPath);
            }

            Debug.Log("Blue noise texture saved to " + assetPath);
        }

        static float[] BuildGaussianKernel(int radius, out int size) {
            size = radius * 2 + 1;
            float sigma = Mathf.Max(0.5f, radius * 0.5f);
            float twoSigmaSq = 2f * sigma * sigma;
            float[] kernel = new float[size * size];
            float sum = 0f;

            for (int y = -radius; y <= radius; y++) {
                for (int x = -radius; x <= radius; x++) {
                    float weight = Mathf.Exp(-(x * x + y * y) / twoSigmaSq);
                    int index = (y + radius) * size + (x + radius);
                    kernel[index] = weight;
                    sum += weight;
                }
            }

            float invSum = 1f / Mathf.Max(1e-6f, sum);
            for (int i = 0; i < kernel.Length; i++) {
                kernel[i] *= invSum;
            }
            return kernel;
        }

        static void ConvolveWrapped(float[] source, float[] target, int size, float[] kernel, int kernelSize, int radius) {
            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    float accum = 0f;
                    for (int ky = -radius; ky <= radius; ky++) {
                        int sy = (y + ky + size) % size;
                        int kernelRow = (ky + radius) * kernelSize;
                        for (int kx = -radius; kx <= radius; kx++) {
                            int sx = (x + kx + size) % size;
                            float weight = kernel[kernelRow + (kx + radius)];
                            accum += source[sy * size + sx] * weight;
                        }
                    }
                    target[y * size + x] = accum;
                }
            }
        }

        static int[] FindExtremeIndices(float[] values, int count, bool findMax) {
            int[] indices = new int[Mathf.Min(count, values.Length)];
            float[] bestValues = new float[indices.Length];
            for (int i = 0; i < bestValues.Length; i++) {
                bestValues[i] = findMax ? float.NegativeInfinity : float.PositiveInfinity;
            }

            for (int i = 0; i < values.Length; i++) {
                float v = values[i];
                for (int j = 0; j < indices.Length; j++) {
                    bool isBetter = findMax ? v > bestValues[j] : v < bestValues[j];
                    if (isBetter) {
                        for (int k = indices.Length - 1; k > j; k--) {
                            bestValues[k] = bestValues[k - 1];
                            indices[k] = indices[k - 1];
                        }
                        bestValues[j] = v;
                        indices[j] = i;
                        break;
                    }
                }
            }

            return indices;
        }

        static int[] BuildRankOrder(float[] values) {
            int[] order = new int[values.Length];
            for (int i = 0; i < values.Length; i++) {
                order[i] = i;
            }

            Array.Sort(order, (a, b) => values[a].CompareTo(values[b]));
            return order;
        }
    }
}
