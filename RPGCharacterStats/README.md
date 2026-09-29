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
| `Editor/` | The four visual editors | Stats / Gameplay / Character / Registry windows |
| `Samples/` | `RPGStats.charstat`, `RPGGameplay.gameplaystat`, `OrcWarriorFSM` | end-to-end example from the design doc |
| `Sandbox/` | Headless compile + smoke-test harness | `dotnet run` verification, no Unity needed |

## Verification (no Unity required)

```bash
cd RPGCharacterStats/Sandbox
dotnet run
```

The sandbox compiles the whole runtime against stub Unity types (same trick as
`myFSM-UnityRuntime/Sandbox`) and runs the RPGCharacterStatsTests suite: the
formula DSL (lexer, parser, dependency order, runtime recalc, blackboard), the
`.charstat` round-trip, registry tags, and the spawn pipeline component matrix.
