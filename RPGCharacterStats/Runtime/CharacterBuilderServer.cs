// RPG Character & Stats System — CharacterBuilderServer (sections 15/16).
//
// The facade (section 20.5): one singleton that hides registry lookup,
// GameObject creation, the 2D/3D component matrix, stats initialization, and
// AI attachment behind Spawn/Despawn/Get*.
//
// The component matrix is section 2.5, implemented line by line:
//   rigidbody always (3D or 2D); gravity per physics mode; collision always
//   (except a 3D non-physics Player, whose CharacterController IS the
//   collider); kinematic rigidbody only for that same CC case / the 2D
//   kinematic player; Animator always; AIInstance subclass for NPCs.

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace RPGCharacterStats
{
    [ExecuteAlways]
    public class CharacterBuilderServer : MonoBehaviour
    {
        public static CharacterBuilderServer Instance { get; private set; }

        public CharacterRegistry registry = new CharacterRegistry();

        protected virtual void Awake()
        {
            if (Instance != null && Instance != this)
            {
#if UNITY_EDITOR
                // ExecuteAlways runs Awake in edit mode too, where Destroy
                // is illegal — a duplicate is removed immediately instead.
                if (!Application.isPlaying) DestroyImmediate(gameObject);
                else Destroy(gameObject);
#else
                Destroy(gameObject);
#endif
                return;
            }
            Instance = this;
        }

        protected virtual void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        protected virtual void OnEnable()
        {
            // Runs in edit mode (ExecuteAlways) and on every play-mode start.
            // This is what makes the registry survive the Play → Stop cycle:
            // the serialized state comes back with the scene, stale spawn
            // bookkeeping is dropped, and a cold cache re-reads the DB.
            if (registry == null) registry = new CharacterRegistry();
            ClearStaleSpawnBookkeeping();

            // A cache row can outlive its definition reference (an in-memory
            // definition dies on a domain reload; a legacy .asset reference
            // that broke comes back as Unity's fake-null). The DB is the
            // truth: drop the dangling rows, then refill from the records.
            if (registry.HasNullDefinitions()) registry.RemoveNullDefinitions();

            if (registry.entries.Count == 0 && LoadAllDefinitionsFromDb() > 0)
            {
                Debug.Log("[RPGStats] registry cache was cold — reloaded " +
                          registry.entries.Count + " character(s) from the CharacterDB");
            }
        }

        // ------------------------------------------------------------------
        // Public API (section 15)
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        // Registry ownership (the single read/write path: DB + LRU cache)
        // ------------------------------------------------------------------

        /// <summary>The ONLY way a character enters the system: persist it
        /// into the CharacterDB (Assets/Resources/CharacterDB — editor write)
        /// and cache it in the registry. Editor windows never touch the cache
        /// directly; the server owns every read and write.</summary>
        public CharacterEntry SaveDefinition(CharacterDefinition def)
        {
            if (def == null) return null;
            CharacterDB.Save(def);      // editor: asset write; runtime: no-op
            return registry.Put(def);   // insert/update + LRU touch + evict
        }

        /// <summary>Pull every definition in the DB into the registry cache —
        /// the Registry window's reload and the cold-cache path.</summary>
        public int LoadAllDefinitionsFromDb()
        {
            if (registry.HasNullDefinitions()) registry.RemoveNullDefinitions();

            List<CharacterDefinition> all = CharacterDB.LoadAll();
            for (int i = 0; i < all.Count; i++)
                registry.Put(all[i]);
            return all.Count;
        }

        /// <summary>ID uniqueness across cache AND database.</summary>
        public bool IsIdTaken(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (registry.ContainsId(id)) return true;   // cache-only check
            return CharacterDB.LoadById(id) != null;
        }

        /// <summary>Lookup by definition NAME first, then ID (section 15's
        /// Spawn(string name) — the registry indexes both). Cache first; on a
        /// miss the DB is read and the result cached (LRU).</summary>
        private CharacterEntry FindDefinitionEntry(string name)
        {
            CharacterEntry entry = registry.FindByName(name);
            if (entry != null) return entry;

            entry = registry.FindById(name);   // touches; falls back to the DB by ID
            if (entry != null) return entry;

            // Name ≠ ID case: pull the whole DB into the cache once, retry.
            if (LoadAllDefinitionsFromDb() > 0)
                return registry.FindByName(name) ?? registry.FindById(name);
            return null;
        }

        /// <summary>Spawn by definition name or ID — the tag serial is
        /// allocated here ("OrcWarrior_001", then _002, ...).</summary>
        public Character Spawn(string name)
        {
            CharacterEntry entry = FindDefinitionEntry(name);
            if (entry == null)
            {
                Debug.LogError("[RPGStats] no character definition named \"" + name + "\"");
                return null;
            }
            CharacterTag tag = registry.AllocateTag(entry.definition.characterName);
            return Materialize(entry, tag);
        }

        /// <summary>Spawn the entry saved under an exact tag (section 15's
        /// SpawnByTag).</summary>
        public Character SpawnByTag(CharacterTag tag)
        {
            CharacterEntry entry = registry.FindByTag(tag);
            if (entry == null || entry.definition == null)
            {
                Debug.LogError("[RPGStats] no character entry for tag " + tag);
                return null;
            }
            if (entry.isSpawned) return entry.instance;
            return Materialize(entry, entry.tag);
        }

        /// <summary>Despawn every spawned instance of a definition name.</summary>
        public void Despawn(string name)
        {
            for (int i = entriesSafe().Count - 1; i >= 0; i--)
            {
                CharacterEntry e = entriesSafe()[i];
                if (e.isSpawned && e.definition != null &&
                    (e.definition.characterName == name || e.definition.characterID == name))
                {
                    DespawnInternal(e);
                }
            }
        }

        public void DespawnByTag(CharacterTag tag)
        {
            CharacterEntry entry = registry.FindByTag(tag);
            if (entry != null) DespawnInternal(entry);
        }

        public List<Character> GetAll(string name)
        {
            List<Character> result = new List<Character>();
            for (int i = 0; i < registry.entries.Count; i++)
            {
                CharacterEntry e = registry.entries[i];
                if (e.isSpawned && e.instance != null && e.definition != null &&
                    (e.definition.characterName == name || e.definition.characterID == name))
                {
                    result.Add(e.instance);
                }
            }
            return result;
        }

        public Character GetByTag(CharacterTag tag)
        {
            CharacterEntry entry = registry.FindByTag(tag);
            return entry != null && entry.isSpawned ? entry.instance : null;
        }

        public bool IsSpawned(CharacterTag tag)
        {
            CharacterEntry entry = registry.FindByTag(tag);
            return entry != null && entry.isSpawned && entry.instance != null;
        }

        // ------------------------------------------------------------------
        // Materialization (section 16, step by step)
        // ------------------------------------------------------------------

        /// <summary>The whole pipeline: create the GameObject, attach the
        /// dimension/kind/physics components, initialize stats, attach AI.
        /// Callers never specify dimension or physics mode — the definition
        /// carries them (section 15).</summary>
        public Character Materialize(CharacterEntry entry, CharacterTag tag)
        {
            CharacterDefinition def = entry.definition;

            GameObject go = new GameObject(tag.ToString());

            // 1) The Character component itself — mapped from the definition's
            //    concrete class ("EnemyCharacterDefinition" → "EnemyCharacter",
            //    with a fallback for custom subclasses).
            Character character = AttachCharacterComponent(go, def);

            // 2) Identity + configuration straight from the definition.
            character.characterID = def.characterID;
            character.characterName = def.characterName;
            character.description = def.description;
            character.dimension = def.dimension;
            character.physicsMode = def.physicsMode;

            // 3) Rigidbody — always, in the character's dimension.
            if (def.dimension == CharacterDimension.TwoD)
            {
                Rigidbody2D body = go.AddComponent<Rigidbody2D>();
                body.gravityScale = def.physicsMode == PhysicsMode.PhysicsBased ? 1f : 0f;
                body.isKinematic = def.kind == CharacterKind.Player
                    && def.physicsMode == PhysicsMode.NonPhysics;
                character.rigidbody2D = body;
            }
            else
            {
                Rigidbody body = go.AddComponent<Rigidbody>();
                body.useGravity = def.physicsMode == PhysicsMode.PhysicsBased;
                // 3D non-physics Player: the rigidbody is kinematic so it
                // never fights the CharacterController (section 2.5 note).
                body.isKinematic = def.kind == CharacterKind.Player
                    && def.physicsMode == PhysicsMode.NonPhysics;
                character.rigidbody3D = body;
            }

            // 4) Collision — always for Player and NPC, except the 3D
            //    non-physics Player: the CharacterController is a collider.
            bool controllerCase = def.dimension == CharacterDimension.ThreeD
                && def.kind == CharacterKind.Player
                && def.physicsMode == PhysicsMode.NonPhysics;

            if (def.dimension == CharacterDimension.TwoD)
            {
                character.collider2D = go.AddComponent<CapsuleCollider2D>();
            }
            else if (!controllerCase)
            {
                character.collider3D = go.AddComponent<CapsuleCollider>();
            }

            // 5) CharacterController — 3D non-physics Player only.
            if (controllerCase)
            {
                character.controller = go.AddComponent<CharacterController>();
            }

            // 6) Animator — always.
            character.animator = go.AddComponent<Animator>();

            // 7) Stat bar before stats initialize, so the hookup sees it.
            character.healthBar = go.AddComponent<HealthBar>();

            // 8) CharacterStats + GameplayStats + blackboard sync (section 19).
            character.InitializeStats(def.charStatText, def.statValues, def.gameplayStatText);

            // 9) AI for NPCs (section 16: "Attach AIInstance if NPC").
            if (def.Kind == CharacterKind.NPC)
            {
                AttachAI(character, def);
            }

            // 10) Movement strategy per the 2.5 matrix.
            character.gameObject.name = go.name;
            AttachMovement(character, def);

            // 11) Mark spawned (the pipeline's last step).
            registry.MarkSpawned(entry, tag, character);
            return character;
        }

        /// <summary>Pick the concrete Character MonoBehaviour for a definition:
        /// "XDefinition" names an "X" character class by convention (Enemy →
        /// EnemyCharacter, FriendlyNPC → FriendlyNPC, Player → PlayerCharacter),
        /// with a Player/NPC fallback when no custom class exists.</summary>
        private Character AttachCharacterComponent(GameObject go, CharacterDefinition def)
        {
            string defTypeName = def.GetType().Name;
            Character character = null;

            if (defTypeName.EndsWith("Definition"))
            {
                string wanted = defTypeName.Substring(0, defTypeName.Length - "Definition".Length);
                character = FindCharacterClassByName(go, wanted);
            }

            if (character == null)
            {
                // Convention misses (custom definition names): fall back by kind.
                character = def.Kind == CharacterKind.Player
                    ? go.AddComponent<PlayerCharacter>()
                    : (Character)go.AddComponent<EnemyCharacter>();
            }
            return character;
        }

        private Character FindCharacterClassByName(GameObject go, string wanted)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int a = 0; a < assemblies.Length; a++)
            {
                Type[] types;
                try { types = assemblies[a].GetTypes(); }
                catch { continue; } // assembly with unloadable types

                for (int t = 0; t < types.Length; t++)
                {
                    Type candidate = types[t];
                    if (candidate.IsAbstract || !candidate.IsSubclassOf(typeof(Character))) continue;
                    if (candidate.Name != wanted && candidate.Name != wanted + "Character") continue;
                    if (!candidate.IsSubclassOf(typeof(Component))) continue;
                    return go.AddComponent(candidate) as Character;
                }
            }
            return null;
        }

        /// <summary>Resolve, validate, and attach the AIInstance subclass for
        /// an NPC definition. A failed contract logs an error and continues —
        /// the character spawns, just without AI (build-time validation
        /// should have blocked the definition first, section 7.5).</summary>
        private void AttachAI(Character character, CharacterDefinition def)
        {
            NPCCharacterDefinition npcDef = def as NPCCharacterDefinition;
            if (npcDef == null || npcDef.aiInstanceType == null ||
                string.IsNullOrEmpty(npcDef.aiInstanceType.typeName))
            {
                Debug.LogWarning("[RPGStats] NPC \"" + def.characterName + "\" has no AI script assigned");
                return;
            }

            Type aiType = npcDef.aiInstanceType.Resolve();
            string problem = AIInstanceContract.Validate(aiType, null); // runtime: derivation only
            if (problem != null)
            {
                Debug.LogError("[RPGStats] AI script for \"" + def.characterName + "\": " + problem);
                return;
            }

            Component ai = character.gameObject.AddComponent(aiType);
            character.attachedAI = ai;
            NPCCharacter npc = character as NPCCharacter;
            if (npc != null) npc.aiInstance = ai;
        }

        /// <summary>The "Movement" column of the section 2.5 matrix.</summary>
        private static void AttachMovement(Character character, CharacterDefinition def)
        {
            PlayerCharacter player = character as PlayerCharacter;
            if (player != null)
            {
                if (def.physicsMode == PhysicsMode.PhysicsBased)
                {
                    player.movement = new PhysicsPlayerMovement();
                }
                else if (def.dimension == CharacterDimension.ThreeD)
                {
                    // The section 2.5 CharacterController case: attach the
                    // user-facing PlayerController (WASD + Space + gravity,
                    // no camera). It is the single mover — the internal
                    // CharacterControllerMovement strategy stays unset so
                    // nothing ever double-applies motion to the capsule.
                    player.gameObject.AddComponent<PlayerController>();
                    player.movement = null;
                }
                else
                {
                    player.movement = new Kinematic2DMovement();
                }
                return;
            }

            NPCCharacter npc = character as NPCCharacter;
            if (npc != null) npc.movement = new NpcMovement();
        }

        private void DespawnInternal(CharacterEntry entry)
        {
            if (entry.instance != null)
            {
                Destroy(entry.instance.gameObject);
            }
            entry.instance = null;
            entry.isSpawned = false;
            entry.spawnedAs = null;
        }

        private List<CharacterEntry> entriesSafe()
        {
            return registry.entries;
        }

        /// <summary>Editor/test convenience: clears ALL spawn bookkeeping
        /// without dropping the cached definitions.</summary>
        public void ClearSpawnBookkeeping()
        {
            for (int i = 0; i < registry.entries.Count; i++)
            {
                registry.entries[i].instance = null;
                registry.entries[i].isSpawned = false;
                registry.entries[i].spawnedAs = null;
            }
        }

        /// <summary>A play-mode spawn dies with play mode, but the serialized
        /// entry it left behind would keep claiming isSpawned forever. Drop
        /// bookkeeping whose instance no longer exists — edit-mode-spawned
        /// characters keep theirs (their GameObjects are real scene objects).
        /// The Unity-side `== null` catches Unity's fake-null for destroyed
        /// components; the headless sandbox uses plain references.</summary>
        private void ClearStaleSpawnBookkeeping()
        {
            for (int i = 0; i < registry.entries.Count; i++)
            {
                CharacterEntry e = registry.entries[i];
                if (!e.isSpawned) continue;
                if (e.instance == null)
                {
                    e.instance = null;
                    e.isSpawned = false;
                    e.spawnedAs = null;
                }
            }
        }
    }
}
