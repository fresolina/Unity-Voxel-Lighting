using System;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Lotec.Demo.Editor {
    /// <summary>
    /// Packs the remote Addressables content as part of every player build, and points its load path
    /// at the folder this particular build will be published to.
    ///
    /// Why this exists: the demo ships exactly ONE scene in Build Settings (Bootstrap). Playground and
    /// Sponza are Addressable groups on the Remote path, fetched at runtime - so the levels are not in
    /// the player at all. That content used to be built by hand and uploaded to a single shared
    /// /WebGL/ folder, which let code and content drift apart silently: a player expecting
    /// BufferGiBakeAsset v4 kept loading a bundle that still held v2, the disk bake was rejected, the
    /// runtime voxelization fallback rasterized nothing (it filters on the editor-only isStatic), and
    /// every level rendered black with no error. Building content HERE makes that split impossible -
    /// a player build always carries content packed from the same commit.
    ///
    /// The load path is per-build (publish_path/ServerData/[BuildTarget]) rather than shared, so
    /// previews and releases no longer overwrite each other's levels and an old release keeps loading
    /// the content it was actually built against.
    /// </summary>
    class RemoteContentBuild : IPreprocessBuildWithReport {
        /// <summary>Remote.LoadPath for this build. CI writes it next to the project (see the
        /// webgl-pages workflow); the env var is the same value for a one-off local build. Absent
        /// locally, where the profile's own value is left untouched.</summary>
        const string LoadPathFileName = "remote-load-path.txt";
        const string LoadPathEnvVar = "VOXEL_REMOTE_LOAD_PATH";
        const string RemoteLoadPathVariable = "Remote.LoadPath";

        // After BufferGiBakeAssetValidator (0): no point packing content for a project it rejects.
        public int callbackOrder => 100;

        public void OnPreprocessBuild(BuildReport report) {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) {
                throw new BuildFailedException(
                    "Addressables settings not found. Every level in this demo is an Addressable on " +
                    "the Remote path, so a player built without them has no levels to load.");
            }

            string loadPath = ResolveLoadPath();
            if (!string.IsNullOrEmpty(loadPath)) {
                settings.profileSettings.SetValue(settings.activeProfileId, RemoteLoadPathVariable, loadPath);
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                Debug.Log($"[RemoteContentBuild] {RemoteLoadPathVariable} = {loadPath}");
            } else {
                Debug.Log($"[RemoteContentBuild] no {LoadPathFileName}/{LoadPathEnvVar}; " +
                          "using the profile's own Remote.LoadPath.");
            }

            // Content is packed right here, so make sure nothing packs it a second time.
            settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;

            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
            if (!string.IsNullOrEmpty(result.Error))
                throw new BuildFailedException("Addressables content build failed: " + result.Error);

            Debug.Log("[RemoteContentBuild] packed remote content for this build.");
        }

        /// <summary>The CI-provided Remote.LoadPath, or null to leave the profile alone. The file wins
        /// over the env var: game-ci runs Unity inside a container, where a file under the mounted
        /// project is guaranteed to arrive and an arbitrary env var is not.</summary>
        static string ResolveLoadPath() {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", LoadPathFileName));
            if (File.Exists(path)) {
                string fromFile = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(fromFile)) return fromFile;
            }
            string fromEnv = Environment.GetEnvironmentVariable(LoadPathEnvVar);
            return string.IsNullOrEmpty(fromEnv) ? null : fromEnv.Trim();
        }
    }
}
