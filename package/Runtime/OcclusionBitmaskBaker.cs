using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    /// <summary>
    /// Bakes a 64-bit directional occlusion bitmask.
    /// Each voxel stores which of 64 Fibonacci-distributed directions have occluded geometry.
    /// </summary>
    [Serializable]
    public class OcclusionBitmaskBaker {
        public const int FibonacciDirectionCount = 64;

        static Vector3[] s_cachedDirections;

        public static Vector3[] GetOrCreateFibonacciDirections() {
            if (s_cachedDirections != null && s_cachedDirections.Length == FibonacciDirectionCount)
                return s_cachedDirections;

            s_cachedDirections = new Vector3[FibonacciDirectionCount];
            float goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f));
            int n = FibonacciDirectionCount;
            for (int i = 0; i < n; ++i) {
                float y = 1f - (i / (float)(n - 1)) * 2f;
                float radius = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float theta = goldenAngle * i;
                float x = Mathf.Cos(theta) * radius;
                float z = Mathf.Sin(theta) * radius;
                s_cachedDirections[i] = new Vector3(x, y, z);
            }
            return s_cachedDirections;
        }

        public ComputeShader bitmaskBakeCompute;

        static readonly int s_boundsMin = Shader.PropertyToID("_BoundsMin");
        static readonly int s_boundsSize = Shader.PropertyToID("_BoundsSize");
        static readonly int s_resolution = Shader.PropertyToID("_Resolution");
        static readonly int s_outBitmask = Shader.PropertyToID("_OutBitmask");
        static readonly int s_sdfTex = Shader.PropertyToID("_SdfTex");
        static readonly int s_sdfMaxSteps = Shader.PropertyToID("_SdfMaxSteps");
        static readonly int s_sdfMinStep = Shader.PropertyToID("_SdfMinStep");
        static readonly int s_sdfThicknessTol = Shader.PropertyToID("_SdfThicknessTol");
        static readonly int s_fibonacciDirs = Shader.PropertyToID("_FibonacciDirs");
        static readonly int s_fibonacciDirCount = Shader.PropertyToID("_FibonacciDirCount");

        public bool TryBake(
            LightingVolume sourceVolume,
            out Texture3D result,
            out string error
        ) {
            result = null;
            error = "";

            if (sourceVolume == null) {
                error = "OcclusionBitmaskBaker: sourceVolume is null";
                return false;
            }
            if (bitmaskBakeCompute == null) {
                error = "OcclusionBitmaskBaker: bitmaskBakeCompute not assigned";
                return false;
            }
            if (sourceVolume.sdfHiresTexture == null) {
                error = "OcclusionBitmaskBaker: sourceVolume has no sdfHiresTexture";
                return false;
            }

            Bounds bounds = sourceVolume.Bounds;
            Vector3Int resolution = sourceVolume.TrimmedMaxResolution;
            int bitmaskSize = resolution.x * resolution.y * resolution.z;

            if (bounds.size.magnitude < 0.01f || resolution.x < 2) {
                error = "OcclusionBitmaskBaker: invalid bounds or resolution";
                return false;
            }

            // Store 64-bit mask as RGBA16_UNorm (sampleable everywhere):
            //   R,G = low/high 16 bits of uint bitmask.x
            //   B,A = low/high 16 bits of uint bitmask.y
            const GraphicsFormat bitmaskFormat = GraphicsFormat.R16G16B16A16_UNorm;
            if (!SystemInfo.IsFormatSupported(bitmaskFormat, GraphicsFormatUsage.Sample)) {
                error = $"OcclusionBitmaskBaker: format {bitmaskFormat} not supported for sampling on this platform";
                return false;
            }

            Vector3[] fibDirs = GetOrCreateFibonacciDirections();
            var fibonacciBuffer = new ComputeBuffer(FibonacciDirectionCount, sizeof(float) * 3);
            fibonacciBuffer.SetData(fibDirs);

            var bitmaskBuffer = new ComputeBuffer(bitmaskSize, sizeof(uint) * 2);
            int kernelIdx = bitmaskBakeCompute.FindKernel("CSMain");

            Vector3 voxelSize = new Vector3(bounds.size.x / resolution.x, bounds.size.y / resolution.y, bounds.size.z / resolution.z);
            float voxelDiag = voxelSize.magnitude;

            bitmaskBakeCompute.SetTexture(kernelIdx, s_sdfTex, sourceVolume.sdfHiresTexture);
            bitmaskBakeCompute.SetInt(s_sdfMaxSteps, 256);
            bitmaskBakeCompute.SetFloat(s_sdfMinStep, voxelDiag * 0.01f);
            bitmaskBakeCompute.SetFloat(s_sdfThicknessTol, voxelDiag * 0.02f);
            bitmaskBakeCompute.SetVector(s_boundsMin, bounds.min);
            bitmaskBakeCompute.SetVector(s_boundsSize, bounds.size);
            bitmaskBakeCompute.SetInts(s_resolution, resolution.x, resolution.y, resolution.z);
            bitmaskBakeCompute.SetBuffer(kernelIdx, s_fibonacciDirs, fibonacciBuffer);
            bitmaskBakeCompute.SetInt(s_fibonacciDirCount, FibonacciDirectionCount);
            bitmaskBakeCompute.SetBuffer(kernelIdx, s_outBitmask, bitmaskBuffer);

            uint groupX = (uint)Mathf.CeilToInt(resolution.x / 8f);
            uint groupY = (uint)Mathf.CeilToInt(resolution.y / 8f);
            uint groupZ = (uint)Mathf.CeilToInt(resolution.z / 8f);

            bitmaskBakeCompute.Dispatch(kernelIdx, (int)groupX, (int)groupY, (int)groupZ);

            fibonacciBuffer.Dispose();

            var readbackRequest = AsyncGPUReadback.Request(bitmaskBuffer);
            readbackRequest.WaitForCompletion();

            if (readbackRequest.hasError) {
                error = "OcclusionBitmaskBaker: readback failed";
                bitmaskBuffer.Dispose();
                return false;
            }

            var readbackData = readbackRequest.GetData<uint>();
            if (readbackData.Length != bitmaskSize * 2) {
                error = $"OcclusionBitmaskBaker: readback size mismatch (got {readbackData.Length}, expected {bitmaskSize * 2})";
                bitmaskBuffer.Dispose();
                return false;
            }

            // Pack uint2 bitmask into RGBA16_UNorm texels (4 ushorts per voxel)
            var packed = new ushort[bitmaskSize * 4];
            for (int voxel = 0; voxel < bitmaskSize; voxel++) {
                uint x = readbackData[voxel * 2 + 0];
                uint y = readbackData[voxel * 2 + 1];

                packed[voxel * 4 + 0] = (ushort)(x & 0xFFFFu);                 // R = low 16 of x
                packed[voxel * 4 + 1] = (ushort)((x >> 16) & 0xFFFFu);         // G = high 16 of x
                packed[voxel * 4 + 2] = (ushort)(y & 0xFFFFu);                 // B = low 16 of y
                packed[voxel * 4 + 3] = (ushort)((y >> 16) & 0xFFFFu);         // A = high 16 of y
            }

            // Create Texture3D
            result = new Texture3D(resolution.x, resolution.y, resolution.z, bitmaskFormat, TextureCreationFlags.None) {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = $"{sourceVolume.BakeRoot.name}_OcclusionBitmask"
            };
            result.SetPixelData(packed, 0);
            result.Apply(false);

            bitmaskBuffer.Dispose();
            Debug.Log($"OcclusionBitmaskBaker: baked successfully ({resolution.x}x{resolution.y}x{resolution.z} = {bitmaskSize} voxels)");
            return true;
        }
    }
}
