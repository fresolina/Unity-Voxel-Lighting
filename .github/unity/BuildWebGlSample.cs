using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class BuildWebGlSample {
    const string s_assetsScenePath = "Assets/Samples/Usage samples/Scenes/Playground.unity";
    const string s_packageScenePath = "Packages/com.lotecsoftware.voxel-lighting/Samples~/Usage samples/Scenes/Playground.unity";
    const string s_outputPath = "build/WebGL";
    const string s_settingsFolder = "Assets/Settings";
    const string s_rendererDataPath = s_settingsFolder + "/CiUniversalRenderer.asset";
    const string s_pipelineAssetPath = s_settingsFolder + "/CiUniversalRenderPipeline.asset";

    static string ResolveScenePath() {
        // Prefer an Assets copy (stable import) but fall back to the package path.
        if (File.Exists(s_assetsScenePath)) return s_assetsScenePath;
        if (File.Exists(s_packageScenePath)) return s_packageScenePath;

        // As a last resort, search the asset database for a scene named Playground.unity.
        string[] guids = AssetDatabase.FindAssets("Playground t:Scene");
        foreach (var g in guids) {
            string path = AssetDatabase.GUIDToAssetPath(g);
            if (path.EndsWith("Playground.unity", StringComparison.OrdinalIgnoreCase))
                return path;
        }

        return null;
    }

    public static void Build() {
        string scenePath = ResolveScenePath();
        if (string.IsNullOrEmpty(scenePath) || !File.Exists(scenePath)) {
            throw new FileNotFoundException($"Sample scene not found at '{s_assetsScenePath}' or '{s_packageScenePath}'.");
        }

        Debug.Log($"Building WebGL sample using scene: {scenePath}");

        // Debug: list scene dependencies and Texture3D assets so CI logs show whether baked textures
        // (e.g. RoomVolume_AlbedoIntensity.asset) are included before building.
        try {
            string[] deps = AssetDatabase.GetDependencies(scenePath, true);
            Debug.Log($"Scene dependencies ({deps.Length}):");
            bool foundAlbedo = false;
            foreach (var d in deps) {
                string guid = AssetDatabase.AssetPathToGUID(d);
                Type mainType = AssetDatabase.GetMainAssetTypeAtPath(d);
                Debug.Log($" - {d} (guid={guid}, type={(mainType != null ? mainType.Name : "null")})");
                if (d.IndexOf("RoomVolume_AlbedoIntensity", StringComparison.OrdinalIgnoreCase) >= 0) foundAlbedo = true;
                // Try to load Texture3D to ensure it's a valid asset
                try {
                    var tex3 = AssetDatabase.LoadAssetAtPath<Texture3D>(d);
                    if (tex3 != null) Debug.Log($"   -> Loaded Texture3D: {tex3.name}");
                } catch (Exception) { }
            }

            string[] allTex3Guids = AssetDatabase.FindAssets("t:Texture3D");
            Debug.Log($"All Texture3D assets found ({allTex3Guids.Length}):");
            foreach (var g in allTex3Guids) {
                string p = AssetDatabase.GUIDToAssetPath(g);
                Debug.Log($" - {p} (guid={g})");
            }

            Debug.Log($"RoomVolume_AlbedoIntensity present in scene dependencies: {foundAlbedo}");
        } catch (Exception e) {
            Debug.LogWarning($"Failed to enumerate scene dependencies: {e}");
        }

        EnsureUrpConfigured();
        ConfigureWebGlPublishing();

        EditorBuildSettings.scenes = new[] {
            new EditorBuildSettingsScene(scenePath, true)
        };

        Directory.CreateDirectory(s_outputPath);

        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = new[] { scenePath },
            locationPathName = s_outputPath,
            target = BuildTarget.WebGL,
            options = BuildOptions.Development
        });

        if (report.summary.result != BuildResult.Succeeded) {
            throw new Exception($"WebGL build failed with result '{report.summary.result}'.");
        }
    }

    static void ConfigureWebGlPublishing() {
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
        PlayerSettings.WebGL.decompressionFallback = false;
        PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.WebGL, Il2CppCodeGeneration.OptimizeSize);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[] {
            GraphicsDeviceType.WebGPU,
        });
    }

    static void EnsureUrpConfigured() {
        Directory.CreateDirectory(s_settingsFolder);

        UniversalRendererData rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(s_rendererDataPath);
        if (rendererData == null) {
            rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(rendererData, s_rendererDataPath);
        }

        UniversalRenderPipelineAsset pipelineAsset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(s_pipelineAssetPath);
        if (pipelineAsset == null) {
            pipelineAsset = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            AssetDatabase.CreateAsset(pipelineAsset, s_pipelineAssetPath);
        }

        SerializedObject serializedPipeline = new SerializedObject(pipelineAsset);
        SerializedProperty rendererDataList = serializedPipeline.FindProperty("m_RendererDataList");
        if (rendererDataList != null) {
            if (rendererDataList.arraySize == 0) {
                rendererDataList.arraySize = 1;
            }

            rendererDataList.GetArrayElementAtIndex(0).objectReferenceValue = rendererData;
        }

        SerializedProperty defaultRendererIndex = serializedPipeline.FindProperty("m_DefaultRendererIndex");
        if (defaultRendererIndex != null) {
            defaultRendererIndex.intValue = 0;
        }

        serializedPipeline.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();

        GraphicsSettings.defaultRenderPipeline = pipelineAsset;
        QualitySettings.renderPipeline = pipelineAsset;
    }
}
