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
            if (server != null) return server;

            server = Object.FindFirstObjectByType<CharacterBuilderServer>();
            if (server != null) return server;

            if (!createIfMissing) return null;
            GameObject go = new GameObject("CharacterBuilderServer");
            Undo.RegisterCreatedObjectUndo(go, "Create CharacterBuilderServer");
            return go.AddComponent<CharacterBuilderServer>();
        }
    }
}
