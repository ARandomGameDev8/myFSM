// RPG Character & Stats System — custom inspector for CharacterBuilderServer.
//
// The server is a pure API facade (Spawn/Despawn/Get*), so its default
// inspector draws nothing. This editor turns the component into the in-scene
// control panel: buttons that open the four RPG tool windows (EditorWindows
// can never be components — this is how you reach them from the scene view),
// plus quick spawn/despawn controls through the same public API the windows
// use.

using UnityEditor;
using UnityEngine;
using RPGCharacterStats;

namespace RPGCharacterStats.EditorTools
{
    [CustomEditor(typeof(CharacterBuilderServer))]
    public class CharacterBuilderServerInspector : Editor
    {
        private CharacterDefinition _toSpawn;
        private string _spawnByName = "";
        private bool _showSpawned = true;

        public override void OnInspectorGUI()
        {
            CharacterBuilderServer server = (CharacterBuilderServer)target;

            EditorGUILayout.HelpBox(
                "The RPG tools are editor windows, not components — open them " +
                "here or from the RPG menu. Spawn works in edit mode too: the " +
                "rig is attached immediately, AI boots when you press Play.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Tool windows", EditorStyles.boldLabel);
            if (GUILayout.Button("Character Stats Builder   (.charstat schema)"))
                CharacterStatsBuilderWindow.Open();
            if (GUILayout.Button("Gameplay Stats Builder   (.gameplaystat formulas)"))
                GameplayStatsBuilderWindow.Open();
            if (GUILayout.Button("Character Builder   (definition assets)"))
                CharacterBuilderWindow.Open();
            if (GUILayout.Button("Character Registry   (browse / spawn)"))
                RegistryWindow.Open();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Quick spawn", EditorStyles.boldLabel);

            _toSpawn = (CharacterDefinition)EditorGUILayout.ObjectField(
                "Definition", _toSpawn, typeof(CharacterDefinition), false);
            if (GUILayout.Button("Spawn selected definition"))
                SpawnFromInspector(server, _toSpawn != null ? _toSpawn.characterID : null);

            EditorGUILayout.BeginHorizontal();
            _spawnByName = EditorGUILayout.TextField(_spawnByName);
            if (GUILayout.Button("Spawn by name", GUILayout.Width(110)))
                SpawnFromInspector(server, _spawnByName);
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Load definitions from Assets"))
                RegistryWindow.LoadDefinitions(server);

            DrawSpawned(server);
        }

        private void SpawnFromInspector(CharacterBuilderServer server, string nameOrId)
        {
            if (string.IsNullOrEmpty(nameOrId))
            {
                EditorUtility.DisplayDialog(
                    "Nothing to spawn",
                    "Assign a CharacterDefinition asset or type a definition name first.",
                    "OK");
                return;
            }
            Character spawned = server.Spawn(nameOrId);
            if (spawned != null)
            {
                Selection.activeGameObject = spawned.gameObject;
                EditorGUIUtility.PingObject(spawned.gameObject);
            }
        }

        /// <summary>The live view of what this server has materialized, with
        /// select/despawn per character — the Registry window's spawn state,
        /// readable straight off the component.</summary>
        private void DrawSpawned(CharacterBuilderServer server)
        {
            int spawnedCount = 0;
            for (int i = 0; i < server.registry.entries.Count; i++)
                if (server.registry.entries[i].isSpawned) spawnedCount++;

            EditorGUILayout.Space();
            _showSpawned = EditorGUILayout.Foldout(_showSpawned,
                "Spawned characters (" + spawnedCount + ")");
            if (!_showSpawned) return;

            for (int i = 0; i < server.registry.entries.Count; i++)
            {
                CharacterEntry entry = server.registry.entries[i];
                if (!entry.isSpawned || entry.definition == null || entry.instance == null)
                    continue;

                EditorGUILayout.BeginHorizontal();

                EditorGUILayout.LabelField(
                    entry.definition.characterName + "  →  " + entry.spawnedAs,
                    GUILayout.MinWidth(150));

                if (GUILayout.Button("Select", GUILayout.Width(55)))
                {
                    Selection.activeGameObject = entry.instance.gameObject;
                    EditorGUIUtility.PingObject(entry.instance.gameObject);
                }
                if (GUILayout.Button("Despawn", GUILayout.Width(65)))
                    server.DespawnByTag(entry.spawnedAs ?? entry.tag);

                EditorGUILayout.EndHorizontal();
            }

            if (spawnedCount == 0)
                EditorGUILayout.LabelField("  nothing spawned yet", EditorStyles.miniLabel);
        }
    }
}
