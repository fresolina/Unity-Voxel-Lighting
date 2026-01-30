using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    [Serializable]
    public class MaterialBaker {
        public ComputeShader MaterialBakeCompute;

        [Tooltip("Downscale factor to produce lower-res material field (choose 2 - 6.")]
        [Range(2, 6)]
        public int DownscaleFactor = 4;

        static readonly int s_triVerts = Shader.PropertyToID("_TriVerts");
        static readonly int s_triCount = Shader.PropertyToID("_TriCount");
        static readonly int s_triMatA = Shader.PropertyToID("_TriMatA");
        static readonly int s_triMatB = Shader.PropertyToID("_TriMatB");
        static readonly int s_triUVs = Shader.PropertyToID("_TriUVs");
        static readonly int s_triAlbedoTex = Shader.PropertyToID("_TriAlbedoTex");
        static readonly int s_triEmissionTex = Shader.PropertyToID("_TriEmissionTex");
        static readonly int s_albedoArray = Shader.PropertyToID("_AlbedoArray");
        static readonly int s_emissionArray = Shader.PropertyToID("_EmissionArray");
        static readonly int s_albedoSize = Shader.PropertyToID("_AlbedoSize");
        static readonly int s_emissionSize = Shader.PropertyToID("_EmissionSize");
        static readonly int s_boundsMin = Shader.PropertyToID("_BoundsMin");
        static readonly int s_boundsSize = Shader.PropertyToID("_BoundsSize");
        static readonly int s_resolution = Shader.PropertyToID("_Resolution");
        static readonly int s_outAlbedo = Shader.PropertyToID("_OutAlbedo");
        static readonly int s_outEmission = Shader.PropertyToID("_OutEmission");

        int _kernel = -1;

        // Bake materials into two CPU-side Texture3D assets.
        // Returns null on success, or an error string describing the failure.
        public string Bake(SdfVolume volume, out Texture3D albedoRoughness, out Texture3D emissionMetallic) {
            albedoRoughness = null;
            emissionMetallic = null;
            Transform root = volume.BakeRoot;
            if (volume == null) return "Target SdfVolume is null.";
            if (MaterialBakeCompute == null) return "Material Bake Compute is not assigned.";
            if (root == null) return "Bake Root is null.";

            // Low resolution is bakedResolution / downscaleFactor
            Bounds bounds = volume.bakedBounds;
            Vector3Int highRes = volume.bakedResolution;

            // Validate DownscaleFactor, ensuring nearest allowed value.
            DownscaleFactor = Mathf.Clamp(DownscaleFactor, 2, 6);

            Vector3Int lowRes = new Vector3Int(
                Math.Max(1, highRes.x / DownscaleFactor),
                Math.Max(1, highRes.y / DownscaleFactor),
                Math.Max(1, highRes.z / DownscaleFactor)
            );

            if (_kernel < 0)
                _kernel = MaterialBakeCompute.FindKernel("CSMain");

            if (!TryBuildTriangleListWorldWithMaterials(root, out Vector3[] triVerts, out Vector4[] triMatA, out Vector4[] triMatB, out Vector2[] triUVs, out int[] triAlbedoTexIdx, out int[] triEmissionTexIdx, out Texture2D[] albedoTextures, out Texture2D[] emissionTextures, out string triErr))
                return triErr;

            int triCount = triMatA.Length;
            if (triCount <= 0) {
                return "Triangle list is empty.";
            }

            int voxelCount = lowRes.x * lowRes.y * lowRes.z;
            if (voxelCount <= 0) {
                return "Invalid low resolution.";
            }

            // Create buffers/textures
            var triVertsBuffer = new ComputeBuffer(triVerts.Length, sizeof(float) * 3, ComputeBufferType.Structured);
            var triMatABuffer = new ComputeBuffer(triCount, sizeof(float) * 4, ComputeBufferType.Structured);
            var triMatBBuffer = new ComputeBuffer(triCount, sizeof(float) * 4, ComputeBufferType.Structured);
            var triUVBuffer = new ComputeBuffer(triUVs.Length, sizeof(float) * 2, ComputeBufferType.Structured);
            var triAlbedoTexBuffer = new ComputeBuffer(triAlbedoTexIdx.Length, sizeof(int), ComputeBufferType.Structured);
            var triEmissionTexBuffer = new ComputeBuffer(triEmissionTexIdx.Length, sizeof(int), ComputeBufferType.Structured);

            // Create 3D RenderTextures for GPU output (float RGBA)
            RenderTexture rtAlbedo = new RenderTexture(lowRes.x, lowRes.y, 0, RenderTextureFormat.ARGBFloat) {
                dimension = TextureDimension.Tex3D,
                volumeDepth = lowRes.z,
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                name = $"{root.name}_Material_AlbedoRoughness"
            };
            RenderTexture rtEmission = new RenderTexture(lowRes.x, lowRes.y, 0, RenderTextureFormat.ARGBFloat) {
                dimension = TextureDimension.Tex3D,
                volumeDepth = lowRes.z,
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                name = $"{root.name}_Material_EmissionMetallic"
            };
            rtAlbedo.Create();
            rtEmission.Create();


            Texture2DArray albedoArray = null;
            Texture2DArray emissionArray = null;
            try {
                triVertsBuffer.SetData(triVerts);
                triMatABuffer.SetData(triMatA);
                triMatBBuffer.SetData(triMatB);
                triUVBuffer.SetData(triUVs);
                triAlbedoTexBuffer.SetData(triAlbedoTexIdx);
                triEmissionTexBuffer.SetData(triEmissionTexIdx);

                MaterialBakeCompute.SetBuffer(_kernel, s_triVerts, triVertsBuffer);
                MaterialBakeCompute.SetInt(s_triCount, triCount);
                MaterialBakeCompute.SetBuffer(_kernel, s_triMatA, triMatABuffer);
                MaterialBakeCompute.SetBuffer(_kernel, s_triMatB, triMatBBuffer);
                MaterialBakeCompute.SetBuffer(_kernel, s_triUVs, triUVBuffer);
                MaterialBakeCompute.SetBuffer(_kernel, s_triAlbedoTex, triAlbedoTexBuffer);
                MaterialBakeCompute.SetBuffer(_kernel, s_triEmissionTex, triEmissionTexBuffer);

                Vector3 bmin = bounds.min;
                Vector3 bsize = bounds.size;

                MaterialBakeCompute.SetVector(s_boundsMin, bmin);
                MaterialBakeCompute.SetVector(s_boundsSize, bsize);
                MaterialBakeCompute.SetInts(s_resolution, lowRes.x, lowRes.y, lowRes.z);
                MaterialBakeCompute.SetTexture(_kernel, s_outAlbedo, rtAlbedo);
                MaterialBakeCompute.SetTexture(_kernel, s_outEmission, rtEmission);
                // If we have albedo/emission texture arrays, create Texture2DArray and copy slices (GPU-side)
                // Albedo array: try create from sources, otherwise create 1x1 fallback
                if (!BuildTexture2DArrayOrFallback(albedoTextures, out albedoArray)) {
                    // Clear per-triangle albedo indices so shader falls back to flat material colors
                    for (int i = 0; i < triAlbedoTexIdx.Length; ++i)
                        triAlbedoTexIdx[i] = -1;
                    triAlbedoTexBuffer.SetData(triAlbedoTexIdx);
                    MaterialBakeCompute.SetTexture(_kernel, s_albedoArray, albedoArray);
                    MaterialBakeCompute.SetInts(s_albedoSize, 1, 1);
                } else {
                    MaterialBakeCompute.SetTexture(_kernel, s_albedoArray, albedoArray);
                    MaterialBakeCompute.SetInts(s_albedoSize, albedoTextures[0].width, albedoTextures[0].height);
                }

                // Emission array: try create from sources, otherwise create 1x1 fallback
                if (!BuildTexture2DArrayOrFallback(emissionTextures, out emissionArray)) {
                    for (int i = 0; i < triEmissionTexIdx.Length; ++i)
                        triEmissionTexIdx[i] = -1;
                    triEmissionTexBuffer.SetData(triEmissionTexIdx);
                    MaterialBakeCompute.SetTexture(_kernel, s_emissionArray, emissionArray);
                    MaterialBakeCompute.SetInts(s_emissionSize, 1, 1);
                } else {
                    MaterialBakeCompute.SetTexture(_kernel, s_emissionArray, emissionArray);
                    MaterialBakeCompute.SetInts(s_emissionSize, emissionTextures[0].width, emissionTextures[0].height);
                }

                MaterialBakeCompute.GetKernelThreadGroupSizes(_kernel, out uint tx, out uint ty, out uint tz);
                int gx = Mathf.CeilToInt(lowRes.x / (float)tx);
                int gy = Mathf.CeilToInt(lowRes.y / (float)ty);
                int gz = Mathf.CeilToInt(lowRes.z / (float)tz);
                MaterialBakeCompute.Dispatch(_kernel, gx, gy, gz);

                // Read back RenderTexture3D outputs into CPU Texture3D using a shared helper.
                if (!TryReadbackRenderTexture3D(rtAlbedo, lowRes, $"{root.name}_AlbedoRoughness", out albedoRoughness)) {
                    return "MaterialBaker: failed to build CPU albedo Texture3D from RenderTexture.";
                }
                if (!TryReadbackRenderTexture3D(rtEmission, lowRes, $"{root.name}_EmissionMetallic", out emissionMetallic)) {
                    return "MaterialBaker: failed to build CPU emission Texture3D from RenderTexture.";
                }

                return null;
            } finally {
                triVertsBuffer.Release();
                triMatABuffer.Release();
                triMatBBuffer.Release();
                triUVBuffer.Release();
                triAlbedoTexBuffer.Release();
                triEmissionTexBuffer.Release();
                rtAlbedo.Release();
                rtEmission.Release();
                // Destroy temporary texture arrays if created
                if (albedoArray != null) UnityEngine.Object.DestroyImmediate(albedoArray);
                if (emissionArray != null) UnityEngine.Object.DestroyImmediate(emissionArray);
            }
        }

        static bool TryBuildTriangleListWorldWithMaterials(
            Transform root,
            out Vector3[] triVerts,
            out Vector4[] triMatA,
            out Vector4[] triMatB,
            out Vector2[] triUVs,
            out int[] triAlbedoTexIdx,
            out int[] triEmissionTexIdx,
            out Texture2D[] albedoTextures,
            out Texture2D[] emissionTextures,
            out string error
        ) {
            error = null;

            List<Vector3> verts = new List<Vector3>(1024);
            List<Vector4> matA = new List<Vector4>(1024);
            List<Vector4> matB = new List<Vector4>(1024);
            List<Vector2> uvs = new List<Vector2>(1024);
            List<int> albedoIdx = new List<int>(1024);
            List<int> emissionIdx = new List<int>(1024);

            var albedoList = new List<Texture2D>();
            var emissionList = new List<Texture2D>();

            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            foreach (MeshRenderer mr in meshRenderers) {
                if (mr == null)
                    continue;
                if (!mr.TryGetComponent(out MeshFilter mf))
                    continue;
                Mesh mesh = mf.sharedMesh;
                if (mesh == null)
                    continue;

                Material[] mats = mr.sharedMaterials;

                int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
                Vector3[] v = mesh.vertices;
                if (v == null || v.Length == 0)
                    continue;

                for (int sm = 0; sm < subMeshCount; sm++) {
                    int[] t;
                    try {
                        t = mesh.GetTriangles(sm);
                    } catch {
                        continue;
                    }
                    if (t == null || t.Length < 3)
                        continue;

                    Material mat = (sm < mats.Length) ? mats[sm] : null;
                    Color baseColor = Color.white;
                    if (mat != null) {
                        if (mat.HasProperty("_BaseColor")) baseColor = mat.GetColor("_BaseColor");
                        else if (mat.HasProperty("_Color")) baseColor = mat.GetColor("_Color");
                    }

                    float roughness = 1f;
                    if (mat != null) {
                        if (mat.HasProperty("_Roughness")) roughness = mat.GetFloat("_Roughness");
                        else if (mat.HasProperty("_Glossiness")) roughness = 1f - mat.GetFloat("_Glossiness");
                        else if (mat.HasProperty("_Smoothness")) roughness = 1f - mat.GetFloat("_Smoothness");
                    }

                    Color emission = Color.black;
                    if (mat != null) {
                        if (mat.HasProperty("_EmissionColor")) emission = mat.GetColor("_EmissionColor");
                    }

                    float metallic = 0f;
                    if (mat != null) {
                        if (mat.HasProperty("_Metallic")) metallic = mat.GetFloat("_Metallic");
                    }

                    for (int i = 0; i + 2 < t.Length; i += 3) {
                        int i0 = t[i + 0];
                        int i1 = t[i + 1];
                        int i2 = t[i + 2];

                        if ((uint)i0 >= (uint)v.Length || (uint)i1 >= (uint)v.Length || (uint)i2 >= (uint)v.Length)
                            continue;

                        verts.Add(mf.transform.localToWorldMatrix.MultiplyPoint3x4(v[i0]));
                        verts.Add(mf.transform.localToWorldMatrix.MultiplyPoint3x4(v[i1]));
                        verts.Add(mf.transform.localToWorldMatrix.MultiplyPoint3x4(v[i2]));

                        matA.Add(new Vector4(baseColor.r, baseColor.g, baseColor.b, roughness));
                        matB.Add(new Vector4(emission.r, emission.g, emission.b, metallic));

                        // Add UVs (if mesh has UVs)
                        Vector2 uv0 = Vector2.zero, uv1 = Vector2.zero, uv2 = Vector2.zero;
                        if (mesh.uv != null && mesh.uv.Length > 0) {
                            var uv = mesh.uv;
                            if ((uint)i0 < (uint)uv.Length) uv0 = uv[i0];
                            if ((uint)i1 < (uint)uv.Length) uv1 = uv[i1];
                            if ((uint)i2 < (uint)uv.Length) uv2 = uv[i2];
                        }
                        uvs.Add(uv0);
                        uvs.Add(uv1);
                        uvs.Add(uv2);

                        // Resolve albedo/emission textures for this material
                        Texture2D albedoTex = null;
                        if (mat != null) {
                            if (mat.HasProperty("_BaseMap")) albedoTex = mat.GetTexture("_BaseMap") as Texture2D;
                            if (albedoTex == null && mat.HasProperty("_MainTex")) albedoTex = mat.GetTexture("_MainTex") as Texture2D;
                        }
                        int aidx = -1;
                        if (albedoTex != null) {
                            aidx = albedoList.IndexOf(albedoTex);
                            if (aidx < 0) { aidx = albedoList.Count; albedoList.Add(albedoTex); }
                        }
                        albedoIdx.Add(aidx);

                        Texture2D emissionTex = null;
                        if (mat != null) {
                            if (mat.HasProperty("_EmissionMap")) emissionTex = mat.GetTexture("_EmissionMap") as Texture2D;
                        }
                        int eidx = -1;
                        if (emissionTex != null) {
                            eidx = emissionList.IndexOf(emissionTex);
                            if (eidx < 0) { eidx = emissionList.Count; emissionList.Add(emissionTex); }
                        }
                        emissionIdx.Add(eidx);
                    }
                }
            }

            triVerts = verts.ToArray();
            triMatA = matA.ToArray();
            triMatB = matB.ToArray();
            triUVs = uvs.ToArray();
            triAlbedoTexIdx = albedoIdx.ToArray();
            triEmissionTexIdx = emissionIdx.ToArray();
            albedoTextures = albedoList.ToArray();
            emissionTextures = emissionList.ToArray();

            return true;
        }

        // Attempt to create a Texture2DArray from source Texture2D list using CPU blit+readback.
        // This is robust across source formats and avoids Graphics.CopyTexture format mismatches.
        static bool TryCreateTexture2DArrayFromSources(Texture2D[] sources, out Texture2DArray array) {
            array = null;
            if (sources == null || sources.Length == 0) return false;
            int w = sources[0].width;
            int h = sources[0].height;
            try {
                array = new Texture2DArray(w, h, sources.Length, TextureFormat.RGBA32, false);
                array.wrapMode = TextureWrapMode.Repeat;
                array.filterMode = FilterMode.Bilinear;

                RenderTexture prevActive = RenderTexture.active;
                for (int i = 0; i < sources.Length; ++i) {
                    var src = sources[i];
                    var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(src, rt);

                    RenderTexture.active = rt;
                    var tmp = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                    tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    tmp.Apply();

                    var cols = tmp.GetPixels();
                    array.SetPixels(cols, i, 0);

                    UnityEngine.Object.DestroyImmediate(tmp);
                    RenderTexture.active = prevActive;
                    RenderTexture.ReleaseTemporary(rt);
                }
                array.Apply();
                return true;
            } catch (Exception ex) {
                Debug.LogWarning($"CPU fallback to build Texture2DArray failed: {ex.Message}");
                if (array != null) UnityEngine.Object.DestroyImmediate(array);
                array = null;
                return false;
            }
        }

        // Helper: build a Texture2DArray from sources or return a 1x1 black fallback.
        // Returns true if a real array was created from sources, false if a fallback was used.
        static bool BuildTexture2DArrayOrFallback(Texture2D[] sources, out Texture2DArray array) {
            array = null;
            if (sources != null && sources.Length > 0) {
                if (TryCreateTexture2DArrayFromSources(sources, out array)) return true;
                Debug.LogWarning("Failed to create Texture2DArray from sources; using 1x1 fallback.");
            }
            // create a 1x1 black fallback array so the shader property is set (won't be sampled)
            array = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false);
            array.SetPixels(new Color[] { Color.black }, 0, 0);
            array.Apply();
            return false;
        }

        // Read back a 3D RenderTexture into a CPU-side Texture3D.
        // Performs per-slice AsyncGPUReadback and falls back to a full 3D AsyncGPUReadback if needed.
        bool TryReadbackRenderTexture3D(RenderTexture srcRT, Vector3Int res, string name, out Texture3D outTex) {
            outTex = null;
            int sliceCount = res.z;
            int slicePixels = res.x * res.y;
            Color[] allCols = new Color[slicePixels * sliceCount];
            RenderTextureFormat rt2DFormat = RenderTextureFormat.ARGBFloat;

            for (int z = 0; z < sliceCount; ++z) {
                var tmpRT = RenderTexture.GetTemporary(res.x, res.y, 0, rt2DFormat);
                try {
                    try { Graphics.CopyTexture(srcRT, z, 0, tmpRT, 0, 0); } catch { Graphics.Blit(srcRT, tmpRT); }
                    AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(tmpRT, 0);
                    req.WaitForCompletion();
                    if (req.hasError) {
                        Debug.LogWarning($"MaterialBaker: AsyncGPUReadback failed for {name} slice {z}.");
                        RenderTexture.ReleaseTemporary(tmpRT);
                        return false;
                    }
                    NativeArray<Color> data = req.GetData<Color>();
                    if (data == null || data.Length == 0) {
                        Debug.LogWarning($"MaterialBaker: AsyncGPUReadback returned empty data for {name} slice {z}.");
                        RenderTexture.ReleaseTemporary(tmpRT);
                        return false;
                    }
                    for (int i = 0; i < slicePixels; ++i) {
                        allCols[z * slicePixels + i] = data[i];
                    }
                } finally {
                    RenderTexture.ReleaseTemporary(tmpRT);
                }
            }

            var cpu = new Texture3D(res.x, res.y, res.z, TextureFormat.RGBA32, mipChain: false) {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                name = name
            };
            cpu.SetPixels(allCols);
            cpu.Apply();
            outTex = cpu;
            return true;
        }
    }
}
