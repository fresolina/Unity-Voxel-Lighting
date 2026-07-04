using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    /// <summary>
    /// Bakes a directional occlusion bitmask.
    /// Each voxel stores which of N Fibonacci-distributed directions have occluded geometry.
    /// Supports 8-bit (R8), 32-bit (RG16), and 64-bit (RGBA16) bitmask textures.
    /// </summary>
    [Serializable]
    public class OcclusionBitmaskBake {
        public enum BitmaskDirectionCount {
            Dir8 = 8,
            Dir32 = 32,
            Dir64 = 64,
        }

        [Tooltip("Number of directions to bake into the bitmask.")]
        public BitmaskDirectionCount directionCount = BitmaskDirectionCount.Dir64;

        [Tooltip("Use only upper hemisphere directions (Y >= 0). Useful when the sun never goes below the horizon.")]
        public bool hemisphereOnly = true;

        public ComputeShader bitmaskBakeCompute;

        static readonly int s_boundsMin = Shader.PropertyToID("_BoundsMin");
        static readonly int s_boundsSize = Shader.PropertyToID("_BoundsSize");
        static readonly int s_resolution = Shader.PropertyToID("_Resolution");
        static readonly int s_outBitmask = Shader.PropertyToID("_OutBitmask");
        static readonly int s_sdfHires = Shader.PropertyToID("_SdfHires");
        static readonly int s_raymarchMaxSteps = Shader.PropertyToID("_RaymarchMaxSteps");
        static readonly int s_raymarchMinStep = Shader.PropertyToID("_RaymarchMinStep");
        static readonly int s_raymarchEpsilon = Shader.PropertyToID("_RaymarchEpsilon");
        static readonly int s_directions = Shader.PropertyToID("_Directions");
        static readonly int s_bitmaskDirCount = Shader.PropertyToID("_BitmaskDirCount");

        static GraphicsFormat GetTextureFormat(int dirCount) {
            return dirCount switch {
                <= 8 => GraphicsFormat.R8_UNorm,
                <= 32 => GraphicsFormat.R16G16_UNorm,
                _ => GraphicsFormat.R16G16B16A16_UNorm,
            };
        }

        public bool TryBake(
            VoxelVolume sourceVolume,
            out Texture3D result,
            out Vector3[] bakedDirections,
            out string error
        ) {
            result = null;
            bakedDirections = null;
            error = "";

            if (sourceVolume == null) {
                error = "OcclusionBitmaskBake: sourceVolume is null";
                return false;
            }
            if (bitmaskBakeCompute == null) {
                error = "OcclusionBitmaskBake: bitmaskBakeCompute not assigned";
                return false;
            }
            if (sourceVolume.sdfHiresTexture == null) {
                error = "OcclusionBitmaskBake: sourceVolume has no sdfHiresTexture";
                return false;
            }

            int dirCount = (int)directionCount;
            Bounds bounds = sourceVolume.Bounds;
            Vector3Int resolution = sourceVolume.TrimmedMaxResolution;
            int bitmaskSize = resolution.x * resolution.y * resolution.z;

            if (bounds.size.magnitude < 0.01f || resolution.x < 2) {
                error = "OcclusionBitmaskBake: invalid bounds or resolution";
                return false;
            }

            GraphicsFormat bitmaskFormat = GetTextureFormat(dirCount);
            if (!SystemInfo.IsFormatSupported(bitmaskFormat, GraphicsFormatUsage.Sample)) {
                error = $"OcclusionBitmaskBake: format {bitmaskFormat} not supported for sampling on this platform";
                return false;
            }

            Vector3[] fibDirs = Fibonacci.GenerateFibonacciDirections(dirCount, hemisphereOnly);
            var fibonacciBuffer = new ComputeBuffer(dirCount, sizeof(float) * 3);
            fibonacciBuffer.SetData(fibDirs);

            var bitmaskBuffer = new ComputeBuffer(bitmaskSize, sizeof(uint) * 2);
            int kernelIdx = bitmaskBakeCompute.FindKernel("CSMain");

            Vector3 voxelSize = new Vector3(bounds.size.x / resolution.x, bounds.size.y / resolution.y, bounds.size.z / resolution.z);
            float voxelDiag = voxelSize.magnitude;

            bitmaskBakeCompute.SetTexture(kernelIdx, s_sdfHires, sourceVolume.sdfHiresTexture);
            bitmaskBakeCompute.SetInt(s_raymarchMaxSteps, 256);
            bitmaskBakeCompute.SetFloat(s_raymarchMinStep, voxelDiag * 0.01f);
            bitmaskBakeCompute.SetFloat(s_raymarchEpsilon, voxelDiag * 0.02f);
            bitmaskBakeCompute.SetVector(s_boundsMin, bounds.min);
            bitmaskBakeCompute.SetVector(s_boundsSize, bounds.size);
            bitmaskBakeCompute.SetInts(s_resolution, resolution.x, resolution.y, resolution.z);
            bitmaskBakeCompute.SetBuffer(kernelIdx, s_directions, fibonacciBuffer);
            bitmaskBakeCompute.SetInt(s_bitmaskDirCount, dirCount);
            bitmaskBakeCompute.SetBuffer(kernelIdx, s_outBitmask, bitmaskBuffer);

            uint groupX = (uint)Mathf.CeilToInt(resolution.x / 8f);
            uint groupY = (uint)Mathf.CeilToInt(resolution.y / 8f);
            uint groupZ = (uint)Mathf.CeilToInt(resolution.z / 8f);

            bitmaskBakeCompute.Dispatch(kernelIdx, (int)groupX, (int)groupY, (int)groupZ);

            fibonacciBuffer.Dispose();

            var readbackRequest = AsyncGPUReadback.Request(bitmaskBuffer);
            readbackRequest.WaitForCompletion();

            if (readbackRequest.hasError) {
                error = "OcclusionBitmaskBake: readback failed";
                bitmaskBuffer.Dispose();
                return false;
            }

            var readbackData = readbackRequest.GetData<uint>();
            if (readbackData.Length != bitmaskSize * 2) {
                error = $"OcclusionBitmaskBake: readback size mismatch (got {readbackData.Length}, expected {bitmaskSize * 2})";
                bitmaskBuffer.Dispose();
                return false;
            }

            result = PackTexture(readbackData, bitmaskSize, dirCount, bitmaskFormat, resolution, sourceVolume.BakeRoot.name);
            bakedDirections = fibDirs;

            bitmaskBuffer.Dispose();
            Debug.Log($"OcclusionBitmaskBake: baked {dirCount} directions ({resolution.x}x{resolution.y}x{resolution.z} = {bitmaskSize} voxels, format={bitmaskFormat})");
            return true;
        }

        static Texture3D PackTexture(
            Unity.Collections.NativeArray<uint> readbackData,
            int bitmaskSize,
            int dirCount,
            GraphicsFormat format,
            Vector3Int resolution,
            string rootName
        ) {
            var tex = new Texture3D(resolution.x, resolution.y, resolution.z, format, TextureCreationFlags.None) {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = $"{rootName}_OcclusionBitmask"
            };

            if (dirCount <= 8) {
                // R8_UNorm: pack low 8 bits of bitmask.x into a single byte
                var packed = new byte[bitmaskSize];
                for (int v = 0; v < bitmaskSize; v++)
                    packed[v] = (byte)(readbackData[v * 2] & 0xFFu);
                tex.SetPixelData(packed, 0);
            } else if (dirCount <= 32) {
                // RG16_UNorm: pack 32-bit bitmask.x into 2 ushorts
                var packed = new ushort[bitmaskSize * 2];
                for (int v = 0; v < bitmaskSize; v++) {
                    uint x = readbackData[v * 2];
                    packed[v * 2 + 0] = (ushort)(x & 0xFFFFu);
                    packed[v * 2 + 1] = (ushort)((x >> 16) & 0xFFFFu);
                }
                tex.SetPixelData(packed, 0);
            } else {
                // RGBA16_UNorm: pack uint2 into 4 ushorts
                var packed = new ushort[bitmaskSize * 4];
                for (int v = 0; v < bitmaskSize; v++) {
                    uint x = readbackData[v * 2 + 0];
                    uint y = readbackData[v * 2 + 1];
                    packed[v * 4 + 0] = (ushort)(x & 0xFFFFu);
                    packed[v * 4 + 1] = (ushort)((x >> 16) & 0xFFFFu);
                    packed[v * 4 + 2] = (ushort)(y & 0xFFFFu);
                    packed[v * 4 + 3] = (ushort)((y >> 16) & 0xFFFFu);
                }
                tex.SetPixelData(packed, 0);
            }

            tex.Apply(false);
            return tex;
        }
    }
}
