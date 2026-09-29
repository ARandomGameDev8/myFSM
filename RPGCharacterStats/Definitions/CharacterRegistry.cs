// RPG Character & Stats System — the Character Registry (section 14).
//
// Stores CharacterEntry records: definition + tag + runtime instance +
// spawned flag. The registry is where the Character Builder saves new
// characters and where CharacterBuilderServer looks them up at spawn time.
// Serial numbers are allocated per name, so spawning three Orc Warriors
// yields OrcWarrior_001, _002, _003 (section 14's example).

using System;
using System.Collections.Generic;

namespace RPGCharacterStats
{
    /// <summary>One registry row: a saved definition, its next-to-use tag, and
    /// the instance currently materialized (or null).</summary>
    [Serializable]
    public class CharacterEntry
    {
        public CharacterTag tag = new CharacterTag();
        public CharacterDefinition definition;
        public Character instance;          // section 14 calls this "instance"
        public bool isSpawned;

        /// <summary>Runtime-only tag assigned at spawn ("OrcWarrior_001").
        /// The design doc keeps the tag ON the entry, not the definition —
        /// the same definition spawns many tagged instances.</summary>
        public CharacterTag spawnedAs;
    }

    /// <summary>The searchable character library (section 17).</summary>
    public class CharacterRegistry
    {
        public readonly List<CharacterEntry> entries = new List<CharacterEntry>();

        /// <summary>Next serial number per definition name, so tags follow the
        /// "_001, _002" pattern the doc shows.</summary>
        private readonly Dictionary<string, int> _nextSerial = new Dictionary<string, int>();

        // ---- add / find ----

        public CharacterEntry Add(CharacterDefinition definition)
        {
            if (definition == null) return null;
            CharacterEntry entry = new CharacterEntry();
            entry.definition = definition;
            entry.tag = new CharacterTag(definition.characterName, 0);
            entries.Add(entry);
            return entry;
        }

        public CharacterEntry FindByTag(CharacterTag tag)
        {
            if (tag == null) return null;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].tag != null &&
                    entries[i].tag.name == tag.name &&
                    entries[i].tag.serialNumber == tag.serialNumber)
                {
                    return entries[i];
                }
            }
            return null;
        }

        /// <summary>Definition ID must be unique in the registry (section 7.5).</summary>
        public CharacterEntry FindById(string characterID)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].definition != null && entries[i].definition.characterID == characterID)
                    return entries[i];
            }
            return null;
        }

        public List<CharacterEntry> Search(string substring)
        {
            List<CharacterEntry> hits = new List<CharacterEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                CharacterDefinition d = entries[i].definition;
                if (d == null) continue;
                if (string.IsNullOrEmpty(substring)
                    || (d.characterName != null && d.characterName.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
                    || (d.characterID != null && d.characterID.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    hits.Add(entries[i]);
                }
            }
            return hits;
        }

        public bool ContainsId(string characterID)
        {
            return FindById(characterID) != null;
        }

        // ---- tag allocation (section 14) ----

        public CharacterTag AllocateTag(string name)
        {
            int serial;
            if (!_nextSerial.TryGetValue(name, out serial))
            {
                serial = 1;
            }
            _nextSerial[name] = serial + 1;
            return new CharacterTag(name, serial);
        }

        // ---- spawn bookkeeping (section 16's last step) ----

        public void MarkSpawned(CharacterEntry entry, CharacterTag spawnTag, Character instance)
        {
            entry.instance = instance;
            entry.spawnedAs = spawnTag;
            entry.isSpawned = instance != null;
        }
    }
}
