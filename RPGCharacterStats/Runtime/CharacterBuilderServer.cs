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
        /// dimension/kind/physics components, initialize stats, attach AI, and
        /// persist the result before marking it spawned. In edit mode the
        /// spawned character is a real scene object, and it only survives the
        /// Play → Stop cycle if the SCENE is written to disk (SaveAssets alone
        /// never saves a scene) — so Materialize saves the open scenes. A
        /// character left as unsaved scene state, or spawned in play mode,
        /// dies with the cycle; that is Unity's engine policy, not a component
        /// deletion. Custom scripts keep their identity across the cycle
        /// because every script's GUID lives in a stable .cs.meta on disk.
        ///
        /// The pipeline is deliberately linear and auditable:
        ///   1. add the character itself (always first),
        ///   2. identity + configuration straight out of the definition,
        ///   3. Rigidbody in the character's dimension,
        ///   4. collision in the character's dimension + kind,
        ///   5. CharacterController for 3D non-physics players (it is the
        ///      collider — no second collider),
        ///   6. Animator, HealthBar, stats, AI,
        ///   7. exactly one Movement component per player,
        ///   8. persist (edit mode: the scene is saved to disk),
        ///   9. mark spawned (only after persistence).
        ///
        /// Everything the character is is attached, configured and saved before
        /// MarkSpawned is called, so an edit-mode spawn is on disk (scene
        /// written) before it is registered — nothing vanishes on Play → Stop.
        public Character Materialize(CharacterEntry entry, CharacterTag tag)
        {
            CharacterDefinition def = entry.definition;

            GameObject go = new GameObject(tag.ToString());

            // ---- 1) the character itself ----
            // The concrete MonoBehaviour the player carries: a PlayerCharacter
            // brings identity, stats and the movement component; an NPC
            // character brings identity, stats and its AIInstance.
            Character character = AttachCharacterComponent(go, def);

            // ---- 2) identity + configuration straight out of the definition ----
            character.characterID = def.characterID;
            character.characterName = def.characterName;
            character.description = def.description;
            character.dimension = def.dimension;
            character.physicsMode = def.physicsMode;

            // ---- 3) Rigidbody, in the character's dimension ----
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
                // 3D non-physics Player: the rigidbody is kinematic so it never
                // fights the CharacterController.
                body.isKinematic = def.kind == CharacterKind.Player
                    && def.physicsMode == PhysicsMode.NonPhysics;
                character.rigidbody3D = body;
            }

            // ---- 4) collision, dimension + kind dependent ----
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

            // ---- 5) CharacterController, 3D non-physics Player only ----
            // It IS the collider for this case (no second collider is added).
            if (controllerCase)
            {
                character.controller = go.AddComponent<CharacterController>();
            }

            // ---- 6) Animator, HealthBar, stats, AI (all optional layers) ----
            // Order matters: the HealthBar hookup reads the stats the very first
            // frame they exist, so InitializeStats runs before the bar is added.
            character.healthBar = go.AddComponent<HealthBar>();
            character.InitializeStats(def.charStatText, def.statValues, def.gameplayStatText);
            if (def.Kind == CharacterKind.NPC)
            {
                character.animator = go.AddComponent<Animator>();
                AttachAI(character, def);
            }

            // ---- 7) Movement: exactly one component per player ----
            // PlayerMovement is the single MonoBehaviour a spawned player carries.
            // It reads WASD/Space and drives whatever body step 3-5 gave the
            // player: CharacterController.Move for the 3D non-physics case,
            // Rigidbody2D/Rigidbody otherwise. No camera code, no invisible
            // strategy classes — the character moves because it owns its
            // movement.
            //
            // NOTE: the body's gravity/kinematic configuration is step 3's
            // decision and is NEVER re-touched here — this step used to zero
            // gravityScale and kinematic-ize every 2D player, clobbering the
            // physics matrix for PhysicsBased 2D players.
            if (def.Kind == CharacterKind.Player)
            {
                PlayerMovement movement = go.AddComponent<PlayerMovement>();
                ((PlayerCharacter)character).movement = movement;
            }

            // ---- 8) persist (edit mode: save the scene the character lives in) ----
            // The scene write is what makes an edit-mode spawn survive the
            // Play → Stop cycle; see SaveAsset for the play-mode story.
            SaveAsset(go, def, character);

            // ---- 9) mark spawned (the pipeline's last step) ----
            // Everything the character is has been attached, configured and saved.
            registry.MarkSpawned(entry, tag, character);
            return character;
        }

        /// <summary>Persists a freshly-built character. In edit mode the character is a
        /// real scene object: saving the open scenes is the persistence step
        /// that keeps it (and every component on it) alive across Play → Stop.
        /// In play mode Unity discards all scene changes on Stop — engine
        /// policy — so a play-mode spawn is a session-scoped instance by
        /// design, and the log says so instead of silently pretending.
        /// The server owns this step; editor windows never touch it.
        /// </summary>
        private void SaveAsset(GameObject go, CharacterDefinition def, Character character)
        {
#if UNITY_EDITOR
            if (go == null) return;

            if (!Application.isPlaying)
            {
                // SaveAssets alone never writes a scene — the spawned
                // character would be lost on the next scene reload. Write the
                // open scenes, then flush the asset database.
                UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
                UnityEditor.AssetDatabase.SaveAssets();
            }
            else
            {
                Debug.Log("[RPGStats] " + go.name + " spawned in play mode — " +
                    "Unity discards play-mode scene changes on Stop. Spawn in " +
                    "edit mode when the character must persist on disk.");
            }
#else
            // Builds never materialize, so no asset is written there.
#endif
        }

        /// <summary>Pick the concrete Character MonoBehaviour for a definition:
        /// "XDefinition" names an "X" character class by convention (Enemy →
        /// EnemyCharacter, FriendlyNPC → FriendlyNPC, Player → PlayerCharacter),
        /// with a Player/NPC fallback when no custom class exists.</summary>
        private Character AttachCharacterComponent(GameObject go, CharacterDefinition def)
        {
            // The component the player actually sees as its behaviour. It carries
            // the definition-provided identity, stats, and (for players) the
            // movement component. It is added FIRST and ONLY — custom scripts
            // survive Play → Stop on their own as long as their .cs.meta files
            // (the script's GUID) stay stable; nothing extra is needed here.
            if (def.Kind == CharacterKind.NPC)
            {
                if (def is EnemyCharacterDefinition)
                    return go.AddComponent<EnemyCharacter>();
                if (def is FriendlyNPCDefinition)
                    return go.AddComponent<FriendlyNPC>();
                return go.AddComponent<EnemyCharacter>();   // default fallback
            }

            // A player is a PlayerCharacter: identity + stats + the movement
            // component the pipeline adds one line later. Nothing else moves it.
            PlayerCharacter pc = go.AddComponent<PlayerCharacter>();
            pc.characterID = def.characterID;
            pc.characterName = def.characterName;
            pc.description = def.description;
            pc.dimension = def.dimension;
            pc.physicsMode = def.physicsMode;
            pc.InitializeStats(def.charStatText, def.statValues, def.gameplayStatText);
            return pc;
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

        /// <summary>The old "Movement" column of the section 2.5 matrix — kept as
        /// a compatibility alias. The spawn pipeline no longer uses it: every
        /// player now carries PlayerMovement directly, so nothing is left as an
        /// invisible strategy class that can vanish on Play → Stop.</summary>
        private static void AttachMovement(Character character, CharacterDefinition def)
        {
            // Not used by the spawn pipeline; PlayerMovement is attached one
            // line after AttachCharacterComponent in Materialize.
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
