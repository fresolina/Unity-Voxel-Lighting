using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    /// <summary>
    /// Bakes signed distance fields (SDF).
    /// </summary>
    [Serializable]
    public class ExactSdfBake : ISdfBake {
        public ComputeShader sdfBakeCompute;

        // Hard-invalid triangles are excluded from the bake. A degenerate triangle has
        // effectively zero usable area, for example when two vertices coincide or all
        // three points are nearly collinear, so its normal/distance math is unstable.
        // Very large triangles relative to the bake bounds are still logged because
        // they can be a useful clue when tracking down mesh import issues.
        const float DegenerateEdgeEpsilon = 1e-5f;
        const float OversizedEdgeBoundsFactor = 0.75f;

        // Largest grid dimension allowed per axis when binning triangles into cells.
        // Caps the acceleration-structure memory for skewed bounds; the grid is sized
        // to roughly one triangle per cell, so typical scenes stay well under this.
        const int MaxGridDim = 128;

        // Variables the SDF compute shader needs.
        static readonly int sTriVerts = Shader.PropertyToID("_TriVerts");
        static readonly int sCellStart = Shader.PropertyToID("_CellStart");
        static readonly int sTriIndices = Shader.PropertyToID("_TriIndices");
        static readonly int sGridDim = Shader.PropertyToID("_GridDim");
        static readonly int sCellSize = Shader.PropertyToID("_CellSize");
        static readonly int sBoundsMin = Shader.PropertyToID("_BoundsMin");
        static readonly int sBoundsSize = Shader.PropertyToID("_BoundsSize");
        static readonly int sResolution = Shader.PropertyToID("_Resolution");
        static readonly int sOutSdf = Shader.PropertyToID("_OutSdf");
        int _kernel = -1;

        public bool TryBake(
            VoxelVolume volume,
            Vector3Int resolution,
            string textureName,
            out Texture3D bakedSdf,
            out string error
        ) {
            bakedSdf = null;
            Debug.Log($"Starting SDF bake (exact) with resolution {resolution} for volume '{volume.name}'", volume);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (volume == null) {
                error = "Target VoxelVolume is null.";
                return false;
            }
            Transform root = volume.BakeRoot;
            if (root == null) {
                error = "Bake Root is null.";
                return false;
            }
            if (sdfBakeCompute == null) {
                error = "SDF Bake Compute is not assigned.";
                return false;
            }

            if (_kernel < 0)
                _kernel = sdfBakeCompute.FindKernel("CSMain");

            if (!TryBuildTriangleListWorld(root, volume.Bounds, out Vector3[] triVerts, out error))
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

            // Bin triangles into a uniform grid so each voxel only tests nearby
            // triangles. The grid yields the exact same nearest triangle as a full
            // scan, so the bake result is unchanged - only faster.
            BuildUniformGrid(triVerts, volume.Bounds, out Vector3Int gridDim, out float cellSize, out uint[] cellStart, out uint[] triIndices);

            // Output is packed half bits in the low 16 bits of a uint (see SdfBake.compute).
            var sdfBuffer = new ComputeBuffer(voxelCount, sizeof(uint), ComputeBufferType.Structured);
            var triBuffer = new ComputeBuffer(triVerts.Length, sizeof(float) * 3, ComputeBufferType.Structured);
            var cellStartBuffer = new ComputeBuffer(cellStart.Length, sizeof(uint), ComputeBufferType.Structured);
            // ComputeBuffer requires a positive count; an empty grid still needs a valid (dummy) buffer.
            var triIndicesBuffer = new ComputeBuffer(Mathf.Max(1, triIndices.Length), sizeof(uint), ComputeBufferType.Structured);

            try {
                triBuffer.SetData(triVerts);
                cellStartBuffer.SetData(cellStart);
                if (triIndices.Length > 0)
                    triIndicesBuffer.SetData(triIndices);

                sdfBakeCompute.SetBuffer(_kernel, sTriVerts, triBuffer);
                sdfBakeCompute.SetBuffer(_kernel, sCellStart, cellStartBuffer);
                sdfBakeCompute.SetBuffer(_kernel, sTriIndices, triIndicesBuffer);
                sdfBakeCompute.SetInts(sGridDim, gridDim.x, gridDim.y, gridDim.z);
                sdfBakeCompute.SetFloat(sCellSize, cellSize);

                Vector3 bmin = volume.Bounds.min;
                Vector3 bsize = volume.Bounds.size;

                sdfBakeCompute.SetVector(sBoundsMin, bmin);
                sdfBakeCompute.SetVector(sBoundsSize, bsize);
                sdfBakeCompute.SetInts(sResolution, resolution.x, resolution.y, resolution.z);
                sdfBakeCompute.SetBuffer(_kernel, sOutSdf, sdfBuffer);

                sdfBakeCompute.GetKernelThreadGroupSizes(_kernel, out uint tx, out uint ty, out uint tz);
                int gx = Mathf.CeilToInt(resolution.x / (float)tx);
                int gy = Mathf.CeilToInt(resolution.y / (float)ty);
                int gz = Mathf.CeilToInt(resolution.z / (float)tz);
                sdfBakeCompute.Dispatch(_kernel, gx, gy, gz);

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

                Debug.Log($"SDF bake (exact) completed in {stopwatch.ElapsedMilliseconds} ms for resolution {resolution} ({triCount} triangles)", volume);
                return true;
            } finally {
                triBuffer.Release();
                sdfBuffer.Release();
                cellStartBuffer.Release();
                triIndicesBuffer.Release();
            }
        }

        // Builds a uniform grid over the bake bounds and registers every triangle in
        // the cells its AABB overlaps. The result is a CSR layout: cellStart[i]..
        // cellStart[i+1] are the slots in triIndices that belong to cell i. The grid
        // is sized to roughly one triangle per cell so the compute shader's expanding
        // shell search touches only a handful of triangles per voxel, while still
        // finding the exact nearest triangle (triangles outside the bounds clamp into
        // border cells, where they remain reachable by edge voxels).
        // Internal so alternative bakers (e.g. JfaSdfBake) can share the grid build.
        internal static void BuildUniformGrid(
            Vector3[] triVerts,
            Bounds bounds,
            out Vector3Int gridDim,
            out float cellSize,
            out uint[] cellStart,
            out uint[] triIndices
        ) {
            int triCount = triVerts.Length / 3;
            Vector3 bmin = bounds.min;
            Vector3 bsize = Vector3.Max(bounds.size, new Vector3(1e-6f, 1e-6f, 1e-6f));

            // Target ~one triangle per cell, then derive a cubic cell size from the
            // bounds volume. The product of the per-axis cell counts is ~triCount.
            int targetCells = Mathf.Max(1, triCount);
            double volume = (double)bsize.x * bsize.y * bsize.z;
            cellSize = (float)System.Math.Cbrt(volume / targetCells);

            gridDim = new Vector3Int(
                Mathf.Clamp(Mathf.CeilToInt(bsize.x / cellSize), 1, MaxGridDim),
                Mathf.Clamp(Mathf.CeilToInt(bsize.y / cellSize), 1, MaxGridDim),
                Mathf.Clamp(Mathf.CeilToInt(bsize.z / cellSize), 1, MaxGridDim)
            );

            // Re-derive cellSize so cubic cells fully cover the bounds even after the
            // per-axis clamp, then trim any axis that ended up with spare cells.
            cellSize = Mathf.Max(bsize.x / gridDim.x, Mathf.Max(bsize.y / gridDim.y, bsize.z / gridDim.z));
            gridDim = new Vector3Int(
                Mathf.Clamp(Mathf.CeilToInt(bsize.x / cellSize), 1, MaxGridDim),
                Mathf.Clamp(Mathf.CeilToInt(bsize.y / cellSize), 1, MaxGridDim),
                Mathf.Clamp(Mathf.CeilToInt(bsize.z / cellSize), 1, MaxGridDim)
            );

            int cellCount = gridDim.x * gridDim.y * gridDim.z;
            float invCellSize = 1f / cellSize;

            // Pass 1: count triangles per cell.
            int[] counts = new int[cellCount];
            for (int tri = 0; tri < triCount; tri++) {
                CellRange(triVerts, tri, bmin, invCellSize, gridDim, out Vector3Int cmin, out Vector3Int cmax);
                for (int z = cmin.z; z <= cmax.z; z++)
                    for (int y = cmin.y; y <= cmax.y; y++)
                        for (int x = cmin.x; x <= cmax.x; x++)
                            counts[x + y * gridDim.x + z * gridDim.x * gridDim.y]++;
            }

            // Prefix sum into CSR offsets.
            cellStart = new uint[cellCount + 1];
            uint running = 0;
            for (int i = 0; i < cellCount; i++) {
                cellStart[i] = running;
                running += (uint)counts[i];
            }
            cellStart[cellCount] = running;

            // Pass 2: scatter triangle indices into their cells.
            triIndices = new uint[running];
            uint[] cursor = new uint[cellCount];
            Array.Copy(cellStart, cursor, cellCount);
            for (int tri = 0; tri < triCount; tri++) {
                CellRange(triVerts, tri, bmin, invCellSize, gridDim, out Vector3Int cmin, out Vector3Int cmax);
                for (int z = cmin.z; z <= cmax.z; z++)
                    for (int y = cmin.y; y <= cmax.y; y++)
                        for (int x = cmin.x; x <= cmax.x; x++) {
                            int cell = x + y * gridDim.x + z * gridDim.x * gridDim.y;
                            triIndices[cursor[cell]++] = (uint)tri;
                        }
            }
        }

        // Computes the inclusive grid-cell range covered by triangle `tri`'s AABB,
        // clamped to the grid so out-of-bounds geometry registers in border cells.
        static void CellRange(
            Vector3[] triVerts,
            int tri,
            Vector3 bmin,
            float invCellSize,
            Vector3Int gridDim,
            out Vector3Int cmin,
            out Vector3Int cmax
        ) {
            int b = tri * 3;
            Vector3 a = triVerts[b + 0];
            Vector3 c1 = triVerts[b + 1];
            Vector3 c2 = triVerts[b + 2];

            Vector3 lo = Vector3.Min(a, Vector3.Min(c1, c2));
            Vector3 hi = Vector3.Max(a, Vector3.Max(c1, c2));

            cmin = ClampCell(lo, bmin, invCellSize, gridDim);
            cmax = ClampCell(hi, bmin, invCellSize, gridDim);
        }

        static Vector3Int ClampCell(Vector3 worldPos, Vector3 bmin, float invCellSize, Vector3Int gridDim) {
            int x = Mathf.Clamp(Mathf.FloorToInt((worldPos.x - bmin.x) * invCellSize), 0, gridDim.x - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt((worldPos.y - bmin.y) * invCellSize), 0, gridDim.y - 1);
            int z = Mathf.Clamp(Mathf.FloorToInt((worldPos.z - bmin.z) * invCellSize), 0, gridDim.z - 1);
            return new Vector3Int(x, y, z);
        }

        struct MeshTriangleDiagnostics {
            public string MeshName;
            public string RendererName;
            public int TriangleCount;
            public int DegenerateCount;
            public int OversizedCount;
            public int SkippedTriangleCount;
            public float MaxEdge;
        }

        // Builds the world-space triangle list used by the compute shader and gathers
        // per-mesh diagnostics so problematic imported geometry can be identified.
        // Internal so alternative bakers (e.g. JfaSdfBake) can share triangle gathering.
        internal static bool TryBuildTriangleListWorld(
            Transform root,
            Bounds bakeBounds,
            out Vector3[] triVerts,
            out string error
        ) {
            error = null;

            List<Vector3> verts = new List<Vector3>(1024);
            float boundsDiagonal = bakeBounds.size.magnitude;
            List<MeshTriangleDiagnostics> diagnostics = new List<MeshTriangleDiagnostics>();

            // MeshRenderer + MeshFilter
            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>();
            foreach (MeshRenderer mr in meshRenderers) {
                if (mr == null)
                    continue;
                if (!MeshBounds.IsBakeEligible(mr))
                    continue;
                if (!mr.TryGetComponent(out MeshFilter mf))
                    continue;
                Mesh mesh = mf.sharedMesh;
                if (mesh == null)
                    continue;

                if (!AppendMeshTrianglesWorld(mesh, mf.transform.localToWorldMatrix, verts, boundsDiagonal, out MeshTriangleDiagnostics meshDiagnostics)) {
                    error = "Failed to read triangles from a MeshFilter mesh.";
                    triVerts = Array.Empty<Vector3>();
                    return false;
                }

                meshDiagnostics.MeshName = mesh.name;
                meshDiagnostics.RendererName = mf.transform.name;
                diagnostics.Add(meshDiagnostics);
            }

            triVerts = verts.ToArray();
            LogSuspiciousTriangleDiagnostics(diagnostics, bakeBounds);

            return true;
        }

        // Reads one mesh into the flattened world-space triangle buffer used by the SDF bake.
        // Invalid triangles are skipped here so they never reach the compute shader.
        static bool AppendMeshTrianglesWorld(
            Mesh mesh,
            Matrix4x4 localToWorld,
            List<Vector3> outTriVerts,
            float boundsDiagonal,
            out MeshTriangleDiagnostics diagnostics
        ) {
            diagnostics = default;
            if (mesh == null)
                return false;

            // NOTE: for large meshes this alloc can be heavy; acceptable for editor bake.
            Vector3[] v = mesh.vertices;
            if (v == null || v.Length == 0)
                return true;
            int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
            for (int sm = 0; sm < subMeshCount; sm++) {
                int[] t;
                try {
                    t = mesh.GetTriangles(sm);
                } catch {
                    // Some meshes can throw for invalid submesh data.
                    continue;
                }

                if (t == null || t.Length < 3)
                    continue;

                for (int i = 0; i + 2 < t.Length; i += 3) {
                    int i0 = t[i + 0];
                    int i1 = t[i + 1];
                    int i2 = t[i + 2];

                    if ((uint)i0 >= (uint)v.Length || (uint)i1 >= (uint)v.Length || (uint)i2 >= (uint)v.Length)
                        continue;

                    Vector3 w0 = localToWorld.MultiplyPoint3x4(v[i0]);
                    Vector3 w1 = localToWorld.MultiplyPoint3x4(v[i1]);
                    Vector3 w2 = localToWorld.MultiplyPoint3x4(v[i2]);

                    if (ShouldSkipTriangle(w0, w1, w2, boundsDiagonal, ref diagnostics))
                        continue;

                    outTriVerts.Add(w0);
                    outTriVerts.Add(w1);
                    outTriVerts.Add(w2);
                }
            }

            return true;
        }

        // Classifies whether a triangle is too broken to be useful for SDF generation.
        // Only degenerate triangles are rejected here.
        static bool ShouldSkipTriangle(
            Vector3 a,
            Vector3 b,
            Vector3 c,
            float boundsDiagonal,
            ref MeshTriangleDiagnostics diagnostics
        ) {
            diagnostics.TriangleCount++;

            float edgeAB = Vector3.Distance(a, b);
            float edgeBC = Vector3.Distance(b, c);
            float edgeCA = Vector3.Distance(c, a);
            float maxEdge = Mathf.Max(edgeAB, Mathf.Max(edgeBC, edgeCA));
            float minEdge = Mathf.Min(edgeAB, Mathf.Min(edgeBC, edgeCA));
            diagnostics.MaxEdge = Mathf.Max(diagnostics.MaxEdge, maxEdge);

            Vector3 cross = Vector3.Cross(b - a, c - a);
            float doubleArea = cross.magnitude;

            if (minEdge <= DegenerateEdgeEpsilon || doubleArea <= DegenerateEdgeEpsilon) {
                diagnostics.DegenerateCount++;
                diagnostics.SkippedTriangleCount++;
                return true;
            }

            if (boundsDiagonal > DegenerateEdgeEpsilon && maxEdge >= boundsDiagonal * OversizedEdgeBoundsFactor) {
                diagnostics.OversizedCount++;
            }

            return false;
        }

        // Emits a compact warning listing meshes that contain suspicious triangles so the
        // source asset can be inspected in DCC tools when the bake shows artifacts.
        static void LogSuspiciousTriangleDiagnostics(List<MeshTriangleDiagnostics> diagnostics, Bounds bakeBounds) {
            if (diagnostics == null || diagnostics.Count == 0)
                return;

            int suspiciousMeshCount = 0;
            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"SDF triangle diagnostics for bake bounds '{bakeBounds.size}'");

            foreach (MeshTriangleDiagnostics diagnostic in diagnostics) {
                if (diagnostic.DegenerateCount == 0 && diagnostic.OversizedCount == 0)
                    continue;

                suspiciousMeshCount++;
                builder.AppendLine(
                    $"- Renderer '{diagnostic.RendererName}' Mesh '{diagnostic.MeshName}': tris={diagnostic.TriangleCount}, skipped={diagnostic.SkippedTriangleCount}, degenerate={diagnostic.DegenerateCount}, oversized={diagnostic.OversizedCount}, maxEdge={diagnostic.MaxEdge:F3}"
                );
            }

            if (suspiciousMeshCount > 0) {
                Debug.LogWarning(builder.ToString());
            }
        }
    }
}

