using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    /// <summary>
    /// Approximate SDF baker using the Jump Flooding Algorithm (JFA), provided as a
    /// secondary implementation for evaluation against the exact <see cref="ExactSdfBake"/>.
    ///
    /// It reuses the same triangle gathering and uniform grid as the exact baker, but
    /// only computes exact distances for a thin band around the surface and then floods
    /// the nearest-triangle index outward. Cost is independent of triangle count after
    /// seeding; the trade-off is that some far-field voxels may pick a triangle that is
    /// not the globally nearest. Distance/sign math matches the exact baker, so outputs
    /// are directly comparable.
    /// </summary>
    [Serializable]
    public class JfaSdfBake : ISdfBake {
        public ComputeShader sdfBakeJfaCompute;

        [Tooltip("Seed search radius in grid cells. Larger seeds a thicker exact band " +
                 "(more accurate, slower); 2 is usually enough for the flood to converge.")]
        public int bandCellRadius = 2;

        [Tooltip("Extra step=1 jump-flood passes appended after the standard sequence " +
                 "to reduce flooding artifacts (the '+N' in JFA+N).")]
        public int extraStep1Passes = 2;

        static readonly int sTriVerts = Shader.PropertyToID("_TriVerts");
        static readonly int sCellStart = Shader.PropertyToID("_CellStart");
        static readonly int sTriIndices = Shader.PropertyToID("_TriIndices");
        static readonly int sGridDim = Shader.PropertyToID("_GridDim");
        static readonly int sCellSize = Shader.PropertyToID("_CellSize");
        static readonly int sBoundsMin = Shader.PropertyToID("_BoundsMin");
        static readonly int sBoundsSize = Shader.PropertyToID("_BoundsSize");
        static readonly int sResolution = Shader.PropertyToID("_Resolution");
        static readonly int sSeedIn = Shader.PropertyToID("_SeedIn");
        static readonly int sSeedOut = Shader.PropertyToID("_SeedOut");
        static readonly int sJfaStep = Shader.PropertyToID("_JfaStep");
        static readonly int sBandCellRadius = Shader.PropertyToID("_BandCellRadius");
        static readonly int sOutSdf = Shader.PropertyToID("_OutSdf");

        int _seedKernel = -1;
        int _jumpFloodKernel = -1;
        int _resolveKernel = -1;

        public bool TryBake(
            VoxelVolume volume,
            Vector3Int resolution,
            string textureName,
            out Texture3D bakedSdf,
            out string error
        ) {
            bakedSdf = null;
            Debug.Log($"Starting SDF bake (JFA) with resolution {resolution} for volume '{volume.name}'", volume);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (volume == null) {
                error = "Target SdfVolume is null.";
                return false;
            }
            Transform root = volume.BakeRoot;
            if (root == null) {
                error = "Bake Root is null.";
                return false;
            }
            if (sdfBakeJfaCompute == null) {
                error = "SDF Bake JFA Compute is not assigned.";
                return false;
            }

            if (_seedKernel < 0) {
                _seedKernel = sdfBakeJfaCompute.FindKernel("Seed");
                _jumpFloodKernel = sdfBakeJfaCompute.FindKernel("JumpFlood");
                _resolveKernel = sdfBakeJfaCompute.FindKernel("Resolve");
            }

            if (!ExactSdfBake.TryBuildTriangleListWorld(root, volume.Bounds, out Vector3[] triVerts, out error))
                return false;

            int triCount = triVerts.Length / 3;
            if (triCount <= 0) {
                error = "Triangle list is empty.";
                return false;
            }

            int voxelCount = resolution.x * resolution.y * resolution.z;
            if (voxelCount <= 0) {
                error = "Invalid resolution.";
                return false;
            }

            ExactSdfBake.BuildUniformGrid(triVerts, volume.Bounds, out Vector3Int gridDim, out float cellSize, out uint[] cellStart, out uint[] triIndices);

            var sdfBuffer = new ComputeBuffer(voxelCount, sizeof(uint), ComputeBufferType.Structured);
            var triBuffer = new ComputeBuffer(triVerts.Length, sizeof(float) * 3, ComputeBufferType.Structured);
            var cellStartBuffer = new ComputeBuffer(cellStart.Length, sizeof(uint), ComputeBufferType.Structured);
            var triIndicesBuffer = new ComputeBuffer(Mathf.Max(1, triIndices.Length), sizeof(uint), ComputeBufferType.Structured);
            // Ping-pong seed buffers (nearest triangle index per voxel).
            var seedA = new ComputeBuffer(voxelCount, sizeof(uint), ComputeBufferType.Structured);
            var seedB = new ComputeBuffer(voxelCount, sizeof(uint), ComputeBufferType.Structured);

            try {
                triBuffer.SetData(triVerts);
                cellStartBuffer.SetData(cellStart);
                if (triIndices.Length > 0)
                    triIndicesBuffer.SetData(triIndices);

                Vector3 bmin = volume.Bounds.min;
                Vector3 bsize = volume.Bounds.size;

                // Bind the geometry/grid inputs that every kernel reads.
                void BindShared(int kernel) {
                    sdfBakeJfaCompute.SetBuffer(kernel, sTriVerts, triBuffer);
                    sdfBakeJfaCompute.SetBuffer(kernel, sCellStart, cellStartBuffer);
                    sdfBakeJfaCompute.SetBuffer(kernel, sTriIndices, triIndicesBuffer);
                }
                BindShared(_seedKernel);
                BindShared(_jumpFloodKernel);
                BindShared(_resolveKernel);

                sdfBakeJfaCompute.SetInts(sGridDim, gridDim.x, gridDim.y, gridDim.z);
                sdfBakeJfaCompute.SetFloat(sCellSize, cellSize);
                sdfBakeJfaCompute.SetVector(sBoundsMin, bmin);
                sdfBakeJfaCompute.SetVector(sBoundsSize, bsize);
                sdfBakeJfaCompute.SetInts(sResolution, resolution.x, resolution.y, resolution.z);
                sdfBakeJfaCompute.SetInt(sBandCellRadius, Mathf.Max(1, bandCellRadius));

                sdfBakeJfaCompute.GetKernelThreadGroupSizes(_seedKernel, out uint tx, out uint ty, out uint tz);
                int gx = Mathf.CeilToInt(resolution.x / (float)tx);
                int gy = Mathf.CeilToInt(resolution.y / (float)ty);
                int gz = Mathf.CeilToInt(resolution.z / (float)tz);

                // Pass 1: seed the narrow band into seedA.
                sdfBakeJfaCompute.SetBuffer(_seedKernel, sSeedOut, seedA);
                sdfBakeJfaCompute.SetBuffer(_seedKernel, sSeedIn, seedB); // unused by Seed, bound to satisfy bindings
                sdfBakeJfaCompute.Dispatch(_seedKernel, gx, gy, gz);

                // Pass 2: jump flood. Standard halving sequence (N/2 .. 1) plus a few
                // extra step=1 passes to clean up flooding artifacts.
                ComputeBuffer read = seedA;
                ComputeBuffer write = seedB;
                foreach (int step in JumpFloodSteps(resolution)) {
                    sdfBakeJfaCompute.SetInt(sJfaStep, step);
                    sdfBakeJfaCompute.SetBuffer(_jumpFloodKernel, sSeedIn, read);
                    sdfBakeJfaCompute.SetBuffer(_jumpFloodKernel, sSeedOut, write);
                    sdfBakeJfaCompute.Dispatch(_jumpFloodKernel, gx, gy, gz);
                    (read, write) = (write, read);
                }

                // Pass 3: resolve exact distance + sign from the final seed buffer.
                sdfBakeJfaCompute.SetBuffer(_resolveKernel, sSeedIn, read);
                sdfBakeJfaCompute.SetBuffer(_resolveKernel, sOutSdf, sdfBuffer);
                sdfBakeJfaCompute.Dispatch(_resolveKernel, gx, gy, gz);

                var req = AsyncGPUReadback.Request(sdfBuffer);
                req.WaitForCompletion();

                if (req.hasError) {
                    error = "AsyncGPUReadback failed.";
                    return false;
                }

                int expected = voxelCount;
                NativeArray<uint> data = req.GetData<uint>();
                if (data.Length != expected) {
                    error = $"Unexpected readback size. Expected {expected} uint values, got {data.Length}.";
                    return false;
                }

                ushort[] packed = new ushort[expected];
                for (int i = 0; i < expected; i++)
                    packed[i] = (ushort)(data[i] & 0xFFFFu);

                bakedSdf = new Texture3D(resolution.x, resolution.y, resolution.z, TextureFormat.RHalf, mipChain: false, true) {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                    name = $"{textureName}_SDF"
                };

                bakedSdf.SetPixelData(packed, 0);
                bakedSdf.Apply(false, false);

                Debug.Log($"SDF bake (JFA) completed in {stopwatch.ElapsedMilliseconds} ms for resolution {resolution} ({triCount} triangles)", volume);
                error = null;
                return true;
            } finally {
                triBuffer.Release();
                sdfBuffer.Release();
                cellStartBuffer.Release();
                triIndicesBuffer.Release();
                seedA.Release();
                seedB.Release();
            }
        }

        // Power-of-two halving sequence (largest step < maxRes down to 1), followed by
        // a few extra step=1 passes (JFA+N) to reduce flooding artifacts.
        System.Collections.Generic.IEnumerable<int> JumpFloodSteps(Vector3Int resolution) {
            int maxRes = Mathf.Max(resolution.x, Mathf.Max(resolution.y, resolution.z));
            int step = 1;
            while (step * 2 < maxRes)
                step *= 2;
            for (; step >= 1; step >>= 1)
                yield return step;
            for (int i = 0; i < Mathf.Max(0, extraStep1Passes); i++)
                yield return 1;
        }
    }
}
