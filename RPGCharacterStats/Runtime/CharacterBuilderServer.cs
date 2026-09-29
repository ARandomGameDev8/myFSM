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
    public class CharacterBuilderServer : MonoBehaviour
    {
        public static CharacterBuilderServer Instance { get; private set; }

        public CharacterRegistry registry = new CharacterRegistry();

        protected virtual void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        // ------------------------------------------------------------------
        // Public API (section 15)
        // ------------------------------------------------------------------

        /// <summary>Lookup by definition NAME first, then ID (section 15's
        /// Spawn(string name) — the registry indexes both).</summary>
        private CharacterEntry FindDefinitionEntry(string name)
        {
            for (int i = 0; i < registry.entries.Count; i++)
            {
                CharacterDefinition d = registry.entries[i].definition;
                if (d != null && d.characterName == name) return registry.entries[i];
            }
            return registry.FindById(name);
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
                    player.movement = new CharacterControllerMovement();
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

        /// <summary>Editor/test convenience: registry rows survive play-mode
        /// reloads only through their definition assets, so this clears stale
        /// spawn bookkeeping without dropping definitions.</summary>
        public void ClearSpawnBookkeeping()
        {
            for (int i = 0; i < registry.entries.Count; i++)
            {
                registry.entries[i].instance = null;
                registry.entries[i].isSpawned = false;
                registry.entries[i].spawnedAs = null;
            }
        }
    }
}
