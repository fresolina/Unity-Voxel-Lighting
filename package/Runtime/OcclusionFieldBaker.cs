using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    /// <summary>
    /// Bakes a directional occlusion field where each direction gets a full 8-bit channel.
    /// Stores the "lit" value (0 = shadow, 1 = fully lit) per Fibonacci direction per voxel.
    /// Output: N/4 RGBA32 Texture3D assets (4 directions packed per texture).
    /// Unlike OcclusionBitmaskBaker, this supports hardware trilinear interpolation.
    /// </summary>
    [Serializable]
    public class OcclusionFieldBaker {
        public enum DirectionCount {
            Dir32 = 32,
            Dir64 = 64,
            Dir128 = 128,
            Dir256 = 256,
        }

        public ComputeShader occlusionFieldBakeCompute;

        [Tooltip("Number of Fibonacci directions to bake.")]
        public DirectionCount directionCount = DirectionCount.Dir64;

        [Tooltip("Use only upper hemisphere directions (Y >= 0). Useful when the sun never goes below the horizon.")]
        public bool hemisphereOnly;

        [Tooltip("Softness of shadow penumbra. Higher = sharper. 0 = binary.")]
        [Range(1f, 128f)]
        public float shadowSoftness = 3f;

        static readonly int s_boundsMin = Shader.PropertyToID("_BoundsMin");
        static readonly int s_boundsSize = Shader.PropertyToID("_BoundsSize");
        static readonly int s_resolution = Shader.PropertyToID("_Resolution");
        static readonly int s_shadowSoftness = Shader.PropertyToID("_ShadowSoftness");
        static readonly int s_directionOffset = Shader.PropertyToID("_DirectionOffset");
        static readonly int s_outOcclusion = Shader.PropertyToID("_OutOcclusion");
        static readonly int s_fibonacciDirs = Shader.PropertyToID("_FibonacciDirs");
        static readonly int s_sdfMaxSteps = Shader.PropertyToID("_SdfMaxSteps");
        static readonly int s_sdfMinStep = Shader.PropertyToID("_SdfMinStep");
        static readonly int s_sdfThicknessTol = Shader.PropertyToID("_SdfThicknessTol");
        static readonly int s_sdfTex = Shader.PropertyToID("_SdfTex");

        /// <summary>
        /// Generate Fibonacci sphere directions for a given count.
        /// Uses centered sampling (i + 0.5) / N consistent with the shader.
        /// If hemisphereOnly is true, only directions with Y >= 0 are kept (re-normalized distribution).
        /// </summary>
        public static Vector3[] GenerateFibonacciDirections(int count, bool hemisphereOnly) {
            if (hemisphereOnly) {
                // Generate 2x directions, keep only upper hemisphere, then trim to count.
                var all = GenerateFibonacciSphere(count * 2);
                var upper = new System.Collections.Generic.List<Vector3>(count);
                foreach (var d in all) {
                    if (d.y >= 0f) upper.Add(d);
                    if (upper.Count >= count) break;
                }
                // If we didn't get enough (shouldn't happen with 2x), fill from full sphere.
                if (upper.Count < count) {
                    Debug.LogWarning($"OcclusionFieldBaker: hemisphere only yielded {upper.Count}/{count} directions, falling back to full sphere for remainder.");
                    var full = GenerateFibonacciSphere(count);
                    for (int i = upper.Count; i < count; i++)
                        upper.Add(full[i]);
                }
                return upper.ToArray();
            }
            return GenerateFibonacciSphere(count);
        }

        static Vector3[] GenerateFibonacciSphere(int n) {
            var dirs = new Vector3[n];
            float goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < n; i++) {
                float y = 1f - ((i + 0.5f) / n) * 2f;
                float radius = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float theta = goldenAngle * i;
                dirs[i] = new Vector3(Mathf.Cos(theta) * radius, y, Mathf.Sin(theta) * radius);
            }
            return dirs;
        }

        /// <summary>
        /// Bake the occlusion field into N/4 RGBA32 Texture3D assets.
        /// Each texture stores 4 directions (one per RGBA channel).
        /// </summary>
        public bool TryBake(
            LightingVolume sourceVolume,
            out Texture3D[] resultTextures,
            out Vector3[] bakedDirections,
            out string error
        ) {
            resultTextures = null;
            bakedDirections = null;
            error = "";

            if (sourceVolume == null) {
                error = "OcclusionFieldBaker: sourceVolume is null";
                return false;
            }
            if (occlusionFieldBakeCompute == null) {
                error = "OcclusionFieldBaker: occlusionFieldBakeCompute not assigned";
                return false;
            }
            if (sourceVolume.sdfHiresTexture == null) {
                error = "OcclusionFieldBaker: sourceVolume has no sdfHiresTexture";
                return false;
            }

            int numDirections = (int)directionCount;
            int textureCount = numDirections / 4;

            Bounds bounds = sourceVolume.Bounds;
            Vector3Int resolution = sourceVolume.TrimmedMaxResolution;
            int voxelCount = resolution.x * resolution.y * resolution.z;

            if (bounds.size.magnitude < 0.01f || resolution.x < 2) {
                error = "OcclusionFieldBaker: invalid bounds or resolution";
                return false;
            }

            if (!SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormatUsage.Sample)) {
                error = "OcclusionFieldBaker: RGBA8_UNorm format not supported for sampling on this platform";
                return false;
            }

            // Generate directions
            bakedDirections = GenerateFibonacciDirections(numDirections, hemisphereOnly);

            // Compute voxel metrics
            Vector3 voxelSize = new Vector3(
                bounds.size.x / resolution.x,
                bounds.size.y / resolution.y,
                bounds.size.z / resolution.z
            );
            float voxelDiag = voxelSize.magnitude;

            int kernelIdx = occlusionFieldBakeCompute.FindKernel("CSMain");

            // Set shared uniforms
            occlusionFieldBakeCompute.SetTexture(kernelIdx, s_sdfTex, sourceVolume.sdfHiresTexture);
            occlusionFieldBakeCompute.SetVector(s_boundsMin, bounds.min);
            occlusionFieldBakeCompute.SetVector(s_boundsSize, bounds.size);
            occlusionFieldBakeCompute.SetInts(s_resolution, resolution.x, resolution.y, resolution.z);
            occlusionFieldBakeCompute.SetFloat(s_shadowSoftness, shadowSoftness);
            occlusionFieldBakeCompute.SetInt(s_sdfMaxSteps, 256);
            occlusionFieldBakeCompute.SetFloat(s_sdfMinStep, voxelDiag * 0.01f);
            occlusionFieldBakeCompute.SetFloat(s_sdfThicknessTol, voxelDiag * 0.02f);

            uint groupX = (uint)Mathf.CeilToInt(resolution.x / 8f);
            uint groupY = (uint)Mathf.CeilToInt(resolution.y / 8f);
            uint groupZ = (uint)Mathf.CeilToInt(resolution.z / 8f);

            resultTextures = new Texture3D[textureCount];

            // Upload all directions as a structured buffer (4 at a time for each dispatch batch)
            var dirBuffer = new ComputeBuffer(4, sizeof(float) * 3);

            try {
                // Bake 4 directions at a time, producing one RGBA texture per batch
                for (int texIdx = 0; texIdx < textureCount; texIdx++) {
                    int dirOffset = texIdx * 4;

                    // Upload the 4 directions for this batch
                    var batchDirs = new Vector3[4];
                    for (int c = 0; c < 4; c++)
                        batchDirs[c] = bakedDirections[dirOffset + c];
                    dirBuffer.SetData(batchDirs);

                    // Output buffer: 4 floats (RGBA) per voxel
                    var outBuffer = new ComputeBuffer(voxelCount * 4, sizeof(float));
                    occlusionFieldBakeCompute.SetBuffer(kernelIdx, s_fibonacciDirs, dirBuffer);
                    occlusionFieldBakeCompute.SetInt(s_directionOffset, dirOffset);
                    occlusionFieldBakeCompute.SetBuffer(kernelIdx, s_outOcclusion, outBuffer);

                    occlusionFieldBakeCompute.Dispatch(kernelIdx, (int)groupX, (int)groupY, (int)groupZ);

                    // Readback
                    var readbackRequest = AsyncGPUReadback.Request(outBuffer);
                    readbackRequest.WaitForCompletion();

                    if (readbackRequest.hasError) {
                        error = $"OcclusionFieldBaker: readback failed for texture {texIdx}";
                        outBuffer.Dispose();
                        DisposePartialResults(resultTextures, texIdx);
                        resultTextures = null;
                        return false;
                    }

                    var readbackData = readbackRequest.GetData<float>();
                    if (readbackData.Length != voxelCount * 4) {
                        error = $"OcclusionFieldBaker: readback size mismatch for texture {texIdx} (got {readbackData.Length}, expected {voxelCount * 4})";
                        outBuffer.Dispose();
                        DisposePartialResults(resultTextures, texIdx);
                        resultTextures = null;
                        return false;
                    }

                    // Pack float values into RGBA8 bytes
                    var packed = new byte[voxelCount * 4];
                    for (int i = 0; i < voxelCount * 4; i++) {
                        packed[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(readbackData[i] * 255f), 0, 255);
                    }

                    var tex = new Texture3D(resolution.x, resolution.y, resolution.z, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None) {
                        filterMode = FilterMode.Trilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        name = $"{sourceVolume.BakeRoot.name}_OcclusionField_{texIdx:D2}"
                    };
                    tex.SetPixelData(packed, 0);
                    tex.Apply(false);

                    resultTextures[texIdx] = tex;
                    outBuffer.Dispose();

                    Debug.Log($"OcclusionFieldBaker: baked texture {texIdx + 1}/{textureCount} (directions {dirOffset}..{dirOffset + 3})");
                }
            } finally {
                dirBuffer.Dispose();
            }

            Debug.Log($"OcclusionFieldBaker: baked {textureCount} textures ({numDirections} directions, {resolution.x}x{resolution.y}x{resolution.z} = {voxelCount} voxels, hemisphere={hemisphereOnly})");
            return true;
        }

        static void DisposePartialResults(Texture3D[] textures, int upTo) {
            for (int i = 0; i < upTo; i++) {
                if (textures[i] != null)
                    UnityEngine.Object.DestroyImmediate(textures[i]);
            }
        }
    }
}
