// RPG Character & Stats System — the one way every RPG editor window (and
// the server's inspector helper flows) reaches the scene's server.
//
// Why this exists: CharacterBuilderServer.Awake sets the singleton Instance,
// but Awake only ran in play mode — so in EDIT MODE every window saw
// Instance == null and each created its own throwaway server. The Character
// Builder registered built characters on one instance while the Registry
// window read another: "built → registry entry added" yet an empty registry,
// plus a pile of orphaned "CharacterBuilderServer" GameObjects.
//
// Resolution order: alive Instance (play mode) → the scene's server (works
// in edit mode too) → create exactly one (with Undo). [ExecuteAlways] on the
// server makes Instance reliable here as well.

using UnityEditor;
using UnityEngine;
using RPGCharacterStats;

namespace RPGCharacterStats.EditorTools
{
    public static class CharacterServerAccess
    {
        public static CharacterBuilderServer Resolve(bool createIfMissing)
        {
            CharacterBuilderServer server = CharacterBuilderServer.Instance;
            if (server != null) return AttachFactories(server);

#if UNITY_2022_2_OR_NEWER
            server = Object.FindFirstObjectByType<CharacterBuilderServer>();
#else
            // The newer API is not available in the Unity 2019.4+ versions
            // supported by myFSM; this fallback keeps the editor utility
            // compilable there (there should be only one server in a scene).
            server = Object.FindObjectOfType<CharacterBuilderServer>();
#endif
            if (server != null) return AttachFactories(server);

            if (!createIfMissing) return null;
            GameObject go = new GameObject("CharacterBuilderServer");
            Undo.RegisterCreatedObjectUndo(go, "Create CharacterBuilderServer");
            return AttachFactories(go.AddComponent<CharacterBuilderServer>());
        }

        public static void EnsureFactories(CharacterBuilderServer server)
        {
            AttachFactories(server);
        }

        private static CharacterBuilderServer AttachFactories(CharacterBuilderServer server)
        {
            if (server != null && !server.EditorFactoriesAttached)
                server.AttachEditorFactories(new StatsFactory(), new CharacterFactory());
            return server;
        }
    }

    /// <summary>Editor builder windows are products held by their owning
    /// CharacterBuilderServer. The owner reference is transient and is rebound
    /// whenever a factory focuses an existing window.</summary>
    public abstract class ServerOwnedBuilderWindow : EditorWindow, IEditorBuilderProduct
    {
        [System.NonSerialized] private CharacterBuilderServer _ownerServer;

        internal void BindOwner(CharacterBuilderServer server)
        {
            _ownerServer = server;
        }

        protected CharacterBuilderServer OwnerServer
        {
            get
            {
                if (_ownerServer == null) _ownerServer = CharacterServerAccess.Resolve(true);
                return _ownerServer;
            }
        }

        public void Activate()
        {
            Show();
            Focus();
        }
    }

    /// <summary>Factory family for the two stats-build pipelines.
    /// CharacterStatsBuilder and GameplayStatsBuilder are its products.</summary>
    internal sealed class StatsFactory : IEditorBuilderFactory
    {
        public IEditorBuilderProduct CreateBuilder(string builderId, CharacterBuilderServer owner)
        {
            if (builderId == "character-stats")
                return CharacterStatsBuilderWindow.CreateFromFactory(owner);
            if (builderId == "gameplay-stats")
                return GameplayStatsBuilderWindow.CreateFromFactory(owner);
            Debug.LogError("[RPGStats] StatsFactory does not recognize builder \"" + builderId + "\"");
            return null;
        }
    }

    /// <summary>Factory for the definition/character builder window.</summary>
    internal sealed class CharacterFactory : IEditorBuilderFactory
    {
        public IEditorBuilderProduct CreateBuilder(string builderId, CharacterBuilderServer owner)
        {
            if (builderId == "character")
                return CharacterBuilderWindow.CreateFromFactory(owner);
            Debug.LogError("[RPGStats] CharacterFactory does not recognize builder \"" + builderId + "\"");
            return null;
        }
    }

    /// <summary>Runs editor commands invoked from an IMGUI callback without
    /// allowing an I/O, spawn, or asset error to unwind through open layout
    /// groups. The exception is still logged with its full stack trace.</summary>
    internal static class EditorActionGuard
    {
        public static void Run(string action, System.Action callback)
        {
            if (callback == null) return;
            try
            {
                callback();
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog(action, ex.Message, "OK");
            }
        }
    }
}
