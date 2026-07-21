using System;
using UnityEngine.SceneManagement;

namespace Lotec.Demo {
    /// <summary>
    /// Minimal contract a bootstrap scene-loader exposes so level-agnostic systems (e.g.
    /// <see cref="PlayerSpawner"/>) can react to level loads without depending on a concrete loader or
    /// its scene-entry type. Both the simple additive <see cref="SceneLoader"/> and an Addressables-
    /// backed loader can implement this, so the reactive systems stay shared and unchanged across
    /// projects. Anything that needs the loaded entry's metadata reads it from the concrete loader.
    /// </summary>
    public interface ISceneLoader {
        /// <summary>True while a level scene is loaded.</summary>
        bool HasLoadedScene { get; }

        /// <summary>The currently loaded level scene (only valid when <see cref="HasLoadedScene"/>).</summary>
        Scene LoadedScene { get; }

        /// <summary>Raised after a level finished loading (or was adopted) and became the active scene.</summary>
        event Action<Scene> SceneLoaded;
    }
}
