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
//
// The whole cache STATE is Unity-serializable (a [Serializable] class of
// serializable fields) so it survives play-mode scene reloads and domain
// reloads: `entries` and the per-name tag serials go through the serializer,
// while the recency list and the serial dictionary are runtime-only and are
// rebuilt lazily from that serialized state on first use.

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
    [Serializable]
    public class CharacterRegistry
    {
        /// <summary>Max cached entries. Spawned entries are pinned and never
        /// evicted; 0 or less means unlimited.</summary>
        public int capacity = 32;

        // NOTE: Unity-serializable state. Public, non-readonly, serializable
        // element types only — this is what makes the registry survive the
        // edit-mode ⇄ play-mode scene reload instead of coming back empty.
        public List<CharacterEntry> entries = new List<CharacterEntry>();

        /// <summary>Serialized next-tag-serial per definition name, so tags
        /// follow the "_001, _002" pattern the doc shows ("OrcWarrior" → 3
        /// means the next tag is _003). One row per name; the runtime
        /// dictionary is rebuilt from these rows after a deserialization.</summary>
        [Serializable]
        public class TagSerial
        {
            public string name;
            public int nextSerial;
        }

        public List<TagSerial> tagSerials = new List<TagSerial>();

        // ---- runtime-only state (rebuilt lazily after a deserialization) ----

        /// <summary>Recency order, front = least recently used. Mirrors the
        /// cached subset of <see cref="entries"/>. Private ⇒ never serialized;
        /// <see cref="Lru"/> rebuilds it from <see cref="entries"/> (insertion
        /// order) the first time it is touched after a reload.</summary>
        [NonSerialized] private List<CharacterEntry> _lru;

        /// <summary>Runtime index over <see cref="tagSerials"/>; rebuilt
        /// lazily. Private ⇒ never serialized.</summary>
        [NonSerialized] private Dictionary<string, int> _nextSerial;

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
            Dictionary<string, int> serials = NextSerials;
            int serial;
            if (!serials.TryGetValue(name, out serial))
            {
                serial = 1;
            }
            serials[name] = serial + 1;

            // Keep the serialized rows in sync so the counter survives
            // play-mode reloads and domain reloads (no "_001 again" after).
            TagSerial row = null;
            for (int i = 0; i < tagSerials.Count; i++)
            {
                if (tagSerials[i].name == name) { row = tagSerials[i]; break; }
            }
            if (row == null)
            {
                row = new TagSerial();
                row.name = name;
                tagSerials.Add(row);
            }
            row.nextSerial = serial + 1;

            return new CharacterTag(name, serial);
        }

        /// <summary>Drops the runtime-only state so the next touch rebuilds it
        /// from the serialized fields — exactly what a deserialization does.
        /// The headless tests use this to simulate a Unity scene reload.</summary>
        internal void ForgetRuntimeState()
        {
            _lru = null;
            _nextSerial = null;
        }

        // ---- spawn bookkeeping (section 16's last step) ----

        public void MarkSpawned(CharacterEntry entry, CharacterTag spawnTag, Character instance)
        {
            entry.instance = instance;
            entry.spawnedAs = spawnTag;
            entry.isSpawned = instance != null;
        }

        // ---- LRU internals ----

        /// <summary>Recency list accessor: lazily rebuilt from the serialized
        /// <see cref="entries"/> whenever the runtime state is missing (right
        /// after a deserialization).</summary>
        private List<CharacterEntry> Lru
        {
            get
            {
                if (_lru == null)
                {
                    _lru = new List<CharacterEntry>(entries.Count);
                    for (int i = 0; i < entries.Count; i++) _lru.Add(entries[i]);
                }
                return _lru;
            }
        }

        /// <summary>Serial dictionary accessor: lazily rebuilt from the
        /// serialized <see cref="tagSerials"/> rows.</summary>
        private Dictionary<string, int> NextSerials
        {
            get
            {
                if (_nextSerial == null)
                {
                    _nextSerial = new Dictionary<string, int>();
                    for (int i = 0; i < tagSerials.Count; i++)
                        _nextSerial[tagSerials[i].name] = tagSerials[i].nextSerial;
                }
                return _nextSerial;
            }
        }

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
            List<CharacterEntry> lru = Lru;
            lru.Remove(entry);
            lru.Add(entry);   // most recently used at the back
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
            List<CharacterEntry> lru = Lru;
            while (entries.Count > capacity)
            {
                CharacterEntry victim = null;
                for (int i = 0; i < lru.Count; i++)
                {
                    CharacterEntry candidate = lru[i];
                    if (candidate == keep || candidate.isSpawned) continue;
                    victim = candidate;
                    break;
                }
                if (victim == null) break;   // only pinned/new entries: soft overflow
                lru.Remove(victim);
                entries.Remove(victim);
            }
        }
    }
}
