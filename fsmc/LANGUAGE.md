# FSM DSL — Language Reference

Every syntactic feature of the FSM language (spec v0.3), exactly as implemented
and enforced by `fsmc`. One `.fsm` file describes one AI system and compiles to
one little-endian `.fsmb` binary module. The binary-level companion to this
document is [`DESIGN.md`](DESIGN.md).

> All examples in this document were compile-tested with `fsmc`.

---

## Table of contents

1. [File layout](#1-file-layout)
2. [Comments](#2-comments)
3. [Tokens, identifiers, keywords](#3-tokens-identifiers-keywords)
4. [Global declarations: `const` and `var`](#4-global-declarations-const-and-var)
5. [`@ENTRY`](#5-entry)
6. [`State`](#6-state)
7. [`temp` variables and scoping](#7-temp-variables-and-scoping)
8. [`Actions` — statements](#8-actions--statements)
9. [Conditionals: `if` / `else if` / `else`](#9-conditionals)
10. [`Traversals` — transitions](#10-traversals--transitions)
11. [Expressions and operators](#11-expressions-and-operators)
12. [Literals](#12-literals)
13. [Vector literals](#13-vector-literals)
14. [Variable resolution and name rules](#14-variable-resolution-and-name-rules)
15. [Function calls and overload resolution](#15-function-calls-and-overload-resolution)
16. [Function tiers and claims](#16-function-tiers-and-claims)
17. [Built-in types (complete list)](#17-built-in-types)
18. [Built-in functions (complete list)](#18-built-in-functions)
19. [Diagnostics and exit codes](#19-diagnostics-and-exit-codes)
20. [Quick reference of what is NOT allowed](#20-what-is-not-allowed)
21. [Worked example](#21-worked-example)

---

## 1. File layout

A `.fsm` file is a sequence of top-level items, in any order:

```ebnf
file      := { constDecl | varDecl | stateDecl | entryDecl }
            — at least one stateDecl, exactly one entryDecl
constDecl := "const" type name "=" constExpr ";"
varDecl   := "var"   type name ";"
stateDecl := "State" name "{" stateBody "}"
stateBody := { tempDecl | actionsBlock | traversalsBlock }
            — at least one of actionsBlock / traversalsBlock
            — each block at most once per state
entryDecl := "@ENTRY" stateName
```

Rules:

- **At least one `State`** is required, and **exactly one `@ENTRY`**.
- **File order matters for globals**: `const`/`var` declarations must appear
  *before* any `State` that references them (the compiler parses in one
  pass). `goto` and `@ENTRY` may name states declared *later* in the file
  (forward state references are fine).
- Names in the global section (`const` + `var`) share one namespace — no
  duplicates. State names share one namespace — no duplicates.
- Everything is **case-sensitive**. `State`, `Actions`, `Traversals` are
  uppercase; `if`, `else`, `goto`, `temp`, `const`, `var` are lowercase.

```fsm
// Order that compiles: globals first, then states, then @ENTRY.
const int tickLimit = 10;
var Object3D player;

State Idle { ... }
State Flee { ... }

@ENTRY Idle
```

## 2. Comments

```fsm
// line comment — runs to end of line
/* block comment,
   may span lines */
```

**The `//` ambiguity.** `//` is also the integer floor-division operator. The
rule: if `//` follows a **"value" character** — a letter, digit, `)`, `]`, or
a closing `"` — with *only whitespace in between*, it is **floor division**.
Otherwise (own line, or after `;`, `=`, `(`, `{`, `}`, `,`, a keyword, an
operator, …) it starts a **comment**. Whitespace alone does **not** break the
ambiguity — only a newline or a non-value token does.

```fsm
temp int a = 7 // 2;        // floor division: a == 3  (the // sticks to the 7)
temp int b = 7; // note     // comment: ';' breaks the value run
temp int c = (7) // 2;      // floor division: ')' is a value character
temp int d = 3 // note      // ERROR: the // sticks to the 3, so "note" becomes code
temp int e = 3;             // safe
// comment on its own line  // safe: a newline always breaks the value run
```

Safest habits: put line comments on their **own line**, or make sure the
previous token on the line is `;`, `=`, `(`, `{`, `}`, `,`, a keyword, or an
operator.

## 3. Tokens, identifiers, keywords

- **Identifiers**: start with a letter or `_`, continue with letters, digits,
  or `_`. (e.g. `player`, `lastDist`, `_private`)
- **Keywords** (reserved, case-sensitive): `State`, `Actions`, `Traversals`,
  `if`, `else`, `goto`, `temp`, `const`, `var`, `true`, `false`, `@ENTRY`.
  `return` is lexed but rejected wherever it appears ("return statements are
  not supported by this language").
- **Operators and punctuation**:
  `+ - * ** / //` `== != < > <= >=` `! && ||` `=` `( ) { } , ;`
- Single `&` or `|` are lex errors (the language has `&&` / `||` only).
- An `@` followed by anything other than `ENTRY` is an error
  ("unknown directive").

## 4. Global declarations: `const` and `var`

### Static constants

```fsm
const <Type> name = <constant-expression>;
```

- The initializer must be a **compile-time constant expression**: literals,
  operators, vector literals, and references to *earlier* `const` declarations
  (no forward references). It is folded at compile time — no code is emitted.
- The expression's type must **exactly** match the declared type (no implicit
  conversions): `const float x = 1.0f;` ✓ — `const float x = 1;` ✗ (int ≠ float).
- Constants are **immutable**: assigning to one is an error.
- Usable in any state (actions, conditions, initializers) once declared, and
  in later constant initializers.

```fsm
const int    maxHits     = 3;
const float  chaseRange  = 10.0f;
const float  chaseR2     = chaseRange * chaseRange;     // earlier const OK
const Vector3 homePos    = Vector3(1.0f, 2.0f, 3.0f);   // vector constants OK
const bool   startPaused = true && false;               // folds to false
```

### Runtime variables

```fsm
var <Type> name;
```

- A `var` is an **externally driven handle/value**: its value arrives at
  runtime through its binding slot (the external engine injects it).
  Therefore **an initializer is a compile error**:

  ```fsm
  var Object3D player = something;   // ERROR
  // "runtime variables cannot have initializers (they are externally driven;
  //  values arrive through their binding slot)"
  ```

- Assignment to a `var` from inside actions **is** allowed (writes back
  through the binding slot): `pos = homePos;`.
- `void` can never be a variable/const type (any kind).

```fsm
var Object3D  player;
var bool      enemyVisible;
var Vector3   pos;
var NavMeshAgent agent;
```

## 5. `@ENTRY`

```fsm
@ENTRY <StateName>
```

- Exactly one per file. Missing → error `missing @ENTRY: exactly one @ENTRY is
  required per file`. Duplicate → error `duplicate @ENTRY`.
- It must name a declared state (declaration order relative to `@ENTRY`
  doesn't matter): `@ENTRY must name a declared state: '...'`.
- The entry state is the one the engine starts in.

## 6. `State`

```fsm
State <Name> {
    temp <Type> <name> [= <expr>];      // optional, any number, any order
    Actions     { ... }                 // at most once
    Traversals  { ... }                 // at most once
}
```

- A state must contain **at least one** of `Actions` / `Traversals`
  (error: `state 'X' must contain an Actions or Traversals block`).
- Duplicating a block is an error (`duplicate Actions block in state 'X'`).
- The two blocks may appear in either order; state-level `temp` declarations
  may appear anywhere in the state body (before, between, or after the
  blocks) and are visible throughout the whole state.
- `goto` is **not** allowed at state level (only inside `Traversals`).
- Top-level `const`/`var` keywords inside a state body are an error.

## 7. `temp` variables and scoping

```fsm
temp <Type> name;            // declared, default value
temp <Type> name = <expr>;   // declared and initialized (exact type match)
```

**Where temps may live — the three scope depths:**

| Depth | Location | Lifetime |
|---|---|---|
| 1 | State body (directly under `State X {`) | Created when the state is **entered** (once, before `Actions` run), destroyed when the state is **exited**. Persists across ticks while the AI stays in the state; visible throughout the whole state body — in both `Actions` and `Traversals`. |
| 2 | `Actions` body | Created on each `Actions` execution, destroyed when `Actions` finishes. Not visible in `Traversals`. |
| 3 | `if` / `else if` / `else` body | Created when that branch runs, destroyed when it finishes. Not visible after the whole `if` chain closes. |

Scoping is C block scoping:

- **Shadowing across nested blocks is allowed.** A `temp int x` inside an
  `if` body hides a state-level `temp int x` only within that block.
- **Redeclaration in the same block is an error** (`redeclaration of 'x' in
  this scope`).
- **Out-of-scope use is an error** (`unknown variable 'x'`), including using a
  branch temp after the `if` closed.
- **A state-level temp is NOT visible in any other state** — each state has
  its own private frame (error: `unknown variable 'x'`).
- **`temp` cannot be declared at file level** (top level only accepts
  `const` / `var` / `State` / `@ENTRY`).
- `temp` is **not allowed at all inside `Traversals`** — not in the traversals
  body and not inside a traversals `if` body (see §10).

```fsm
State Chase {
    temp float lastDist = 99.0f;        // depth 1: lives as long as Chase
    Actions {
        temp float dist = 3.0f;         // depth 2: dies with this Actions run
        if (dist < 5.0f) {
            temp float near = dist;     // depth 3: dies with this branch
        }
    }
    Traversals {
        if (lastDist < 1.0f) { goto Flee; }   // depth-1 temp: visible here
        goto Chase;
    }
}
```

## 8. `Actions` — statements

`Actions` runs once per tick while the AI is in the state. Allowed statements:

```ebnf
actionStmt := tempDecl
             | varName "=" expression ";"        // assignment
             | functionCall ";"                  // Tier 2 / Tier 3 call
             | ifChain
             | ";"                               // empty statement
```

- **Assignment**: the target may be a `var` (runtime) or a `temp` in scope —
  never a `const` (`static constants cannot be assigned: 'x'`). The
  expression's type must **exactly** match the target's type.
- **Function call statements**: Tier 2 and Tier 3 functions are statements
  (see §16). Tier 1 functions produce values — use them inside expressions
  (`temp float d = distanceTo(a, b);`).
- **Not allowed in Actions**: `goto` (transitions live in `Traversals`),
  `return`, bare blocks (`{ }` not attached to a keyword), and nested
  conditionals (see §9).

```fsm
Actions {
    temp float dist = getDistanceTo(player, enemy);
    lastDist = dist;                        // state-level temp, exact type
    if (enemyVisible) {
        temp Vector3 dir = normalize(directionTo(player, enemy));
        moveTowards(agent, dir, 5.0f);      // Tier 3 call statement
    } else if (lastDist < 3.0f) {
        followTarget(agent, enemy);
    } else {
        stopMovement(agent);
    }
}
```

## 9. Conditionals

```ebnf
ifChain := "if" "(" boolExpr ")" "{" actionStmts "}"
          { "else if" "(" boolExpr ")" "{" actionStmts "}" }
          [ "else" "{" actionStmts "}" ]
```

- The condition must have type **`bool`** (error: `if condition must be a
  bool expression, got 'X'`).
- Bodies are blocks (curly braces required), even for one statement.
- An `else if` chain is one conditional construct; an `else` may only
  terminate a chain (no statements between `else` and its body).
- **Nested conditionals are a compile error** (`nested conditionals are not
  allowed inside conditionals`). If you need "if inside if", flatten it into
  one `else if` chain.
- Empty branch bodies are allowed: `if (a) { }`.

```fsm
if (a && b) { ... }
else if (c)   { ... }
else          { ... }
```

## 10. `Traversals` — transitions

`Traversals` runs once per tick, after `Actions`, and decides the state for
the next tick. Its grammar is deliberately strict:

```ebnf
travBody := { travIf } [ bareGoto ]
travIf   := "if" "(" boolExpr ")" "{" "goto" stateName ";" "}"
bareGoto := "goto" stateName ";"
```

Rules:

- Only `if` and `goto` statements are allowed (anything else → error
  `only 'if' and 'goto' statements are allowed in Traversals`).
- Each `if` body must contain **exactly one** `goto` — not zero, not two,
  nothing else.
- **`else` is not allowed in Traversals** (not even `else if`).
- The body must contain **at least one** `goto` (empty body → error
  `Traversals body must contain at least one goto statement`).
- A bare `goto` placed **before** any `if` is legal but emits a **warning**
  (`bare goto before any if: subsequent statements are dead code (this
  transition always fires)`) — put the fallback `goto` **last**.
- A state with no `if`s at all (just a bare `goto`) still compiles — it is a
  pure self-loop / pure transition.

Evaluation: ifs are checked top to bottom; the **first true** transition fires
for the next tick; if none is true, the bare `goto` (or the last one reached)
fires. At runtime exactly one transition per tick always exists.

```fsm
Traversals {
    if (lastDist < 1.0f)  { goto Flee; }     // checked first
    if (!enemyVisible)    { goto Flee; }
    goto Chase;                              // fallback — always last
}
```

## 11. Expressions and operators

Expressions build values. **There are no implicit conversions anywhere** —
every operation checks exact/promoted types at compile time.

### Precedence (C precedence; higher binds tighter)

| Precedence | Operators | Associativity |
|---|---|---|
| lowest | `\|\|` | left |
| | `&&` | left |
| | `==` `!=` | left |
| | `<` `>` `<=` `>=` | left |
| | `+` `-` | left |
| | `*` `/` `//` | left |
| | `**` | **right** (`2 ** 3 ** 2` = `2 ** (3 ** 2)`) |
| highest | unary `!` unary `-` | right |
| | primary: literals, variables, calls, `( expr )`, vector literals | — |

Use parentheses whenever in doubt — they never hurt.

### Operator type rules

| Operator | Operands | Result |
|---|---|---|
| `&&` `\|\|` | `bool` + `bool` | `bool` |
| `==` `!=` | exact same type (numeric or `bool`); **handles rejected** | `bool` |
| `<` `>` `<=` `>=` | numeric **scalars** (int/float/double, promoted); **vectors rejected** (even same-type); `bool` rejected; handles rejected | `bool` |
| `+` `-` | same-type vectors (component-wise); scalar+scalar with promotion (int→float→double); `bool` rejected; handles rejected | promoted scalar / same vector type |
| `*` | scalars (promoted); scalar × vector **in either order**; vector × vector **rejected** (use `dot()`); `bool`/handles rejected | promoted scalar / same vector type |
| `/` | numeric scalars, **`int / int` → `float`** (true division); no vectors, no `bool` | `float` or promoted scalar |
| `//` | **`int // int` only** | `int`, **floor** (toward −∞: `-7 // 2 == -4`) |
| `**` | scalar numerics only (no vectors) | same type, or promoted when mixed |
| unary `!` | `bool` | `bool` |
| unary `-` | any numeric (scalars and vectors, component-wise) | same type |

Notes:

- **`int / int` gives `float`** — there is no integer division. Want an int
  quotient? Use `//` (floor) or `int(floor(a / b))`-style via `floor(...)`.
- **`//` floors toward negative infinity**, not toward zero: `7 // 2 == 3`
  and `-7 // 2 == -4`.
- **Handle types** (Object*, Camera*, …) cannot be used in arithmetic,
  comparisons, or relations at all — use the dedicated functions
  (`distanceTo`, `isActive`, …).
- Scalar promotion only happens **between scalars** (`int` + `float` →
  `float`). A `float` and a `double` mix → `double`. Vectors never promote
  across types (`Vector2` + `Vector3` is an error).

## 12. Literals

| Literal | Example | Type |
|---|---|---|
| integer | `42`, `-7` (unary minus), `0` | `int` — 32-bit; out of range is an error |
| decimal / exponent | `3.14`, `1e3`, `2.5E-4` | **`double`** (the default!) |
| decimal with `f` suffix | `3.14f`, `1.0F` | `float` |
| boolean | `true`, `false` | `bool` |
| string | `"hello"` | **not supported — compile error** |

- **Gotcha:** `5.0` is a `double`, not a `float`. So
  `temp float x = 5.0;` is a **type-mismatch error** — write `5.0f`.
- String literals are recognized (so you get a precise diagnostic) but the
  language has no string type: `string literals are not supported (string
  type is reserved for future use)`. Supported escapes if you ever need them:
  `\n \t \r \" \\`.

## 13. Vector literals

Vectors (and quaternions) can only be built **at compile time**, using
typed-constructor syntax:

```fsm
Vector2(1.0f, 0.0f)
Vector3(0.0f, 1.0f, 0.0f)
Quaternion(0.0f, 0.0f, 0.0f, 1.0f)
```

Rules:

- Component count must match the type: `Vector2` → 2, `Vector3` → 3,
  `Quaternion` → 4 (error: `vector literal 'Vector3' expects 3 components,
  got N`).
- Each component must be a **constant `float` expression** — literals,
  arithmetic on them, and earlier `const` values (integer components are
  rejected: `vector literal 'Vector3' components must be float, got int`).
  So `Vector3(x, 1.0f, 2.0f)` with a runtime `x` is an error — vectors have
  **no runtime constructor**.
- Usable in constant initializers (`const`, `temp` initializers) and as call
  arguments, since they fold to immediate values.

## 14. Variable resolution and name rules

Lookup order when an identifier is used as a variable:

1. **Temporaries** — innermost enclosing block first (depth 3 → 2 → 1),
   including the current state's state-level frame.
2. **Globals** — `const` and `var` declarations that appear *earlier in the
   file* than the use.

Consequences:

- A temp **shadows** an identically-named global (and vice versa, the other
  way around).
- Unknown names are errors: `unknown variable 'x'`.
- Globals must be declared before first use in the file (one-pass parse).
- `const` initializers may only reference *earlier* consts.
- The same temp name may be reused in *different* blocks (new scope each
  time); it may not be redeclared in the *same* block.

## 15. Function calls and overload resolution

```fsm
funcName(arg1, arg2, ...);        // statement form (Tier 2 / Tier 3)
temp float d = distanceTo(a, b);  // expression form (Tier 1)
```

**Resolution is full-tuple, exact-type, with NO implicit conversions**
(spec 0.4). For a call `f(a₁, …, aₙ)`:

1. Look up all overloads of `f` (none → `unknown function 'f'`).
2. Keep the overloads with the right **arity**.
3. Keep the overloads whose parameter types **exactly** match every argument
   type — no widening, no promotion, no 2D/3D substitution.
4. Exactly one survivor → resolved. Zero → error. Two or more → ambiguity
   error.

Both failure modes list your argument types **and every candidate** with its
full signature, e.g.:

```
fsm.fsm:5:9: error: no overload of 'goTo' matches arguments (NavMeshAgent, int).
Candidates: goTo(NavMeshAgent agent, Vector3 dest);
            goTo(NavMeshAgent agent, Object3D dest);
            goTo(Object3D agent, Vector3 dest);
            goTo(Object3D agent, Object3D dest);
            goTo(Object2D agent, Vector2 dest);
            goTo(Object2D agent, Object2D dest)
```

Practical consequences:

- `goTo(agent, 5)` fails — there is no `(NavMeshAgent, int)` overload.
- A `float` where a `double` is expected fails — pass a `double` expression.
- 2D and 3D are separate overloads: `getPosition(Object3D)` returns `Vector3`,
  `getPosition(Object2D)` returns `Vector2`. Mixed (e.g. a `Camera3D` and an
  `Object2D`) fails.

## 16. Function tiers and claims

Functions come in three tiers; the tier determines where and how you may call
them.

| Tier | Meaning | Where it can appear | Constraint |
|---|---|---|---|
| 1 — query | Read-only engine state | expressions, conditions, initializers, call arguments | none |
| 2 — mutate | Writes engine state | statement only (`emit`, `setPosition`, …) | first argument (the mutated object) must **not** be a static `const` — pass a runtime `var` or `temp` |
| 3 — Controller-driven | Navigation / steering / control that the Controller owns | statement only | first argument must be a **variable** (runtime `var` or `temp`) so the ownership handoff can be encoded at compile time |

Tier 3 functions that take an agent/object **claim** fields of that variable
for the duration of the call. The compiler emits `CLAIM` before the call and
`RELEASE` after it. Which fields are claimed, per function:

| Function | Claimed fields |
|---|---|
| `goTo`, `findShortestPathAndMove`, `sprintTowards`, `moveTowards`, `stopMovement` | `agent.position`, `agent.velocity` |
| `followTarget`, `follow` | `agent.position`, `agent.velocity`, `agent.rotation` |
| `lookAt` | `src.rotation` |
| `wait`, `waitUntil` | (none) |

Claimed variables are treated as **Controller-owned** for the module: a
runtime `var` that is claimed by any Tier 3 call anywhere in the file is
marked owner = DSL in the binary.

```fsm
var NavMeshAgent agent;

State Chase {
    Actions {
        temp Vector3 dir = Vector3(0.0f, 0.0f, 1.0f);
        moveTowards(agent, dir, 5.0f);   // CLAIM position+velocity → call → RELEASE
    }
    Traversals { goto Chase; }
}
```

## 17. Built-in types

All 21 built-in types (the single source of truth is
`lib/builtin_types.cpp`; every type name in the compiler resolves through
`BuiltinTypes::find`):

| Type | Tag | Bytes | Kind |
|---|---|---|---|
| `void` | 0x00 | 0 | primitive, no storage (return type only) |
| `int` | 0x01 | 4 | primitive, numeric |
| `float` | 0x02 | 4 | primitive, numeric |
| `double` | 0x03 | 8 | primitive, numeric |
| `bool` | 0x04 | 1 | primitive |
| `Vector2` | 0x10 | 8 | vector, numeric (2 floats) |
| `Vector3` | 0x11 | 12 | vector, numeric (3 floats) |
| `Quaternion` | 0x12 | 16 | vector, numeric (4 floats) |
| `Object2D` | 0x20 | 4 | object handle |
| `Object3D` | 0x21 | 4 | object handle |
| `Transform2D` | 0x22 | 4 | object handle |
| `Transform3D` | 0x23 | 4 | object handle |
| `Camera2D` | 0x30 | 4 | camera handle |
| `Camera3D` | 0x31 | 4 | camera handle |
| `Sprite2D` | 0x40 | 4 | sprite handle |
| `Sprite3D` | 0x41 | 4 | sprite handle |
| `AnimationController2D` | 0x50 | 4 | animation handle |
| `AnimationController3D` | 0x51 | 4 | animation handle |
| `PhysicsObject2D` | 0x60 | 4 | physics handle |
| `PhysicsObject3D` | 0x61 | 4 | physics handle |
| `NavMeshAgent` | 0x70 | 4 | navigation handle |

Handles are opaque 4-byte references: no arithmetic, no comparisons, no
literals — only the functions below operate on them.

## 18. Built-in functions

All 179 overloads (single source of truth: `lib/builtin_functions.cpp`).
`T` = tier (1 query / 2 mutate / 3 Controller-driven). Where a name has
several overloads, each is listed on its own line.

### Math — T1 (IDs 0x0000–0x0018)

| Function | Signature |
|---|---|
| sin / cos / tan | `(float x) → float` |
| asin / acos / atan | `(float x) → float` |
| atan2 | `(float y, float x) → float` |
| sqrt | `(float x) → float` |
| pow | `(float base, float exp) → float` |
| abs | `(float x) → float` |
| sign | `(float x) → float` |
| clamp | `(float x, float lo, float hi) → float` |
| lerp | `(float a, float b, float t) → float` |
| min / max | `(float a, float b) → float` |
| floor / ceil / round | `(float x) → float` |
| normalize | `(Vector3 v) → Vector3` |
| dot | `(Vector3 a, Vector3 b) → float` |
| cross | `(Vector3 a, Vector3 b) → Vector3` |
| distance | `(Vector3 a, Vector3 b) → float` |
| magnitude | `(Vector3 v) → float` |
| random | `() → float` |
| randomRange | `(float lo, float hi) → float` |

### Object — (IDs 0x0100–0x0115)

| Function | Tier | Signature |
|---|---|---|
| getPosition | 1 | `(Object3D obj) → Vector3` ; `(Object2D obj) → Vector2` |
| getRotation | 1 | `(Object3D obj) → Quaternion` |
| getScale | 1 | `(Object3D obj) → Vector3` ; `(Object2D obj) → Vector2` |
| setPosition | 2 | `(Object3D obj, Vector3 pos)` ; `(Object2D obj, Vector2 pos)` |
| setRotation | 2 | `(Object3D obj, Quaternion rot)` |
| setScale | 2 | `(Object3D obj, Vector3 scale)` ; `(Object2D obj, Vector2 scale)` |
| distanceTo | 1 | `(Object3D a, Object3D b) → float` ; `(Object2D a, Object2D b) → float` |
| directionTo | 1 | `(Object3D a, Object3D b) → Vector3` ; `(Object2D a, Object2D b) → Vector2` |
| isActive | 1 | `(Object3D obj) → bool` ; `(Object2D obj) → bool` |
| setActive | 2 | `(Object3D obj, bool active)` ; `(Object2D obj, bool active)` |
| getTag | 1 | `(Object3D obj) → int` ; `(Object2D obj) → int` |
| getLayer | 1 | `(Object3D obj) → int` ; `(Object2D obj) → int` |

### Sprite — (IDs 0x0200–0x020B)

| Function | Tier | Signature |
|---|---|---|
| getColor | 1 | `(Sprite3D spr) → Vector3` ; `(Sprite2D spr) → Vector3` |
| setColor | 2 | `(Sprite3D spr, Vector3 rgb)` ; `(Sprite2D spr, Vector3 rgb)` |
| isVisible | 1 | `(Sprite3D spr) → bool` ; `(Sprite2D spr) → bool` |
| setVisible | 2 | `(Sprite3D spr, bool visible)` ; `(Sprite2D spr, bool visible)` |
| getBounds | 1 | `(Sprite3D spr) → Vector3` ; `(Sprite2D spr) → Vector2` |
| getSize | 1 | `(Sprite3D spr) → Vector2` ; `(Sprite2D spr) → Vector2` |

### Animation — (IDs 0x0300–0x0311)

| Function | Tier | Signature |
|---|---|---|
| play | 2 | `(AnimationController3D ctrl)` ; `(AnimationController2D ctrl)` |
| stop | 2 | `(AnimationController3D ctrl)` ; `(AnimationController2D ctrl)` |
| pause | 2 | `(AnimationController3D ctrl)` ; `(AnimationController2D ctrl)` |
| resume | 2 | `(AnimationController3D ctrl)` ; `(AnimationController2D ctrl)` |
| isPlaying | 1 | `(AnimationController3D ctrl) → bool` ; `(AnimationController2D ctrl) → bool` |
| getCurrentClip | 1 | `(AnimationController3D ctrl) → int` ; `(AnimationController2D ctrl) → int` |
| setSpeed | 2 | `(AnimationController3D ctrl, float speed)` ; `(AnimationController2D ctrl, float speed)` |
| getProgress | 1 | `(AnimationController3D ctrl) → float` ; `(AnimationController2D ctrl) → float` |
| setAnimation | 2 | `(AnimationController3D ctrl, int clip)` ; `(AnimationController2D ctrl, int clip)` |

### Physics — (IDs 0x0400–0x0411)

| Function | Tier | Signature |
|---|---|---|
| getVelocity | 1 | `(PhysicsObject3D phys) → Vector3` ; `(PhysicsObject2D phys) → Vector2` |
| setVelocity | 2 | `(PhysicsObject3D phys, Vector3 v)` ; `(PhysicsObject2D phys, Vector2 v)` |
| getMass | 1 | `(PhysicsObject3D phys) → float` ; `(PhysicsObject2D phys) → float` |
| applyForce | 2 | `(PhysicsObject3D phys, Vector3 force)` ; `(PhysicsObject2D phys, Vector2 force)` |
| applyImpulse | 2 | `(PhysicsObject3D phys, Vector3 impulse)` ; `(PhysicsObject2D phys, Vector2 impulse)` |
| isGrounded | 1 | `(PhysicsObject3D phys) → bool` ; `(PhysicsObject2D phys) → bool` |
| isColliding | 1 | `(PhysicsObject3D phys) → bool` ; `(PhysicsObject2D phys) → bool` |
| getCollisionNormal | 1 | `(PhysicsObject3D phys) → Vector3` ; `(PhysicsObject2D phys) → Vector2` |
| raycast | 1 | `(Vector3 origin, Vector3 dir, float dist) → bool` ; `(Vector2 origin, Vector2 dir, float dist) → bool` |

### Camera — (IDs 0x0500–0x0509)

| Function | Tier | Signature |
|---|---|---|
| getPosition | 1 | `(Camera3D cam) → Vector3` ; `(Camera2D cam) → Vector2` |
| isInView | 1 | `(Camera3D cam, Object3D obj) → bool` ; `(Camera2D cam, Object2D obj) → bool` |
| screenToWorld | 1 | `(Camera3D cam, Vector2 screen) → Vector3` ; `(Camera2D cam, Vector2 screen) → Vector2` |
| worldToScreen | 1 | `(Camera3D cam, Vector3 world) → Vector2` ; `(Camera2D cam, Vector3 world) → Vector2` |
| getViewport | 1 | `(Camera3D cam) → Vector2` ; `(Camera2D cam) → Vector2` |

### Navigation — (IDs 0x0600–0x062C)

| Function | Tier | Signature |
|---|---|---|
| findPath | 1 | `(Vector3 from, Vector3 to) → int` ; `(Vector2 from, Vector2 to) → int` |
| getNextWaypoint | 1 | `(int path) → Vector3` |
| getPathLength | 1 | `(int path) → float` |
| hasReachedDestination | 1 | `(NavMeshAgent agent, Vector3 tgt) → bool` ; `(NavMeshAgent agent, Object3D tgt) → bool` ; `(Object3D agent, Vector3 tgt) → bool` ; `(Object3D agent, Object3D tgt) → bool` ; `(Object2D agent, Vector2 tgt) → bool` ; `(Object2D agent, Object2D tgt) → bool` |
| goTo | 3 | `(NavMeshAgent agent, Vector3 dest)` ; `(NavMeshAgent agent, Object3D dest)` ; `(Object3D agent, Vector3 dest)` ; `(Object3D agent, Object3D dest)` ; `(Object2D agent, Vector2 dest)` ; `(Object2D agent, Object2D dest)` |
| followTarget | 3 | `(NavMeshAgent agent, Object3D tgt)` ; `(NavMeshAgent agent, Object2D tgt)` ; `(Object3D agent, Object3D tgt)` ; `(Object2D agent, Object2D tgt)` |
| findShortestPathAndMove | 3 | `(NavMeshAgent agent, Vector3 tgt)` ; `(NavMeshAgent agent, Object3D tgt)` ; `(Object3D agent, Vector3 tgt)` ; `(Object3D agent, Object3D tgt)` ; `(Object2D agent, Vector2 tgt)` ; `(Object2D agent, Object2D tgt)` |
| follow | 3 | `(NavMeshAgent agent, Object3D tgt)` ; `(NavMeshAgent agent, Object2D tgt)` ; `(Object3D agent, Object3D tgt)` ; `(Object2D agent, Object2D tgt)` |
| sprintTowards | 3 | `(NavMeshAgent agent, Vector3 dest, float speedMult)` ; `(NavMeshAgent agent, Object3D dest, float speedMult)` ; `(Object3D agent, Vector3 dest, float speedMult)` ; `(Object3D agent, Object3D dest, float speedMult)` ; `(Object2D agent, Vector2 dest, float speedMult)` ; `(Object2D agent, Object2D dest, float speedMult)` |
| moveTowards | 3 | `(NavMeshAgent agent, Vector3 dest, float speed)` ; `(NavMeshAgent agent, Object3D dest, float speed)` ; `(Object3D agent, Vector3 dest, float speed)` ; `(Object3D agent, Object3D dest, float speed)` ; `(Object2D agent, Vector2 dest, float speed)` ; `(Object2D agent, Object2D dest, float speed)` |
| stopMovement | 3 | `(NavMeshAgent agent)` ; `(Object3D agent)` ; `(Object2D agent)` |

### Perception — (IDs 0x0700–0x070D)

| Function | Tier | Signature |
|---|---|---|
| lookAt | 3 | `(Object3D src, Object3D tgt)` ; `(Object2D src, Object2D tgt)` |
| isInLineOfSight | 1 | `(Object3D src, Object3D tgt) → bool` ; `(Object2D src, Object2D tgt) → bool` |
| isInRange | 1 | `(Object3D src, Object3D tgt, float radius) → bool` ; `(Object2D src, Object2D tgt, float radius) → bool` |
| getAngleTo | 1 | `(Object3D src, Object3D tgt) → float` ; `(Object2D src, Object2D tgt) → float` |
| getDistanceTo | 1 | `(Object3D src, Object3D tgt) → float` ; `(Object2D src, Object2D tgt) → float` |
| getNearestOfTag | 1 | `(Vector3 pos, int tag, float radius) → Object3D` ; `(Vector2 pos, int tag, float radius) → Object2D` |
| getAllInRadius | 1 | `(Vector3 pos, float radius) → int` ; `(Vector2 pos, float radius) → int` |

### Steering — (IDs 0x0800–0x0809)

| Function | Tier | Signature |
|---|---|---|
| getFleeDirection | 1 | `(Vector3 from, Vector3 threat) → Vector3` ; `(Vector2 from, Vector2 threat) → Vector2` |
| getPursuitPosition | 1 | `(Object3D tgt, float speed) → Vector3` ; `(Object2D tgt, float speed) → Vector2` |
| getSeparationVector | 1 | `(Object3D agent, int neighbors) → Vector3` ; `(Object2D agent, int neighbors) → Vector2` |
| getArrivalVector | 1 | `(Vector3 pos, Vector3 dest, float slowRadius) → Vector3` ; `(Vector2 pos, Vector2 dest, float slowRadius) → Vector2` |
| getWanderVector | 1 | `(Vector3 pos, float radius) → Vector3` ; `(Vector2 pos, float radius) → Vector2` |

### Sensing — (IDs 0x0900–0x0901)

| Function | Tier | Signature |
|---|---|---|
| getRaycastHit | 1 | `(Vector3 origin, Vector3 dir, float dist) → Object3D` ; `(Vector2 origin, Vector2 dir, float dist) → Object2D` |

### Control — (IDs 0x0A00–0x0A02)

| Function | Tier | Signature |
|---|---|---|
| wait | 3 | `(float seconds)` |
| waitUntil | 3 | `(bool condition)` |
| emit | 2 | `(int eventId)` |

## 19. Diagnostics and exit codes

Every diagnostic is formatted `file:line:col: kind: message`:

```
fsm.fsm:5:9:  error: no overload of 'goTo' matches arguments (NavMeshAgent, int). Candidates: ...
fsm.fsm:8:9:  warning: bare goto before any if: subsequent statements are dead code (this transition always fires)
1 error, 1 warning
```

| Exit code | Meaning |
|---|---|
| 0 | compiled; `.fsmb` written (message `input.fsm -> output.fsmb (N bytes)`) |
| 1 | usage / I/O error (bad arguments, missing input file, unwritable output) |
| 2 | one or more compile **errors** — **no `.fsmb` is ever emitted**, not even partially. Warnings alone do not prevent output. |

## 20. What is NOT allowed

Quick index of the common compile errors (exact messages):

| Construct | Error |
|---|---|
| `goto` outside `Traversals` (state level, Actions, if body) | `goto is only allowed inside Traversals` |
| `return` anywhere | `return statements are not supported by this language` |
| `if` inside an `if`/`else if`/`else` body | `nested conditionals are not allowed inside conditionals` |
| `else` / `else if` in `Traversals` | `else is not allowed in Traversals` |
| Anything but one `goto` in a traversals `if` body | `Traversals if body must contain exactly one goto statement` |
| `Traversals` with no `goto` at all | `Traversals body must contain at least one goto statement` |
| `temp` in `Traversals` (body or if body) | `only 'if' and 'goto' statements are allowed in Traversals` |
| Bare `{ ... }` block not attached to a keyword | `unexpected block; { must follow a State, Actions, Traversals, if, else if, or else` |
| `var T x = ...;` (runtime initializer) | `runtime variables cannot have initializers (they are externally driven; values arrive through their binding slot)` |
| `const T x;` without initializer / non-constant initializer | `static constant 'x' initializer is not a compile-time constant: ...` |
| Assigning to a `const` | `static constants cannot be assigned: 'x'` |
| `const`/`var` inside a `State` body | `global 'const'/'var' declarations are not allowed inside a State body` |
| State with neither Actions nor Traversals | `state 'X' must contain an Actions or Traversals block` |
| Duplicate `Actions`/`Traversals` in one state | `duplicate Actions block in state 'X'` |
| No `@ENTRY` / two `@ENTRY` / entry names unknown state | `missing @ENTRY: exactly one @ENTRY is required per file` / `duplicate @ENTRY (exactly one is required per file)` / `@ENTRY must name a declared state: 'X'` |
| Duplicate state / global name | `duplicate state 'X'` / `duplicate global variable 'x'` |
| `void` as a variable type | `void cannot be used as a variable type` |
| String literal anywhere | `string literals are not supported (string type is reserved for future use)` |
| `vector * vector` | `vector * vector is not supported (use dot() for a dot product)` |
| Relational op on vectors / bool | `relational operators are not defined for vectors` |
| Mixed vector types in `+` `-` | `mixed vector types: Vector2 and Vector3` |
| `//` with non-int operands | `'//' requires two int operands, got ...` |
| `int / int` expected int | result is `float` — assign to a `float` |
| Type mismatch on init/assign | `type mismatch: cannot initialize 'x' of type 'float' with expression of type 'int'` |
| Non-bool `if` condition | `if condition must be a bool expression, got 'X'` |
| Unknown function / function | `unknown function 'name'` |
| No / multiple matching overloads | `no overload of 'name' matches arguments (...). Candidates: ...` / `call to 'name' ... is ambiguous. Candidates: ...` |
| Tier 2 first arg is a static `const` (e.g. `emit(constId)`) | `static constants cannot be the mutating argument of Tier 2 function 'name'` |
| Tier 3 first arg is not a variable | `Tier 3 function 'name' claims 'agent.position'; its first argument must be a runtime or temporary variable so the claim can be bound` |
| Out-of-scope temp | `unknown variable 'x'` |
| Redeclaration in same block | `redeclaration of 'x' in this scope` |
| `goto`/`@ENTRY` to unknown state | `unknown state 'X' in goto` / `@ENTRY must name a declared state: 'X'` |
| Unknown `@directive` | `unknown directive '@foo' (expected @ENTRY)` |
| Int literal outside 32-bit range | `integer literal out of range for int (32-bit)` |
| Wrong vector-literal arity / non-const / non-float component | `vector literal 'Vector3' expects 3 components, got N` / `... components must be constant float expressions (...)` / `... components must be float, got int` |

## 21. Worked example

The full language in one file (this is `tests/fixtures/two_state.fsm`):

```fsm
// two_state.fsm — if / else if / else, temps at all three depths,
// Traversals with two ifs + a bare default goto.
const float chaseRange = 10.0f;        // global const, compile-time constant

var Object3D player;                   // runtime vars: no initializers,
var Object3D enemy;                    // values arrive through binding slots
var NavMeshAgent agent;
var bool enemyVisible;

State Chase {
    temp float lastDist = 99.0f;       // depth 1: created on state entry,
                                       // destroyed on state exit
    Actions {
        temp float dist = getDistanceTo(player, enemy);   // depth 2, Tier 1 query
        lastDist = dist;                                  // exact-type assign
        if (enemyVisible) {
            temp Vector3 dir = normalize(directionTo(player, enemy)); // depth 3
            moveTowards(agent, dir, 5.0f);                // Tier 3: CLAIM/CALL/RELEASE
        } else if (lastDist < 3.0f) {
            followTarget(agent, enemy);
        } else {
            stopMovement(agent);
        }
    }
    Traversals {
        if (lastDist < 1.0f) { goto Flee; }   // state-level temp visible here
        if (!enemyVisible)   { goto Flee; }   // each if: exactly one goto
        goto Chase;                           // fallback last (no warning)
    }
}

State Flee {
    Actions {
        temp Vector3 away = getFleeDirection(getPosition(player), getPosition(enemy));
        moveTowards(agent, away, 8.0f);
    }
    Traversals {
        if (!enemyVisible) { goto Chase; }
        goto Flee;
    }
}

@ENTRY Chase                                // exactly one, names a declared state
```

Compile it:

```sh
./bin/fsmc fsmc/tests/fixtures/two_state.fsm     # → two_state.fsmb (1246 bytes)
```
