// RPG Character & Stats System — the persistent character database.
//
// One .asset per CharacterDefinition under Assets/Resources/CharacterDB/.
// The folder lives under Resources/ so reads work in the EDITOR and in
// BUILDS through Resources.Load — the CharacterBuilderServer owns every
// read and write (single ownership); this class is just its storage layer.
//
//   write (editor only):  CharacterDB.Save(def)     → one asset per character
//   read by id:           CharacterDB.LoadById(id)  → O(1) Resources.Load (the
//                           cache-miss path — asset filename = sanitized ID)
//   read everything:      CharacterDB.LoadAll()     → Resources.LoadAll
//
// The registry (the server's LRU cache) sits in front of this; the DB is the
// source of truth that survives domain reloads, scene changes and builds.

using System.Collections.Generic;
using UnityEngine;

namespace RPGCharacterStats
{
    public static class CharacterDB
    {
        public const string EditorFolder = "Assets/Resources/CharacterDB";
        public const string ResourceRoot = "CharacterDB";

        /// <summary>Persist a definition as its own asset, replacing any
        /// earlier row with the same ID (one row per character). Editor-only:
        /// a build never writes. Throws with Unity's message when the write
        /// is refused — the editor windows surface it as a dialog.</summary>
        public static bool Save(CharacterDefinition def)
        {
#if UNITY_EDITOR
            if (def == null) return false;
            EnsureFolder("Assets", "Resources");
            EnsureFolder("Assets/Resources", "CharacterDB");

            string path = EditorFolder + "/" + SafeAssetName(def.characterID) + ".asset";
            UnityEngine.Object existing = UnityEditor.AssetDatabase.LoadMainAssetAtPath(path);
            if (existing != null && existing != def)
                UnityEditor.AssetDatabase.DeleteAsset(path);   // keep one row per ID

            UnityEditor.AssetDatabase.CreateAsset(def, path);
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
#endif

        /// <summary>The cache-miss read: load one definition by character ID
        /// (asset filename = sanitized ID).</summary>
        public static CharacterDefinition LoadById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return Resources.Load<CharacterDefinition>(ResourceRoot + "/" + SafeAssetName(id));
        }

        /// <summary>Every definition in the database (cold-cache path).</summary>
        public static List<CharacterDefinition> LoadAll()
        {
            List<CharacterDefinition> all = new List<CharacterDefinition>();
            CharacterDefinition[] loaded = Resources.LoadAll<CharacterDefinition>(ResourceRoot);
            for (int i = 0; i < loaded.Length; i++)
                if (loaded[i] != null) all.Add(loaded[i]);
            return all;
        }

        /// <summary>Asset filename from a character ID: filesystem-invalid
        /// characters become '_', whitespace is trimmed, empty → NewCharacter.
        /// The Character Builder sanitizes through this same function, so the
        /// ID it validates is the filename the DB stores.</summary>
        public static string SafeAssetName(string name)
        {
            string trimmed = (name ?? "").Trim();
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                trimmed = trimmed.Replace(c, '_');
            return string.IsNullOrWhiteSpace(trimmed) ? "NewCharacter" : trimmed;
        }
    }
}
