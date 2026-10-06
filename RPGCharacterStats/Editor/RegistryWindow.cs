// RPG Character & Stats System — Registry window (section 17).
//
// The searchable character library: one row per CharacterEntry with its tag,
// kind, dimension, and physics mode, plus Edit (open the definition asset in
// the inspector) and Spawn (materialize through CharacterBuilderServer).
// The search box filters by name/ID the way section 17's "Search: [ Orc ]"
// shows.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using RPGCharacterStats;

namespace RPGCharacterStats.EditorTools
{
    public class RegistryWindow : EditorWindow
    {
        private string _search = "";
        private Vector2 _scroll;

        [MenuItem("RPG/Character Registry", priority = 3)]
        public static void Open()
        {
            GetWindow<RegistryWindow>("Character Registry");
        }

        private void OnEnable()
        {
            // Show the database right away: a cold cache pulls everything once.
            CharacterBuilderServer server = CharacterServerAccess.Resolve(false);
            if (server != null && server.registry.entries.Count == 0)
                server.LoadAllDefinitionsFromDb();
        }

        private CharacterBuilderServer Server
        {
            // One shared resolution for every window: the alive singleton in
            // play mode, else the scene's server, else create exactly one.
            // Creating per access is what spawned duplicate "servers".
            get { return CharacterServerAccess.Resolve(true); }
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
                string search = EditorGUILayout.TextField(_search);
                if (search != _search)
                {
                    _search = search;
                    GUIUtility.ExitGUI();
                }
            }

            CharacterBuilderServer server = Server;
            List<CharacterEntry> hits = server.registry.Search(_search);

            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;
                if (hits.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "No characters. Build one in RPG ▸ Character Builder, or load .charstat/.gameplaystat assets.", MessageType.Info);
                }

                for (int i = 0; i < hits.Count; i++)
                {
                    CharacterEntry entry = hits[i];
                    CharacterDefinition def = entry.definition;
                    if (def == null) continue;

                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField(def.characterName, EditorStyles.boldLabel, GUILayout.Width(140));
                            EditorGUILayout.LabelField(def.Kind == CharacterKind.Player ? "Player" : "NPC", GUILayout.Width(50));
                            EditorGUILayout.LabelField(def.dimension == CharacterDimension.TwoD ? "2D" : "3D", GUILayout.Width(30));
                            EditorGUILayout.LabelField(
                                def.physicsMode == PhysicsMode.PhysicsBased ? "Physics" : "Non-physics", GUILayout.Width(90));

                            if (GUILayout.Button("Edit", GUILayout.Width(60)))
                            {
                                Selection.activeObject = def;
                                EditorGUIUtility.PingObject(def);
                            }

                            if (entry.isSpawned)
                            {
                                if (GUILayout.Button("Despawn", GUILayout.Width(70)))
                                {
                                    EditorActionGuard.Run("Despawn failed", delegate
                                    {
                                        server.DespawnByTag(entry.spawnedAs ?? entry.tag);
                                    });
                                    GUIUtility.ExitGUI();
                                }
                            }
                            else if (GUILayout.Button("Spawn", GUILayout.Width(70)))
                            {
                                EditorActionGuard.Run("Spawn failed", delegate
                                {
                                    server.Spawn(def.characterID);
                                });
                                GUIUtility.ExitGUI();
                            }
                        }

                        EditorGUILayout.LabelField("ID: " + def.characterID +
                            (entry.isSpawned ? "   |   spawned as " + entry.spawnedAs : ""),
                            EditorStyles.miniLabel);
                    }
                }
            }

            if (GUILayout.Button("Reload all from CharacterDB"))
            {
                EditorActionGuard.Run("Reload CharacterDB failed", delegate
                {
                    LoadDefinitions(server);
                });
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>Reload the registry cache from the persistent CharacterDB.
        /// Public so the server's inspector reuses it (same library, two
        /// entry points).</summary>
        public static void LoadDefinitions(CharacterBuilderServer server)
        {
            int loaded = server.LoadAllDefinitionsFromDb();
            Debug.Log("[RPGStats] CharacterDB → " + loaded + " definition(s) cached in the registry");
            if (loaded == 0)
                Debug.LogWarning("[RPGStats] CharacterDB is empty — build a character in RPG ▸ Character Builder first.");
        }
    }
}
