using System.Collections.Generic;
using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// A list of dynamic point/spot lights for <see cref="LocalLights"/> to publish. This is
    /// the ONLY place lights are authored: a level's own lights go on a provider in that level (put it on
    /// the settings GameObject carrying <see cref="BufferGiFields"/>), and the bootstrap's player-carried
    /// lights go on a provider there. "Lights that live in this scene" is one concept, so it is one
    /// component - and it is the ONLY component this feature needs. Listing a light here is the entire
    /// setup; nothing has to be placed anywhere else for it to light the scene.
    ///
    /// Per-scene lists exist because Unity has no cross-scene references, so a single persistent object
    /// could never point at an additively loaded level's lights. Same problem BufferGiFields solves for
    /// the GI field inputs, and the same shape of answer, except this one uses a self-registry rather
    /// than a scene scan: every enabled provider adds itself to <see cref="All"/>, so additively loaded
    /// levels just work and there is no manager dependency to race.
    ///
    /// A listed light is switched by enabling/disabling the Light (or its GameObject) - lights that
    /// aren't <c>isActiveAndEnabled</c> are skipped. Disabling THIS component takes the whole list out at
    /// once. Lights created after load can be added with <see cref="Add"/>.
    ///
    /// Only the first <see cref="LocalLights.MaxPointLights"/> point and
    /// <see cref="LocalLights.MaxSpotLights"/> spot lights are published; <see cref="Priority"/>
    /// decides who survives that budget.
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways] // must also register in edit mode - the GI solve and direct lighting run there
    [AddComponentMenu("Lotec/Voxel Lighting/Local Lights Provider")]
    public class LocalLightsProvider : MonoBehaviour {
        // Self-registry: every enabled provider adds itself, so LocalLights needs no scene scan and no
        // load-order guarantee (mirrors VoxelVolume's registry).
        static readonly List<LocalLightsProvider> s_all = new List<LocalLightsProvider>();

        /// <summary>All enabled providers, in registration order. The publisher collects them by
        /// <see cref="Priority"/> first and uses this order only to break ties.</summary>
        public static IReadOnlyList<LocalLightsProvider> All => s_all;

        [Tooltip("Who survives when the 4 point + 4 spot budget is tight: HIGHER is collected first, and " +
                 "lights are dropped from the end. Give the player's own lights (flashlight, candle) a " +
                 "higher priority than a level's torches, so walking into a lamp-lit room can't switch " +
                 "off what the player is carrying. Equal priorities keep registration order.")]
        [SerializeField] int _priority;

        [Tooltip("The dynamic point/spot lights in THIS scene, published for fragment direct lighting + " +
                 "GI. Disable a light to switch it off; disable this component to switch the whole " +
                 "list off. No other setup is needed - listing them here is the whole job.")]
        [SerializeField] List<Light> _lights = new List<Light>();

        /// <summary>Collection order against other providers; higher is collected first and so survives
        /// the point/spot budget. See the field tooltip.</summary>
        public int Priority => _priority;

        /// <summary>This scene's lights. Entries can be null if one was deleted; every reader skips those.</summary>
        public IReadOnlyList<Light> Lights => _lights;

        void OnEnable() {
            if (!s_all.Contains(this))
                s_all.Add(this);
        }

        void OnDisable() {
            s_all.Remove(this);
        }

        /// <summary>Add a light created after load (a spawned pickup, a dropped torch). Ignores nulls and
        /// duplicates. Lights that ship WITH the level belong in the serialized list instead.</summary>
        public void Add(Light light) {
            if (light != null && !_lights.Contains(light))
                _lights.Add(light);
        }

        /// <summary>Remove a light added by <see cref="Add"/> (or a serialized one). Returns false if it
        /// wasn't listed. Not needed just to switch a light off - disable the Light for that.</summary>
        public bool Remove(Light light) => _lights.Remove(light);
    }
}
