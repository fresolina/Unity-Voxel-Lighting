using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Lotec.Lighting.Editor {
    /// <summary>
    /// Fails a player build when a committed <see cref="BufferGiBakeAsset"/> is not at the current
    /// <see cref="BufferGiBakeAsset.Version"/>.
    ///
    /// A version bump makes every older asset unloadable - BakeAssetValid rejects it, the updater
    /// falls back to voxelizing at runtime, and the only sign is one warning in the player log. That
    /// is easy to miss: the Playground sample's assets sat at v2 through two bumps (v3 in "separate
    /// wall thickening", v4 in "remove the _bakedNormals toggle") and the staleness only surfaced
    /// when someone read the console of a WebGL preview.
    ///
    /// Runs as a build preprocessor rather than a CI script on purpose: reading the version needs
    /// Unity's own deserializer (the assets are PreferBinarySerialization), and this way one check
    /// covers CI and local builds without booting a second editor.
    /// </summary>
    class BufferGiBakeAssetValidator : IPreprocessBuildWithReport {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report) {
            List<string> stale = CollectStale();
            if (stale.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine($"Buffer GI: {stale.Count} baked voxelization asset(s) are not at version " +
                          $"{BufferGiBakeAsset.Version} and would be REJECTED at runtime (the volume would " +
                          "voxelize on load instead of using its bake):");
            foreach (string line in stale) sb.AppendLine(line);
            sb.AppendLine("Re-bake them (BufferGiUpdater inspector -> Bake Voxelization To Disk) and commit " +
                          "the result. If a stale asset is deliberate, delete it or remove it from its " +
                          "BufferGiFields list rather than shipping one that cannot load.");
            throw new BuildFailedException(sb.ToString());
        }

        /// <summary>Every bake asset whose stored version differs from the current one, as
        /// "path: version=N, expected M" lines. Shared with the menu item below.</summary>
        static List<string> CollectStale() {
            var stale = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(BufferGiBakeAsset))) {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<BufferGiBakeAsset>(path);
                // Only the VERSION is checked. Grid/bounds mismatches are legitimate (an asset for a
                // volume that is not the active one), but a version mismatch can never load.
                if (asset != null && asset.version != BufferGiBakeAsset.Version)
                    stale.Add($"  {path}: version={asset.version}, expected {BufferGiBakeAsset.Version}");
            }
            return stale;
        }

        [MenuItem("Tools/Lotec/Voxel Lighting/Check Baked Voxelization Assets")]
        static void CheckFromMenu() {
            List<string> stale = CollectStale();
            if (stale.Count == 0) {
                UnityEngine.Debug.Log($"Buffer GI: all baked voxelization assets are at version {BufferGiBakeAsset.Version}.");
                return;
            }
            UnityEngine.Debug.LogWarning($"Buffer GI: {stale.Count} baked voxelization asset(s) need a re-bake:\n" +
                                         string.Join("\n", stale));
        }
    }
}
