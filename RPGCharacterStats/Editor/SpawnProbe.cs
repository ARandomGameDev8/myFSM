// RPG Character & Stats System — Play → Stop survival probe (DIAGNOSTIC ONLY).
//
// Answers one question with evidence instead of theory: does a custom script
// added as a component to a spawned character survive the Play → Stop cycle?
//
// Workflow (in the Unity editor, with a scene open that holds a
// CharacterBuilderServer):
//   1. Menu "RPG/Diagnostics/1. Spawn Probe Character (edit mode)"  — spawns
//      a 3D non-physics player through the real Materialize pipeline, then
//      attaches GuidProbeTag as the ONLY extra custom script and reports the
//      component list.
//   2. Press Play, then Stop.
//   3. Menu "RPG/Diagnostics/2. Re-inspect probe after Play → Stop" — finds
//      the probe and reports which components survived. PASS means every
//      component is a real script; a "missing script" slot shows as a null
//      component inside a non-empty GetComponents<Component>() array.
//   4. Menu "RPG/Diagnostics/3. Audit scene script GUIDs" — dumps every
//      m_Script reference in the open scene and flags the ones whose GUID is
//      defined by NO .cs.meta anywhere under Assets (the missing-script
//      root cause, visible before anything is even spawned).
//
// Remove this file and GuidProbeTag.cs together once the phenomenon is fixed.

using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using RPGCharacterStats;

namespace RPGCharacterStats.EditorTools
{
    public static class SpawnProbe
    {
        private const string ProbeDefId = "probe_player";

        [MenuItem("RPG/Diagnostics/1. Spawn Probe Character (edit mode)")]
        public static void SpawnProbeCharacter()
        {
            CharacterBuilderServer server = CharacterServerAccess.Resolve(true);
            if (server == null) { Debug.LogError("[Probe] no CharacterBuilderServer in this scene."); return; }

            // Build a minimal player definition straight through the single
            // write path (DB + registry cache), exactly like the real windows.
            PlayerCharacterDefinition def = ScriptableObject.CreateInstance<PlayerCharacterDefinition>();
            def.characterID = ProbeDefId;
            def.characterName = "ProbePlayer";
            def.description = "Play→Stop survival probe";
            def.dimension = CharacterDimension.ThreeD;
            def.kind = CharacterKind.Player;
            def.physicsMode = PhysicsMode.NonPhysics;
            def.charStatText =
                "float Vitality: (min: 0, max: 999, default: 10)\n" +
                "int Level:      (min: 1, max: 100, default: 1)\n";
            server.SaveDefinition(def);

            // Fresh definition → ForceSpawn through the Materialize pipeline.
            CharacterEntry entry = server.registry.FindById(ProbeDefId);
            if (entry == null) { Debug.LogError("[Probe] definition did not land in the registry."); return; }
            entry.isSpawned = false;   // force a fresh materialization
            entry.instance = null;

            Character character = server.SpawnByTag(entry.tag);
            if (character == null) { Debug.LogError("[Probe] SpawnByTag returned null."); return; }

            // THE experiment: one extra custom script, attached like any other.
            GuidProbeTag probe = character.gameObject.AddComponent<GuidProbeTag>();
            probe.attachedAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            EditorUtility.SetDirty(probe);
            SaveProbeScene();

            Debug.Log("[Probe] spawned " + character.gameObject.name + " at " +
                probe.attachedAt + " — now press PLAY, then STOP, then run menu 2.\n" +
                "Components at spawn: " + DescribeComponents(character.gameObject));
        }

        [MenuItem("RPG/Diagnostics/2. Re-inspect probe after Play → Stop")]
        public static void ReinspectProbe()
        {
            GameObject probe = GameObject.Find("ProbePlayer_001");
            if (probe == null)
            {
                // Tag serial may have advanced across cycles; fall back to a scan.
                CharacterBuilderServer server = CharacterServerAccess.Resolve(false);
                if (server != null && server.registry != null)
                {
                    for (int i = 0; i < server.registry.entries.Count; i++)
                    {
                        CharacterEntry e = server.registry.entries[i];
                        if (e.isSpawned && e.instance != null &&
                            e.definition != null && e.definition.characterID == ProbeDefId)
                        {
                            probe = e.instance.gameObject;
                            break;
                        }
                    }
                }
            }
            if (probe == null) { Debug.LogError("[Probe] no probe character found — run menu 1 first."); return; }

            // Count null slots: Unity represents a missing script as a null
            // component INSIDE a non-empty GetComponents array.
            Component[] all = probe.GetComponents<Component>();
            int missing = 0;
            StringBuilder missingList = new StringBuilder();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null)
                {
                    missing++;
                    missingList.Append("\n  - slot " + i + ": MISSING SCRIPT");
                }
            }

            GuidProbeTag tag = probe.GetComponent<GuidProbeTag>();
            if (tag != null)
            {
                tag.survivedStops++;
                EditorUtility.SetDirty(tag);
                SaveProbeScene();
            }

            string verdict = missing == 0
                ? "PASS — every component survived, including the custom scripts."
                : "FAIL — " + missing + " component slot(s) lost their script:" + missingList;

            Debug.Log("[Probe] after Play → Stop, " + probe.name + " holds " + all.Length +
                " component slots, " + missing + " missing.\n" + verdict +
                "\nComponents now: " + DescribeComponents(probe));
        }

        [MenuItem("RPG/Diagnostics/3. Audit scene script GUIDs")]
        public static void AuditSceneGuids()
        {
            string scenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(scenePath) || !File.Exists(scenePath))
            {
                Debug.LogError("[Probe] active scene is not saved to disk — save it first.");
                return;
            }

            // 1) every GUID Unity assigns to a script in this project
            HashSet<string> known = new HashSet<string>();
            foreach (string meta in Directory.GetFiles("Assets", "*.cs.meta", SearchOption.AllDirectories))
            {
                foreach (string line in File.ReadAllLines(meta))
                {
                    if (!line.StartsWith("guid: ")) continue;
                    known.Add(line.Substring(6).Trim());
                }
            }

            // 2) every m_Script reference in the scene file
            List<string> referenced = new List<string>();
            foreach (string line in File.ReadAllLines(scenePath))
            {
                const string marker = "m_Script: {fileID: 11500000, guid: ";
                int at = line.IndexOf(marker);
                if (at < 0) continue;
                referenced.Add(line.Substring(at + marker.Length, 32));
            }

            StringBuilder report = new StringBuilder();
            report.Append("[Probe] scene ").Append(scenePath)
                  .Append(": ").Append(referenced.Count).Append(" script reference(s), ")
                  .Append(known.Count).Append(" scripts defined in the project.\n");

            int dead = 0;
            for (int i = 0; i < referenced.Count; i++)
            {
                string guid = referenced[i];
                if (known.Contains(guid)) continue;
                dead++;
                report.Append("  DEAD GUID (no .cs.meta defines it): ").Append(guid).Append('\n');
            }
            report.Append(dead == 0
                ? "No dead GUIDs — every scene script resolves to a real file."
                : dead + " dead GUID(s) — those components ARE the missing scripts. Recreate or re-link them.");

            Debug.Log(report.ToString());
        }

        private static string DescribeComponents(GameObject go)
        {
            Component[] all = go.GetComponents<Component>();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < all.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(all[i] != null ? all[i].GetType().Name : "<MISSING SCRIPT>");
            }
            return sb.ToString();
        }

        private static void SaveProbeScene()
        {
            // Materialize saves before the probe tag is added. Save again so
            // this diagnostic tests script GUID/reload behavior rather than an
            // unsaved Edit Mode change being rolled back by the editor.
            UnityEngine.SceneManagement.Scene scene =
                UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
            UnityEditor.AssetDatabase.SaveAssets();
        }
    }
}
