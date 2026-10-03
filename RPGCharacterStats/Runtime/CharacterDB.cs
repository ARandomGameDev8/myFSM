// RPG Character & Stats System — the persistent character database.
//
// A REAL database: creating a character saves it permanently, and the record
// survives anything the CODE goes through — folder re-copies, script
// re-imports, refactors, renames — with no manual "repair" step, ever.
//
//   Assets/Resources/CharacterDB/<safe-id>.json     ← one record per character
//
// WHY JSON RECORDS AND NOT .asset SCRIPTABLEOBJECTS: a .asset points at its
// script class by .meta GUID. Unity mints those GUIDs at import time, so
// deleting the package folder and copying it back gives every script a NEW
// GUID — and every record loses its class link ("the associated script can
// not be loaded", fields LOOK deleted). A database row must not care what
// happened to the code that reads it: these records are plain text holding
// the DATA plus the definition class NAME, so no .meta file can ever break
// them again. They are also diffable and hand-readable.
//
//   { "formatVersion": 1,
//     "definitionType": "RPGCharacterStats.PlayerCharacterDefinition",
//     "kind": 0,                       ← redundant, powers the fallback below
//     "data": { ...the definition's fields... } }
//
// If a definition class is ever genuinely deleted, the record still loads on
// the closest available class (Player vs NPC by `kind`) and keeps every field
// it can hold — the data outlives the code.
//
// SELF-HEALING (editor): the first DB touch of a session migrates any legacy
// .asset rows to JSON. A row whose script link already broke is re-linked IN
// PLACE first (we know which class it is from its serialized field layout),
// then migrated. The user runs nothing; there is nothing to repair by hand.
//
// The registry (the server's LRU cache) sits in front of this; the server
// owns every read and write (single ownership). Reads work in the editor AND
// in builds (.json imports as a TextAsset, loaded through Resources).

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace RPGCharacterStats
{
    public static class CharacterDB
    {
        public const string EditorFolder = "Assets/Resources/CharacterDB";
        public const string ResourceRoot = "CharacterDB";

        private const int FormatVersion = 1;

        /// <summary>The on-disk record: which class wrote it, plus its data.
        /// Public + [Serializable] because JsonUtility needs both.</summary>
        [Serializable]
        public class Record
        {
            public int formatVersion;
            public string definitionType;   // FullName of the definition subclass
            public CharacterKind kind;      // redundant copy for the class-missing fallback
            public string data;             // the definition itself, as JSON
        }

        // ------------------------------------------------------------------
        // Write (editor only — builds read, they never write)
        // ------------------------------------------------------------------

        /// <summary>Persist a definition as its own JSON record, replacing any
        /// earlier row with the same ID (one row per character, permanent
        /// until overwritten by another save of the same ID).</summary>
        public static bool Save(CharacterDefinition def)
        {
#if UNITY_EDITOR
            if (def == null) return false;
            EnsureFolder("Assets", "Resources");
            EnsureFolder("Assets/Resources", "CharacterDB");

            string id = SafeAssetName(def.characterID);
            string path = EditorFolder + "/" + id + ".json";
            File.WriteAllText(path, EncodeRecord(def));

            DropLegacyAssetRow(id);   // one row per ID: the JSON replaced it

            UnityEditor.AssetDatabase.ImportAsset(path);
            UnityEditor.AssetDatabase.SaveAssets();
            return true;
#else
            return false;   // builds read; they never write
#endif
        }

#if UNITY_EDITOR
        private static void EnsureFolder(string parent, string name)
        {
            string path = parent + "/" + name;
            if (!UnityEditor.AssetDatabase.IsValidFolder(path))
                UnityEditor.AssetDatabase.CreateFolder(parent, name);
        }

        private static void DropLegacyAssetRow(string sanitizedId)
        {
            string legacy = EditorFolder + "/" + sanitizedId + ".asset";
            if (!System.IO.File.Exists(legacy)) return;
            UnityEditor.AssetDatabase.DeleteAsset(legacy);
        }
#endif

        // ------------------------------------------------------------------
        // Reads (editor AND builds)
        // ------------------------------------------------------------------

        /// <summary>The cache-miss read: load one record by character ID
        /// (record filename = sanitized ID).</summary>
        public static CharacterDefinition LoadById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            EnsureHealed();
            TextAsset record = Resources.Load<TextAsset>(ResourceRoot + "/" + SafeAssetName(id));
            return record == null ? null : DecodeRecord(record.text);
        }

        /// <summary>Every record in the database (cold-cache path).</summary>
        public static List<CharacterDefinition> LoadAll()
        {
            EnsureHealed();
            List<CharacterDefinition> all = new List<CharacterDefinition>();
            TextAsset[] loaded = Resources.LoadAll<TextAsset>(ResourceRoot);
            for (int i = 0; i < loaded.Length; i++)
            {
                if (loaded[i] == null) continue;
                CharacterDefinition def = DecodeRecord(loaded[i].text);
                if (def != null) all.Add(def);
            }
            return all;
        }

        // ------------------------------------------------------------------
        // The record format — pure functions, exercised headless by the tests
        // ------------------------------------------------------------------

        /// <summary>Serialize a definition into a self-describing record.</summary>
        public static string EncodeRecord(CharacterDefinition def)
        {
            if (def == null) return null;
            Record record = new Record();
            record.formatVersion = FormatVersion;
            record.definitionType = def.GetType().FullName;
            record.kind = def.kind;
            record.data = JsonUtility.ToJson(def);
            return JsonUtility.ToJson(record);
        }

        /// <summary>Rebuild a definition from a record. The saved class name is
        /// preferred; if that class is gone (deleted script, renamed package),
        /// the record still loads on the closest concrete class by `kind` and
        /// keeps every field it can hold — a row never fails to open.</summary>
        public static CharacterDefinition DecodeRecord(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            Record record;
            try { record = JsonUtility.FromJson<Record>(json); }
            catch (Exception ex)
            {
                Debug.LogWarning("[RPGStats] unreadable character record skipped: " + ex.Message);
                return null;
            }
            if (record == null || string.IsNullOrEmpty(record.data)) return null;

            Type type = ResolveType(record.definitionType);
            if (type == null || type.IsAbstract || !typeof(CharacterDefinition).IsAssignableFrom(type))
            {
                Debug.LogWarning("[RPGStats] character record's class \"" + record.definitionType +
                                 "\" is unavailable — loading on the fallback for kind " + record.kind);
                type = FallbackType(record.kind);
            }

            CharacterDefinition def;
            try { def = (CharacterDefinition)ScriptableObject.CreateInstance(type); }
            catch (Exception ex)
            {
                Debug.LogWarning("[RPGStats] could not construct " + type.Name + ": " + ex.Message);
                return null;
            }
            JsonUtility.FromJsonOverwrite(record.data, def);
            return def;
        }

        private static Type ResolveType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            Type type = Type.GetType(fullName, false);
            if (type != null) return type;
            // Plain full name: find it in any loaded assembly (the package
            // normally compiles into Assembly-CSharp).
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type found = assemblies[i].GetType(fullName, false);
                if (found != null) return found;
            }
            return null;
        }

        private static Type FallbackType(CharacterKind kind)
        {
            return kind == CharacterKind.Player
                ? typeof(PlayerCharacterDefinition)
                : typeof(EnemyCharacterDefinition);
        }

        // ------------------------------------------------------------------
        // Asset filename from a character ID: filesystem-invalid characters
        // become '_', whitespace is trimmed, empty → NewCharacter. The
        // Character Builder sanitizes through this same function, so the ID it
        // validates is the record filename the DB stores.
        // ------------------------------------------------------------------
        public static string SafeAssetName(string name)
        {
            string trimmed = (name ?? "").Trim();
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                trimmed = trimmed.Replace(c, '_');
            return string.IsNullOrWhiteSpace(trimmed) ? "NewCharacter" : trimmed;
        }

        // ------------------------------------------------------------------
        // Self-heal (editor only): legacy .asset rows become JSON records on
        // the first DB touch of a session — including rows whose script link
        // broke when the package folder was deleted and re-copied. No dialog,
        // no manual repair step; the console tells the story afterwards.
        // ------------------------------------------------------------------
#if UNITY_EDITOR
        private static bool _healDone;

        /// <summary>Force the heal pass right now (the RPG menu calls this);
        /// returns the number of rows in the database afterwards.</summary>
        public static int HealNow()
        {
            _healDone = false;
            return Healed();
        }

        private static int Healed()
        {
            EnsureHealed();
            return LoadAll().Count;
        }

        private static void EnsureHealed()
        {
            if (_healDone) return;
            _healDone = true;
            if (!UnityEditor.AssetDatabase.IsValidFolder(EditorFolder)) return;

            try
            {
                string[] assets = System.IO.Directory.GetFiles(EditorFolder, "*.asset");
                if (assets.Length == 0) return;

                HashSet<string> known = KnownDefinitionGuids();
                int migrated = 0;
                for (int i = 0; i < assets.Length; i++)
                {
                    string file = assets[i].Replace('\\', '/');
                    string guid = ScriptGuidOf(file);
                    if (guid == null) continue;                      // not a definition asset

                    if (!known.Contains(guid))                       // broken script link → relink in place
                    {
                        if (!RelinkBrokenScript(file))
                        {
                            Debug.LogWarning("[RPGStats] " + file +
                                " has an unknown layout — left untouched, not a character row");
                            continue;
                        }
                        UnityEditor.AssetDatabase.ImportAsset(file);
                    }

                    CharacterDefinition def =
                        UnityEditor.AssetDatabase.LoadAssetAtPath<CharacterDefinition>(file);
                    if (def == null)
                    {
                        Debug.LogWarning("[RPGStats] could not read " + file + " — not migrated");
                        continue;
                    }

                    Save(def);                                       // writes <id>.json, drops same-ID .asset
                    if (System.IO.File.Exists(file))
                        UnityEditor.AssetDatabase.DeleteAsset(file); // row renamed since the old save
                    migrated++;
                    Debug.Log("[RPGStats] migrated character \"" + def.characterName +
                              "\" (" + def.characterID + ") to a JSON record — permanent, GUID-proof");
                }
                if (migrated > 0)
                {
                    UnityEditor.AssetDatabase.Refresh();
                    Debug.Log("[RPGStats] CharacterDB: " + migrated +
                              " legacy .asset row(s) migrated to JSON records");
                }
            }
            catch (Exception ex)
            {
                _healDone = false;   // Unity was mid-import or the folder was busy — retry next touch
                Debug.LogWarning("[RPGStats] CharacterDB heal postponed: " + ex.Message);
            }
        }

        /// <summary>Current script GUID per concrete CharacterDefinition
        /// subclass — a row is healthy iff its guid is in this set.</summary>
        private static HashSet<string> KnownDefinitionGuids()
        {
            HashSet<string> guids = new HashSet<string>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int a = 0; a < assemblies.Length; a++)
            {
                Type[] types;
                try { types = assemblies[a].GetTypes(); }
                catch { continue; }   // assembly with unloadable types

                for (int t = 0; t < types.Length; t++)
                {
                    Type type = types[t];
                    if (type.IsAbstract || !type.IsSubclassOf(typeof(CharacterDefinition))) continue;
                    string guid = ScriptGuidOfType(type);
                    if (guid != null) guids.Add(guid);
                }
            }
            return guids;
        }

        private static string ScriptGuidOf(string file)
        {
            // The legacy format stored the script GUID literally in the asset's
            // m_Script line; a stale/removed script still leaves that line
            // behind with a GUID that no longer resolves. Read it so the
            // heal pass can tell a broken row from a row of a different
            // (still-healthy) script class.
            System.Text.RegularExpressions.Match match =
                System.Text.RegularExpressions.Regex.Match(
                    System.IO.File.ReadAllText(file),
                    @"m_Script:\s*\{[^}]*guid:\s*([0-9a-fA-F]{32})");
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }

        /// <summary>Re-link a row whose m_Script guid went stale: identify the
        /// class from its serialized field layout (PlayerCharacterDefinition
        /// serializes "inputAxisPrefix"; NPC definitions serialize
        /// "aiInstanceType"; Enemy and Friendly serialize identically —
        /// default to Enemy), then rewrite the guid to the current one.
        /// Purely mechanical, runs without any user interaction.</summary>
        private static bool RelinkBrokenScript(string file)
        {
            string yaml = System.IO.File.ReadAllText(file);

            bool hasPlayerField = System.Text.RegularExpressions.Regex.IsMatch(
                yaml, @"^\s*inputAxisPrefix:", System.Text.RegularExpressions.RegexOptions.Multiline);
            bool hasNpcField = System.Text.RegularExpressions.Regex.IsMatch(
                yaml, @"^\s*aiInstanceType:", System.Text.RegularExpressions.RegexOptions.Multiline);

            Type type = null;
            if (hasPlayerField)
            {
                type = ConcreteDefinition(t => t.Name == "PlayerCharacterDefinition")
                    ?? ConcreteDefinition(t => typeof(PlayerCharacterDefinition).IsAssignableFrom(t));
            }
            else if (hasNpcField)
            {
                type = ConcreteDefinition(t => t.Name == "EnemyCharacterDefinition")
                    ?? ConcreteDefinition(t => typeof(EnemyCharacterDefinition).IsAssignableFrom(t))
                    ?? ConcreteDefinition(t => typeof(FriendlyNPCDefinition).IsAssignableFrom(t));
            }
            if (type == null) return false;

            string newGuid = ScriptGuidOfType(type);
            if (string.IsNullOrEmpty(newGuid)) return false;

            string replaced = System.Text.RegularExpressions.Regex.Replace(
                yaml,
                @"(m_Script:\s*\{[^}]*guid:\s*)([0-9a-fA-F]{32})",
                "$1" + newGuid);
            if (replaced == yaml) return false;

            System.IO.File.WriteAllText(file, replaced);
            return true;
        }

        private static Type ConcreteDefinition(Func<Type, bool> predicate)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int a = 0; a < assemblies.Length; a++)
            {
                Type[] types;
                try { types = assemblies[a].GetTypes(); }
                catch { continue; }

                for (int t = 0; t < types.Length; t++)
                {
                    Type type = types[t];
                    if (type.IsAbstract || !type.IsSubclassOf(typeof(CharacterDefinition))) continue;
                    if (predicate(type)) return type;
                }
            }
            return null;
        }
#else
        /// <summary>Builds: nothing to migrate — JSON records are the only rows
        /// a build ever sees.</summary>
        private static void EnsureHealed() { }
#endif
    }
}
