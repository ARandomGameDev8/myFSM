# RPG Character & Stats System

A data-driven character framework for Unity: reusable stat schemas (`.charstat`),
a formula DSL for derived stats (`.gameplaystat`), a character builder and
registry, and the runtime server that materializes characters as 2D or 3D,
Player or NPC, physics-based or not.

Full design: `Docs/RPG_Character_Stats_System_Design_Document.md` (the document this package implements, section for section).

## Layout (one folder per component, like section 3.1)

| Folder | Component | Produces / does |
|---|---|---|
| `Shared/` | Vocabulary: enums, `StatField`, `Item`, `Stats` | shared data classes |
| `CharacterStats/` | Character Stats system | `.charstat` parse/write, `CharacterStats`, `CharStatAsset` |
| `GameplayStats/` | Formula DSL + runtime | `.gameplaystat` parse, compile to delegates, recalc server, `Blackboard` |
| `Definitions/` | Character definitions + registry | `CharacterDefinition` tree, `CharacterRegistry`, tags |
| `Runtime/` | `Character` hierarchy + `CharacterBuilderServer` + `CharacterDB` | spawn pipeline, 2D/3D movement controllers, `PlayerController` (WASD + Space + gravity via `CharacterController`, no camera — attached automatically to 3D non-physics Players); definitions persist in `Assets/Resources/CharacterDB/` (one .asset per character, readable in builds via `Resources`), and the registry is the server's **LRU cache** in front of that DB — the server owns every read and write, the cache is Unity-serialized (survives Play → Stop) and auto-reloads from the DB when cold |
| `UI/` | `StatBar` and the five concrete bars | health/shield/stamina/magic/XP visuals |
| `Editor/` | The four visual editors + the server's inspector | Stats / Gameplay / Character / Registry windows; `CharacterBuilderServer`'s custom inspector opens them and spawns from the scene |
| `Samples/` | `RPGStats.charstat`, `RPGGameplay.gameplaystat`, `OrcWarriorFSM` | end-to-end example from the design doc |
| `Sandbox~/` | Headless compile + smoke-test harness | `dotnet run` verification, no Unity needed |

## Updating this package (read this once, save yourself a day)

Every script gets a `.meta` GUID **when Unity imports it**. The repo has no
`.meta` files (Unity generates them) — so if you **delete**
`Assets/MyFSM/RPGCharacterStats/` and copy it back in, every script comes back
with a **new GUID**. Everything that referenced the old ones then breaks:

- scene-spawned characters → "missing behaviour" on every component,
- the definition rows in `Assets/Resources/CharacterDB/` → "the associated
  script can not be loaded", fields *look* empty (the data is still on disk —
  only the script link is gone),
- the `CharacterBuilderServer` object in the scene.

**The fix going forward — copy OVER the old folder, never delete first:**

```bash
# from this repo's checkout (in-place overwrite, .meta files survive):
cp -r RPGCharacterStats/.  <your-project>/Assets/MyFSM/RPGCharacterStats/
```

Overwriting keeps every existing `.meta`, so every GUID — and every scene
object, spawn, and DB row that referenced them — keeps working. New files
added by the update just get fresh GUIDs on import, which is fine.

### If things are already broken (one-time recovery)

1. **CharacterDB rows:** run `RPG → Repair CharacterDB Script References`
   (menu in Unity). It rewrites each broken row's `m_Script` GUID to the
   current script — the stored data reappears. (Enemy-vs-Friendly rows can't
   be told apart by their fields; the repair defaults them to Enemy — rebuild
   a FriendlyNPC by hand if you had one.)
2. **Spawned characters / the server object:** a "missing behaviour" slot
   cannot be identified after the fact — delete those objects and re-spawn,
   and delete a broken `CharacterBuilderServer` object, then reopen any RPG
   window (it recreates the server and auto-pulls the DB into the registry).
3. Do the `cp -r` update above **first**, so the repair links to the GUIDs
   that will keep existing.

## Verification (no Unity required)

```bash
cd RPGCharacterStats/Sandbox~
dotnet run
```

The folder is named `Sandbox~` (trailing tilde) so **Unity ignores it entirely** —
if the package folder is ever copied under a project's `Assets/`, the stub types
and the compiled `RPGCharacterStatsSandbox.dll` are never imported (importing that
dll makes every Unity type ambiguous, CS0433). The sandbox compiles the whole runtime against stub Unity types (same trick as
`myFSM-UnityRuntime/Sandbox`) and runs the RPGCharacterStatsTests suite: the
formula DSL (lexer, parser, dependency order, runtime recalc, blackboard), the
`.charstat` round-trip, registry tags, and the spawn pipeline component matrix.
