# RPG Character & Stats System — Design Document

## 1. Overview

The RPG Character & Stats System is a data-driven character framework for Unity.

The system separates:

- **Character stat schemas/presets** — what stats a character can have.
- **Character definitions** — the values and identity of one particular character.
- **Gameplay stats** — values derived from formulas rather than hardcoded calculations.
- **Character registry** — persistent access to built character definitions.
- **Runtime character instances** — materialized Unity characters.
- **AI, UI, and Blackboard integration** — systems that consume or react to character/gameplay stats.

The overall design is intended to remain modular and reusable across different RPG projects.

---

# 2. Core Design Principles

## 2.1 Data-Driven Design

Character data and gameplay-stat formulas are represented as data rather than being hardcoded into individual character classes.

This allows designers to create and modify characters through builder tools and preset files.

## 2.2 Separation of Responsibilities

The system separates the responsibilities of:

- Defining available character stats.
- Defining gameplay-stat calculations.
- Creating individual characters.
- Registering characters.
- Materializing characters at runtime.

## 2.3 Runtime Independence from Authoring

Runtime systems should not need to know how a character was authored.

A runtime character ultimately receives:

```text
Character
├── CharacterStats
├── GameplayStats
├── CharacterController
├── Animator
├── Rigidbody2D
└── AIInstance (NPC only)
```

## 2.4 O(1) Gameplay-Stat Evaluation

Gameplay-stat formulas do not support loops.

After parsing, formulas are compiled into callbacks and executed in dependency order.

This guarantees O(1) work per gameplay-stat client.

---

# 3. High-Level Architecture

```text
                    DESIGN TIME
                         │
        ┌────────────────┼────────────────┐
        │                │                │
        ▼                ▼                ▼
 Character Stats   Gameplay Stats    Character Builder
    Builder            Builder              │
        │                │                  │
        ▼                ▼                  │
   .charstat       .gameplaystat             │
        │                │                  │
        └────────────────┴──────────────────┘
                         │
                         ▼
                Character Definition
                         │
                         ▼
                Character Registry
                         │
                  ┌──────┴──────┐
                  ▼             ▼
                 Edit          Spawn
                                │
                                ▼
                         Runtime Character
                                │
                    ┌───────────┴───────────┐
                    ▼                       ▼
              CharacterStats          GameplayStats
                                            │
                              ┌─────────────┴─────────────┐
                              ▼                           ▼
                         Stat Bars                  FSM Blackboard
```

---

# 4. Abstract Base Classes

## 4.1 Item

```text
Item (abstract)
    string id
    string name
    string description
    Sprite icon
    bool isStackable
    float weight
```

## 4.2 Stats

```text
Stats (abstract)
    List<StatField> fields

    abstract void Recalculate()
    abstract float GetFloat(string name)
    abstract int GetInt(string name)
    abstract bool GetBool(string name)
```

## 4.3 StatField

```text
StatField
    string name
    StatType type          // Float, Int, Bool
    float floatValue
    int intValue
    bool boolValue
```

## 4.4 Character

```text
Character (abstract)
    string characterID
    string name
    string description
    CharacterStats characterStats
    GameplayStats gameplayStats
    CharacterController controller
    Animator animator
    Rigidbody2D rigidbody
    HealthBar healthBar
```

## 4.5 CharacterDefinition

```text
CharacterDefinition (abstract)
    string characterID
    string name
    string description
    List<StatValueOverride> statValues
```

```text
StatValueOverride
    string statName
    StatType type
    float floatValue
    int intValue
    bool boolValue
```

---

# 5. Character Hierarchy

```text
Character (abstract)
│
├── PlayerCharacter
│     ├── input handling
│     ├── inventory
│     └── level up logic
│
└── NPCCharacter (abstract)
      ├── AIInstance
      └── AI logic
          │
          ├── EnemyCharacter
          └── FriendlyNPC
```

The same distinction exists at definition time:

```text
CharacterDefinition
│
├── PlayerCharacterDefinition
│
└── NPCCharacterDefinition
      │
      ├── EnemyCharacterDefinition
      └── FriendlyNPCDefinition
```

NPC definitions additionally reference an `AIInstance`.

---

# 6. Character Stats System

## 6.1 Character Stats Builder

The Character Stats Builder creates reusable `.charstat` presets.

A `.charstat` defines:

- Stat names.
- Stat types.
- Default values.
- Optional minimum values.
- Optional maximum values.

Example:

```text
float Strength:     (min: 0, max: 999, default: 10)
float Vitality:     (min: 0, max: 999, default: 10)
int   Level:        (min: 1, max: 100, default: 1)
bool  IsUndead:     (default: false)
```

The preset answers:

> What stats does this character type/system have?

It does not represent one individual character.

---

# 7. Character Builder

The Character Builder creates a concrete character using a `.charstat` preset.

The workflow is:

```text
Select .charstat preset
        ↓
Load available stats
        ↓
Fill character-specific values
        ↓
Enter character metadata
        ↓
Choose Player or NPC
        ↓
Configure AI if NPC
        ↓
Build / Save
```

Example:

```text
Preset: RPGStats.charstat

Name: Orc Warrior
Character ID: orc_warrior
Type: NPC
AI: OrcWarriorFSM

Strength: 42
Vitality: 30
Dexterity: 12
Level: 5
IsUndead: false
```

Multiple characters can use the same `.charstat` preset while having different values.

---

# 8. Gameplay Stats

Gameplay stats are live calculated values.

They are never hardcoded directly into character classes.

```text
GameplayStats
    List<GameplayStatFormula> formulas
    Blackboard blackboard
    ref → CharacterStats
```

## 8.1 GameplayStatFormula

```text
GameplayStatFormula
    string name
    StatType type
    string primaryInput
    List<SecondaryInput> secondaryInputs
    string formulaExpression
```

## 8.2 Inputs

```text
SecondaryInput (abstract)
    string name
    StatType type
```

Possible input types:

```text
ConstantInput
    float value

CharacterStatInput
    string statName

GameplayStatInput
    string gameplayStatName

BlackboardInput
    string blackboardKey

ExternalBindInput
    Object targetObject
    string memberPath
```

---

# 9. Gameplay Stat Formula Language

The `.gameplaystat` format defines gameplay-stat formulas.

Format:

```text
[type] [name]: (primary [type] [var], [type] [var], ...) => {
    [statements]
    return [value]
}
```

Example:

```text
float MaxHP: (primary float Vitality, float K) => {
    float base = Vitality * 10;
    return clamp(base + K, 0, 9999);
}
```

Another example:

```text
bool ImmuneToPoison: (primary bool IsUndead) => {
    return IsUndead;
}
```

Another:

```text
int XPToNextLevel: (primary int Level, float Multiplier) => {
    int base = Level * 100;
    return floor(base * Multiplier);
}
```

## 9.1 Primary Parameter Rule

The first parameter is always the primary input.

The primary input must correspond exactly to a CharacterStat.

Both its **name and type must match**.

For example, if the character preset contains:

```text
float Vitality
```

then:

```text
(primary float Vitality, float K)
```

is valid.

These are invalid:

```text
(primary int Vitality, float K)
```

```text
(primary float Health, float K)
```

The first changes the type.

The second changes the name.

This prevents the formula from silently referring to a different character stat.

## 9.2 Formula Statements

Supported statement categories:

- Built-in mathematical functions.
- `if`.
- `else if`.
- `else`.
- Temporary variable declarations.
- Variable assignments.
- `return`.

Built-in functions include:

```text
sqrt
pow
abs
min
max
clamp
floor
ceil
round
log
sin
cos
```

Loops are not supported.

Every formula must have a return statement.

The return type must match the declared gameplay-stat type.

---

# 10. Gameplay Stats Builder

The Gameplay Stats Builder creates reusable gameplay-stat definitions.

Workflow:

```text
Select .charstat
        ↓
Create gameplay stats
        ↓
Write formula
        ↓
Parse formula signature
        ↓
Generate input representation
        ↓
Validate dependencies
        ↓
Save .gameplaystat
```

The formula text is the source of truth for the function signature.

For example:

```text
(primary float Vitality, float K)
```

automatically produces the corresponding parameter information:

```text
Primary:
    Vitality
    Float

Secondary:
    K
    Float
```

The user should not have to manually duplicate the primary parameter declaration in another field.

---

# 11. Parser and Compiler Pipeline

```text
.gameplaystat
      ↓
Lexer
      ↓
Token Stream
      ↓
Parser
      ↓
GameplayStat AST
      ↓
Dependency Graph
      ↓
Topological Sort
      ↓
Code Generator
      ↓
Compiled C# Delegates
      ↓
GameplayStatClients
```

The parser reports:

- Type mismatch.
- Unknown variable/function.
- Missing return.
- Circular dependency.
- Missing primary parameter.

Errors include line and column information.

---

# 12. GameplayStats Runtime

The runtime system uses:

```text
GameplayStatsServer
    List<GameplayStatClient> clients
    ref → CharacterStats
```

Each client contains:

```text
GameplayStatClient
    string name
    StatType returnType
    Func<...> callback
    StatField output
```

Recalculation:

```text
CharacterStats.Set(...)
        ↓
GameplayStatsServer.Recalculate()
        ↓
iterate clients in topological order
        ↓
execute callback
        ↓
write result to StatField
        ↓
OnStatChanged
        ↓
OnRecalculated
```

The server does not contain individual gameplay-stat formulas.

The compiled clients contain the calculation logic.

---

# 13. Blackboard

The GameplayStats system contains a Blackboard for runtime variables and external bindings.

```text
Blackboard
    List<BlackboardVariable> variables
```

```text
BlackboardVariable
    string name
    StatType type
    float runtimeFloat
    int runtimeInt
    bool runtimeBool
    bool isExternallyBound
    Object boundObject
    string boundMemberPath
```

Operations:

```text
Get(name)
Set(name, value)
Bind(name, target, memberPath)
UpdateExternalBindings()
```

This allows temporary runtime modifiers and external systems to participate without changing the CharacterStats definition.

---

# 14. Character Registry

The registry stores character definitions and their runtime instances.

```text
CharacterRegistry
    List<CharacterEntry> entries
```

```text
CharacterEntry
    CharacterTag tag
    CharacterDefinition definition
    Character instance
    bool isSpawned
```

Character tags contain:

```text
CharacterTag
    string name
    int serialNumber
```

Example:

```text
OrcWarrior_001
OrcWarrior_002
VillageElder_001
Player_001
```

---

# 15. CharacterBuilderServer

`CharacterBuilderServer` is the central service for character registration and runtime materialization.

```text
CharacterBuilderServer
    singleton
    CharacterRegistry registry
```

Public API:

```text
Spawn(string name)
SpawnByTag(CharacterTag tag)

Despawn(string name)
DespawnByTag(CharacterTag tag)

GetAll(string name)
GetByTag(CharacterTag tag)

IsSpawned(CharacterTag tag)
```

The server hides the complexity of finding definitions, creating instances, attaching components, and initializing their systems.

---

# 16. Character Spawn Pipeline

When a character is spawned:

```text
CharacterBuilderServer.Spawn()
        ↓
Find matching CharacterEntry
        ↓
Instantiate GameObject
        ↓
Attach CharacterController
        ↓
Attach Animator
        ↓
Attach Rigidbody2D
        ↓
Populate CharacterStats
        ↓
Compile / attach GameplayStatsServer
        ↓
Attach AIInstance if NPC
        ↓
Attach StatBars
        ↓
Mark CharacterEntry as spawned
```

The runtime character therefore becomes the materialized form of the saved character definition.

---

# 17. Registry Editor Workflow

The registry provides a searchable character library.

```text
REGISTRY

Search: [ Orc ]

OrcWarrior_001     NPC       [Edit] [Spawn]
OrcWarrior_002     NPC       [Edit] [Spawn]
OrcWarrior_003     NPC       [Edit] [Spawn]
Player_001         Player    [Edit] [Spawn]
```

## Edit

```text
Registry
    ↓
CharacterEntry
    ↓
CharacterDefinition
    ↓
Character Builder
    ↓
Modify
    ↓
Rebuild
    ↓
Update saved definition
    ↓
Regenerate .gameplaystat if necessary
```

## Spawn

```text
Registry
    ↓
CharacterEntry
    ↓
CharacterDefinition
    ↓
Materialize
    ↓
Runtime Character
```

---

# 18. Stat Bars

`StatBar` is the base abstraction for visual stat bars.

```text
StatBar (abstract)
    backgroundColor
    backgroundOpacity
    fillColor
    fillOpacity
    showTrace
    traceColor
    traceOpacity
    traceDecayDelay
    traceDecaySpeed
    fillLerpSpeed

    SetMaxValue(float)
    UpdateValue(float)
    AnimateFill()
    AnimateTrace()
```

Implementations:

```text
HealthBar
ShieldBar
StaminaBar
MagicBar
XPBar
```

They react to gameplay-stat changes rather than calculating the gameplay stats themselves.

---

# 19. Runtime Event Flow

```text
CharacterStats
      │
      │ Set / Level Up / Equipment Change
      ▼
GameplayStatsServer
      │
      ▼
Recalculate
      │
      ▼
GameplayStatClients
      │
      ▼
Gameplay Stat Fields
      │
      ├───────────────┐
      ▼               ▼
StatBar         FSM Blackboard
```

A level-up is therefore not required to manually recalculate every dependent statistic.

For example:

```text
SetInt("Level", newLevel)
        ↓
GameplayStats.Recalculate()
        ↓
XP / derived stats update
```

---

# 20. Design Patterns Used

## 20.1 Singleton

`CharacterBuilderServer` is explicitly designed as a singleton:

```text
CharacterBuilderServer.Instance
```

It provides one global character-building/registry service.

## 20.2 Observer

GameplayStats exposes:

```text
OnStatChanged
OnRecalculated
```

Other systems such as StatBars and the FSM Blackboard can react to changes without the GameplayStats system directly controlling those systems.

## 20.3 Strategy

Gameplay-stat calculations are represented as compiled callbacks.

The `GameplayStatsServer` executes a calculation without hardcoding the actual formula.

Different formulas therefore represent different calculation strategies.

## 20.4 Factory / Factory-Like Creation

The CharacterBuilderServer materializes the correct runtime character from a stored CharacterDefinition.

The definition determines whether the resulting character is a Player or NPC variant.

## 20.5 Facade / Service Layer

`CharacterBuilderServer` hides the internal complexity of registry lookup, instantiation, component setup, and initialization behind operations such as:

```text
Spawn()
Despawn()
GetByTag()
```

## 20.6 Data-Driven Design

`.charstat`, `.gameplaystat`, CharacterDefinitions, and the registry allow game behavior and character configuration to be represented as data.

## 20.7 DSL Compiler Architecture

The `.gameplaystat` system is a domain-specific language.

Its pipeline is:

```text
Source Text
    ↓
Lexer
    ↓
Parser
    ↓
AST
    ↓
Dependency Analysis
    ↓
Topological Sort
    ↓
Code Generation
    ↓
Runtime Delegates
```

This is not one of the classic GoF patterns, but it is a major architectural component of the system.

---

# 21. Pattern Classification

| Pattern / Architecture | Usage |
|---|---|
| Singleton | Explicit |
| Observer | Strong |
| Strategy | Strong |
| Factory | Factory-like |
| Facade / Service Layer | Strong |
| Data-Driven Design | Strong |
| DSL / Compiler Pipeline | Strong |
| Prototype | Conceptually similar through presets, but not a formal Prototype implementation |
| Adapter | Some adapter-like behavior through external Blackboard bindings |
| Composite | Not currently a formal Composite implementation |
| Inheritance / Polymorphism | Used extensively |

The architecture should not force additional design patterns merely for the sake of using them. Patterns should remain consequences of clear responsibilities.

---

# 22. Full Design-Time Pipeline

```text
1. Character Stats Builder
        ↓
   Create / modify .charstat
        ↓

2. Gameplay Stats Builder
        ↓
   Create formulas
        ↓
   Validate formulas
        ↓
   Save .gameplaystat
        ↓

3. Character Builder
        ↓
   Select .charstat preset
        ↓
   Fill character values
        ↓
   Select Player / NPC
        ↓
   Configure AI if NPC
        ↓
   Build
        ↓

4. Character Registry
        ↓
   Store CharacterEntry
        ↓

5. Runtime
        ↓
   Spawn by name or tag
        ↓
   Materialize Character
        ↓
   Initialize CharacterStats
        ↓
   Initialize GameplayStats
        ↓
   Initialize AI / UI
```

---

# 23. Example End-to-End Character

## Preset

```text
RPGStats.charstat

float Strength:   (min: 0, max: 999, default: 10)
float Vitality:   (min: 0, max: 999, default: 10)
float Dexterity:  (min: 0, max: 999, default: 10)
int Level:        (min: 1, max: 100, default: 1)
bool IsUndead:    (default: false)
```

## Gameplay Stats

```text
float MaxHP: (primary float Vitality, float K) => {
    float base = Vitality * 10;
    return clamp(base + K, 0, 9999);
}

bool ImmuneToPoison: (primary bool IsUndead) => {
    return IsUndead;
}

int XPToNextLevel: (primary int Level, float Multiplier) => {
    int base = Level * 100;
    return floor(base * Multiplier);
}
```

## Character

```text
Name: Orc Warrior
ID: orc_warrior
Type: NPC
AI: OrcWarriorFSM

Strength: 42
Vitality: 30
Dexterity: 12
Level: 5
IsUndead: false
```

## Runtime

```text
CharacterBuilderServer.Spawn("OrcWarrior")
        ↓
OrcWarrior_001
        ↓
CharacterStats
        ↓
GameplayStats
        ↓
MaxHP / XPToNextLevel / ImmuneToPoison
        ↓
StatBars / FSM Blackboard
```

---

# 24. Architectural Goal

The system ultimately aims to provide a workflow where a designer can:

1. Define reusable character-stat presets.
2. Define reusable gameplay-stat formulas.
3. Create individual characters from those presets.
4. Save them into a central registry.
5. Search and edit existing characters.
6. Spawn characters on demand.
7. Have derived statistics automatically recalculate when their inputs change.
8. Allow AI and UI systems to consume those values without tightly coupling themselves to the character-stat implementation.

The central architectural separation is:

```text
Character Stats Builder
    = Defines the vocabulary

Gameplay Stats Builder
    = Defines the mathematics

Character Builder
    = Defines an individual character

Character Registry
    = Stores and retrieves characters

CharacterBuilderServer
    = Materializes characters

Runtime Systems
    = Consume the resulting character data
```

This separation is the foundation of the system.
