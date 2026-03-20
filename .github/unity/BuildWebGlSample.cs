using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class BuildWebGlSample {
    const string s_scenePath = "Assets/Samples/Usage samples/Scenes/Playground.unity";
    const string s_outputPath = "build/WebGL";
    const string s_settingsFolder = "Assets/Settings";
    const string s_rendererDataPath = s_settingsFolder + "/CiUniversalRenderer.asset";
    const string s_pipelineAssetPath = s_settingsFolder + "/CiUniversalRenderPipeline.asset";

    public static void Build() {
        if (!File.Exists(s_scenePath)) {
            throw new FileNotFoundException($"Sample scene not found at '{s_scenePath}'.");
        }

        EnsureUrpConfigured();
        ConfigureWebGlPublishing();

        EditorBuildSettings.scenes = new[] {
            new EditorBuildSettingsScene(s_scenePath, true)
        };

        Directory.CreateDirectory(s_outputPath);

        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = new[] { s_scenePath },
            locationPathName = s_outputPath,
            target = BuildTarget.WebGL,
            options = BuildOptions.None
        });

        if (report.summary.result != BuildResult.Succeeded) {
            throw new Exception($"WebGL build failed with result '{report.summary.result}'.");
        }
    }

    static void ConfigureWebGlPublishing() {
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
        PlayerSettings.WebGL.decompressionFallback = true;
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
