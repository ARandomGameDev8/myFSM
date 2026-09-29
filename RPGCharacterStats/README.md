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
| `Runtime/` | `Character` hierarchy + `CharacterBuilderServer` | spawn pipeline, 2D/3D movement controllers |
| `UI/` | `StatBar` and the five concrete bars | health/shield/stamina/magic/XP visuals |
| `Editor/` | The four visual editors + the server's inspector | Stats / Gameplay / Character / Registry windows; `CharacterBuilderServer`'s custom inspector opens them and spawns from the scene |
| `Samples/` | `RPGStats.charstat`, `RPGGameplay.gameplaystat`, `OrcWarriorFSM` | end-to-end example from the design doc |
| `Sandbox~/` | Headless compile + smoke-test harness | `dotnet run` verification, no Unity needed |

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
