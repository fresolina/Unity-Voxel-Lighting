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
    public class SdfBaker {
        public ComputeShader sdfBakeCompute;

        // Hard-invalid triangles are excluded from the bake. A degenerate triangle has
        // effectively zero usable area, for example when two vertices coincide or all
        // three points are nearly collinear, so its normal/distance math is unstable.
        // Very large triangles relative to the bake bounds are still logged because
        // they can be a useful clue when tracking down mesh import issues.
        const float DegenerateEdgeEpsilon = 1e-5f;
        const float OversizedEdgeBoundsFactor = 0.75f;

        // Variables the SDF compute shader needs.
        static readonly int sTriVerts = Shader.PropertyToID("_TriVerts");
        static readonly int sTriCount = Shader.PropertyToID("_TriCount");
        static readonly int sBoundsMin = Shader.PropertyToID("_BoundsMin");
        static readonly int sBoundsSize = Shader.PropertyToID("_BoundsSize");
        static readonly int sResolution = Shader.PropertyToID("_Resolution");
        static readonly int sOutSdf = Shader.PropertyToID("_OutSdf");
        int _kernel = -1;

        public bool TryBake(
            LightingVolume volume,
            Vector3Int resolution,
            string textureName,
            out Texture3D bakedSdf,
            out string error
        ) {
            bakedSdf = null;
            Debug.Log($"Starting SDF bake with resolution {resolution} for volume '{volume.name}'", volume);
            if (volume == null) {
                error = "Target SdfVolume is null.";
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

            // Output is packed half bits in the low 16 bits of a uint (see SdfBake.compute).
            var sdfBuffer = new ComputeBuffer(voxelCount, sizeof(uint), ComputeBufferType.Structured);
            var triBuffer = new ComputeBuffer(triVerts.Length, sizeof(float) * 3, ComputeBufferType.Structured);

            try {
                triBuffer.SetData(triVerts);
                sdfBakeCompute.SetBuffer(_kernel, sTriVerts, triBuffer);
                sdfBakeCompute.SetInt(sTriCount, triCount);

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

                return true;
            } finally {
                triBuffer.Release();
                sdfBuffer.Release();
            }
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
        static bool TryBuildTriangleListWorld(
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
                if (!IsBakeEligible(mr))
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

        static bool IsBakeEligible(Renderer renderer) {
            GameObject gameObject = renderer.gameObject;
            return gameObject.activeInHierarchy && gameObject.isStatic;
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

