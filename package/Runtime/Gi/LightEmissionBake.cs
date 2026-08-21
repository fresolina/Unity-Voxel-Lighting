using System.Collections.Generic;
using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Bakes the Unity <see cref="Light"/>s listed on the scene's <see cref="VoxelLights"/> components
    /// into a Buffer GI field's voxelized material slice, so a baked light lights the scene exactly the
    /// way an emissive MATERIAL does: the voxel the light sits in gets the light's hue as albedo plus an
    /// emission intensity, and from there the solve makes no distinction - it bounces off walls, it is
    /// occluded by geometry, and it costs nothing per frame (unlike the realtime local lights, which
    /// re-shadow every solve).
    ///
    /// Only POINT lights are baked. A spot's cone cannot be expressed by a voxel that radiates equally
    /// in all directions, and a directional light is the sun (already handled by the solve's own main
    /// light), so both are left alone.
    ///
    /// MEMBERSHIP is authoring data, serialized on VoxelLights by the bake button, because
    /// <c>Light.lightmapBakeType</c> does not exist in player assemblies - a player could never work out
    /// which lights are Baked. Everything here is otherwise runtime code: given the lists, a player
    /// resolves cells, sums radiance and dispatches, which is what lets a baked light be switched on and
    /// off at runtime (see <see cref="Inject"/>) instead of being frozen at bake time.
    ///
    /// Runs after the mesh raster (geometry must not overwrite a light voxel) and before the derive
    /// passes, so the light voxel comes out solid + emissive like any other emitter.
    /// </summary>
    public static class LightEmissionBake {
        // Voxels lit per dispatch; longer lists are batched. Matches MAX_BAKED_LIGHTS in BufferGiSolve.compute.
        const int MaxPerDispatch = 16;

        static readonly int s_bakedLightCount = Shader.PropertyToID("_BakedLightCount");
        static readonly int s_bakedLightCell = Shader.PropertyToID("_BakedLightCell");
        static readonly int s_bakedLightAlbedoEmission = Shader.PropertyToID("_BakedLightAlbedoEmission");
        static readonly int s_materialWrite = Shader.PropertyToID("_MaterialWrite");
        static readonly int s_surface = Shader.PropertyToID("_Surface");

        static readonly Vector4[] s_cells = new Vector4[MaxPerDispatch];
        static readonly Vector4[] s_albedoEmission = new Vector4[MaxPerDispatch];
        // Scratch, reused so a per-field inject allocates nothing after the first.
        static readonly List<Vector3Int> s_cellList = new List<Vector3Int>();
        static readonly List<Vector3> s_hue = new List<Vector3>();      // radiance summed over ALL listed lights
        static readonly List<Vector3> s_emission = new List<Vector3>(); // radiance summed over the lights that are ON
        static readonly HashSet<Light> s_seen = new HashSet<Light>();

        /// <summary>Refresh the set of holders the injects read. Scene-wide rather than "the active
        /// volume's own", because the coarse field spans every detailed volume and has to see their
        /// lights too - a fire inside a detailed box must keep burning once the camera leaves it. The
        /// per-field cell test drops whatever falls outside a given grid.</summary>
        public static void CollectHolders(List<VoxelLights> holders) {
            if (holders == null) return;
            holders.Clear();
            // Include-then-filter rather than FindObjectsInactive.Exclude: that flag is about the
            // GameObject, and this needs DISABLED COMPONENTS on active objects (a switched-off group is
            // still stamped, see Inject) while genuinely unloaded/deactivated subtrees stay out.
            foreach (VoxelLights holder in Object.FindObjectsByType<VoxelLights>(FindObjectsInactive.Include)) {
                if (holder == null || !holder.gameObject.activeInHierarchy) continue;
                if (holder.Lights.Count > 0) holders.Add(holder);
            }
        }

        /// <summary>
        /// Stamp the listed lights that fall inside this field into its material + surface slices. The
        /// caller has already bound _FieldOffset and the grid resolution constants; the world-to-cell
        /// mapping happens here so co-located lights can be summed before packing.
        ///
        /// The split between the two sums is what makes a runtime switch cheap. ALBEDO is stamped from
        /// every listed light, on or off, so the voxel is solid either way - which means the occupancy
        /// bitfield, and the surface + air-distance fields derived from it, are the same whatever the
        /// switches say. Only the EMISSION byte tracks the switches, and nothing is derived from it, so
        /// flipping one means re-running this dispatch and nothing else.
        /// </summary>
        /// <param name="origin">Field grid's world-space min corner.</param>
        /// <param name="voxelSize">Field's per-axis voxel size - it sets how bright one voxel has to be.</param>
        /// <param name="grid">Field's cubic resolution; lights outside it are dropped.</param>
        public static void Inject(ComputeShader cs, int kernel, ComputeBuffer material, ComputeBuffer surface,
                List<VoxelLights> holders, Vector3 origin, Vector3 voxelSize, int grid) {
            if (cs == null || kernel < 0 || material == null || surface == null || holders == null) return;

            s_cellList.Clear();
            s_hue.Clear();
            s_emission.Clear();
            s_seen.Clear();
            float meanProjectedArea = MeanProjectedArea(voxelSize);
            foreach (VoxelLights holder in holders) {
                if (holder == null) continue;
                // A disabled COMPONENT is the volume's group switch: its lights keep their albedo (so
                // nothing derived from occupancy moves) but stop emitting - exactly what happens to an
                // individual light that was switched off.
                bool holderOn = holder.isActiveAndEnabled;
                IReadOnlyList<Light> lights = holder.Lights;
                for (int i = 0; i < lights.Count; i++) {
                    Light light = lights[i];
                    // Overlapping volumes can list one light twice; it must only be counted once.
                    if (light == null || !s_seen.Add(light)) continue;
                    if (!TryGetCell(light.transform.position, origin, voxelSize, grid, out Vector3Int cell)) continue;
                    Vector3 radiance = VoxelRadiance(light, meanProjectedArea);
                    if (radiance.sqrMagnitude <= 0f) continue; // black or zero-intensity: never lights anything

                    // Lights sharing a cell are SUMMED. A single voxel is often coarser than the prop
                    // (the fireplace's flame + glow lights are 18 cm apart in a half-metre voxel), so
                    // dropping all but the last would quietly lose most of the fire's output. Summing
                    // radiance is the physically right merge, and doing it here avoids atomics on the
                    // packed material word - and leaves one thread per slot in the kernel.
                    int at = s_cellList.IndexOf(cell);
                    if (at < 0) {
                        at = s_cellList.Count;
                        s_cellList.Add(cell);
                        s_hue.Add(Vector3.zero);
                        s_emission.Add(Vector3.zero);
                    }
                    s_hue[at] += radiance;
                    if (holderOn && light.isActiveAndEnabled) s_emission[at] += radiance;
                }
            }
            if (s_cellList.Count == 0) return;

            cs.SetBuffer(kernel, s_materialWrite, material);
            cs.SetBuffer(kernel, s_surface, surface);
            for (int start = 0; start < s_cellList.Count; start += MaxPerDispatch) {
                int count = Mathf.Min(MaxPerDispatch, s_cellList.Count - start);
                int packed = 0;
                for (int i = 0; i < count; i++) {
                    Vector3 emission = s_emission[start + i];
                    float peak = Mathf.Max(emission.x, Mathf.Max(emission.y, emission.z));
                    // Hue from the LIT sum while anything in the cell is on, so a cell holding two
                    // differently coloured lights gets the right colour when only one of them burns.
                    // With everything off, fall back to the full sum: the magnitude is carried by the
                    // (now zero) emission channel, and this only has to keep the albedo non-zero so the
                    // voxel stays SOLID and the derived fields don't change under the switch.
                    Vector3 hue = peak > 0f ? emission : s_hue[start + i];
                    float huePeak = Mathf.Max(hue.x, Mathf.Max(hue.y, hue.z));
                    if (huePeak <= 0f) continue;
                    Vector3Int cell = s_cellList[start + i];
                    s_cells[packed] = new Vector4(cell.x, cell.y, cell.z, 0f);
                    // Hue only - the magnitude rides in the emission channel, which has the log range.
                    s_albedoEmission[packed] = new Vector4(hue.x / huePeak, hue.y / huePeak, hue.z / huePeak,
                                                           BufferGiUpdater.EncodeEmission8(peak));
                    packed++;
                }
                if (packed == 0) continue;
                cs.SetInt(s_bakedLightCount, packed);
                cs.SetVectorArray(s_bakedLightCell, s_cells);
                cs.SetVectorArray(s_bakedLightAlbedoEmission, s_albedoEmission);
                cs.Dispatch(kernel, Mathf.CeilToInt(packed / 64f), 1, 1);
            }
        }

        /// <summary>Change stamp over everything a bare <see cref="Inject"/> re-dispatch can fix on its
        /// own: which lights are switched on, and their colour/intensity. BufferGiUpdater samples this
        /// every frame and re-injects on a change - that IS the runtime light switch.</summary>
        public static int StateHash(List<VoxelLights> holders) {
            int hash = 17;
            if (holders == null) return hash;
            unchecked {
                foreach (VoxelLights holder in holders) {
                    if (holder == null) continue;
                    hash = hash * 31 + (holder.isActiveAndEnabled ? 1 : 0);
                    IReadOnlyList<Light> lights = holder.Lights;
                    for (int i = 0; i < lights.Count; i++) {
                        Light light = lights[i];
                        if (light == null) { hash *= 31; continue; }
                        hash = hash * 31 + (light.isActiveAndEnabled ? 1 : 0);
                        hash = hash * 31 + light.color.GetHashCode();
                        hash = hash * 31 + light.intensity.GetHashCode();
                    }
                }
            }
            return hash;
        }

        /// <summary>Change stamp over what a re-inject CANNOT fix: membership and world positions. The
        /// kernel only ever stamps cells, never un-stamps them, so a light that was moved, added or
        /// removed would leave its old voxel burning - that needs the whole voxelization redone.
        /// Sampled while authoring only; at runtime a light that moves is a realtime light, not a baked
        /// one.</summary>
        public static int LayoutHash(List<VoxelLights> holders) {
            int hash = 17;
            if (holders == null) return hash;
            unchecked {
                foreach (VoxelLights holder in holders) {
                    if (holder == null) continue;
                    IReadOnlyList<Light> lights = holder.Lights;
                    // Count + positions, no object identity: swapping one light for another at exactly
                    // the same position stamps exactly the same voxel, and its colour/intensity is
                    // StateHash's business - so identity would only add churn.
                    hash = hash * 31 + lights.Count;
                    for (int i = 0; i < lights.Count; i++) {
                        Light light = lights[i];
                        if (light == null) { hash *= 31; continue; }
                        hash = hash * 31 + light.transform.position.GetHashCode();
                    }
                }
            }
            return hash;
        }

#if UNITY_EDITOR
        /// <summary>Membership gate for the bake button: a POINT light whose mode is Baked or Mixed.
        /// The bake type is the whole switch - marking a light Baked is how the user opts in - and Mixed
        /// counts because for this renderer the voxelization IS the GI, which is precisely the half of a
        /// Mixed light an emissive voxel provides.
        ///
        /// Deliberately NOT gated on the light being enabled, nor on its GameObject being static (the
        /// gate the geometry raster uses): a light that is currently off is exactly what a runtime
        /// switch turns ON, so it has to be in the list to be found. Editor-only, because
        /// <c>lightmapBakeType</c> is - which is why membership is serialized on VoxelLights at all.</summary>
        public static bool IsBakeCandidate(Light light) {
            return light != null
                && light.type == LightType.Point
                && (light.lightmapBakeType == LightmapBakeType.Baked
                    || light.lightmapBakeType == LightmapBakeType.Mixed);
        }
#endif

        // World position -> grid cell, matching BgiWorldToGrid + floor in BufferGiField.hlsl.
        static bool TryGetCell(Vector3 world, Vector3 origin, Vector3 voxelSize, int grid, out Vector3Int cell) {
            Vector3 g = new Vector3((world.x - origin.x) / Mathf.Max(voxelSize.x, 1e-6f),
                                    (world.y - origin.y) / Mathf.Max(voxelSize.y, 1e-6f),
                                    (world.z - origin.z) / Mathf.Max(voxelSize.z, 1e-6f));
            cell = new Vector3Int(Mathf.FloorToInt(g.x), Mathf.FloorToInt(g.y), Mathf.FloorToInt(g.z));
            return cell.x >= 0 && cell.y >= 0 && cell.z >= 0
                && cell.x < grid && cell.y < grid && cell.z < grid;
        }

        // Mean projected area of one voxel box: for any convex body that is surface area / 4, i.e. the
        // average silhouette a gather ray sees no matter which of the three faces it arrives through.
        static float MeanProjectedArea(Vector3 voxelSize) {
            return 0.5f * (voxelSize.x * voxelSize.y + voxelSize.y * voxelSize.z + voxelSize.z * voxelSize.x);
        }

        // The radiance the voxel must carry to light the room like the point light it replaces. A surface
        // at distance d receives irradiance C/d^2 from the light, where C is the light's FinalColor - the
        // exact value the realtime path feeds the solve (see GetDirectLight). The same surface's
        // cosine-weighted gather sees an emissive voxel of area A over solid angle A/d^2 and folds it in
        // as L*A/(pi*d^2). Equating the two gives L = pi*C/A, so a coarse field's bigger voxels get a
        // proportionally DIMMER radiance and both fields agree on how bright the fire is.
        //
        // Range is deliberately ignored: an emissive voxel falls off as 1/d^2 forever, while URP's range
        // window is an authoring cutoff, not physics - so a baked light reaches a little further than the
        // same light does on the realtime path.
        static Vector3 VoxelRadiance(Light light, float meanProjectedArea) {
            Vector4 color = light.FinalColor();
            float scale = Mathf.PI / Mathf.Max(meanProjectedArea, 1e-6f);
            return new Vector3(color.x, color.y, color.z) * scale;
        }
    }
}
