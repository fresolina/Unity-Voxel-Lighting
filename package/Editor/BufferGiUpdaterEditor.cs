using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lotec.Lighting.Editor {
    /// <summary>
    /// Adds a "Bake Voxelization To Disk" button: captures the buffer GI's voxelized material/surface
    /// slices - the coarse field plus every detailed field - into one BufferGiBakeAsset per field,
    /// each named for its field and saved in the scene-adjacent bake folder (same convention as the
    /// texture bakes). The assets are assigned back to the component, so load uploads the coarse asset
    /// + the asset matching the active fine volume instead of re-rasterizing the scene meshes.
    /// </summary>
    [CustomEditor(typeof(BufferGiUpdater))]
    public class BufferGiUpdaterEditor : UnityEditor.Editor {
        const string AssetSuffix = "-BufferGiVoxelization";

        public override VisualElement CreateInspectorGUI() {
            var root = new VisualElement();
            InspectorElement.FillDefaultInspector(root, serializedObject, this);
            root.Add(new VisualElement { style = { height = 10 } });

            root.Add(new Button(OnBakeClicked) {
                text = "Bake Voxelization To Disk",
                style = { height = 30, marginTop = 5, marginBottom = 5 }
            });
            return root;
        }

        void OnBakeClicked() {
            var gi = target as BufferGiUpdater;
            if (gi == null) return;

            // Force a clean reinit before capturing. In edit mode the GPU buffers can be left over
            // from a previous scene/volume (they still pass IsValid, so EnsureInitialized keeps them),
            // and capturing against that stale state throws. Releasing here makes CaptureFieldToAsset's
            // EnsureInitialized reallocate + re-rasterize from scratch - the same clean slate entering
            // Play gives via its domain reload, so baking no longer needs a Play round-trip first.
            gi.ReleaseBuffers();

            // Per-level inputs (coarse field, detailed fields, bake assets) live on the level's
            // BufferGiFields, not on this persistent updater. Resolve it from the active volume's scene
            // and bind it so CoarseOrigin/Size use this provider's coarse field during capture.
            BufferGiFields fields = gi.Fields != null ? gi.Fields : BufferGiFields.Find(gi.Volume);
            if (fields == null) {
                Debug.LogError("Buffer GI bake: no BufferGiFields found for the active volume's level. Open " +
                               "the level scene (which holds the BufferGiFields provider) alongside the bootstrap " +
                               "scene, make its volume active, then bake.", gi);
                return;
            }
            gi.EditorBindFields(fields);

            // Refresh every derived bound this capture is about to read. VoxelVolume.Bounds and
            // MeshBounds.Bounds are SERIALIZED derived values - they only re-derive on OnValidate or an
            // explicit recompute - so a bake taken after e.g. a Max Resolution change, but before
            // anything re-derived them, records the OLD padded box. The runtime then compares the asset
            // against freshly derived bounds, rejects it, and silently falls back to runtime
            // voxelization. VoxelSdfBaker already recomputes before baking; this gives the GI bake the
            // same guarantee so the two bakes can't drift apart.
            RefreshBakeBounds(gi, fields);

            // Membership of the BAKED LIGHTS, onto each field volume's VoxelLights. Same reason as the
            // bounds above - it is serialized authoring data the runtime depends on - but here the
            // dependency is absolute: Light.lightmapBakeType does not exist in a player, so if this list
            // isn't written now, nothing downstream can ever work out which lights are Baked.
            int bakedLights = RefreshBakedLights(gi, fields);
            VoxelVolume folderVolume = gi.Volume != null ? gi.Volume : FindAnyDetailedVolume(fields);
            if (folderVolume == null) {
                Debug.LogError("Buffer GI bake: need at least one VoxelVolume (the active volume or a detailed field with a VoxelVolume sibling) to resolve the save folder.", gi);
                return;
            }
            string folder = VoxelBakeEditorUtil.GetSceneBakeFolder(folderVolume.gameObject, "-VoxelLighting");

            if (string.IsNullOrEmpty(folder)) return;

            var assets = new List<BufferGiBakeAsset>();

            // Coarse field (one asset, named for the coarse MeshBounds).
            if (fields.CoarseField != null && fields.CoarseField.Root != null) {
                var coarse = ScriptableObject.CreateInstance<BufferGiBakeAsset>();
                coarse.name = fields.CoarseField.name + AssetSuffix;
                if (gi.CaptureFieldToAsset(coarse, true, fields.CoarseField.Root, gi.CoarseOrigin, gi.CoarseSize))
                    assets.Add(Save(coarse, folder));
                else
                    Object.DestroyImmediate(coarse);
            }

            // Each detailed (fine) field: its runtime grid is its sibling VoxelVolume's padded bounds.
            foreach (MeshBounds field in fields.DetailedFields) {
                if (field == null) continue;
                if (!gi.TryGetDetailedFieldGrid(field, out Transform root, out Vector3 origin, out Vector3 size))
                    continue;
                var fine = ScriptableObject.CreateInstance<BufferGiBakeAsset>();
                fine.name = field.name + AssetSuffix;
                if (gi.CaptureFieldToAsset(fine, false, root, origin, size))
                    assets.Add(Save(fine, folder));
                else
                    Object.DestroyImmediate(fine);
            }

            if (assets.Count == 0) {
                Debug.LogWarning("Buffer GI bake: nothing baked (no coarse field and no detailed fields with a VoxelVolume sibling).", gi);
                return;
            }

            // Assign the freshly baked set onto the level's provider (replacing the old list) so load
            // uploads them for this level instead of re-rasterizing.
            var fieldsSo = new SerializedObject(fields);
            SerializedProperty list = fieldsSo.FindProperty("_bakeAssets");
            list.ClearArray();
            for (int i = 0; i < assets.Count; i++) {
                list.InsertArrayElementAtIndex(i);
                list.GetArrayElementAtIndex(i).objectReferenceValue = assets[i];
            }
            fieldsSo.ApplyModifiedProperties();
            Debug.Log($"Buffer GI: baked {assets.Count} field voxelization asset(s) to '{folder}', assigned to " +
                      $"'{fields.name}'; bound {bakedLights} baked light(s) to their volumes' Voxel Lights.", gi);
        }

        // Fill each field volume's VoxelLights with the Baked/Mixed POINT lights inside its bounds,
        // adding the component where it is missing (the other per-volume binders are auto-added by their
        // bakers the same way). Returns how many lights were bound, for the bake log.
        //
        // The lights themselves are NOT stored in the bake assets - the runtime re-stamps them from these
        // lists on every upload, which is what makes them switchable in a player. So this list is the
        // whole handover from Editor to runtime, and it is written on every bake rather than only when
        // empty: a light that was retyped, moved out of the volume or deleted has to leave it again.
        /// <summary>Refresh every field volume's VoxelLights from the scene, adding the component where
        /// a volume has baked lights and none yet. Called by the bake button, so a bake can never ship a
        /// list that went stale since the last manual refresh. <paramref name="fields"/> may be null for
        /// a fine-only level.</summary>
        static int RefreshBakedLights(BufferGiUpdater gi, BufferGiFields fields) {
            var seen = new HashSet<VoxelVolume>();
            int bound = FillVolumeLights(gi.Volume, seen);
            if (fields == null) return bound;
            foreach (MeshBounds field in fields.DetailedFields) {
                if (field == null) continue;
                bound += FillVolumeLights(field.GetComponent<VoxelVolume>(), seen);
            }
            // The coarse field only has a volume of its own in some setups; when it doesn't, its lights
            // still arrive - the runtime injects every VoxelLights in the scene into BOTH fields.
            if (fields.CoarseField != null)
                bound += FillVolumeLights(fields.CoarseField.GetComponent<VoxelVolume>(), seen);
            return bound;
        }

        // Adding the component is this method's own job; deciding WHICH lights belong is
        // LightEmissionBake.FillFromScene's, which the component's own Reset and Refresh call too. One
        // definition, three entry points - they used to be two implementations that could drift.
        static int FillVolumeLights(VoxelVolume vv, HashSet<VoxelVolume> seen) {
            if (vv == null || !seen.Add(vv)) return 0;

            VoxelLights holder = vv.GetComponent<VoxelLights>();
            if (holder == null) {
                // Undo.AddComponent fires Reset, which fills the list - so a volume that has no baked
                // lights ends up with an empty component rather than none. Add it only when there is
                // something to hold, and let Reset do the filling when there is.
                if (!LightEmissionBake.HasBakeCandidateInside(vv)) return 0;
                holder = Undo.AddComponent<VoxelLights>(vv.gameObject);
            } else {
                LightEmissionBake.FillFromScene(holder);
            }
            return holder.Lights.Count;
        }

        // Re-derive (and persist) the bounds every capture path reads:
        //   - the ACTIVE volume, whose Bounds become GridOrigin/GridSize - what the runtime compares the
        //     FINE asset against, so this is the one that has to be current for the match to succeed;
        //   - each detailed field's sibling VoxelVolume, which is the box the fine capture records;
        //   - the coarse MeshBounds, which CoarseWorldBounds scales into the coarse capture's box.
        // VoxelVolume.RecomputeBoundsAndResolution refreshes its own MeshBounds as part of the job, so
        // the coarse field only needs a direct Recompute when it has no VoxelVolume sibling.
        static void RefreshBakeBounds(BufferGiUpdater gi, BufferGiFields fields) {
            var seen = new HashSet<Object>();
            RecomputeVolume(gi.Volume, seen);
            foreach (MeshBounds field in fields.DetailedFields) {
                if (field == null) continue;
                RecomputeVolume(field.GetComponent<VoxelVolume>(), seen);
            }
            if (fields.CoarseField != null && seen.Add(fields.CoarseField)) {
                Undo.RecordObject(fields.CoarseField, "Refresh Buffer GI Bake Bounds");
                fields.CoarseField.Recompute();
                EditorUtility.SetDirty(fields.CoarseField);
            }
        }

        // SetDirty on both components: RecomputeBoundsAndResolution writes the volume's _bounds /
        // _trimmedMaxResolution AND its MeshBounds' _bounds, and those have to reach the saved scene or
        // a build would ship the stale box the asset no longer matches.
        static void RecomputeVolume(VoxelVolume vv, HashSet<Object> seen) {
            if (vv == null || !seen.Add(vv)) return;
            MeshBounds source = vv.GetComponent<MeshBounds>();
            Undo.RecordObject(vv, "Refresh Buffer GI Bake Bounds");
            if (source != null) {
                Undo.RecordObject(source, "Refresh Buffer GI Bake Bounds");
                seen.Add(source);
            }
            vv.RecomputeBoundsAndResolution();
            EditorUtility.SetDirty(vv);
            if (source != null) EditorUtility.SetDirty(source);
        }

        // Save (or overwrite in place) one field asset in the bake folder.
        static BufferGiBakeAsset Save(BufferGiBakeAsset asset, string folder) {
            string path = System.IO.Path.Combine(folder, $"{asset.name}.asset");
            return VoxelBakeEditorUtil.SaveAsset(asset, path, "Buffer GI Voxelization");
        }

        static VoxelVolume FindAnyDetailedVolume(BufferGiFields fields) {
            foreach (MeshBounds field in fields.DetailedFields) {
                if (field == null) continue;
                VoxelVolume vv = field.GetComponent<VoxelVolume>();
                if (vv != null) return vv;
            }
            return null;
        }
    }
}
