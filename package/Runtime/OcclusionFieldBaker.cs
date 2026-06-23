using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    /// <summary>
    /// Bakes a directional occlusion field where each direction gets a normalized RGBA channel.
    /// Stores the "lit" value (0 = shadow, 1 = fully lit) per Fibonacci direction per voxel.
    /// Output: N/4 normalized RGBA Texture3D assets (4 directions packed per texture).
    /// Unlike OcclusionBitmaskBaker, this supports hardware trilinear interpolation.
    /// </summary>
    [Serializable]
    public class OcclusionFieldBaker {
        public enum DirectionCount {
            Dir1Sun = 1,
            Dir8 = 8,
            Dir32 = 32,
            Dir64 = 64,
            Dir128 = 128,
            Dir256 = 256,
        }

        public ComputeShader occlusionFieldBakeCompute;

        [Tooltip("Number of directions to bake.\nDir 1 Sun bakes current sun direction.")]
        public DirectionCount directionCount = DirectionCount.Dir64;

        [Tooltip("Use only upper hemisphere directions (Y >= 0). Useful when the sun never goes below the horizon.")]
        public bool hemisphereOnly = true;

        [Tooltip("Softness of shadow penumbra. Higher = sharper. 0 = binary.")]
        [Range(1f, 128f)]
        public float shadowSoftness = 3f;

        static readonly int s_boundsMin = Shader.PropertyToID("_BoundsMin");
        static readonly int s_boundsSize = Shader.PropertyToID("_BoundsSize");
        static readonly int s_resolution = Shader.PropertyToID("_Resolution");
        static readonly int s_raymarchSoftness = Shader.PropertyToID("_RaymarchSoftness");
        static readonly int s_directionOffset = Shader.PropertyToID("_DirectionOffset");
        static readonly int s_outOcclusion = Shader.PropertyToID("_OutOcclusion");
        static readonly int s_directions = Shader.PropertyToID("_Directions");
        static readonly int s_raymarchMaxSteps = Shader.PropertyToID("_RaymarchMaxSteps");
        static readonly int s_raymarchMinStep = Shader.PropertyToID("_RaymarchMinStep");
        static readonly int s_raymarchEpsilon = Shader.PropertyToID("_RaymarchEpsilon");
        static readonly int s_sdfTex = Shader.PropertyToID("_SdfTex");

        static GraphicsFormat GetOcclusionFieldFormat(bool isSingleDir) {
            if (isSingleDir)
                return GraphicsFormat.R8_UNorm;
#if UNITY_EDITOR
            // if (UnityEditor.EditorUserBuildSettings.activeBuildTarget == UnityEditor.BuildTarget.Android)
            //     return GraphicsFormat.R4G4B4A4_UNormPack16;
#endif
            return GraphicsFormat.R8G8B8A8_UNorm;
        }

        static void SetPackedTextureData(Texture3D texture, NativeArray<float> readbackData, int voxelCount, GraphicsFormat format) {
            if (format == GraphicsFormat.R4G4B4A4_UNormPack16) {
                var packed = new ushort[voxelCount];
                for (int voxel = 0; voxel < voxelCount; voxel++) {
                    int baseIndex = voxel * 4;
                    ushort r = (ushort)Mathf.Clamp(Mathf.RoundToInt(readbackData[baseIndex + 0] * 15f), 0, 15);
                    ushort g = (ushort)Mathf.Clamp(Mathf.RoundToInt(readbackData[baseIndex + 1] * 15f), 0, 15);
                    ushort b = (ushort)Mathf.Clamp(Mathf.RoundToInt(readbackData[baseIndex + 2] * 15f), 0, 15);
                    ushort a = (ushort)Mathf.Clamp(Mathf.RoundToInt(readbackData[baseIndex + 3] * 15f), 0, 15);
                    packed[voxel] = (ushort)((r << 12) | (g << 8) | (b << 4) | a);
                }
                texture.SetPixelData(packed, 0);
                return;
            }

            var packedRgba8 = new byte[voxelCount * 4];
            for (int i = 0; i < voxelCount * 4; i++)
                packedRgba8[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(readbackData[i] * 255f), 0, 255);
            texture.SetPixelData(packedRgba8, 0);
        }

        /// <summary>
        /// Bake the occlusion field into N/4 normalized RGBA Texture3D assets.
        /// Each texture stores 4 directions (one per RGBA channel).
        /// </summary>
        public bool TryBake(
            LightingVolume sourceVolume,
            out Texture3D[] resultTextures,
            out Vector3[] bakedDirections,
            out string error
        ) {
            resultTextures = null;
            bakedDirections = null;
            error = "";

            if (sourceVolume == null) {
                error = "OcclusionFieldBaker: sourceVolume is null";
                return false;
            }
            if (occlusionFieldBakeCompute == null) {
                error = "OcclusionFieldBaker: occlusionFieldBakeCompute not assigned";
                return false;
            }
            if (sourceVolume.sdfHiresTexture == null) {
                error = "OcclusionFieldBaker: sourceVolume has no sdfHiresTexture";
                return false;
            }

            Bounds bounds = sourceVolume.Bounds;
            Vector3Int resolution = sourceVolume.TrimmedMaxResolution;
            int voxelCount = resolution.x * resolution.y * resolution.z;

            if (bounds.size.magnitude < 0.01f || resolution.x < 2) {
                error = "OcclusionFieldBaker: invalid bounds or resolution";
                return false;
            }

            // Compute voxel metrics
            Vector3 voxelSize = new Vector3(
                bounds.size.x / resolution.x,
                bounds.size.y / resolution.y,
                bounds.size.z / resolution.z
            );
            float voxelDiag = voxelSize.magnitude;

            uint groupX = (uint)Mathf.CeilToInt(resolution.x / 8f);
            uint groupY = (uint)Mathf.CeilToInt(resolution.y / 8f);
            uint groupZ = (uint)Mathf.CeilToInt(resolution.z / 8f);

            int numDirections = (int)directionCount;
            bool isSingleDir = directionCount == DirectionCount.Dir1Sun;
            int dirsPerBatch = isSingleDir ? 1 : 4;
            int textureCount = isSingleDir ? 1 : numDirections / 4;

            GraphicsFormat textureFormat = GetOcclusionFieldFormat(isSingleDir);
            if (!SystemInfo.IsFormatSupported(textureFormat, GraphicsFormatUsage.Sample)) {
                error = $"OcclusionFieldBaker: format {textureFormat} not supported for sampling on this platform";
                return false;
            }

            Vector3[] packedDirections;
            if (isSingleDir) {
                packedDirections = new[] { OcclusionFieldQuery.GetSunDirection() };
            } else {
                packedDirections = Fibonacci.GenerateFibonacciDirections(numDirections, hemisphereOnly);
            }
            bakedDirections = packedDirections;

            int kernelIdx = occlusionFieldBakeCompute.FindKernel("CSMain");

            // Set shared uniforms
            occlusionFieldBakeCompute.SetTexture(kernelIdx, s_sdfTex, sourceVolume.sdfHiresTexture);
            occlusionFieldBakeCompute.SetVector(s_boundsMin, bounds.min);
            occlusionFieldBakeCompute.SetVector(s_boundsSize, bounds.size);
            occlusionFieldBakeCompute.SetInts(s_resolution, resolution.x, resolution.y, resolution.z);
            occlusionFieldBakeCompute.SetFloat(s_raymarchSoftness, shadowSoftness);
            occlusionFieldBakeCompute.SetInt(s_raymarchMaxSteps, 256);
            occlusionFieldBakeCompute.SetFloat(s_raymarchMinStep, voxelDiag * 0.01f);
            occlusionFieldBakeCompute.SetFloat(s_raymarchEpsilon, voxelDiag * 0.02f);

            resultTextures = new Texture3D[textureCount];

            var dirBuffer = new ComputeBuffer(dirsPerBatch, sizeof(float) * 3);

            try {
                for (int texIdx = 0; texIdx < textureCount; texIdx++) {
                    int dirOffset = texIdx * dirsPerBatch;

                    var batchDirs = new Vector3[dirsPerBatch];
                    for (int c = 0; c < dirsPerBatch; c++)
                        batchDirs[c] = packedDirections[dirOffset + c];
                    dirBuffer.SetData(batchDirs);

                    var outBuffer = new ComputeBuffer(voxelCount * dirsPerBatch, sizeof(float));
                    occlusionFieldBakeCompute.SetBuffer(kernelIdx, s_directions, dirBuffer);
                    occlusionFieldBakeCompute.SetInt(s_directionOffset, dirOffset);
                    occlusionFieldBakeCompute.SetBuffer(kernelIdx, s_outOcclusion, outBuffer);

                    occlusionFieldBakeCompute.Dispatch(kernelIdx, (int)groupX, (int)groupY, (int)groupZ);

                    // Readback
                    var readbackRequest = AsyncGPUReadback.Request(outBuffer);
                    readbackRequest.WaitForCompletion();

                    if (readbackRequest.hasError) {
                        error = $"OcclusionFieldBaker: readback failed for texture {texIdx}";
                        outBuffer.Dispose();
                        DisposePartialResults(resultTextures, texIdx);
                        resultTextures = null;
                        return false;
                    }

                    var readbackData = readbackRequest.GetData<float>();
                    if (readbackData.Length != voxelCount * dirsPerBatch) {
                        error = $"OcclusionFieldBaker: readback size mismatch for texture {texIdx} (got {readbackData.Length}, expected {voxelCount * dirsPerBatch})";
                        outBuffer.Dispose();
                        DisposePartialResults(resultTextures, texIdx);
                        resultTextures = null;
                        return false;
                    }

                    var tex = new Texture3D(resolution.x, resolution.y, resolution.z, textureFormat, TextureCreationFlags.None) {
                        filterMode = FilterMode.Trilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        name = isSingleDir
                            ? $"{sourceVolume.BakeRoot.name}_OcclusionField_Sun"
                            : $"{sourceVolume.BakeRoot.name}_OcclusionField_{texIdx:D2}"
                    };

                    if (isSingleDir) {
                        var packedR8 = new byte[voxelCount];
                        for (int i = 0; i < voxelCount; i++)
                            packedR8[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(readbackData[i] * 255f), 0, 255);
                        tex.SetPixelData(packedR8, 0);
                    } else {
                        SetPackedTextureData(tex, readbackData, voxelCount, textureFormat);
                    }
                    tex.Apply(false);

                    resultTextures[texIdx] = tex;
                    outBuffer.Dispose();

                    Debug.Log($"OcclusionFieldBaker: baked texture {texIdx + 1}/{textureCount} (directions {dirOffset}..{dirOffset + dirsPerBatch - 1}, format={textureFormat})");
                }
            } finally {
                dirBuffer.Dispose();
            }

            Debug.Log($"OcclusionFieldBaker: baked {textureCount} textures ({numDirections} directions, {resolution.x}x{resolution.y}x{resolution.z} = {voxelCount} voxels, hemisphere={hemisphereOnly}, format={textureFormat})");
            return true;
        }

        static void DisposePartialResults(Texture3D[] textures, int upTo) {
            for (int i = 0; i < upTo; i++) {
                if (textures[i] != null)
                    UnityEngine.Object.DestroyImmediate(textures[i]);
            }
        }
    }
}
