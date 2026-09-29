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

        private CharacterBuilderServer Server
        {
            get
            {
                CharacterBuilderServer server = CharacterBuilderServer.Instance;
                if (server == null)
                {
                    // Convenience for editor-time use: create the service if
                    // a scene doesn't have it yet.
                    GameObject go = new GameObject("CharacterBuilderServer");
                    server = go.AddComponent<CharacterBuilderServer>();
                }
                return server;
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
            _search = EditorGUILayout.TextField(_search);
            EditorGUILayout.EndHorizontal();

            CharacterBuilderServer server = Server;
            List<CharacterEntry> hits = server.registry.Search(_search);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
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

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();

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
                        server.DespawnByTag(entry.spawnedAs ?? entry.tag);
                    }
                }
                else if (GUILayout.Button("Spawn", GUILayout.Width(70)))
                {
                    server.Spawn(def.characterID);
                }

                EditorGUILayout.EndHorizontal();

                EditorGUILayout.LabelField("ID: " + def.characterID +
                    (entry.isSpawned ? "   |   spawned as " + entry.spawnedAs : ""),
                    EditorStyles.miniLabel);

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndScrollView();

            if (GUILayout.Button("Load definitions from Assets"))
            {
                LoadDefinitions(server);
            }
        }

        /// <summary>Pull every saved CharacterDefinition asset into the
        /// registry so spawned characters survive editor restarts by
        /// reloading the same assets.</summary>
        private static void LoadDefinitions(CharacterBuilderServer server)
        {
            string[] guids = AssetDatabase.FindAssets("t:CharacterDefinition");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                CharacterDefinition def = AssetDatabase.LoadAssetAtPath<CharacterDefinition>(path);
                if (def != null && !server.registry.ContainsId(def.characterID))
                {
                    server.registry.Add(def);
                }
            }
            Debug.Log("[RPGStats] registry loaded " + guids.Length + " definition(s)");
        }
    }
}
