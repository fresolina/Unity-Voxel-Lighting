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
        public ComputeShader bitmaskBakeCompute;
        [Min(0.1f)] public float occlusionDistance = 5.0f;

        static readonly int sTriVerts = Shader.PropertyToID("_TriVerts");
        static readonly int sTriCount = Shader.PropertyToID("_TriCount");
        static readonly int sBoundsMin = Shader.PropertyToID("_BoundsMin");
        static readonly int sBoundsSize = Shader.PropertyToID("_BoundsSize");
        static readonly int sResolution = Shader.PropertyToID("_Resolution");
        static readonly int sOutBitmask = Shader.PropertyToID("_OutBitmask");
        static readonly int sOcclusionDistance = Shader.PropertyToID("_OcclusionDistance");

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

            // Collect all triangle vertices from scene geometry
            var meshFilters = GameObject.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
            var allVerts = new System.Collections.Generic.List<Vector3>();

            foreach (var mf in meshFilters) {
                if (mf.sharedMesh == null) continue;

                var mesh = mf.sharedMesh;
                var verts = mesh.vertices;
                var tris = mesh.triangles;

                // Transform verts to world space
                var matrix = mf.transform.localToWorldMatrix;
                for (int i = 0; i < tris.Length; i++) {
                    allVerts.Add(matrix.MultiplyPoint(verts[tris[i]]));
                }
            }

            if (allVerts.Count == 0) {
                error = "BitmaskOcclusionBaker: no geometry found in scene";
                result = new Texture3D(resolution.x, resolution.y, resolution.z, bitmaskFormat, TextureCreationFlags.None) {
                    filterMode = FilterMode.Point,
                    name = $"{sourceVolume.BakeRoot.name}_BitmaskOcclusion"
                };
                // Ensure deterministic all-zero texture.
                result.SetPixelData(new ushort[bitmaskSize * 4], 0);
                result.Apply();
                return true;
            }

            // Create compute buffers
            var triBuffer = new ComputeBuffer(allVerts.Count, sizeof(float) * 3);
            triBuffer.SetData(allVerts);

            var bitmaskBuffer = new ComputeBuffer(bitmaskSize, sizeof(uint) * 2); // uint2

            // Dispatch compute shader
            int kernelIdx = bitmaskBakeCompute.FindKernel("CSMain");

            bitmaskBakeCompute.SetBuffer(kernelIdx, sTriVerts, triBuffer);
            bitmaskBakeCompute.SetInt(sTriCount, allVerts.Count);
            bitmaskBakeCompute.SetVector(sBoundsMin, bounds.min);
            bitmaskBakeCompute.SetVector(sBoundsSize, bounds.size);
            bitmaskBakeCompute.SetInts(sResolution, resolution.x, resolution.y, resolution.z);
            bitmaskBakeCompute.SetFloat(sOcclusionDistance, occlusionDistance);
            bitmaskBakeCompute.SetBuffer(kernelIdx, sOutBitmask, bitmaskBuffer);

            uint groupX = (uint)Mathf.CeilToInt(resolution.x / 8f);
            uint groupY = (uint)Mathf.CeilToInt(resolution.y / 8f);
            uint groupZ = (uint)Mathf.CeilToInt(resolution.z / 8f);

            bitmaskBakeCompute.Dispatch(kernelIdx, (int)groupX, (int)groupY, (int)groupZ);

            // Read back bitmask
            var readbackRequest = AsyncGPUReadback.Request(bitmaskBuffer);
            readbackRequest.WaitForCompletion();

            if (readbackRequest.hasError) {
                error = "BitmaskOcclusionBaker: readback failed";
                triBuffer.Dispose();
                bitmaskBuffer.Dispose();
                return false;
            }

            var readbackData = readbackRequest.GetData<uint>();
            if (readbackData.Length != bitmaskSize * 2) {
                error = $"BitmaskOcclusionBaker: readback size mismatch (got {readbackData.Length}, expected {bitmaskSize * 2})";
                triBuffer.Dispose();
                bitmaskBuffer.Dispose();
                return false;
            }

            // Pack uint2 bitmask into RGBA16_UNorm texels (4 ushorts per voxel)
            var packed = new ushort[bitmaskSize * 4];
            for (int voxel = 0; voxel < bitmaskSize; voxel++) {
                uint x = readbackData[voxel * 2 + 0];
                uint y = readbackData[voxel * 2 + 1];

                packed[voxel * 4 + 0] = (ushort)(x & 0xFFFFu);
                packed[voxel * 4 + 1] = (ushort)(x >> 16);
                packed[voxel * 4 + 2] = (ushort)(y & 0xFFFFu);
                packed[voxel * 4 + 3] = (ushort)(y >> 16);
            }

            // Create Texture3D
            result = new Texture3D(resolution.x, resolution.y, resolution.z, bitmaskFormat, TextureCreationFlags.None) {
                filterMode = FilterMode.Point,
                name = $"{sourceVolume.BakeRoot.name}_OcclusionBitmask"
            };
            result.filterMode = FilterMode.Point; // No interpolation for bitmask
            result.SetPixelData(packed, 0);
            result.Apply();

            triBuffer.Dispose();
            bitmaskBuffer.Dispose();

            Debug.Log($"OcclusionBitmaskBaker: baked successfully ({resolution.x}x{resolution.y}x{resolution.z} = {bitmaskSize} voxels, {allVerts.Count} vertices, occlusion distance {occlusionDistance}m)");
            return true;
        }
    }
}
