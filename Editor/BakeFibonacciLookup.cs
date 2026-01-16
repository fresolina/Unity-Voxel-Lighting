using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lotec.VoxelLighting.Editor {
    /// Editor baker: creates a 2D octahedral lookup texture where each texel stores
    /// the 4 nearest Fibonacci direction indices (0..63) in R,G,B,A channels.
    /// Saved as an 32-bit PNG and imported with point filtering, clamp, no sRGB.
    /// </summary>
    public static class FibonacciIndexGenerator {
        public static int textureSize = 16;
        public static int totalDirections = 64;

        [MenuItem("Tools/Unity Voxel Lighting/Bake Fibonacci Lookup")]
        public static void BakeMenu() {
            GenerateTexture($"VoxelLighting/FibonacciCheat_Indices{textureSize}.png");
        }

        public static void GenerateTexture(string path) {
            Texture2D tex = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false) {
                filterMode = FilterMode.Point, // Crucial: We want exact indices, no blurring
                wrapMode = TextureWrapMode.Clamp,
            };

            Vector3[] fibDirs = GenerateFibonacciPoints(totalDirections);

            for (int y = 0; y < textureSize; y++) {
                for (int x = 0; x < textureSize; x++) {
                    // 1. Get UV in range [-1, 1]
                    Vector2 uv = new Vector2(
                        (float)x / (textureSize - 1) * 2f - 1f,
                        (float)y / (textureSize - 1) * 2f - 1f
                    );

                    // 2. Convert UV to 3D Direction (Unpack Octahedral)
                    Vector3 dir = UnpackOctahedral(uv);

                    // 3. Find 4 nearest Fibonacci indices
                    int[] nearest = GetFourNearest(dir, fibDirs);

                    // Store 4 indices in R, G, B, and A
                    tex.SetPixel(x, y, new Color(
                        nearest[0] / 255f,
                        nearest[1] / 255f,
                        nearest[2] / 255f,
                        nearest[3] / 255f
                    ));
                }
            }

            tex.Apply();

            // Save to Assets
            File.WriteAllBytes(Path.Join(Application.dataPath, path), tex.EncodeToPNG());
            Debug.Log("Texture Saved to " + path);
            string assetPath = Path.Combine("Assets", path);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            // Configure indices importer: point filter, clamp, no sRGB
            TextureImporter tiIdx = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (tiIdx != null) {
                tiIdx.textureType = TextureImporterType.Default;
                tiIdx.sRGBTexture = false;
                tiIdx.wrapMode = TextureWrapMode.Clamp;
                tiIdx.filterMode = FilterMode.Point;
                tiIdx.textureCompression = TextureImporterCompression.Uncompressed;
                tiIdx.isReadable = false;
                tiIdx.SaveAndReimport();
            } else {
                Debug.LogError("Failed to get TextureImporter for " + assetPath);
            }
        }

        static Vector3 UnpackOctahedral(Vector2 uv) {
            Vector3 v = new Vector3(uv.x, 1.0f - Mathf.Abs(uv.x) - Mathf.Abs(uv.y), uv.y);
            if (v.y < 0) {
                // Important: fold x/z simultaneously (mirror across diagonals) to match PackOctahedral in shader.
                float oldX = v.x;
                float oldZ = v.z;
                v.x = (1.0f - Mathf.Abs(oldZ)) * (oldX >= 0.0f ? 1.0f : -1.0f);
                v.z = (1.0f - Mathf.Abs(oldX)) * (oldZ >= 0.0f ? 1.0f : -1.0f);
            }
            return v.normalized;
        }

        static Vector3[] GenerateFibonacciPoints(int n) {
            Vector3[] points = new Vector3[n];
            float phi = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < n; i++) {
                float y = 1f - (i / (float)(n - 1)) * 2f;
                float radius = Mathf.Sqrt(1f - y * y);
                float theta = phi * i;
                points[i] = new Vector3(Mathf.Cos(theta) * radius, y, Mathf.Sin(theta) * radius);
            }
            return points;
        }

        static int[] GetFourNearest(Vector3 target, Vector3[] points) {
            int[] bestIndices = new int[4];
            float[] bestDistances = { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };

            for (int i = 0; i < points.Length; i++) {
                float dist = Vector3.Angle(target, points[i]);
                for (int j = 0; j < 4; j++) {
                    if (dist < bestDistances[j]) {
                        // Shift lower values down
                        for (int k = 3; k > j; k--) {
                            bestDistances[k] = bestDistances[k - 1];
                            bestIndices[k] = bestIndices[k - 1];
                        }
                        bestDistances[j] = dist;
                        bestIndices[j] = i;
                        break;
                    }
                }
            }
            return bestIndices;
        }
    }
}
