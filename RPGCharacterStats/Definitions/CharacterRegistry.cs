// RPG Character & Stats System — the registry: an LRU CACHE in front of the
// persistent CharacterDB (sections 14/17).
//
// The CharacterBuilderServer owns every read and write (single ownership).
// The cache holds the small set of most-frequently-accessed characters:
//   • Put / Add        → insert-or-update, touch, enforce capacity (LRU evict)
//   • FindById         → cache first; on a miss the DB is read and cached
//   • FindByTag / Name → cache lookups that touch (keep hot entries hot)
//   • Search           → cache-only, deliberately does NOT touch (browsing a
//                        long list must not rewrite the recency order)
// Eviction never drops a materialized (spawned) entry — those are pinned;
// if everything is pinned the cache overflows softly until one despawns.
// The DB keeps every character forever — eviction only un-caches it.

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

    /// <summary>The LRU character cache the server keeps in front of the
    /// CharacterDB (section 17's searchable library).</summary>
    public class CharacterRegistry
    {
        /// <summary>Max cached entries. Spawned entries are pinned and never
        /// evicted; 0 or less means unlimited.</summary>
        public int capacity = 32;

        public readonly List<CharacterEntry> entries = new List<CharacterEntry>();

        /// <summary>Recency order, front = least recently used. Mirrors the
        /// cached subset of <see cref="entries"/>.</summary>
        private readonly List<CharacterEntry> _lru = new List<CharacterEntry>();

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
            Touch(entry);
            Evict(entry);
            return entry;
        }

        /// <summary>Insert-or-update by ID — the DB write-through lands here.
        /// Re-saving a character updates its row instead of duplicating it.
        /// </summary>
        public CharacterEntry Put(CharacterDefinition definition)
        {
            if (definition == null) return null;
            CharacterEntry existing = FindInCache(definition.characterID);
            if (existing != null)
            {
                existing.definition = definition;
                Touch(existing);
                return existing;
            }
            return Add(definition);
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
                    Touch(entries[i]);
                    return entries[i];
                }
            }
            return null;
        }

        /// <summary>Definition ID must be unique in the registry (section 7.5).
        /// Cache first; on a miss the persistent DB is read and the result is
        /// cached (the LRU miss path).</summary>
        public CharacterEntry FindById(string characterID)
        {
            CharacterEntry entry = FindInCache(characterID);
            if (entry != null)
            {
                Touch(entry);
                return entry;
            }

            CharacterDefinition fromDb = CharacterDB.LoadById(characterID);
            if (fromDb == null) return null;
            return Put(fromDb);
        }

        /// <summary>Cache lookup by definition name (Spawn(string name)'s
        /// first stop). Cache-only: the server orchestrates the DB fallback.
        /// </summary>
        public CharacterEntry FindByName(string name)
        {
            if (name == null) return null;
            for (int i = 0; i < entries.Count; i++)
            {
                CharacterDefinition d = entries[i].definition;
                if (d != null && d.characterName == name)
                {
                    Touch(entries[i]);
                    return entries[i];
                }
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

        /// <summary>Cache-only membership check — never touches the DB and
        /// never refreshes recency (validation loops call this).</summary>
        public bool ContainsId(string characterID)
        {
            return FindInCache(characterID) != null;
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

        // ---- LRU internals ----

        /// <summary>Cache-only lookup: no recency refresh, no DB fallback.
        /// </summary>
        private CharacterEntry FindInCache(string characterID)
        {
            if (characterID == null) return null;
            for (int i = 0; i < entries.Count; i++)
            {
                CharacterDefinition d = entries[i].definition;
                if (d != null && d.characterID == characterID) return entries[i];
            }
            return null;
        }

        private void Touch(CharacterEntry entry)
        {
            _lru.Remove(entry);
            _lru.Add(entry);   // most recently used at the back
        }

        /// <summary>Enforce capacity: drop least-recently-used entries that
        /// are not currently materialized. The freshly-inserted entry is
        /// never its own eviction victim — if the only candidates are pinned
        /// entries and the insert itself, the cache overflows softly (the
        /// alternative would evict a character the very next line spawns).
        /// The DB keeps the data — eviction only un-caches it.</summary>
        private void Evict(CharacterEntry keep)
        {
            if (capacity <= 0) return;
            while (entries.Count > capacity)
            {
                CharacterEntry victim = null;
                for (int i = 0; i < _lru.Count; i++)
                {
                    CharacterEntry candidate = _lru[i];
                    if (candidate == keep || candidate.isSpawned) continue;
                    victim = candidate;
                    break;
                }
                if (victim == null) break;   // only pinned/new entries: soft overflow
                _lru.Remove(victim);
                entries.Remove(victim);
            }
        }
    }
}
