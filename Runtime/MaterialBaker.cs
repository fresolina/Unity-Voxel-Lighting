using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    [Serializable]
    public class MaterialBaker {
        public ComputeShader materialBakeCompute;

        [Tooltip("Downscale factor to produce lower-res material field (choose 3, 4 or 5).")]
        [Range(3, 5)]
        public int downscaleFactor = 4;

        static readonly int sTriVerts = Shader.PropertyToID("_TriVerts");
        static readonly int sTriCount = Shader.PropertyToID("_TriCount");
        static readonly int sTriMatA = Shader.PropertyToID("_TriMatA");
        static readonly int sTriMatB = Shader.PropertyToID("_TriMatB");
        static readonly int sTriUVs = Shader.PropertyToID("_TriUVs");
        static readonly int sTriAlbedoTex = Shader.PropertyToID("_TriAlbedoTex");
        static readonly int sTriEmissionTex = Shader.PropertyToID("_TriEmissionTex");
        static readonly int sAlbedoArray = Shader.PropertyToID("_AlbedoArray");
        static readonly int sEmissionArray = Shader.PropertyToID("_EmissionArray");
        static readonly int sAlbedoSize = Shader.PropertyToID("_AlbedoSize");
        static readonly int sEmissionSize = Shader.PropertyToID("_EmissionSize");
        static readonly int sBoundsMin = Shader.PropertyToID("_BoundsMin");
        static readonly int sBoundsSize = Shader.PropertyToID("_BoundsSize");
        static readonly int sResolution = Shader.PropertyToID("_Resolution");
        static readonly int sOutAlbedo = Shader.PropertyToID("_OutAlbedo");
        static readonly int sOutEmission = Shader.PropertyToID("_OutEmission");

        int _kernel = -1;

        public bool TryBake(
            SdfVolume volume,
            out Texture3D albedoRoughness,
            out Texture3D emissionMetallic,
            out string error
        ) {
            albedoRoughness = null;
            emissionMetallic = null;

            if (volume == null) {
                error = "Target SdfVolume is null.";
                return false;
            }
            if (materialBakeCompute == null) {
                error = "Material Bake Compute is not assigned.";
                return false;
            }

            Transform root = volume.BakeRoot;
            if (root == null) {
                error = "Bake Root is null.";
                return false;
            }

            if (!volume.TryComputeBoundsFromRoot(out Bounds bounds)) {
                error = "No meshes found under Bake Root (MeshRenderer+MeshFilter).";
                return false;
            }

            // Low resolution is bakedResolution / downscaleFactor (allowed: 3,4,5)
            volume.bakedBounds = bounds;
            volume.RecomputeBoundsAndResolution();
            Vector3Int highRes = volume.bakedResolution;

            int ds = downscaleFactor;
            if (ds < 3) ds = 3;
            else if (ds > 5) ds = 5;
            // normalize to nearest allowed value (3/4/5). Keep stored value in-range.
            downscaleFactor = ds;

            Vector3Int lowRes = new Vector3Int(
                Math.Max(1, highRes.x / ds),
                Math.Max(1, highRes.y / ds),
                Math.Max(1, highRes.z / ds)
            );

            if (_kernel < 0)
                _kernel = materialBakeCompute.FindKernel("CSMain");

            if (!TryBuildTriangleListWorldWithMaterials(root, out Vector3[] triVerts, out Vector4[] triMatA, out Vector4[] triMatB, out Vector2[] triUVs, out int[] triAlbedoTexIdx, out int[] triEmissionTexIdx, out Texture2D[] albedoTextures, out Texture2D[] emissionTextures, out error))
                return false;

            int triCount = triMatA.Length;
            if (triCount <= 0) {
                error = "Triangle list is empty.";
                return false;
            }

            int voxelCount = lowRes.x * lowRes.y * lowRes.z;
            if (voxelCount <= 0) {
                error = "Invalid low resolution.";
                return false;
            }

            // Create buffers/textures
            var triVertsBuffer = new ComputeBuffer(triVerts.Length, sizeof(float) * 3, ComputeBufferType.Structured);
            var triMatABuffer = new ComputeBuffer(triCount, sizeof(float) * 4, ComputeBufferType.Structured);
            var triMatBBuffer = new ComputeBuffer(triCount, sizeof(float) * 4, ComputeBufferType.Structured);
            var triUVBuffer = new ComputeBuffer(triUVs.Length, sizeof(float) * 2, ComputeBufferType.Structured);
            var triAlbedoTexBuffer = new ComputeBuffer(triAlbedoTexIdx.Length, sizeof(int), ComputeBufferType.Structured);
            var triEmissionTexBuffer = new ComputeBuffer(triEmissionTexIdx.Length, sizeof(int), ComputeBufferType.Structured);

            // Create 3D RenderTextures for GPU output (RGBA float)
            RenderTexture rtA = new RenderTexture(lowRes.x, lowRes.y, 0, RenderTextureFormat.ARGBFloat) {
                dimension = TextureDimension.Tex3D,
                volumeDepth = lowRes.z,
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                name = $"{root.name}_Material_AlbedoRoughness"
            };
            RenderTexture rtB = new RenderTexture(lowRes.x, lowRes.y, 0, RenderTextureFormat.ARGBFloat) {
                dimension = TextureDimension.Tex3D,
                volumeDepth = lowRes.z,
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                name = $"{root.name}_Material_EmissionMetallic"
            };
            rtA.Create();
            rtB.Create();


            Texture2DArray albedoArray = null;
            Texture2DArray emissionArray = null;
            try {
                triVertsBuffer.SetData(triVerts);
                triMatABuffer.SetData(triMatA);
                triMatBBuffer.SetData(triMatB);
                triUVBuffer.SetData(triUVs);
                triAlbedoTexBuffer.SetData(triAlbedoTexIdx);
                triEmissionTexBuffer.SetData(triEmissionTexIdx);

                materialBakeCompute.SetBuffer(_kernel, sTriVerts, triVertsBuffer);
                materialBakeCompute.SetInt(sTriCount, triCount);
                materialBakeCompute.SetBuffer(_kernel, sTriMatA, triMatABuffer);
                materialBakeCompute.SetBuffer(_kernel, sTriMatB, triMatBBuffer);
                materialBakeCompute.SetBuffer(_kernel, sTriUVs, triUVBuffer);
                materialBakeCompute.SetBuffer(_kernel, sTriAlbedoTex, triAlbedoTexBuffer);
                materialBakeCompute.SetBuffer(_kernel, sTriEmissionTex, triEmissionTexBuffer);

                Vector3 bmin = bounds.min;
                Vector3 bsize = bounds.size;

                materialBakeCompute.SetVector(sBoundsMin, bmin);
                materialBakeCompute.SetVector(sBoundsSize, bsize);
                materialBakeCompute.SetInts(sResolution, lowRes.x, lowRes.y, lowRes.z);
                materialBakeCompute.SetTexture(_kernel, sOutAlbedo, rtA);
                materialBakeCompute.SetTexture(_kernel, sOutEmission, rtB);
                // If we have albedo/emission texture arrays, create Texture2DArray and copy slices (GPU-side)
                // Albedo array: try create from sources, otherwise create 1x1 fallback
                bool albedoArrayValid = false;
                if (albedoTextures != null && albedoTextures.Length > 0) {
                    if (TryCreateTexture2DArrayFromSources(albedoTextures, out albedoArray)) {
                        materialBakeCompute.SetTexture(_kernel, sAlbedoArray, albedoArray);
                        // pass array size to shader
                        materialBakeCompute.SetInts(sAlbedoSize, albedoTextures[0].width, albedoTextures[0].height);
                        albedoArrayValid = true;
                    } else {
                        Debug.LogWarning("Failed to create albedo Texture2DArray. Using flat material colors instead.");
                    }
                }
                if (!albedoArrayValid) {
                    // Clear per-triangle albedo indices so shader falls back to flat material colors
                    for (int i = 0; i < triAlbedoTexIdx.Length; ++i) triAlbedoTexIdx[i] = -1;
                    triAlbedoTexBuffer.SetData(triAlbedoTexIdx);
                    // create a 1x1 black fallback array so the shader property is set (won't be sampled)
                    albedoArray = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false);
                    albedoArray.SetPixels(new Color[] { Color.black }, 0, 0);
                    albedoArray.Apply();
                    materialBakeCompute.SetTexture(_kernel, sAlbedoArray, albedoArray);
                    materialBakeCompute.SetInts(sAlbedoSize, 1, 1);
                }

                // Emission array: try create from sources, otherwise create 1x1 fallback
                bool emissionArrayValid = false;
                if (emissionTextures != null && emissionTextures.Length > 0) {
                    if (TryCreateTexture2DArrayFromSources(emissionTextures, out emissionArray)) {
                        materialBakeCompute.SetTexture(_kernel, sEmissionArray, emissionArray);
                        materialBakeCompute.SetInts(sEmissionSize, emissionTextures[0].width, emissionTextures[0].height);
                        emissionArrayValid = true;
                    } else {
                        Debug.LogWarning("Failed to create emission Texture2DArray. Using flat material values instead.");
                    }
                }
                if (!emissionArrayValid) {
                    for (int i = 0; i < triEmissionTexIdx.Length; ++i) triEmissionTexIdx[i] = -1;
                    triEmissionTexBuffer.SetData(triEmissionTexIdx);
                    emissionArray = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false);
                    emissionArray.SetPixels(new Color[] { Color.black }, 0, 0);
                    emissionArray.Apply();
                    materialBakeCompute.SetTexture(_kernel, sEmissionArray, emissionArray);
                    materialBakeCompute.SetInts(sEmissionSize, 1, 1);
                }

                materialBakeCompute.GetKernelThreadGroupSizes(_kernel, out uint tx, out uint ty, out uint tz);
                int gx = Mathf.CeilToInt(lowRes.x / (float)tx);
                int gy = Mathf.CeilToInt(lowRes.y / (float)ty);
                int gz = Mathf.CeilToInt(lowRes.z / (float)tz);
                materialBakeCompute.Dispatch(_kernel, gx, gy, gz);

                // Copy GPU RenderTextures into Texture3D objects entirely on GPU (no CPU readback)
                Texture3D tempAlbedo3D = null;
                // Prefer creating Texture3D with explicit GraphicsFormat that matches RenderTexture ARGBFloat
                try {
                    tempAlbedo3D = new Texture3D(lowRes.x, lowRes.y, lowRes.z, GraphicsFormat.R32G32B32A32_SFloat, TextureCreationFlags.None) {
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Trilinear,
                        name = $"{root.name}_AlbedoRoughness"
                    };
                } catch {
                    Debug.LogWarning("GraphicsFormat.R32G32B32A32_SFloat not supported, falling back to TextureFormat.RGBAFloat for albedo.");
                    tempAlbedo3D = new Texture3D(lowRes.x, lowRes.y, lowRes.z, TextureFormat.RGBAFloat, mipChain: false) {
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Trilinear,
                        name = $"{root.name}_AlbedoRoughness"
                    };
                }
                albedoRoughness = tempAlbedo3D;
                // Copy each 3D slice from the RenderTexture into the Texture3D on the GPU

                try {
                    for (int z = 0; z < lowRes.z; ++z) {
                        Graphics.CopyTexture(rtA, z, 0, albedoRoughness, z, 0);
                    }

                } catch (Exception ex) {
                    Debug.LogWarning($"Per-slice Graphics.CopyTexture failed: {ex.Message}. Falling back to per-slice AsyncGPUReadback.");
                    // Fallback: read back GPU data slice-by-slice and build Texture3D on CPU
                    int sliceCount = lowRes.z;
                    int slicePixels = lowRes.x * lowRes.y;
                    Color[] allCols = new Color[slicePixels * sliceCount];
                    var rt2DFormat = RenderTextureFormat.ARGBFloat;
                    for (int z = 0; z < sliceCount; ++z) {
                        var tmpRT = RenderTexture.GetTemporary(lowRes.x, lowRes.y, 0, rt2DFormat);
                        try {
                            Graphics.CopyTexture(rtA, z, 0, tmpRT, 0, 0);
                        } catch (Exception copyEx) {
                            Debug.LogWarning($"Copy slice {z} to 2D RT failed: {copyEx.Message}. Trying Graphics.Blit fallback.");
                            Graphics.Blit(rtA, tmpRT);
                        }
                        var reqSlice = AsyncGPUReadback.Request(tmpRT, 0);
                        reqSlice.WaitForCompletion();
                        if (reqSlice.hasError) {
                            Debug.LogWarning($"AsyncGPUReadback failed for slice {z}. Trying ReadPixels fallback.");
                            // ReadPixels fallback
                            RenderTexture prev = RenderTexture.active;
                            RenderTexture.active = tmpRT;
                            var tex2D = new Texture2D(lowRes.x, lowRes.y, TextureFormat.RGBAFloat, false, true);
                            tex2D.ReadPixels(new Rect(0, 0, lowRes.x, lowRes.y), 0, 0);
                            tex2D.Apply();
                            var px = tex2D.GetPixels();

                            Array.Copy(px, 0, allCols, z * slicePixels, slicePixels);
                            UnityEngine.Object.DestroyImmediate(tex2D);
                            RenderTexture.active = prev;
                            RenderTexture.ReleaseTemporary(tmpRT);
                            continue;
                        }
                        var data = reqSlice.GetData<Color>();

                        // Copy into the master array
                        for (int i = 0; i < slicePixels; ++i) allCols[z * slicePixels + i] = data[i];
                        RenderTexture.ReleaseTemporary(tmpRT);
                    }
                    Texture3D cpuAlbedo3D = new Texture3D(lowRes.x, lowRes.y, lowRes.z, TextureFormat.RGBAFloat, mipChain: false) {
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Trilinear,
                        name = $"{root.name}_AlbedoRoughness"
                    };
                    cpuAlbedo3D.SetPixels(allCols);
                    cpuAlbedo3D.Apply();
                    albedoRoughness = cpuAlbedo3D;
                }

                // Validation: Async readback of destination Texture3D to detect silent CopyTexture failures
                try {
                    var validateReq = AsyncGPUReadback.Request(albedoRoughness, 0);
                    validateReq.WaitForCompletion();
                    if (validateReq.hasError) {
                        Debug.LogWarning("Validation AsyncGPUReadback failed for albedo Texture3D. Rebuilding on CPU.");
                        // Rebuild on CPU (same as fallback above)
                        int sliceCount_v = lowRes.z;
                        int slicePixels_v = lowRes.x * lowRes.y;
                        Color[] allCols_v = new Color[slicePixels_v * sliceCount_v];
                        var rt2DFormat_v = RenderTextureFormat.ARGBFloat;
                        for (int z = 0; z < sliceCount_v; ++z) {
                            var tmpRT = RenderTexture.GetTemporary(lowRes.x, lowRes.y, 0, rt2DFormat_v);
                            try {
                                Graphics.CopyTexture(rtA, z, 0, tmpRT, 0, 0);
                            } catch {
                                Graphics.Blit(rtA, tmpRT);
                            }
                            var reqSlice_v = AsyncGPUReadback.Request(tmpRT, 0);
                            reqSlice_v.WaitForCompletion();
                            if (reqSlice_v.hasError) {
                                RenderTexture prev = RenderTexture.active;
                                RenderTexture.active = tmpRT;
                                var tex2D_v = new Texture2D(lowRes.x, lowRes.y, TextureFormat.RGBAFloat, false, true);
                                tex2D_v.ReadPixels(new Rect(0, 0, lowRes.x, lowRes.y), 0, 0);
                                tex2D_v.Apply();
                                var px_v = tex2D_v.GetPixels();
                                Array.Copy(px_v, 0, allCols_v, z * slicePixels_v, slicePixels_v);
                                UnityEngine.Object.DestroyImmediate(tex2D_v);
                                RenderTexture.active = prev;
                                RenderTexture.ReleaseTemporary(tmpRT);
                                continue;
                            }
                            var data_v = reqSlice_v.GetData<Color>();
                            for (int i = 0; i < slicePixels_v; ++i) allCols_v[z * slicePixels_v + i] = data_v[i];
                            RenderTexture.ReleaseTemporary(tmpRT);
                        }
                        var cpuAlbedo3D_v = new Texture3D(lowRes.x, lowRes.y, lowRes.z, TextureFormat.RGBAFloat, mipChain: false) {
                            wrapMode = TextureWrapMode.Clamp,
                            filterMode = FilterMode.Trilinear,
                            name = $"{root.name}_AlbedoRoughness"
                        };
                        cpuAlbedo3D_v.SetPixels(allCols_v);
                        cpuAlbedo3D_v.Apply();
                        albedoRoughness = cpuAlbedo3D_v;
                    } else {
                        var chk = validateReq.GetData<Color>();
                        if (chk.Length == 0) {
                            Debug.LogWarning("Validation read returned no data for albedo Texture3D. Rebuilding on CPU.");
                            // same rebuild block
                            int sliceCount_v = lowRes.z;
                            int slicePixels_v = lowRes.x * lowRes.y;
                            Color[] allCols_v = new Color[slicePixels_v * sliceCount_v];
                            var rt2DFormat_v = RenderTextureFormat.ARGBFloat;
                            for (int z = 0; z < sliceCount_v; ++z) {
                                var tmpRT = RenderTexture.GetTemporary(lowRes.x, lowRes.y, 0, rt2DFormat_v);
                                try { Graphics.CopyTexture(rtA, z, 0, tmpRT, 0, 0); } catch { Graphics.Blit(rtA, tmpRT); }
                                var reqSlice_v = AsyncGPUReadback.Request(tmpRT, 0);
                                reqSlice_v.WaitForCompletion();
                                if (reqSlice_v.hasError) {
                                    RenderTexture prev = RenderTexture.active;
                                    RenderTexture.active = tmpRT;
                                    var tex2D_v = new Texture2D(lowRes.x, lowRes.y, TextureFormat.RGBAFloat, false, true);
                                    tex2D_v.ReadPixels(new Rect(0, 0, lowRes.x, lowRes.y), 0, 0);
                                    tex2D_v.Apply();
                                    var px_v = tex2D_v.GetPixels();
                                    Array.Copy(px_v, 0, allCols_v, z * slicePixels_v, slicePixels_v);
                                    UnityEngine.Object.DestroyImmediate(tex2D_v);
                                    RenderTexture.active = prev;
                                    RenderTexture.ReleaseTemporary(tmpRT);
                                    continue;
                                }
                                var data_v = reqSlice_v.GetData<Color>();
                                for (int i = 0; i < slicePixels_v; ++i) allCols_v[z * slicePixels_v + i] = data_v[i];
                                RenderTexture.ReleaseTemporary(tmpRT);
                            }
                            var cpuAlbedo3D_v = new Texture3D(lowRes.x, lowRes.y, lowRes.z, TextureFormat.RGBAFloat, mipChain: false) {
                                wrapMode = TextureWrapMode.Clamp,
                                filterMode = FilterMode.Trilinear,
                                name = $"{root.name}_AlbedoRoughness"
                            };
                            cpuAlbedo3D_v.SetPixels(allCols_v);
                            cpuAlbedo3D_v.Apply();
                            albedoRoughness = cpuAlbedo3D_v;
                        } else {
                            // quick sanity check on first pixel to detect gross invalid values
                            Color c0 = chk[0];
                            bool invalid = false;
                            if (float.IsNaN(c0.r) || float.IsNaN(c0.g) || float.IsNaN(c0.b) || float.IsNaN(c0.a)) invalid = true;
                            if (float.IsInfinity(c0.r) || float.IsInfinity(c0.g) || float.IsInfinity(c0.b) || float.IsInfinity(c0.a)) invalid = true;
                            if (Mathf.Abs(c0.r) > 1e7f || Mathf.Abs(c0.g) > 1e7f || Mathf.Abs(c0.b) > 1e7f || Mathf.Abs(c0.a) > 1e7f) invalid = true;
                            if (invalid) {
                                Debug.LogWarning($"Validation pixel looks invalid ({c0}). Rebuilding albedo Texture3D on CPU.");
                                int sliceCount_v = lowRes.z;
                                int slicePixels_v = lowRes.x * lowRes.y;
                                Color[] allCols_v = new Color[slicePixels_v * sliceCount_v];
                                var rt2DFormat_v = RenderTextureFormat.ARGBFloat;
                                for (int z = 0; z < sliceCount_v; ++z) {
                                    var tmpRT = RenderTexture.GetTemporary(lowRes.x, lowRes.y, 0, rt2DFormat_v);
                                    try { Graphics.CopyTexture(rtA, z, 0, tmpRT, 0, 0); } catch { Graphics.Blit(rtA, tmpRT); }
                                    var reqSlice_v = AsyncGPUReadback.Request(tmpRT, 0);
                                    reqSlice_v.WaitForCompletion();
                                    if (reqSlice_v.hasError) {
                                        RenderTexture prev = RenderTexture.active;
                                        RenderTexture.active = tmpRT;
                                        var tex2D_v = new Texture2D(lowRes.x, lowRes.y, TextureFormat.RGBAFloat, false, true);
                                        tex2D_v.ReadPixels(new Rect(0, 0, lowRes.x, lowRes.y), 0, 0);
                                        tex2D_v.Apply();
                                        var px_v = tex2D_v.GetPixels();
                                        Array.Copy(px_v, 0, allCols_v, z * slicePixels_v, slicePixels_v);
                                        UnityEngine.Object.DestroyImmediate(tex2D_v);
                                        RenderTexture.active = prev;
                                        RenderTexture.ReleaseTemporary(tmpRT);
                                        continue;
                                    }
                                    var data_v = reqSlice_v.GetData<Color>();
                                    for (int i = 0; i < slicePixels_v; ++i) allCols_v[z * slicePixels_v + i] = data_v[i];
                                    RenderTexture.ReleaseTemporary(tmpRT);
                                }
                                var cpuAlbedo3D_v = new Texture3D(lowRes.x, lowRes.y, lowRes.z, TextureFormat.RGBAFloat, mipChain: false) {
                                    wrapMode = TextureWrapMode.Clamp,
                                    filterMode = FilterMode.Trilinear,
                                    name = $"{root.name}_AlbedoRoughness"
                                };
                                cpuAlbedo3D_v.SetPixels(allCols_v);
                                cpuAlbedo3D_v.Apply();
                                albedoRoughness = cpuAlbedo3D_v;
                            }
                        }
                    }
                } catch (Exception ex) {
                    Debug.LogWarning($"Validation of albedo Texture3D failed unexpectedly: {ex.Message}. Falling back to CPU rebuild.");
                    // very last resort: rebuild on CPU
                    int sliceCount_v = lowRes.z;
                    int slicePixels_v = lowRes.x * lowRes.y;
                    Color[] allCols_v = new Color[slicePixels_v * sliceCount_v];
                    var rt2DFormat_v = RenderTextureFormat.ARGBFloat;
                    for (int z = 0; z < sliceCount_v; ++z) {
                        var tmpRT = RenderTexture.GetTemporary(lowRes.x, lowRes.y, 0, rt2DFormat_v);
                        try { Graphics.CopyTexture(rtA, z, 0, tmpRT, 0, 0); } catch { Graphics.Blit(rtA, tmpRT); }
                        RenderTexture prev = RenderTexture.active;
                        RenderTexture.active = tmpRT;
                        var tex2D_v = new Texture2D(lowRes.x, lowRes.y, TextureFormat.RGBAFloat, false, true);
                        tex2D_v.ReadPixels(new Rect(0, 0, lowRes.x, lowRes.y), 0, 0);
                        tex2D_v.Apply();
                        var px_v = tex2D_v.GetPixels();
                        Array.Copy(px_v, 0, allCols_v, z * slicePixels_v, slicePixels_v);
                        UnityEngine.Object.DestroyImmediate(tex2D_v);
                        RenderTexture.active = prev;
                        RenderTexture.ReleaseTemporary(tmpRT);
                    }
                    var cpuAlbedo3D_v2 = new Texture3D(lowRes.x, lowRes.y, lowRes.z, TextureFormat.RGBAFloat, mipChain: false) {
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Trilinear,
                        name = $"{root.name}_AlbedoRoughness"
                    };
                    cpuAlbedo3D_v2.SetPixels(allCols_v);
                    cpuAlbedo3D_v2.Apply();
                    albedoRoughness = cpuAlbedo3D_v2;
                }



                Texture3D tempEmission3D = null;
                try {
                    tempEmission3D = new Texture3D(lowRes.x, lowRes.y, lowRes.z, GraphicsFormat.R32G32B32A32_SFloat, TextureCreationFlags.None) {
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Trilinear,
                        name = $"{root.name}_EmissionMetallic"
                    };
                } catch {
                    tempEmission3D = new Texture3D(lowRes.x, lowRes.y, lowRes.z, TextureFormat.RGBAFloat, mipChain: false) {
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Trilinear,
                        name = $"{root.name}_EmissionMetallic"
                    };
                }
                emissionMetallic = tempEmission3D;

                try {
                    for (int z = 0; z < lowRes.z; ++z) {
                        Graphics.CopyTexture(rtB, z, 0, emissionMetallic, z, 0);
                    }

                } catch (Exception ex) {
                    Debug.LogWarning($"Per-slice Graphics.CopyTexture failed for emission: {ex.Message}. Falling back to per-slice AsyncGPUReadback.");
                    int sliceCountB = lowRes.z;
                    int slicePixelsB = lowRes.x * lowRes.y;
                    Color[] allColsB = new Color[slicePixelsB * sliceCountB];
                    var rt2DFormatB = RenderTextureFormat.ARGBFloat;
                    for (int z = 0; z < sliceCountB; ++z) {
                        var tmpRT = RenderTexture.GetTemporary(lowRes.x, lowRes.y, 0, rt2DFormatB);
                        try {
                            Graphics.CopyTexture(rtB, z, 0, tmpRT, 0, 0);
                        } catch (Exception copyEx) {
                            Debug.LogWarning($"Copy slice {z} to 2D RT failed: {copyEx.Message}. Trying Graphics.Blit fallback.");
                            Graphics.Blit(rtB, tmpRT);
                        }
                        var reqSlice = AsyncGPUReadback.Request(tmpRT, 0);
                        reqSlice.WaitForCompletion();
                        if (reqSlice.hasError) {
                            Debug.LogWarning($"AsyncGPUReadback failed for emission slice {z}. Trying ReadPixels fallback.");
                            RenderTexture prev = RenderTexture.active;
                            RenderTexture.active = tmpRT;
                            var tex2D = new Texture2D(lowRes.x, lowRes.y, TextureFormat.RGBAFloat, false, true);
                            tex2D.ReadPixels(new Rect(0, 0, lowRes.x, lowRes.y), 0, 0);
                            tex2D.Apply();
                            var px = tex2D.GetPixels();

                            Array.Copy(px, 0, allColsB, z * slicePixelsB, slicePixelsB);
                            UnityEngine.Object.DestroyImmediate(tex2D);
                            RenderTexture.active = prev;
                            RenderTexture.ReleaseTemporary(tmpRT);
                            continue;
                        }
                        var dataB = reqSlice.GetData<Color>();

                        for (int i = 0; i < slicePixelsB; ++i) allColsB[z * slicePixelsB + i] = dataB[i];
                        RenderTexture.ReleaseTemporary(tmpRT);
                    }
                    Texture3D cpuEmission3D = new Texture3D(lowRes.x, lowRes.y, lowRes.z, TextureFormat.RGBAFloat, mipChain: false) {
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Trilinear,
                        name = $"{root.name}_EmissionMetallic"
                    };
                    cpuEmission3D.SetPixels(allColsB);
                    cpuEmission3D.Apply();
                    emissionMetallic = cpuEmission3D;
                }

                // Validate emission Texture3D contents; if invalid, rebuild on CPU similarly to albedo fallback
                try {
                    var validateReqE = AsyncGPUReadback.Request(emissionMetallic, 0);
                    validateReqE.WaitForCompletion();
                    if (validateReqE.hasError) {
                        Debug.LogWarning("Validation AsyncGPUReadback failed for emission Texture3D. Rebuilding on CPU.");
                        // rebuild
                        int sliceCountB_v = lowRes.z;
                        int slicePixelsB_v = lowRes.x * lowRes.y;
                        Color[] allColsB_v = new Color[slicePixelsB_v * sliceCountB_v];
                        var rt2DFormatB_v = RenderTextureFormat.ARGBFloat;
                        for (int z = 0; z < sliceCountB_v; ++z) {
                            var tmpRT = RenderTexture.GetTemporary(lowRes.x, lowRes.y, 0, rt2DFormatB_v);
                            try { Graphics.CopyTexture(rtB, z, 0, tmpRT, 0, 0); } catch { Graphics.Blit(rtB, tmpRT); }
                            var reqSlice_v = AsyncGPUReadback.Request(tmpRT, 0);
                            reqSlice_v.WaitForCompletion();
                            if (reqSlice_v.hasError) {
                                RenderTexture prev = RenderTexture.active;
                                RenderTexture.active = tmpRT;
                                var tex2D_v = new Texture2D(lowRes.x, lowRes.y, TextureFormat.RGBAFloat, false, true);
                                tex2D_v.ReadPixels(new Rect(0, 0, lowRes.x, lowRes.y), 0, 0);
                                tex2D_v.Apply();
                                var px_v = tex2D_v.GetPixels();
                                Array.Copy(px_v, 0, allColsB_v, z * slicePixelsB_v, slicePixelsB_v);
                                UnityEngine.Object.DestroyImmediate(tex2D_v);
                                RenderTexture.active = prev;
                                RenderTexture.ReleaseTemporary(tmpRT);
                                continue;
                            }
                            var data_v = reqSlice_v.GetData<Color>();
                            for (int i = 0; i < slicePixelsB_v; ++i) allColsB_v[z * slicePixelsB_v + i] = data_v[i];
                            RenderTexture.ReleaseTemporary(tmpRT);
                        }
                        var cpuEmission3D_v = new Texture3D(lowRes.x, lowRes.y, lowRes.z, TextureFormat.RGBAFloat, mipChain: false) {
                            wrapMode = TextureWrapMode.Clamp,
                            filterMode = FilterMode.Trilinear,
                            name = $"{root.name}_EmissionMetallic"
                        };
                        cpuEmission3D_v.SetPixels(allColsB_v);
                        cpuEmission3D_v.Apply();
                        emissionMetallic = cpuEmission3D_v;
                    } else {
                        var chkE = validateReqE.GetData<Color>();
                        if (chkE.Length == 0) {
                            Debug.LogWarning("Validation read returned no data for emission Texture3D. Rebuilding on CPU.");
                            // rebuild (same as above)
                            int sliceCountB_v = lowRes.z;
                            int slicePixelsB_v = lowRes.x * lowRes.y;
                            Color[] allColsB_v = new Color[slicePixelsB_v * sliceCountB_v];
                            var rt2DFormatB_v = RenderTextureFormat.ARGBFloat;
                            for (int z = 0; z < sliceCountB_v; ++z) {
                                var tmpRT = RenderTexture.GetTemporary(lowRes.x, lowRes.y, 0, rt2DFormatB_v);
                                try { Graphics.CopyTexture(rtB, z, 0, tmpRT, 0, 0); } catch { Graphics.Blit(rtB, tmpRT); }
                                var reqSlice_v = AsyncGPUReadback.Request(tmpRT, 0);
                                reqSlice_v.WaitForCompletion();
                                if (reqSlice_v.hasError) {
                                    RenderTexture prev = RenderTexture.active;
                                    RenderTexture.active = tmpRT;
                                    var tex2D_v = new Texture2D(lowRes.x, lowRes.y, TextureFormat.RGBAFloat, false, true);
                                    tex2D_v.ReadPixels(new Rect(0, 0, lowRes.x, lowRes.y), 0, 0);
                                    tex2D_v.Apply();
                                    var px_v = tex2D_v.GetPixels();
                                    Array.Copy(px_v, 0, allColsB_v, z * slicePixelsB_v, slicePixelsB_v);
                                    UnityEngine.Object.DestroyImmediate(tex2D_v);
                                    RenderTexture.active = prev;
                                    RenderTexture.ReleaseTemporary(tmpRT);
                                    continue;
                                }
                                var data_v = reqSlice_v.GetData<Color>();
                                for (int i = 0; i < slicePixelsB_v; ++i) allColsB_v[z * slicePixelsB_v + i] = data_v[i];
                                RenderTexture.ReleaseTemporary(tmpRT);
                            }
                            var cpuEmission3D_v = new Texture3D(lowRes.x, lowRes.y, lowRes.z, TextureFormat.RGBAFloat, mipChain: false) {
                                wrapMode = TextureWrapMode.Clamp,
                                filterMode = FilterMode.Trilinear,
                                name = $"{root.name}_EmissionMetallic"
                            };
                            cpuEmission3D_v.SetPixels(allColsB_v);
                            cpuEmission3D_v.Apply();
                            emissionMetallic = cpuEmission3D_v;
                        } else {
                            Color c0e = chkE[0];
                            bool invalE = false;
                            if (float.IsNaN(c0e.r) || float.IsNaN(c0e.g) || float.IsNaN(c0e.b) || float.IsNaN(c0e.a)) invalE = true;
                            if (float.IsInfinity(c0e.r) || float.IsInfinity(c0e.g) || float.IsInfinity(c0e.b) || float.IsInfinity(c0e.a)) invalE = true;
                            if (Mathf.Abs(c0e.r) > 1e7f || Mathf.Abs(c0e.g) > 1e7f || Mathf.Abs(c0e.b) > 1e7f || Mathf.Abs(c0e.a) > 1e7f) invalE = true;
                            if (invalE) {
                                Debug.LogWarning($"Validation pixel looks invalid ({c0e}). Rebuilding emission Texture3D on CPU.");
                                int sliceCountB_v = lowRes.z;
                                int slicePixelsB_v = lowRes.x * lowRes.y;
                                Color[] allColsB_v = new Color[slicePixelsB_v * sliceCountB_v];
                                var rt2DFormatB_v = RenderTextureFormat.ARGBFloat;
                                for (int z = 0; z < sliceCountB_v; ++z) {
                                    var tmpRT = RenderTexture.GetTemporary(lowRes.x, lowRes.y, 0, rt2DFormatB_v);
                                    try { Graphics.CopyTexture(rtB, z, 0, tmpRT, 0, 0); } catch { Graphics.Blit(rtB, tmpRT); }
                                    var reqSlice_v = AsyncGPUReadback.Request(tmpRT, 0);
                                    reqSlice_v.WaitForCompletion();
                                    if (reqSlice_v.hasError) {
                                        RenderTexture prev = RenderTexture.active;
                                        RenderTexture.active = tmpRT;
                                        var tex2D_v = new Texture2D(lowRes.x, lowRes.y, TextureFormat.RGBAFloat, false, true);
                                        tex2D_v.ReadPixels(new Rect(0, 0, lowRes.x, lowRes.y), 0, 0);
                                        tex2D_v.Apply();
                                        var px_v = tex2D_v.GetPixels();
                                        Array.Copy(px_v, 0, allColsB_v, z * slicePixelsB_v, slicePixelsB_v);
                                        UnityEngine.Object.DestroyImmediate(tex2D_v);
                                        RenderTexture.active = prev;
                                        RenderTexture.ReleaseTemporary(tmpRT);
                                        continue;
                                    }
                                    var data_v = reqSlice_v.GetData<Color>();
                                    for (int i = 0; i < slicePixelsB_v; ++i) allColsB_v[z * slicePixelsB_v + i] = data_v[i];
                                    RenderTexture.ReleaseTemporary(tmpRT);
                                }
                                var cpuEmission3D_v = new Texture3D(lowRes.x, lowRes.y, lowRes.z, TextureFormat.RGBAFloat, mipChain: false) {
                                    wrapMode = TextureWrapMode.Clamp,
                                    filterMode = FilterMode.Trilinear,
                                    name = $"{root.name}_EmissionMetallic"
                                };
                                cpuEmission3D_v.SetPixels(allColsB_v);
                                cpuEmission3D_v.Apply();
                                emissionMetallic = cpuEmission3D_v;
                            }
                        }
                    }
                } catch (Exception ex) {
                    Debug.LogWarning($"Validation of emission Texture3D failed unexpectedly: {ex.Message}. Falling back to CPU rebuild.");
                    int sliceCountB_v = lowRes.z;
                    int slicePixelsB_v = lowRes.x * lowRes.y;
                    Color[] allColsB_v = new Color[slicePixelsB_v * sliceCountB_v];
                    var rt2DFormatB_v = RenderTextureFormat.ARGBFloat;
                    for (int z = 0; z < sliceCountB_v; ++z) {
                        var tmpRT = RenderTexture.GetTemporary(lowRes.x, lowRes.y, 0, rt2DFormatB_v);
                        try { Graphics.CopyTexture(rtB, z, 0, tmpRT, 0, 0); } catch { Graphics.Blit(rtB, tmpRT); }
                        RenderTexture prev = RenderTexture.active;
                        RenderTexture.active = tmpRT;
                        var tex2D_v = new Texture2D(lowRes.x, lowRes.y, TextureFormat.RGBAFloat, false, true);
                        tex2D_v.ReadPixels(new Rect(0, 0, lowRes.x, lowRes.y), 0, 0);
                        tex2D_v.Apply();
                        var px_v = tex2D_v.GetPixels();
                        Array.Copy(px_v, 0, allColsB_v, z * slicePixelsB_v, slicePixelsB_v);
                        UnityEngine.Object.DestroyImmediate(tex2D_v);
                        RenderTexture.active = prev;
                        RenderTexture.ReleaseTemporary(tmpRT);
                    }
                    var cpuEmission3D_v2 = new Texture3D(lowRes.x, lowRes.y, lowRes.z, TextureFormat.RGBAFloat, mipChain: false) {
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Trilinear,
                        name = $"{root.name}_EmissionMetallic"
                    };
                    cpuEmission3D_v2.SetPixels(allColsB_v);
                    cpuEmission3D_v2.Apply();
                    emissionMetallic = cpuEmission3D_v2;
                }

                return true;
            } finally {
                triVertsBuffer.Release();
                triMatABuffer.Release();
                triMatBBuffer.Release();
                triUVBuffer.Release();
                triAlbedoTexBuffer.Release();
                triEmissionTexBuffer.Release();
                rtA.Release();
                rtB.Release();
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

        // Attempt to create a Texture2DArray from source Texture2D list using GPU copy.
        // All source textures must have identical width/height and compatible formats for Graphics.CopyTexture to succeed.
        static bool TryCreateTexture2DArrayFromSources(Texture2D[] sources, out Texture2DArray array) {
            array = null;
            if (sources == null || sources.Length == 0) return false;
            int w = sources[0].width;
            int h = sources[0].height;
            TextureFormat fmt = sources[0].format;
            var firstGF = sources[0].graphicsFormat;

            for (int i = 1; i < sources.Length; ++i) {
                if (sources[i] == null) return false;
                if (sources[i].width != w || sources[i].height != h) return false;
                if (sources[i].format != fmt) return false;
                if (sources[i].graphicsFormat != firstGF) return false;
            }

            // Prefer GPU blit into an uncompressed array: render each source into an uncompressed RT
            // then copy that RT into the Texture2DArray slice. This avoids copying directly from
            // compressed asset formats (ASTC/etc.) and keeps the work on GPU.
            try {
                array = new Texture2DArray(w, h, sources.Length, TextureFormat.RGBA32, false);
                array.wrapMode = TextureWrapMode.Repeat;
                array.filterMode = FilterMode.Bilinear;

                var rtDescFormat = RenderTextureFormat.ARGB32;
                for (int i = 0; i < sources.Length; ++i) {
                    var src = sources[i];
                    var tmpRT = RenderTexture.GetTemporary(w, h, 0, rtDescFormat);
                    try {
                        Graphics.Blit(src, tmpRT);
                        // copy from temporary RT (uncompressed) into the array slice
                        Graphics.CopyTexture(tmpRT, 0, 0, array, i, 0);
                    } finally {
                        RenderTexture.ReleaseTemporary(tmpRT);
                    }
                }
                array.Apply();

                return true;
            } catch (Exception ex) {
                Debug.LogWarning($"GPU blit->CopyTexture path failed (will fallback to CPU): {ex.Message}");
                if (array != null) UnityEngine.Object.DestroyImmediate(array);
                array = null;
            }

            // Fallback: render each source into an uncompressed RGBA32 slice via RenderTexture + ReadPixels (editor-only / slower)
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
    }
}
