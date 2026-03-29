using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class BuildWebGlSample {
    const string s_scenePath = "Packages/com.lotecsoftware.voxel-lighting/Samples/Usage samples/Scenes/Playground.unity";
    const string s_outputPath = "build/WebGL";
    const string s_settingsFolder = "Assets/Settings";
    const string s_rendererDataPath = s_settingsFolder + "/CiUniversalRenderer.asset";
    const string s_pipelineAssetPath = s_settingsFolder + "/CiUniversalRenderPipeline.asset";

    public static void Build() {
        string scenePath = s_scenePath;
        if (string.IsNullOrEmpty(scenePath) || !File.Exists(scenePath)) {
            throw new FileNotFoundException($"Sample scene not found at '{s_scenePath}'.");
        }

        Debug.Log($"Building WebGL sample using scene: {scenePath}");

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
            Debug.Log($"URP: rendererData not found at '{s_rendererDataPath}', creating.");
            rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(rendererData, s_rendererDataPath);
            AssetDatabase.ImportAsset(s_rendererDataPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(s_rendererDataPath);
            Debug.Log($"URP: rendererData created -> {(rendererData != null ? rendererData.name : "null")} (path={s_rendererDataPath})");
        } else {
            Debug.Log($"URP: rendererData loaded -> {rendererData.name} (path={s_rendererDataPath})");
        }
        try {
            string rdGuid = AssetDatabase.AssetPathToGUID(s_rendererDataPath);
            Debug.Log($"URP: rendererData GUID: {rdGuid}");
        } catch (Exception) { }

        UniversalRenderPipelineAsset pipelineAsset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(s_pipelineAssetPath);
        if (pipelineAsset == null) {
            Debug.Log($"URP: pipeline asset not found at '{s_pipelineAssetPath}', creating.");
            pipelineAsset = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            AssetDatabase.CreateAsset(pipelineAsset, s_pipelineAssetPath);
            AssetDatabase.ImportAsset(s_pipelineAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            pipelineAsset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(s_pipelineAssetPath);
            Debug.Log($"URP: pipeline asset created -> {(pipelineAsset != null ? pipelineAsset.name : "null")} (path={s_pipelineAssetPath})");
        } else {
            Debug.Log($"URP: pipeline asset loaded -> {pipelineAsset.name} (path={s_pipelineAssetPath})");
        }
        try {
            string paGuid = AssetDatabase.AssetPathToGUID(s_pipelineAssetPath);
            Debug.Log($"URP: pipeline asset GUID: {paGuid}");
        } catch (Exception) { }

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
