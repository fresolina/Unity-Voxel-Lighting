using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Lotec.Lighting
{
    /// <summary>
    /// Occlusion bitmask bake: traces scene triangles through the uniform grid, so the bitmask path
    /// carries no geometry-SDF dependency.
    ///
    /// It reuses OcclusionFieldTrace.compute wholesale - occupancy, near-surface band, trace and
    /// dilate are all direction-count agnostic. The only thing that differs from the field bake is
    /// the pack step: each direction's supersampled visibility is thresholded to a single bit rather
    /// than quantised to a byte, and the signed-distance re-encode is skipped (one bit has no room
    /// for a distance, which is why the bitmask can never reach the field's edge sharpness).
    ///
    /// Beyond dropping the SDF, this replaced an earlier sphere-trace bake that had none of:
    ///   - no leaking through sub-voxel geometry, because the hit test is exact;
    ///   - dilation into solid voxels, so the runtime's 8-tap no longer pulls all-occluded bits from
    ///     inside walls and darkens their base;
    ///   - a supersampled threshold, which puts each bit's boundary at the statistically right place
    ///     instead of wherever the voxel centre happened to fall.
    /// </summary>
    [Serializable]
    public class ExactOcclusionBitmaskBake
    {
        public enum BitmaskDirectionCount
        {
            Dir8 = 8,
            Dir32 = 32,
            Dir64 = 64,
        }

        // Directions are traced in batches so the readback buffer stays small: 64 directions in one
        // dispatch would be voxelCount * 64 floats (~150 MB at 580k voxels).
        const int DirectionsPerBatch = 4;

        public ComputeShader occlusionFieldTraceCompute;

        [Tooltip("Number of directions to bake into the bitmask.")]
        public BitmaskDirectionCount directionCount = BitmaskDirectionCount.Dir64;

        [Tooltip("Use only upper hemisphere directions (Y >= 0). Useful when the sun never goes below the horizon.")]
        public bool hemisphereOnly = true;

        [Tooltip("EXTRA rays per direction, on top of the ray straight down the direction itself " +
                 "(which is always traced). This is the setting that matters here: the result is " +
                 "thresholded to one bit, so more rays only change WHERE that threshold falls - which " +
                 "is exactly what the runtime's 8-tap reconstruction needs to be accurate.")]
        [Range(0, 64)]
        public int penumbraSamples = 8;

        [Tooltip("How the extra rays are placed on the light's disc.")]
        public ExactOcclusionFieldBake.SampleDistribution sampleDistribution = ExactOcclusionFieldBake.SampleDistribution.Jittered;

        [Tooltip("Stop sampling early once half the samples agree. Safe here - a voxel whose samples " +
                 "all agree lands on the same side of the 0.5 threshold either way.")]
        public bool adaptiveSampling = true;

        [Tooltip("Cone width in voxels, measured 32 voxels from the occluder. Has little effect on a " +
                 "bitmask: the result is thresholded at 0.5 and cone width barely moves that " +
                 "iso-surface. Left exposed because it still nudges sub-voxel boundary placement.")]
        [Range(0f, 16f)]
        public float penumbraVoxels = 2f;

        [Tooltip("Only trace voxels within this many voxels of a surface. The bitmask is only ever " +
                 "read just off a surface, so tracing open air is wasted work. 0 = trace everything.")]
        [Range(0, 16)]
        public int traceBandVoxels = 4;

        public bool HemisphereOnly => hemisphereOnly;

        static readonly int s_boundsMin = Shader.PropertyToID("_BoundsMin");
        static readonly int s_boundsSize = Shader.PropertyToID("_BoundsSize");
        static readonly int s_resolution = Shader.PropertyToID("_Resolution");
        static readonly int s_gridDim = Shader.PropertyToID("_GridDim");
        static readonly int s_cellSize = Shader.PropertyToID("_CellSize");
        static readonly int s_triVerts = Shader.PropertyToID("_TriVerts");
        static readonly int s_cellStart = Shader.PropertyToID("_CellStart");
        static readonly int s_triIndices = Shader.PropertyToID("_TriIndices");
        static readonly int s_occupancy = Shader.PropertyToID("_Occupancy");
        static readonly int s_traceMask = Shader.PropertyToID("_TraceMask");
        static readonly int s_outOcclusion = Shader.PropertyToID("_OutOcclusion");
        static readonly int s_directions = Shader.PropertyToID("_Directions");
        static readonly int s_dirCount = Shader.PropertyToID("_DirCount");
        static readonly int s_penumbraSamples = Shader.PropertyToID("_PenumbraSamples");
        static readonly int s_minSamples = Shader.PropertyToID("_MinSamples");
        static readonly int s_coneCosMax = Shader.PropertyToID("_ConeCosMax");
        static readonly int s_coneHalfAngle = Shader.PropertyToID("_ConeHalfAngle");
        static readonly int s_sampleDistribution = Shader.PropertyToID("_SampleDistribution");
        static readonly int s_coneOffsets = Shader.PropertyToID("_ConeOffsets");
        static readonly int s_traceBandVoxels = Shader.PropertyToID("_TraceBandVoxels");
        static readonly int s_maxRayDistance = Shader.PropertyToID("_MaxRayDistance");

        static GraphicsFormat GetTextureFormat(int dirCount)
        {
            return dirCount switch
            {
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
        )
        {
            result = null;
            bakedDirections = null;
            error = "";

            if (sourceVolume == null)
            {
                error = "ExactOcclusionBitmaskBake: sourceVolume is null";
                return false;
            }
            if (occlusionFieldTraceCompute == null)
            {
                error = "ExactOcclusionBitmaskBake: occlusionFieldTraceCompute not assigned";
                return false;
            }

            // No SDF dependency, so this path owns bounds/resolution rather than relying on
            // VoxelSdfBaker having run first. Idempotent when it did.
            sourceVolume.RecomputeBoundsAndResolution();

            Transform root = sourceVolume.BakeRoot;
            if (root == null)
            {
                error = "ExactOcclusionBitmaskBake: Bake Root is null";
                return false;
            }

            Bounds bounds = sourceVolume.Bounds;
            Vector3Int resolution = sourceVolume.TrimmedMaxResolution;
            int voxelCount = resolution.x * resolution.y * resolution.z;

            if (bounds.size.magnitude < 0.01f || resolution.x < 2)
            {
                error = "ExactOcclusionBitmaskBake: invalid bounds or resolution";
                return false;
            }

            int dirCount = (int)directionCount;
            GraphicsFormat bitmaskFormat = GetTextureFormat(dirCount);
            if (!SystemInfo.IsFormatSupported(bitmaskFormat, GraphicsFormatUsage.Sample))
            {
                error = $"ExactOcclusionBitmaskBake: format {bitmaskFormat} not supported for sampling on this platform";
                return false;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (!ExactSdfBake.TryBuildTriangleListWorld(root, bounds, out Vector3[] triVerts, out error))
                return false;
            int triCount = triVerts.Length / 3;
            if (triCount <= 0)
            {
                error = "ExactOcclusionBitmaskBake: triangle list is empty";
                return false;
            }

            ExactSdfBake.BuildUniformGrid(triVerts, bounds, out Vector3Int gridDim, out float cellSize,
                                          out uint[] cellStart, out uint[] triIndices);

            Vector3[] fibDirs = Fibonacci.GenerateFibonacciDirections(dirCount, hemisphereOnly);

            int kOccupancy = occlusionFieldTraceCompute.FindKernel("CSBuildOccupancy");
            int kMarkBand = occlusionFieldTraceCompute.FindKernel("CSMarkBand");
            int kTrace = occlusionFieldTraceCompute.FindKernel("CSTrace");
            int kDilate = occlusionFieldTraceCompute.FindKernel("CSDilate");

            int groupX = Mathf.CeilToInt(resolution.x / 8f);
            int groupY = Mathf.CeilToInt(resolution.y / 8f);
            int groupZ = Mathf.CeilToInt(resolution.z / 8f);

            float halfAngle = Mathf.Atan(Mathf.Max(0f, penumbraVoxels) / 64f);
            int extraSamples = Mathf.Max(0, penumbraSamples);
            int totalSamples = 1 + extraSamples;
            Vector2[] coneOffsets = ExactOcclusionFieldBake.BuildEvenDiscOffsets(extraSamples);

            var triBuffer = new ComputeBuffer(triVerts.Length, sizeof(float) * 3, ComputeBufferType.Structured);
            var cellStartBuffer = new ComputeBuffer(cellStart.Length, sizeof(uint), ComputeBufferType.Structured);
            var triIndicesBuffer = new ComputeBuffer(Mathf.Max(1, triIndices.Length), sizeof(uint), ComputeBufferType.Structured);
            var occupancyBuffer = new ComputeBuffer(voxelCount, sizeof(uint), ComputeBufferType.Structured);
            var traceMaskBuffer = new ComputeBuffer(voxelCount, sizeof(uint), ComputeBufferType.Structured);
            var dirBuffer = new ComputeBuffer(DirectionsPerBatch, sizeof(float) * 3);
            var coneOffsetBuffer = new ComputeBuffer(coneOffsets.Length, sizeof(float) * 2, ComputeBufferType.Structured);
            var outBuffer = new ComputeBuffer(voxelCount * DirectionsPerBatch, sizeof(float));

            try
            {
                triBuffer.SetData(triVerts);
                cellStartBuffer.SetData(cellStart);
                if (triIndices.Length > 0)
                    triIndicesBuffer.SetData(triIndices);
                coneOffsetBuffer.SetData(coneOffsets);

                occlusionFieldTraceCompute.SetVector(s_boundsMin, bounds.min);
                occlusionFieldTraceCompute.SetVector(s_boundsSize, bounds.size);
                occlusionFieldTraceCompute.SetInts(s_resolution, resolution.x, resolution.y, resolution.z);
                occlusionFieldTraceCompute.SetInts(s_gridDim, gridDim.x, gridDim.y, gridDim.z);
                occlusionFieldTraceCompute.SetFloat(s_cellSize, cellSize);
                occlusionFieldTraceCompute.SetInt(s_dirCount, DirectionsPerBatch);
                occlusionFieldTraceCompute.SetInt(s_penumbraSamples, extraSamples);
                occlusionFieldTraceCompute.SetInt(s_minSamples, adaptiveSampling ? Mathf.Max(2, totalSamples / 2) : totalSamples);
                occlusionFieldTraceCompute.SetInt(s_sampleDistribution,
                    sampleDistribution == ExactOcclusionFieldBake.SampleDistribution.Even ? 1 : 0);
                occlusionFieldTraceCompute.SetFloat(s_coneCosMax, Mathf.Cos(halfAngle));
                occlusionFieldTraceCompute.SetFloat(s_coneHalfAngle, halfAngle);
                occlusionFieldTraceCompute.SetInt(s_traceBandVoxels, Mathf.Max(0, traceBandVoxels));
                occlusionFieldTraceCompute.SetFloat(s_maxRayDistance, bounds.size.magnitude);

                // Direction-independent, so they run once and every batch reuses them.
                BindGrid(kOccupancy);
                occlusionFieldTraceCompute.SetBuffer(kOccupancy, s_occupancy, occupancyBuffer);
                occlusionFieldTraceCompute.Dispatch(kOccupancy, groupX, groupY, groupZ);

                occlusionFieldTraceCompute.SetBuffer(kMarkBand, s_occupancy, occupancyBuffer);
                occlusionFieldTraceCompute.SetBuffer(kMarkBand, s_traceMask, traceMaskBuffer);
                occlusionFieldTraceCompute.Dispatch(kMarkBand, groupX, groupY, groupZ);

                BindGrid(kTrace);
                occlusionFieldTraceCompute.SetBuffer(kTrace, s_occupancy, occupancyBuffer);
                occlusionFieldTraceCompute.SetBuffer(kTrace, s_traceMask, traceMaskBuffer);
                occlusionFieldTraceCompute.SetBuffer(kTrace, s_coneOffsets, coneOffsetBuffer);
                occlusionFieldTraceCompute.SetBuffer(kTrace, s_directions, dirBuffer);
                occlusionFieldTraceCompute.SetBuffer(kTrace, s_outOcclusion, outBuffer);
                occlusionFieldTraceCompute.SetBuffer(kDilate, s_occupancy, occupancyBuffer);
                occlusionFieldTraceCompute.SetBuffer(kDilate, s_outOcclusion, outBuffer);

                // Two uints per voxel = 64 bits, one per direction. Bit i set means direction i is
                // occluded - the convention VoxelOcclusion.hlsl's GetShadowBitTrilinear8Tap inverts.
                var bits = new uint[voxelCount * 2];
                var batchDirs = new Vector3[DirectionsPerBatch];

                for (int dirOffset = 0; dirOffset < dirCount; dirOffset += DirectionsPerBatch)
                {
                    for (int c = 0; c < DirectionsPerBatch; c++)
                    {
                        int dirIdx = dirOffset + c;
                        batchDirs[c] = dirIdx < dirCount ? fibDirs[dirIdx] : fibDirs[dirCount - 1];
                    }
                    dirBuffer.SetData(batchDirs);

                    occlusionFieldTraceCompute.Dispatch(kTrace, groupX, groupY, groupZ);
                    occlusionFieldTraceCompute.Dispatch(kDilate, groupX, groupY, groupZ);

                    var readbackRequest = AsyncGPUReadback.Request(outBuffer);
                    readbackRequest.WaitForCompletion();
                    if (readbackRequest.hasError)
                    {
                        error = $"ExactOcclusionBitmaskBake: readback failed for directions {dirOffset}..{dirOffset + DirectionsPerBatch - 1}";
                        return false;
                    }

                    NativeArray<float> lit = readbackRequest.GetData<float>();
                    for (int c = 0; c < DirectionsPerBatch; c++)
                    {
                        int dirIdx = dirOffset + c;
                        if (dirIdx >= dirCount)
                            break;

                        int word = dirIdx < 32 ? 0 : 1;
                        uint bit = 1u << (dirIdx & 31);
                        for (int v = 0; v < voxelCount; v++)
                        {
                            // Threshold the SUPERSAMPLED visibility, so the bit flips where the
                            // shadow boundary actually crosses the voxel rather than at its centre.
                            if (lit[v * DirectionsPerBatch + c] < 0.5f)
                                bits[v * 2 + word] |= bit;
                        }
                    }
                }

                result = PackBitmaskTexture(bits, voxelCount, dirCount, bitmaskFormat, resolution, root.name);
                bakedDirections = fibDirs;

                LogBakeSummary(sourceVolume, occupancyBuffer, traceMaskBuffer, voxelCount, triCount,
                               gridDim, dirCount, resolution, totalSamples, bitmaskFormat, stopwatch);
                return true;
            }
            finally
            {
                triBuffer.Release();
                cellStartBuffer.Release();
                triIndicesBuffer.Release();
                occupancyBuffer.Release();
                traceMaskBuffer.Release();
                dirBuffer.Release();
                coneOffsetBuffer.Release();
                outBuffer.Release();
            }

            void BindGrid(int kernel)
            {
                occlusionFieldTraceCompute.SetBuffer(kernel, s_triVerts, triBuffer);
                occlusionFieldTraceCompute.SetBuffer(kernel, s_cellStart, cellStartBuffer);
                occlusionFieldTraceCompute.SetBuffer(kernel, s_triIndices, triIndicesBuffer);
            }
        }

        /// <summary>
        /// Packs the per-voxel uint2 into the texture format the runtime decodes. The authority on
        /// this layout is GetBitmaskAtVoxel in VoxelOcclusion.hlsl - change one and change the other.
        /// Point filtering is mandatory: these are bits, and any hardware blend corrupts them.
        /// </summary>
        static Texture3D PackBitmaskTexture(
            uint[] bits,
            int voxelCount,
            int dirCount,
            GraphicsFormat format,
            Vector3Int resolution,
            string rootName
        )
        {
            var tex = new Texture3D(resolution.x, resolution.y, resolution.z, format, TextureCreationFlags.None)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = $"{rootName}_OcclusionBitmask"
            };

            if (dirCount <= 8)
            {
                var packed = new byte[voxelCount];
                for (int v = 0; v < voxelCount; v++)
                    packed[v] = (byte)(bits[v * 2] & 0xFFu);
                tex.SetPixelData(packed, 0);
            }
            else if (dirCount <= 32)
            {
                var packed = new ushort[voxelCount * 2];
                for (int v = 0; v < voxelCount; v++)
                {
                    uint x = bits[v * 2];
                    packed[v * 2 + 0] = (ushort)(x & 0xFFFFu);
                    packed[v * 2 + 1] = (ushort)((x >> 16) & 0xFFFFu);
                }
                tex.SetPixelData(packed, 0);
            }
            else
            {
                var packed = new ushort[voxelCount * 4];
                for (int v = 0; v < voxelCount; v++)
                {
                    uint x = bits[v * 2 + 0];
                    uint y = bits[v * 2 + 1];
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

        static void LogBakeSummary(
            VoxelVolume volume,
            ComputeBuffer occupancyBuffer,
            ComputeBuffer traceMaskBuffer,
            int voxelCount,
            int triCount,
            Vector3Int gridDim,
            int dirCount,
            Vector3Int resolution,
            int totalSamples,
            GraphicsFormat format,
            System.Diagnostics.Stopwatch stopwatch
        )
        {
            int solid = 0, traced = 0;
            var occReq = AsyncGPUReadback.Request(occupancyBuffer);
            var maskReq = AsyncGPUReadback.Request(traceMaskBuffer);
            occReq.WaitForCompletion();
            maskReq.WaitForCompletion();
            if (!occReq.hasError && !maskReq.hasError)
            {
                NativeArray<uint> occ = occReq.GetData<uint>();
                NativeArray<uint> mask = maskReq.GetData<uint>();
                for (int i = 0; i < voxelCount; i++)
                {
                    if (occ[i] != 0) solid++;
                    else if (mask[i] != 0) traced++;
                }
            }

            Debug.Log(
                $"ExactOcclusionBitmaskBake: {dirCount} directions ({resolution.x}x{resolution.y}x{resolution.z} = " +
                $"{voxelCount} voxels, format={format}) in {stopwatch.ElapsedMilliseconds} ms. " +
                $"{triCount} triangles in a {gridDim.x}x{gridDim.y}x{gridDim.z} grid. " +
                $"Traced {traced} voxels ({100f * traced / Mathf.Max(1, voxelCount):F1}%), " +
                $"{solid} solid, {voxelCount - traced - solid} skipped as far from any surface. " +
                $"{totalSamples} rays/direction.",
                volume);
        }
    }
}
