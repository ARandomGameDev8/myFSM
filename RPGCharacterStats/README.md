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
| `Runtime/` | `Character` hierarchy + `CharacterBuilderServer` + `CharacterDB` | spawn pipeline, 2D/3D movement controllers, `PlayerController` (WASD + Space + gravity via `CharacterController`, no camera — attached automatically to 3D non-physics Players); definitions persist as **JSON records** in `Assets/Resources/CharacterDB/` (one record per character, self-describing, readable in builds via `Resources`), and the registry is the server's **LRU cache** in front of that DB — the server owns every read and write, the cache is Unity-serialized (survives Play → Stop) and auto-reloads from the DB when cold |
| `UI/` | `StatBar` and the five concrete bars | health/shield/stamina/magic/XP visuals |
| `Editor/` | The four visual editors + the server's inspector | Stats / Gameplay / Character / Registry windows; `CharacterBuilderServer`'s custom inspector opens them and spawns from the scene |
| `Samples/` | `RPGStats.charstat`, `RPGGameplay.gameplaystat`, `OrcWarriorFSM` | end-to-end example from the design doc |
| `Sandbox~/` | Headless compile + smoke-test harness | `dotnet run` verification, no Unity needed |

## Persistence (a real database, not fragile Unity assets)

Every character you create is saved **permanently** as a JSON record under
`Assets/Resources/CharacterDB/<character-id>.json`. The record is
self-describing: it stores the DATA plus the definition class *name* — never
a script GUID, never a `.meta` reference. What that buys you:

- **Delete and re-copy the package folder → the database doesn't care.** The
  old `.asset` format broke exactly this way (a `.asset` points at its script
  by GUID; a re-copy mints new GUIDs and every row "loses" its fields). JSON
  records have no such link to break.
- **Refactors/renames don't orphan rows.** If a definition class is ever
  genuinely gone, the record still loads on the closest concrete class (by
  its saved `kind`) and keeps every field it can hold.
- **Legacy `.asset` rows migrate themselves.** The first time anything
  touches the DB in an editor session (server `OnEnable`, a window, the
  inspector), legacy rows — including ones whose script link is already
  broken — are re-linked and rewritten as JSON records, automatically. There
  is nothing to repair by hand; `RPG → CharacterDB — Check & Heal` runs the
  same pass on demand and reports what's on disk.
- **Reads work in the editor AND in builds** (`.json` imports as a
  `TextAsset`; the DB loads through `Resources`). Writes are editor-only.
- **Corrupt rows can't take the database down** — an unreadable record is
  skipped with a warning while the healthy rows load.

The registry stays what it was designed to be: the server's own LRU cache in
front of this DB — cache-first reads, DB fallback on miss, spawned entries
pinned. The cache is Unity-serialized and auto-reloads from the DB when cold,
so characters persist across Play → Stop too.

## Updating this package

The DATABASE is update-proof by design. Copy the new folder over the old one
(or even delete + re-copy — the records survive that now):

```bash
cp -r RPGCharacterStats/.  <your-project>/Assets/MyFSM/RPGCharacterStats/
```

Copying over the old folder (instead of deleting) is still the **polite**
way, for one reason Unity owns, not us: *scene objects* (spawned characters,
the `CharacterBuilderServer` component) reference their scripts by GUID, and
a delete + re-copy regenerates GUIDs, giving those scene objects "missing
behaviour" warnings. That's Unity's scene serialization, not the database —
the data is safe either way; you'd just delete and re-spawn those objects.

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
