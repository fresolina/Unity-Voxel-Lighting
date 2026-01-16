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

        static Vector3[] sCachedFibonacciDirectionsV3;
        static Vector4[] sCachedFibonacciDirectionsV4;

        public static Vector3[] GetOrCreateFibonacciDirectionsV3() {
            if (sCachedFibonacciDirectionsV3 != null && sCachedFibonacciDirectionsV3.Length == FibonacciDirectionCount)
                return sCachedFibonacciDirectionsV3;

            sCachedFibonacciDirectionsV3 = new Vector3[FibonacciDirectionCount];
            float goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f));
            int n = FibonacciDirectionCount;
            for (int i = 0; i < n; ++i) {
                float y = 1f - (i / (float)(n - 1)) * 2f;
                float radius = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float theta = goldenAngle * i;
                float x = Mathf.Cos(theta) * radius;
                float z = Mathf.Sin(theta) * radius;
                sCachedFibonacciDirectionsV3[i] = new Vector3(x, y, z);
            }

            // Keep Vector4 cache in sync, since shaders consume Vector4 arrays.
            sCachedFibonacciDirectionsV4 = null;
            return sCachedFibonacciDirectionsV3;
        }

        public static Vector4[] GetOrCreateFibonacciDirectionsV4() {
            if (sCachedFibonacciDirectionsV4 != null && sCachedFibonacciDirectionsV4.Length == FibonacciDirectionCount)
                return sCachedFibonacciDirectionsV4;

            Vector3[] v3 = GetOrCreateFibonacciDirectionsV3();
            sCachedFibonacciDirectionsV4 = new Vector4[FibonacciDirectionCount];
            for (int i = 0; i < FibonacciDirectionCount; i++) {
                Vector3 d = v3[i];
                sCachedFibonacciDirectionsV4[i] = new Vector4(d.x, d.y, d.z, 0f);
            }

            return sCachedFibonacciDirectionsV4;
        }

        [System.Serializable]
        public struct uint2 {
            public uint x;
            public uint y;

            public uint2(uint x, uint y) {
                this.x = x;
                this.y = y;
            }
        }
        public ComputeShader bitmaskBakeCompute;
        [Min(0.1f)] public float occlusionDistance = 5.0f;
        [Tooltip("Number of Fibonacci directions to bake (supported: 64).")]
        public int fibonacciDirectionCount = FibonacciDirectionCount;
        [Tooltip("When enabled, the compute shader writes a popcount per voxel to a debug buffer which is read back for validation.")]
        public bool debugWritePopcount = false;
        [Tooltip("When enabled, the compute shader writes first-hit direction indices to a debug buffer which is read back for validation.")]
        public bool debugWriteFirstHit = false;
        [Tooltip("When enabled, the compute shader writes a compact byte signature per voxel for quick pattern inspection.")]
        public bool debugWriteSignature = false;

        static readonly int sBoundsMin = Shader.PropertyToID("_BoundsMin");
        static readonly int sBoundsSize = Shader.PropertyToID("_BoundsSize");
        static readonly int sResolution = Shader.PropertyToID("_Resolution");
        static readonly int sOutBitmask = Shader.PropertyToID("_OutBitmask");
        static readonly int sOcclusionDistance = Shader.PropertyToID("_OcclusionDistance");

        // public Texture3D BakeVoxelGrid(uint2[,,] bakedMasks, int width, int height, int depth) {
        //     // Use R16G16B16A16_SFloat to ensure the GPU texture units are happy
        //     Texture3D tex = new Texture3D(width, height, depth, GraphicsFormat.R16G16B16A16_SFloat, TextureCreationFlags.None);
        //     tex.filterMode = FilterMode.Point;

        //     // Create a raw ushort array. 
        //     // 4 ushorts per voxel (R, G, B, A)
        //     int totalVoxels = width * height * depth;
        //     ushort[] rawData = new ushort[totalVoxels * 4];

        //     for (int z = 0; z < depth; z++) {
        //         for (int y = 0; y < height; y++) {
        //             for (int x = 0; x < width; x++) {
        //                 uint2 mask = bakedMasks[x, y, z];
        //                 int baseIdx = (x + (y * width) + (z * width * height)) * 4;

        //                 // Raw bit-stuffing: No math, just data
        //                 rawData[baseIdx + 0] = (ushort)(mask.x & 0xFFFF);        // Lower 16 of x
        //                 rawData[baseIdx + 1] = (ushort)((mask.x >> 16) & 0xFFFF); // Upper 16 of x
        //                 rawData[baseIdx + 2] = (ushort)(mask.y & 0xFFFF);        // Lower 16 of y
        //                 rawData[baseIdx + 3] = (ushort)((mask.y >> 16) & 0xFFFF); // Upper 16 of y
        //             }
        //         }
        //     }

        //     tex.SetPixelData(rawData, 0, 0);
        //     tex.Apply(false); // No mips needed for bitmasks

        //     return tex;
        // }

        public bool TryBake(
            SdfVolume sourceVolume,
            out Texture3D result,
            out string error
        ) {
            result = null;
            error = "";

            if (sourceVolume == null) {
                error = "BitmaskOcclusionBaker: sourceVolume is null";
                return false;
            }

            if (bitmaskBakeCompute == null) {
                error = "BitmaskOcclusionBaker: bitmaskBakeCompute not assigned";
                return false;
            }

            Bounds bounds = sourceVolume.bakedBounds;
            Vector3Int resolution = sourceVolume.bakedResolution;
            int bitmaskSize = resolution.x * resolution.y * resolution.z;

            if (bounds.size.magnitude < 0.01f || resolution.x < 2) {
                error = "BitmaskOcclusionBaker: invalid bounds or resolution";
                return false;
            }

            // Store 64-bit mask as RGBA16_UNorm (sampleable everywhere):
            //   R,G = low/high 16 bits of uint bitmask.x
            //   B,A = low/high 16 bits of uint bitmask.y
            const GraphicsFormat bitmaskFormat = GraphicsFormat.R16G16B16A16_UNorm;
            if (!SystemInfo.IsFormatSupported(bitmaskFormat, GraphicsFormatUsage.Sample)) {
                error = $"BitmaskOcclusionBaker: format {bitmaskFormat} not supported for sampling on this platform";
                return false;
            }

            // Create compute buffer for output bitmask
            var bitmaskBuffer = new ComputeBuffer(bitmaskSize, sizeof(uint) * 2); // uint2
            // Generate Fibonacci directions on the CPU and upload to the compute shader
            if (fibonacciDirectionCount != FibonacciDirectionCount) {
                error = $"OcclusionBitmaskBaker: only {FibonacciDirectionCount} directions are supported currently";
                bitmaskBuffer.Dispose();
                return false;
            }
            // IMPORTANT: The direction set (and ordering) must match the cheat lookup texture baker
            // in Editor/BakeFibonacciLookup.cs, otherwise indices will map to the wrong directions.
            // Source of truth is the cached generator in this class.
            Vector3[] fibDirs = GetOrCreateFibonacciDirectionsV3();
            var fibonacciBuffer = new ComputeBuffer(fibonacciDirectionCount, sizeof(float) * 3);
            fibonacciBuffer.SetData(fibDirs);

            // NOTE: The compute shader declares these debug buffers unconditionally.
            // Unity will throw "Property (...) is not set" unless we bind them before Dispatch.
            // We always bind them, and only read them back/log them when debug toggles are enabled.
            ComputeBuffer popcountBuffer = null;
            ComputeBuffer firstHitBuffer = null;
            ComputeBuffer sigBuffer = null;

            // Dispatch compute shader
            int kernelIdx = bitmaskBakeCompute.FindKernel("CSMain");

            // Bind SDF texture and parameters
            if (sourceVolume.sdfTexture == null) {
                error = "BitmaskOcclusionBaker: sourceVolume has no sdfTexture";
                bitmaskBuffer.Dispose();
                return false;
            }

            bitmaskBakeCompute.SetTexture(kernelIdx, "_SdfTex", sourceVolume.sdfTexture);
            // Reasonable defaults for SDF tracing
            // int defaultMaxSteps = Mathf.Clamp(resolution.x * 2, 16, 256);
            int defaultMaxSteps = 256;
            bitmaskBakeCompute.SetInt("_SdfMaxSteps", defaultMaxSteps);
            Vector3 voxelSize = new Vector3(bounds.size.x / resolution.x, bounds.size.y / resolution.y, bounds.size.z / resolution.z);
            float voxelDiag = voxelSize.magnitude;
            bitmaskBakeCompute.SetFloat("_SdfMinStep", voxelDiag * 0.01f);
            // Use a smaller thickness tolerance to avoid over-counting near-surface partial voxels
            bitmaskBakeCompute.SetFloat("_SdfThicknessTol", voxelDiag * 0.02f);
            bitmaskBakeCompute.SetVector(sBoundsMin, bounds.min);
            bitmaskBakeCompute.SetVector(sBoundsSize, bounds.size);
            bitmaskBakeCompute.SetInts(sResolution, resolution.x, resolution.y, resolution.z);
            bitmaskBakeCompute.SetFloat(sOcclusionDistance, occlusionDistance);
            bitmaskBakeCompute.SetBuffer(kernelIdx, Shader.PropertyToID("_FibonacciDirs"), fibonacciBuffer);
            bitmaskBakeCompute.SetInt(Shader.PropertyToID("_FibonacciDirCount"), fibonacciDirectionCount);
            bitmaskBakeCompute.SetBuffer(kernelIdx, sOutBitmask, bitmaskBuffer);

            popcountBuffer = new ComputeBuffer(bitmaskSize, sizeof(uint));
            firstHitBuffer = new ComputeBuffer(bitmaskSize, sizeof(uint));
            sigBuffer = new ComputeBuffer(bitmaskSize, sizeof(uint));
            bitmaskBakeCompute.SetBuffer(kernelIdx, Shader.PropertyToID("_OutBitmaskPopcount"), popcountBuffer);
            bitmaskBakeCompute.SetBuffer(kernelIdx, Shader.PropertyToID("_OutBitmaskFirstHit"), firstHitBuffer);
            bitmaskBakeCompute.SetBuffer(kernelIdx, Shader.PropertyToID("_OutBitmaskSig"), sigBuffer);

            uint groupX = (uint)Mathf.CeilToInt(resolution.x / 8f);
            uint groupY = (uint)Mathf.CeilToInt(resolution.y / 8f);
            uint groupZ = (uint)Mathf.CeilToInt(resolution.z / 8f);

            bitmaskBakeCompute.Dispatch(kernelIdx, (int)groupX, (int)groupY, (int)groupZ);

            // Release fibonacci buffer after dispatch (we only need it on GPU during dispatch)
            fibonacciBuffer.Dispose();

            // Read back bitmask
            var readbackRequest = AsyncGPUReadback.Request(bitmaskBuffer);
            readbackRequest.WaitForCompletion();

            if (readbackRequest.hasError) {
                error = "BitmaskOcclusionBaker: readback failed";
                bitmaskBuffer.Dispose();

                return false;
            }

            var readbackData = readbackRequest.GetData<uint>();
            if (readbackData.Length != bitmaskSize * 2) {
                error = $"BitmaskOcclusionBaker: readback size mismatch (got {readbackData.Length}, expected {bitmaskSize * 2})";
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
            result.filterMode = FilterMode.Point; // No interpolation for bitmask
            result.SetPixelData(packed, 0);
            result.Apply(false);
            // Optionally read back popcount debug buffer and log a few stats
            if (debugWritePopcount && popcountBuffer != null) {
                var popReq = AsyncGPUReadback.Request(popcountBuffer);
                popReq.WaitForCompletion();
                if (!popReq.hasError) {
                    var popData = popReq.GetData<uint>();
                    // compute some simple stats
                    long sum = 0;
                    int nonzero = 0;
                    int samplesToLog = Math.Min(8, bitmaskSize);
                    for (int i = 0; i < popData.Length; i++) {
                        uint v = popData[i];
                        sum += v;
                        if (v != 0) nonzero++;
                    }
                    float avg = popData.Length > 0 ? (float)sum / popData.Length : 0f;
                    Debug.Log($"OcclusionBitmaskBaker: popcount avg={avg:F2}, nonzero voxels={nonzero}/{popData.Length}");
                    // log first few sample values
                    string samples = "";
                    for (int i = 0; i < samplesToLog; ++i) samples += popData[i] + (i + 1 < samplesToLog ? ", " : "");
                    Debug.Log($"OcclusionBitmaskBaker: popcount samples: {samples}");
                } else {
                    Debug.LogWarning("OcclusionBitmaskBaker: popcount readback had error");
                }
            }
            if (debugWriteFirstHit && firstHitBuffer != null) {
                var popReq = AsyncGPUReadback.Request(firstHitBuffer);
                popReq.WaitForCompletion();
                if (!popReq.hasError) {
                    var data = popReq.GetData<uint>();
                    int nonzero = 0;
                    for (int i = 0; i < data.Length; ++i) if (data[i] != 0xFFFFFFFFu) nonzero++;
                    Debug.Log($"OcclusionBitmaskBaker: firstHit non-empty voxels={nonzero}/{data.Length}");
                    // log a few samples
                    int samplesToLog = Math.Min(8, data.Length);
                    string samples = "";
                    for (int i = 0; i < samplesToLog; ++i) samples += data[i] + (i + 1 < samplesToLog ? ", " : "");
                    Debug.Log($"OcclusionBitmaskBaker: firstHit samples: {samples}");
                } else Debug.LogWarning("OcclusionBitmaskBaker: firstHit readback had error");
            }
            if (debugWriteSignature && sigBuffer != null) {
                var popReq = AsyncGPUReadback.Request(sigBuffer);
                popReq.WaitForCompletion();
                if (!popReq.hasError) {
                    var data = popReq.GetData<uint>();
                    // compute histogram of signatures for coarse inspection
                    var hist = new int[256];
                    for (int i = 0; i < data.Length; ++i) hist[data[i] & 0xFF]++;
                    int nonzero = 0; for (int i = 0; i < 256; ++i) if (hist[i] != 0) nonzero++;
                    Debug.Log($"OcclusionBitmaskBaker: signature distinctBytes={nonzero}");
                    // log first few signature samples
                    int samplesToLog = Math.Min(8, data.Length);
                    string samples = "";
                    for (int i = 0; i < samplesToLog; ++i) samples += (data[i] & 0xFF) + (i + 1 < samplesToLog ? ", " : "");
                    Debug.Log($"OcclusionBitmaskBaker: signature samples: {samples}");
                } else Debug.LogWarning("OcclusionBitmaskBaker: signature readback had error");
            }

            popcountBuffer?.Dispose();
            firstHitBuffer?.Dispose();
            sigBuffer?.Dispose();

            bitmaskBuffer.Dispose();
            Debug.Log($"OcclusionBitmaskBaker: baked successfully ({resolution.x}x{resolution.y}x{resolution.z} = {bitmaskSize} voxels)");
            return true;
        }
    }
}
