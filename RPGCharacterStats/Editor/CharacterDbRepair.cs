// RPG Character & Stats System — CharacterDB repair (one-time GUID fixer).
//
// The problem this solves: deleting Assets/MyFSM/RPGCharacterStats/ and
// copying the folder back in generates NEW .meta GUIDs for every script.
// Every serialized reference to those scripts then dangles — definition
// .assets under Assets/Resources/CharacterDB, spawned characters, the scene's
// CharacterBuilderServer — and Unity says "The associated script can not be
// loaded", with every field looking empty.
//
// It is NOT empty: the .asset's YAML still holds every field VALUE. Only its
// m_Script line points at a GUID that no longer exists. This menu rewrites
// that line to the CURRENT script GUID, and the data comes straight back.
//
// Spawned scene objects and the server component cannot be re-linked this way
// (a "missing behaviour" slot doesn't record which script it was) — delete
// those and re-spawn / reopen a window after repairing the DB.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace RPGCharacterStats.EditorTools
{
    public static class CharacterDbRepair
    {
        private const string MenuTitle = "Repair CharacterDB Script References";

        [MenuItem("RPG/" + MenuTitle, priority = 20)]
        public static void Repair()
        {
            // 1) Current script GUID per concrete CharacterDefinition subclass.
            //    A definition asset is healthy iff its m_Script guid is in here.
            List<Type> concrete = new List<Type>();
            Dictionary<string, Type> guidToType = new Dictionary<string, Type>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch { continue; }   // assembly with unloadable types

                for (int i = 0; i < types.Length; i++)
                {
                    Type t = types[i];
                    if (t.IsAbstract || !t.IsSubclassOf(typeof(CharacterDefinition))) continue;
                    MonoScript script = MonoScript.FromType(t);
                    if (script == null) continue;
                    string path = AssetDatabase.GetAssetPath(script);
                    if (string.IsNullOrEmpty(path)) continue;
                    string guid = AssetDatabase.AssetPathToGUID(path);
                    if (string.IsNullOrEmpty(guid)) continue;
                    guidToType[guid] = t;
                    concrete.Add(t);
                }
            }

            // 2) Scan the DB folder for rows whose script reference dangles.
            string folder = CharacterDB.EditorFolder;
            if (!AssetDatabase.IsValidFolder(folder))
            {
                EditorUtility.DisplayDialog(MenuTitle,
                    "No " + folder + " folder — nothing to repair.", "OK");
                return;
            }

            List<string> files = new List<string>(Directory.GetFiles(folder, "*.asset"));
            List<string> broken = new List<string>();
            List<string> healthy = new List<string>();
            for (int i = 0; i < files.Count; i++)
            {
                string guid = ScriptGuidOf(files[i]);
                if (guid == null) continue;                       // not a definition asset
                if (guidToType.ContainsKey(guid)) { healthy.Add(files[i]); continue; }
                broken.Add(files[i]);
            }

            if (broken.Count == 0)
            {
                EditorUtility.DisplayDialog(MenuTitle,
                    healthy.Count + " definition row(s) checked — all script references are healthy.\n\n" +
                    "If a row still shows empty fields, its .asset is not in " + folder + ".",
                    "OK");
                return;
            }

            // 3) Re-link each broken row to the definition class its YAML
            //    field layout identifies, then reimport (data reappears).
            int repaired = 0;
            List<string> report = new List<string>();
            for (int i = 0; i < broken.Count; i++)
            {
                string file = broken[i];
                string yaml = File.ReadAllText(file);

                Type type = PickDefinitionType(yaml, concrete);
                if (type == null)
                {
                    report.Add("✗ " + Path.GetFileName(file) +
                               " — unknown field layout, rebuild this one by hand");
                    continue;
                }

                string newGuid;
                if (RewriteScriptGuid(file, type, out newGuid))
                {
                    repaired++;
                    report.Add("✓ " + Path.GetFileName(file) + " → " + type.Name +
                               " (" + YamlField(yaml, "characterName") + ")");
                }
                else
                {
                    report.Add("✗ " + Path.GetFileName(file) + " — could not rewrite (read-only?)");
                }
            }

            AssetDatabase.Refresh();

            Debug.Log("[RPGStats] CharacterDB repair: " + repaired + "/" + broken.Count +
                      " broken row(s) re-linked, " + healthy.Count + " already healthy.\n" +
                      string.Join("\n", report.ToArray()) +
                      "\n\nSpawned scene objects and the CharacterBuilderServer component with " +
                      "\"missing behaviour\" cannot be re-linked — delete and re-spawn / reopen a window.");

            EditorUtility.DisplayDialog(MenuTitle,
                repaired + " of " + broken.Count + " broken row(s) re-linked (" +
                healthy.Count + " were already healthy).\n\n" +
                "Characters with \"missing behaviour\" in the scene must be deleted and re-spawned — " +
                "a missing script slot cannot be identified after the fact.\n\n" +
                "From now on, update the package by copying OVER the old folder (never delete first), " +
                "so the .meta GUIDs survive. See the README's \"Updating this package\" section.",
                "OK");
        }

        /// <summary>The m_Script guid of an .asset file, or null when the file
        /// has no script reference (not a definition asset).</summary>
        private static string ScriptGuidOf(string file)
        {
            Match match = Regex.Match(
                File.ReadAllText(file),
                @"m_Script:\s*\{[^}]*guid:\s*([0-9a-fA-F]{32})");
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }

        /// <summary>Pick the definition class a broken row was saved as, from
        /// its serialized field layout: PlayerCharacterDefinition serializes
        /// "inputAxisPrefix"; NPC definitions serialize "aiInstanceType".
        /// Enemy and Friendly serialize identically — default to Enemy.</summary>
        private static Type PickDefinitionType(string yaml, List<Type> concrete)
        {
            bool hasPlayerField = Regex.IsMatch(yaml, @"^\s*inputAxisPrefix:", RegexOptions.Multiline);
            bool hasNpcField = Regex.IsMatch(yaml, @"^\s*aiInstanceType:", RegexOptions.Multiline);

            if (hasPlayerField)
            {
                Type canonical = concrete.Find(delegate (Type t) { return t.Name == "PlayerCharacterDefinition"; });
                if (canonical != null) return canonical;
                return concrete.Find(delegate (Type t) { return typeof(PlayerCharacterDefinition).IsAssignableFrom(t); });
            }

            if (hasNpcField)
            {
                Type enemy = concrete.Find(delegate (Type t) { return t.Name == "EnemyCharacterDefinition"; });
                if (enemy != null) return enemy;
                return concrete.Find(delegate (Type t) { return typeof(EnemyCharacterDefinition).IsAssignableFrom(t); })
                    ?? concrete.Find(delegate (Type t) { return typeof(FriendlyNPCDefinition).IsAssignableFrom(t); });
            }

            return null;
        }

        /// <summary>Rewrite the broken m_Script guid to the class's current
        /// script guid and reimport the asset.</summary>
        private static bool RewriteScriptGuid(string file, Type type, out string newGuid)
        {
            MonoScript script = MonoScript.FromType(type);
            if (script == null) { newGuid = null; return false; }
            string scriptPath = AssetDatabase.GetAssetPath(script);
            if (string.IsNullOrEmpty(scriptPath)) { newGuid = null; return false; }

            newGuid = AssetDatabase.AssetPathToGUID(scriptPath);
            if (string.IsNullOrEmpty(newGuid)) return false;

            string text = File.ReadAllText(file);
            string replaced = Regex.Replace(
                text,
                @"(m_Script:\s*\{[^}]*guid:\s*)([0-9a-fA-F]{32})",
                "$1" + newGuid);
            if (replaced == text) return false;

            File.WriteAllText(file, replaced);
            AssetDatabase.ImportAsset(file);
            return true;
        }

        /// <summary>Best-effort YAML field read for the report line.</summary>
        private static string YamlField(string yaml, string field)
        {
            Match match = Regex.Match(yaml, @"^\s*" + field + @":\s*(.*)$", RegexOptions.Multiline);
            string value = match.Success ? match.Groups[1].Value.Trim() : "";
            return value.Length == 0 ? "?" : value.Trim('"');
        }
    }
}
