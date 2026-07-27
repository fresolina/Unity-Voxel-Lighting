using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace Lotec.Lighting
{
    /// <summary>
    /// Exact occlusion field bake: traces scene triangles through the uniform grid instead of
    /// sphere-tracing an SDF, and derives the penumbra by supersampling instead of from a
    /// distance-based estimator.
    ///
    /// The SDF path (<see cref="OcclusionFieldBake"/>) leaks - a wall thinner than a voxel has its
    /// negative interior smoothed away by trilinear interpolation, so rays pass straight through,
    /// which shows up as gaps in walls near the floor. That is a spatial sampling limit, not a
    /// precision one, so no SDF resolution or storage format fixes it. Offline the exact answer is
    /// affordable: the grid is sized to ~1 triangle per cell, so a ray tests only a handful.
    ///
    /// Output is byte-identical in format to the SDF path (lit values, 4 directions per RGBA
    /// texture), so nothing downstream - binder, asset saving, runtime HLSL - changes.
    /// </summary>
    [Serializable]
    public class ExactOcclusionFieldBake : IOcclusionFieldBake
    {
        /// <summary>How the light's angular size - and therefore the penumbra - is authored.</summary>
        public enum PenumbraMode
        {
            Voxels,   // resolution-relative: the penumbra always stays something the field can store
            SunAngle, // physical: a fixed angular diameter, independent of field resolution
        }

        /// <summary>How the extra penumbra rays are placed on the light's disc.</summary>
        public enum SampleDistribution
        {
            Jittered, // hashed + stratified; converges smoothly, costs noise at low sample counts
            Even,     // deterministic concentric rings; predictable, banding instead of noise
        }

        // Distance, in voxels, at which `penumbraVoxels` is measured. Only sets the RATE at which the
        // penumbra widens; it still grows with occluder distance, so contact hardening is preserved.
        const float PenumbraReferenceDistanceVoxels = 32f;

        public ComputeShader occlusionFieldTraceCompute;

        [Tooltip("Number of directions to bake.\nDir 1 Sun bakes current sun direction.")]
        public OcclusionFieldBake.DirectionCount directionCount = OcclusionFieldBake.DirectionCount.Dir1Sun;

        [Tooltip("Use only upper hemisphere directions (Y >= 0). Useful when the sun never goes below the horizon.")]
        public bool hemisphereOnly = true;

        [Tooltip("EXTRA rays per direction, on top of the ray straight down the direction itself " +
                 "(which is always traced). 0 = hard shadows from that single ray.\n\n" +
                 "This is the setting that matters under Signed Distance encoding: more rays place " +
                 "the shadow boundary more accurately WITHIN a voxel, which is what the fragment " +
                 "reconstructs a sharp edge from.")]
        [FormerlySerializedAs("samplesPerDirection")]
        [Range(0, 64)]
        public int penumbraSamples = 8;

        [Tooltip("How the extra rays are placed on the light's disc. Jittered converges smoothly but " +
                 "is noisy at low counts; Even is a deterministic ring pattern - 3 samples form a " +
                 "triangle halfway to the disc edge - which bands instead of dithering.")]
        public SampleDistribution sampleDistribution = SampleDistribution.Jittered;

        [Tooltip("Stop sampling early once half the samples agree. Big speedup on the fully lit and " +
                 "fully shadowed voxels that dominate a scene, but it can flatten faint partial " +
                 "occlusion in the penumbra, and it truncates the Even pattern mid-ring. Turn off " +
                 "for a final bake or when comparing distributions.")]
        public bool adaptiveSampling = true;

        [Tooltip("Whether the light's angular size is authored relative to the voxel grid or as a " +
                 "fixed real-world sun angle.\n\n" +
                 "NOTE: under Signed Distance encoding both modes barely matter. That pass thresholds " +
                 "visibility at 0.5 and keeps only the iso-surface, which cone width hardly moves - " +
                 "measured under 1% of voxels shifting by more than 8/255 between a hard cone and a " +
                 "4-voxel one. Softness there is VoxelOcclusionField.shadowEdgeVoxels, set at runtime.")]
        public PenumbraMode penumbraMode = PenumbraMode.Voxels;

        [Tooltip("Voxels mode: penumbra width in voxels, measured 32 voxels from the occluder. Keeps " +
                 "the softest shadow to something the field can actually store. 0 = hard.\n\n" +
                 "Only has real effect under Visibility encoding - see the Penumbra Mode tooltip.")]
        [Range(0f, 16f)]
        public float penumbraVoxels = 2f;

        [Tooltip("Sun Angle mode: angular DIAMETER of the light in degrees. The real sun is ~0.53; " +
                 "larger values give visibly softer shadows. At these voxel sizes a physically " +
                 "correct sun bakes a penumbra far narrower than one voxel, so it looks like hard " +
                 "shadows - exaggerate it, or use Voxels mode.\n\n" +
                 "Only has real effect under Visibility encoding - see the Penumbra Mode tooltip.")]
        [Range(0f, 30f)]
        public float sunAngularDiameter = 0.53f;

        [Tooltip("Only trace voxels within this many voxels of a surface. The field is only ever read " +
                 "just off a surface, so tracing open air is wasted work. 0 = trace everything.")]
        [Range(0, 16)]
        public int traceBandVoxels = 4;

        [Tooltip("Signed Distance stores the distance to the shadow boundary instead of the visibility " +
                 "fraction, so the fragment reconstructs a SHARP edge from the same coarse grid - the " +
                 "SDF-font trick - and softness moves to VoxelOcclusionField.shadowEdgeVoxels, tunable " +
                 "at runtime with no rebake. Rounds off creases where two shadow edges meet, so pick " +
                 "Visibility for foliage-like occluders.")]
        public VoxelOcclusionField.ShadowEncoding shadowEncoding = VoxelOcclusionField.ShadowEncoding.SignedDistance;

        [Tooltip("Signed Distance only: the +/- voxel range the 8-bit channel spans. Caps how wide a " +
                 "runtime penumbra can get; 4 voxels gives 1/32-voxel precision.")]
        [Range(1f, 16f)]
        public float sdfRangeVoxels = 4f;

        public bool HemisphereOnly => hemisphereOnly;
        public VoxelOcclusionField.ShadowEncoding Encoding => shadowEncoding;
        public float SdfRangeVoxels => sdfRangeVoxels;

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
        static readonly int s_sdfChannel = Shader.PropertyToID("_SdfChannel");
        static readonly int s_jfaStep = Shader.PropertyToID("_JfaStep");
        static readonly int s_sdfRange = Shader.PropertyToID("_SdfRange");
        static readonly int s_siteIn = Shader.PropertyToID("_SiteIn");
        static readonly int s_siteOut = Shader.PropertyToID("_SiteOut");

        /// <summary>
        /// Jump-flood step schedule: the largest power of two covering the encode range, halved down
        /// to 1, plus one extra step-1 pass to clean up the usual JFA propagation artifacts.
        ///
        /// It MUST be derived from the range rather than fixed. A flood propagates at most the sum of
        /// its steps, and any voxel it never reaches falls back to the clamp value in
        /// CSResolveShadowSdf - so a schedule shorter than the range stores "at the limit" for voxels
        /// that are merely far, which reads as a wrong distance rather than an obvious artifact.
        /// </summary>
        static int[] BuildJfaSteps(float rangeVoxels)
        {
            int step = 1;
            while (step < Mathf.CeilToInt(rangeVoxels))
                step *= 2;

            var steps = new System.Collections.Generic.List<int>();
            for (; step >= 1; step >>= 1)
                steps.Add(step);
            steps.Add(1);
            return steps.ToArray();
        }

        public bool TryBake(
            VoxelVolume sourceVolume,
            out Texture3D[] resultTextures,
            out Vector3[] bakedDirections,
            out string error
        )
        {
            resultTextures = null;
            bakedDirections = null;
            error = "";

            if (sourceVolume == null)
            {
                error = "ExactOcclusionFieldBake: sourceVolume is null";
                return false;
            }
            if (occlusionFieldTraceCompute == null)
            {
                error = "ExactOcclusionFieldBake: occlusionFieldTraceCompute not assigned";
                return false;
            }

            // This path has no SDF dependency, so it cannot rely on VoxelSdfBaker having run first -
            // it owns bounds/resolution itself. Recomputing is idempotent when the SDF baker did run.
            sourceVolume.RecomputeBoundsAndResolution();

            Transform root = sourceVolume.BakeRoot;
            if (root == null)
            {
                error = "ExactOcclusionFieldBake: Bake Root is null";
                return false;
            }

            Bounds bounds = sourceVolume.Bounds;
            Vector3Int resolution = sourceVolume.TrimmedMaxResolution;
            int voxelCount = resolution.x * resolution.y * resolution.z;

            if (bounds.size.magnitude < 0.01f || resolution.x < 2)
            {
                error = "ExactOcclusionFieldBake: invalid bounds or resolution";
                return false;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (!ExactSdfBake.TryBuildTriangleListWorld(root, bounds, out Vector3[] triVerts, out error))
                return false;
            int triCount = triVerts.Length / 3;
            if (triCount <= 0)
            {
                error = "ExactOcclusionFieldBake: triangle list is empty";
                return false;
            }

            ExactSdfBake.BuildUniformGrid(triVerts, bounds, out Vector3Int gridDim, out float cellSize,
                                          out uint[] cellStart, out uint[] triIndices);

            int numDirections = (int)directionCount;
            bool isSingleDir = directionCount == OcclusionFieldBake.DirectionCount.Dir1Sun;
            int dirsPerBatch = isSingleDir ? 1 : 4;
            int textureCount = isSingleDir ? 1 : numDirections / 4;

            GraphicsFormat textureFormat = OcclusionFieldBake.GetOcclusionFieldFormat(isSingleDir);
            if (!SystemInfo.IsFormatSupported(textureFormat, GraphicsFormatUsage.Sample))
            {
                error = $"ExactOcclusionFieldBake: format {textureFormat} not supported for sampling on this platform";
                return false;
            }

            Vector3[] packedDirections = isSingleDir
                ? new[] { OcclusionFieldQuery.GetSunDirection() }
                : Fibonacci.GenerateFibonacciDirections(numDirections, hemisphereOnly);
            bakedDirections = packedDirections;

            int kOccupancy = occlusionFieldTraceCompute.FindKernel("CSBuildOccupancy");
            int kMarkBand = occlusionFieldTraceCompute.FindKernel("CSMarkBand");
            int kTrace = occlusionFieldTraceCompute.FindKernel("CSTrace");
            int kDilate = occlusionFieldTraceCompute.FindKernel("CSDilate");
            int kSdfSeed = occlusionFieldTraceCompute.FindKernel("CSSeedShadowSdf");
            int kSdfFlood = occlusionFieldTraceCompute.FindKernel("CSJumpFloodShadowSdf");
            int kSdfResolve = occlusionFieldTraceCompute.FindKernel("CSResolveShadowSdf");
            bool encodeSdf = shadowEncoding == VoxelOcclusionField.ShadowEncoding.SignedDistance;

            int groupX = Mathf.CeilToInt(resolution.x / 8f);
            int groupY = Mathf.CeilToInt(resolution.y / 8f);
            int groupZ = Mathf.CeilToInt(resolution.z / 8f);

            // Voxels mode: penumbra width w at distance L is 2*L*tan(halfAngle). Solving for
            // w = penumbraVoxels voxels at L = 32 voxels, the voxel size cancels - the angle comes out
            // purely resolution-relative. Sun Angle mode is simply half the authored diameter.
            float halfAngle = penumbraMode == PenumbraMode.SunAngle
                ? Mathf.Max(0f, sunAngularDiameter) * 0.5f * Mathf.Deg2Rad
                : Mathf.Atan(Mathf.Max(0f, penumbraVoxels) / (2f * PenumbraReferenceDistanceVoxels));

            int extraSamples = Mathf.Max(0, penumbraSamples);
            int totalSamples = 1 + extraSamples; // the direction's own ray is always traced
            Vector2[] coneOffsets = BuildEvenDiscOffsets(extraSamples);

            var triBuffer = new ComputeBuffer(triVerts.Length, sizeof(float) * 3, ComputeBufferType.Structured);
            var cellStartBuffer = new ComputeBuffer(cellStart.Length, sizeof(uint), ComputeBufferType.Structured);
            var triIndicesBuffer = new ComputeBuffer(Mathf.Max(1, triIndices.Length), sizeof(uint), ComputeBufferType.Structured);
            var occupancyBuffer = new ComputeBuffer(voxelCount, sizeof(uint), ComputeBufferType.Structured);
            var traceMaskBuffer = new ComputeBuffer(voxelCount, sizeof(uint), ComputeBufferType.Structured);
            var dirBuffer = new ComputeBuffer(dirsPerBatch, sizeof(float) * 3);
            var coneOffsetBuffer = new ComputeBuffer(coneOffsets.Length, sizeof(float) * 2, ComputeBufferType.Structured);
            // Jump-flood ping-pong. Only allocated for the SignedDistance encoding, but the buffers
            // must exist either way because the kernels declare them.
            int siteCount = encodeSdf ? voxelCount : 1;
            var siteBufferA = new ComputeBuffer(siteCount, sizeof(float) * 4, ComputeBufferType.Structured);
            var siteBufferB = new ComputeBuffer(siteCount, sizeof(float) * 4, ComputeBufferType.Structured);

            try
            {
                coneOffsetBuffer.SetData(coneOffsets);
                triBuffer.SetData(triVerts);
                cellStartBuffer.SetData(cellStart);
                if (triIndices.Length > 0)
                    triIndicesBuffer.SetData(triIndices);

                occlusionFieldTraceCompute.SetVector(s_boundsMin, bounds.min);
                occlusionFieldTraceCompute.SetVector(s_boundsSize, bounds.size);
                occlusionFieldTraceCompute.SetInts(s_resolution, resolution.x, resolution.y, resolution.z);
                occlusionFieldTraceCompute.SetInts(s_gridDim, gridDim.x, gridDim.y, gridDim.z);
                occlusionFieldTraceCompute.SetFloat(s_cellSize, cellSize);
                occlusionFieldTraceCompute.SetInt(s_dirCount, dirsPerBatch);
                occlusionFieldTraceCompute.SetInt(s_penumbraSamples, extraSamples);
                occlusionFieldTraceCompute.SetInt(s_minSamples, adaptiveSampling ? Mathf.Max(2, totalSamples / 2) : totalSamples);
                occlusionFieldTraceCompute.SetInt(s_sampleDistribution, sampleDistribution == SampleDistribution.Even ? 1 : 0);
                occlusionFieldTraceCompute.SetFloat(s_coneCosMax, Mathf.Cos(halfAngle));
                occlusionFieldTraceCompute.SetFloat(s_coneHalfAngle, halfAngle);
                occlusionFieldTraceCompute.SetInt(s_traceBandVoxels, Mathf.Max(0, traceBandVoxels));
                occlusionFieldTraceCompute.SetFloat(s_maxRayDistance, bounds.size.magnitude);
                occlusionFieldTraceCompute.SetFloat(s_sdfRange, Mathf.Max(1f, sdfRangeVoxels));

                // Pass 1+2: conservative occupancy, then the near-surface band. Both are direction
                // independent, so they run once and are reused by every texture batch.
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
                occlusionFieldTraceCompute.SetBuffer(kDilate, s_occupancy, occupancyBuffer);

                resultTextures = new Texture3D[textureCount];

                for (int texIdx = 0; texIdx < textureCount; texIdx++)
                {
                    int dirOffset = texIdx * dirsPerBatch;

                    var batchDirs = new Vector3[dirsPerBatch];
                    for (int c = 0; c < dirsPerBatch; c++)
                        batchDirs[c] = packedDirections[dirOffset + c];
                    dirBuffer.SetData(batchDirs);

                    var outBuffer = new ComputeBuffer(voxelCount * dirsPerBatch, sizeof(float));
                    try
                    {
                        occlusionFieldTraceCompute.SetBuffer(kTrace, s_directions, dirBuffer);
                        occlusionFieldTraceCompute.SetBuffer(kTrace, s_outOcclusion, outBuffer);
                        occlusionFieldTraceCompute.Dispatch(kTrace, groupX, groupY, groupZ);

                        // Fill solid voxels from their air neighbours so the runtime's trilinear tap
                        // never blends a value sampled from inside geometry. Runs BEFORE the SDF
                        // conversion so the boundary is traced through a continuous field.
                        occlusionFieldTraceCompute.SetBuffer(kDilate, s_outOcclusion, outBuffer);
                        occlusionFieldTraceCompute.Dispatch(kDilate, groupX, groupY, groupZ);

                        if (encodeSdf)
                            EncodeShadowSdf(outBuffer, dirsPerBatch, groupX, groupY, groupZ,
                                            kSdfSeed, kSdfFlood, kSdfResolve, siteBufferA, siteBufferB);

                        var readbackRequest = AsyncGPUReadback.Request(outBuffer);
                        readbackRequest.WaitForCompletion();

                        if (readbackRequest.hasError)
                        {
                            error = $"ExactOcclusionFieldBake: readback failed for texture {texIdx}";
                            DisposePartialResults(resultTextures, texIdx);
                            resultTextures = null;
                            return false;
                        }

                        NativeArray<float> readbackData = readbackRequest.GetData<float>();
                        if (readbackData.Length != voxelCount * dirsPerBatch)
                        {
                            error = $"ExactOcclusionFieldBake: readback size mismatch for texture {texIdx} " +
                                    $"(got {readbackData.Length}, expected {voxelCount * dirsPerBatch})";
                            DisposePartialResults(resultTextures, texIdx);
                            resultTextures = null;
                            return false;
                        }

                        var tex = new Texture3D(resolution.x, resolution.y, resolution.z, textureFormat, TextureCreationFlags.None)
                        {
                            filterMode = FilterMode.Trilinear,
                            wrapMode = TextureWrapMode.Clamp,
                            name = isSingleDir
                                ? $"{sourceVolume.BakeRoot.name}_OcclusionField_Sun"
                                : $"{sourceVolume.BakeRoot.name}_OcclusionField_{texIdx:D2}"
                        };

                        if (isSingleDir)
                        {
                            var packedR8 = new byte[voxelCount];
                            for (int i = 0; i < voxelCount; i++)
                                packedR8[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(readbackData[i] * 255f), 0, 255);
                            tex.SetPixelData(packedR8, 0);
                        }
                        else
                        {
                            OcclusionFieldBake.SetPackedTextureData(tex, readbackData, voxelCount, textureFormat);
                        }
                        tex.Apply(false);

                        resultTextures[texIdx] = tex;
                    }
                    finally
                    {
                        outBuffer.Dispose();
                    }
                }

                LogBakeSummary(sourceVolume, occupancyBuffer, traceMaskBuffer, voxelCount, triCount,
                               gridDim, textureCount, numDirections, resolution, halfAngle, totalSamples,
                               sampleDistribution, shadowEncoding, stopwatch);
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
                siteBufferA.Release();
                siteBufferB.Release();
            }

            void BindGrid(int kernel)
            {
                occlusionFieldTraceCompute.SetBuffer(kernel, s_triVerts, triBuffer);
                occlusionFieldTraceCompute.SetBuffer(kernel, s_cellStart, cellStartBuffer);
                occlusionFieldTraceCompute.SetBuffer(kernel, s_triIndices, triIndicesBuffer);
            }
        }

        /// <summary>
        /// Rewrites each channel of <paramref name="outBuffer"/> from a visibility fraction into a
        /// signed distance to the shadow boundary, via a bounded jump flood. Channels are converted
        /// one at a time: they occupy disjoint slots, so no channel can disturb another.
        /// </summary>
        void EncodeShadowSdf(
            ComputeBuffer outBuffer,
            int channelCount,
            int groupX, int groupY, int groupZ,
            int kSeed, int kFlood, int kResolve,
            ComputeBuffer siteA, ComputeBuffer siteB
        )
        {
            for (int channel = 0; channel < channelCount; channel++)
            {
                occlusionFieldTraceCompute.SetInt(s_sdfChannel, channel);

                occlusionFieldTraceCompute.SetBuffer(kSeed, s_outOcclusion, outBuffer);
                occlusionFieldTraceCompute.SetBuffer(kSeed, s_siteOut, siteA);
                occlusionFieldTraceCompute.Dispatch(kSeed, groupX, groupY, groupZ);

                ComputeBuffer read = siteA;
                ComputeBuffer write = siteB;
                foreach (int step in BuildJfaSteps(Mathf.Max(1f, sdfRangeVoxels)))
                {
                    occlusionFieldTraceCompute.SetInt(s_jfaStep, step);
                    occlusionFieldTraceCompute.SetBuffer(kFlood, s_siteIn, read);
                    occlusionFieldTraceCompute.SetBuffer(kFlood, s_siteOut, write);
                    occlusionFieldTraceCompute.Dispatch(kFlood, groupX, groupY, groupZ);
                    (read, write) = (write, read);
                }

                occlusionFieldTraceCompute.SetBuffer(kResolve, s_outOcclusion, outBuffer);
                occlusionFieldTraceCompute.SetBuffer(kResolve, s_siteIn, read);
                occlusionFieldTraceCompute.Dispatch(kResolve, groupX, groupY, groupZ);
            }
        }

        // Traced-vs-skipped is the number to watch when the bake feels slow: it says how much of the
        // volume the near-surface band actually excluded.
        static void LogBakeSummary(
            VoxelVolume volume,
            ComputeBuffer occupancyBuffer,
            ComputeBuffer traceMaskBuffer,
            int voxelCount,
            int triCount,
            Vector3Int gridDim,
            int textureCount,
            int numDirections,
            Vector3Int resolution,
            float halfAngle,
            int totalSamples,
            SampleDistribution distribution,
            VoxelOcclusionField.ShadowEncoding encoding,
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
                $"ExactOcclusionFieldBake: {textureCount} textures ({numDirections} directions, " +
                $"{resolution.x}x{resolution.y}x{resolution.z} = {voxelCount} voxels) in {stopwatch.ElapsedMilliseconds} ms. " +
                $"{triCount} triangles in a {gridDim.x}x{gridDim.y}x{gridDim.z} grid. " +
                $"Traced {traced} voxels ({100f * traced / Mathf.Max(1, voxelCount):F1}%), " +
                $"{solid} solid, {voxelCount - traced - solid} skipped as far from any surface. " +
                $"Cone half-angle {halfAngle * Mathf.Rad2Deg:F3} deg, " +
                $"{totalSamples} rays/direction ({distribution}), encoding {encoding}.",
                volume);
        }

        /// <summary>
        /// Deterministic sample positions on the unit disc for <see cref="SampleDistribution.Even"/>,
        /// laid out as concentric rings. Ring k of K sits at radius (k + 0.5) / K and gets a share of
        /// the samples proportional to its radius, so the points stay roughly equal-area rather than
        /// bunching at the centre. Successive rings are rotated by half a step to avoid spokes.
        ///
        /// With one ring this is exactly the requested pattern: 3 samples form a triangle halfway to
        /// the disc edge. The centre of the disc is not included here - the ray straight down the
        /// direction itself is always traced separately.
        /// </summary>
        internal static Vector2[] BuildEvenDiscOffsets(int count)
        {
            if (count <= 0)
                return new[] { Vector2.zero }; // compute buffers cannot be empty

            // One ring per ~3 samples, so count == 3 yields the single-ring triangle.
            int ringCount = Mathf.Max(1, Mathf.RoundToInt(Mathf.Sqrt(count / 3f)));

            float radiusSum = 0f;
            for (int k = 0; k < ringCount; k++)
                radiusSum += (k + 0.5f) / ringCount;

            var offsets = new Vector2[count];
            int written = 0;
            float cumulative = 0f;
            for (int k = 0; k < ringCount; k++)
            {
                float radius = (k + 0.5f) / ringCount;

                // Cumulative rounding: guarantees the ring counts sum to exactly `count`.
                cumulative += radius / radiusSum;
                int ringCountTarget = Mathf.RoundToInt(cumulative * count);
                int ringSamples = ringCountTarget - written;
                if (ringSamples <= 0)
                    continue;

                float angleStep = 2f * Mathf.PI / ringSamples;
                float angleOffset = angleStep * 0.5f * k;
                for (int j = 0; j < ringSamples; j++)
                {
                    float angle = angleOffset + angleStep * j;
                    offsets[written++] = new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
                }
            }

            // Any shortfall from rounding lands on the outer ring rather than staying (0,0), which
            // would silently duplicate the centre ray.
            for (; written < count; written++)
                offsets[written] = new Vector2(1f, 0f);

            return offsets;
        }

        static void DisposePartialResults(Texture3D[] textures, int upTo)
        {
            for (int i = 0; i < upTo; i++)
            {
                if (textures[i] != null)
                    UnityEngine.Object.DestroyImmediate(textures[i]);
            }
        }
    }
}
