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
| `Runtime/` | `Character` hierarchy + `CharacterBuilderServer` + `CharacterDB` | spawn pipeline, 2D/3D movement, optional render-only 3D cube/capsule/Resources-model child, and player camera modes (DoNotAlter / first person / third person); the default movement remains camera-independent. Definitions persist as **JSON records** in `Assets/Resources/CharacterDB/` (one record per character, self-describing, readable in builds via `Resources`), and the registry is the server's **LRU cache** in front of that DB — the server owns every read and write, the cache is Unity-serialized (survives Play → Stop) and auto-reloads from the DB when cold |
| `UI/` | `StatBar`, the five concrete bars, and `RuntimeCharacterMonitorMenu` | health/shield/stamina/magic/XP visuals plus the live in-game character/stat/server inspector |
| `Editor/` | The four visual editors + the server's inspector | The singleton `CharacterBuilderServer` owns both factories and their builder windows: `StatsFactory` groups Character Stats and Gameplay Stats; `CharacterFactory` creates the Character Builder. The workflow remains Character Stats → Gameplay Stats → Character Builder; the Registry window remains separate |
| `Samples/` | `RPGStats.charstat`, `RPGGameplay.gameplaystat`, `OrcWarriorFSM` | end-to-end example from the design doc |
| `Sandbox~/` | Headless compile + smoke-test harness | `dotnet run` verification, no Unity needed |

## Components attached at spawn (source is explicit)

The RPG spawn pipeline does **not** generate C# component scripts. It adds
compiled component types that are defined in this project: `Character`,
`PlayerCharacter`, `EnemyCharacter`, and `FriendlyNPC` (`Runtime/Character.cs`
and `Runtime/CharacterHierarchy.cs`); `PlayerMovement` (`Runtime/PlayerMovement.cs`);
and `HealthBar` (`UI/StatBars.cs`). `PlayerController`
(`Runtime/PlayerController.cs`) is another repo-defined, optional standalone
mover, but the spawn pipeline does not attach it. The pipeline also adds
Unity's built-in physics, collider, controller, animator, and rigidbody
components as required by the character definition. Optional 3D visuals are
child `GameObject` instances made from Unity primitives or a model loaded by
its `Resources` path; visual
colliders are disabled, so the existing root-collider matrix stays unchanged.
`CharacterBuilderServer.Materialize` is the complete attachment matrix
(`Runtime/CharacterBuilderServer.cs`). These component **instances** are
created with `AddComponent` during spawn; their C# classes are already defined
and compiled. The editor may also create a scene
`CharacterBuilderServer` GameObject if one is missing, then add that existing
script type—again, no component source is generated.

For an NPC, the optional AI type is a compiled `MonoScript` selected in the
Character Builder and stored by type name in the definition. The RPG package
itself does not create that C# source. Separately, the optional myFSM burst
compiler **does** generate one `XxxAI.cs` subclass per compiled `.fsm` when a
user invokes **Compile**, **Compile All**, or **Sweep + Compile**. It writes a
real `.cs` asset under the configured `ScriptFolder` (default
`Assets/MyFSM`), where Unity imports and compiles it; it is not a hidden
in-memory component and is not generated merely by launching the editor.
Recompiling overwrites that generated file, so custom edits belong in a
separate `XxxAI.Manual.cs` partial file (which you can track in version
control), not in generated output. See
`myFSM-UnityRuntime/Docs/Compiler.md` and `Docs/AIInstance.md` for the exact
workflow and manual extension points.

## In-game live character monitor

In Play Mode, `CharacterBuilderServer` automatically adds the UGUI-based
`RuntimeCharacterMonitorMenu` to its singleton GameObject. Click the small
**CHARACTERS** launcher or press **F2** to open it. The roster refreshes while
the game runs and lists active `Character` components plus inactive spawned
instances tracked by the character registry, including characters spawned
after the menu opens. Select one to inspect three tabs:

- **Character Stats** — that character's live base values and schema ranges.
- **Gameplay Stats** — its derived formula outputs in dependency/topological
  order.
- **Server Monitor** — the same derived outputs with source-reference status,
  callback availability, per-client evaluation counts, recalculation pass
  counts, and a rolling live change log.

The monitor listens to `CharacterStats.OnStatChanged`,
`GameplayStatsServer.OnStatChanged`, and `OnRecalculated`; it also samples the
selected stat fields periodically to catch direct field edits that bypass the
normal setters. It uses a runtime UGUI Canvas, not `OnGUI`/IMGUI. The Unity
project needs the built-in **Unity UI (UGUI)** package available. The monitor is
created at runtime and is not saved as a scene component.

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

## Scene components, script GUIDs, and Play Mode

Definition JSON persistence is separate from Unity scene serialization. Unity
stores each `MonoBehaviour` component's script reference as a GUID from that
script asset's `.meta` file. The `.meta` sidecars in this repository are part
of the Unity package identity: keep them beside their assets and include them
in package updates. If a `.meta` is deleted or regenerated, its GUID changes,
so a component saved in a scene/prefab can become **Missing Script** even when
the `.cs` file is still present. New Unity scripts need their actual
Unity-generated `.meta` sidecars preserved too; do not hand-mint replacements.

The spawn pipeline does not remove user-added components. It saves an
edit-mode spawn after attaching the components it knows about. If you add an
extra component later in Edit Mode, save the scene after the change. If the
character (or its extra components) was created/added during Play Mode, Unity
reverts those runtime scene changes when Play Mode stops; that is normal. For
gameplay spawns, keep a prefab/definition and spawn it each run rather than
expecting a runtime instance to survive Stop.

If the GameObject remains but shows **Missing Script**, first check Console for
compile errors and then inspect the scene's `m_Script` GUID (the existing
`RPG → Diagnostics → 3. Audit scene script GUIDs` menu can help for scripts
under `Assets/`). Restore the original `.meta` from the Unity project backup or
an older package revision if possible. If that GUID is gone, remove the missing
component slot and add the script again; Unity cannot infer the lost script
identity or recover fields that existed only in that missing component.

## Updating this package

The DATABASE records are update-proof by design. Copy the new folder over the
old one (or delete + re-copy — the JSON records survive that now, but scene
component references do not necessarily survive):

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
