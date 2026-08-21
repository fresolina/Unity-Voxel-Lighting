using System;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEngine;

namespace Lotec.Demo.Editor {
    /// <summary>
    /// Points the remote Addressables content at the folder THIS build will be published to, so the
    /// content packed for a build always matches the code in it.
    ///
    /// Why this exists: the demo ships exactly ONE scene in Build Settings (Bootstrap). Playground and
    /// Sponza are Addressable groups on the Remote path, fetched at runtime - so the levels are not in
    /// the player at all. That content used to be built by hand and uploaded to a single shared
    /// /WebGL/ folder, which let code and content drift apart silently: a player expecting
    /// BufferGiBakeAsset v4 kept loading a bundle that still held v2, the disk bake was rejected, the
    /// runtime voxelization fallback rasterized nothing (it filters on the editor-only isStatic), and
    /// every level rendered black with no error at all.
    ///
    /// The load path is per-build (publish_path/ServerData/[BuildTarget]) rather than shared, so
    /// previews and releases no longer overwrite each other's levels, and an old release keeps loading
    /// the content it was actually built against.
    ///
    /// <b>This is a <see cref="BuildPlayerProcessor"/>, not an IPreprocessBuildWithReport, and it does
    /// NOT build the content itself.</b> Both points are load-bearing:
    /// <list type="bullet">
    /// <item>IPreprocessBuildWithReport runs INSIDE the player build, where the asset bundle pipeline
    /// is already locked - calling BuildPlayerContent there throws "Cannot build asset bundles while a
    /// build is in progress". BuildPlayerProcessor.PrepareForBuild runs before the build starts.</item>
    /// <item>Addressables' own AddressablesPlayerBuildProcessor (callbackOrder 1) does the content
    /// build, and also copies link.xml and registers streaming asset paths. Re-implementing that would
    /// mean re-implementing those too, so this only has to run FIRST (callbackOrder 0) and leave the
    /// right load path and build-with-player setting behind for it.</item>
    /// </list>
    /// </summary>
    public class RemoteContentBuild : BuildPlayerProcessor {
        /// <summary>Remote.LoadPath for this build. CI writes it next to the project (see the
        /// webgl-pages workflow); the env var is the same value for a one-off local build. Absent
        /// locally, where the profile's own value and the developer's own preference are left
        /// untouched.</summary>
        const string LoadPathFileName = "remote-load-path.txt";
        const string LoadPathEnvVar = "VOXEL_REMOTE_LOAD_PATH";
        const string RemoteLoadPathVariable = "Remote.LoadPath";

        // Before AddressablesPlayerBuildProcessor (1), which reads both values set below.
        public override int callbackOrder => 0;

        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext) {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) {
                throw new BuildFailedException(
                    "Addressables settings not found. Every level in this demo is an Addressable on " +
                    "the Remote path, so a player built without them has no levels to load.");
            }

            string loadPath = ResolveLoadPath();
            if (string.IsNullOrEmpty(loadPath)) {
                Debug.Log($"[RemoteContentBuild] no {LoadPathFileName}/{LoadPathEnvVar}; leaving " +
                          "Remote.LoadPath and the build-with-player setting as authored.");
                return;
            }

            settings.profileSettings.SetValue(settings.activeProfileId, RemoteLoadPathVariable, loadPath);
            // A CI build must always carry freshly packed content - never a developer's local
            // preference, and never a stale upload.
            settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.BuildWithPlayer;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();

            // Addressables resolves the relative Remote.BuildPath against the CURRENT DIRECTORY, which
            // is the project folder in the editor but can differ under a batchmode builder - so the
            // publish step cannot assume one fixed location. Logged so the build log settles it.
            Debug.Log($"[RemoteContentBuild] {RemoteLoadPathVariable} = {loadPath}" +
                      " | cwd=" + Directory.GetCurrentDirectory() +
                      " | dataPath=" + Application.dataPath +
                      " | Remote.BuildPath=" + settings.profileSettings.GetValueByName(
                          settings.activeProfileId, "Remote.BuildPath"));
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
