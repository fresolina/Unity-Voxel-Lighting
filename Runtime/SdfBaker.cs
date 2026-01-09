using System;
using System.Collections.Generic;
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

        // Variables the SDF compute shader needs.
        static readonly int sTriVerts = Shader.PropertyToID("_TriVerts");
        static readonly int sTriCount = Shader.PropertyToID("_TriCount");
        static readonly int sBoundsMin = Shader.PropertyToID("_BoundsMin");
        static readonly int sBoundsSize = Shader.PropertyToID("_BoundsSize");
        static readonly int sResolution = Shader.PropertyToID("_Resolution");
        static readonly int sOutSdf = Shader.PropertyToID("_OutSdf");
        int _kernel = -1;

        public bool TryBake(
            SdfVolume volume,
            out Texture3D bakedSdf,
            out string error
        ) {
            bakedSdf = null;

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

            if (!volume.TryComputeBoundsFromRoot(out Bounds bounds)) {
                error = "No meshes found under Bake Root (MeshRenderer+MeshFilter).";
                return false;
            }

            volume.bakedBounds = bounds;
            volume.RecomputeBoundsAndResolution();

            if (_kernel < 0)
                _kernel = sdfBakeCompute.FindKernel("CSMain");

            if (!TryBuildTriangleListWorld(root, out Vector3[] triVerts, out error))
                return false;

            int triCount = triVerts.Length / 3;
            if (triCount <= 0) {
                error = "Triangle list is empty.";
                return false;
            }

            int voxelCount = volume.bakedResolution.x * volume.bakedResolution.y * volume.bakedResolution.z;
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

                Vector3 bmin = volume.bakedBounds.min;
                Vector3 bsize = volume.bakedBounds.size;

                sdfBakeCompute.SetVector(sBoundsMin, bmin);
                sdfBakeCompute.SetVector(sBoundsSize, bsize);
                sdfBakeCompute.SetInts(sResolution, volume.bakedResolution.x, volume.bakedResolution.y, volume.bakedResolution.z);
                sdfBakeCompute.SetBuffer(_kernel, sOutSdf, sdfBuffer);

                sdfBakeCompute.GetKernelThreadGroupSizes(_kernel, out uint tx, out uint ty, out uint tz);
                int gx = Mathf.CeilToInt(volume.bakedResolution.x / (float)tx);
                int gy = Mathf.CeilToInt(volume.bakedResolution.y / (float)ty);
                int gz = Mathf.CeilToInt(volume.bakedResolution.z / (float)tz);
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

                bakedSdf = new Texture3D(volume.bakedResolution.x, volume.bakedResolution.y, volume.bakedResolution.z, TextureFormat.RHalf, true, true) {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                    name = $"{root.name}_SDF"
                };

                bakedSdf.SetPixelData(packed, 0);
                bakedSdf.Apply(false, false);

                return true;
            } finally {
                triBuffer.Release();
                sdfBuffer.Release();
            }
        }

        static bool TryBuildTriangleListWorld(
            Transform root,
            out Vector3[] triVerts,
            out string error
        ) {
            error = null;

            List<Vector3> verts = new List<Vector3>(1024);

            // MeshRenderer + MeshFilter
            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            foreach (MeshRenderer mr in meshRenderers) {
                if (mr == null)
                    continue;
                if (!mr.TryGetComponent(out MeshFilter mf))
                    continue;
                Mesh mesh = mf.sharedMesh;
                if (mesh == null)
                    continue;

                if (!AppendMeshTrianglesWorld(mesh, mf.transform.localToWorldMatrix, verts)) {
                    error = "Failed to read triangles from a MeshFilter mesh.";
                    triVerts = Array.Empty<Vector3>();
                    return false;
                }
            }

            triVerts = verts.ToArray();

            return true;
        }

        static bool AppendMeshTrianglesWorld(
            Mesh mesh,
            Matrix4x4 localToWorld,
            List<Vector3> outTriVerts
        ) {
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

                    outTriVerts.Add(localToWorld.MultiplyPoint3x4(v[i0]));
                    outTriVerts.Add(localToWorld.MultiplyPoint3x4(v[i1]));
                    outTriVerts.Add(localToWorld.MultiplyPoint3x4(v[i2]));
                }
            }

            return true;
        }
    }
}

