# fsmc — Design Notes

`fsmc` compiles one `.fsm` source file (one engine-agnostic AI system) into one
little-endian `.fsmb` binary module, byte-for-byte per spec v0.3 §3.

```
fsmc input.fsm -o output.fsmb       # compile
fsmc -d input.fsmb -o input.fsmd    # disassemble (read back + human-readable dump; -o - = stdout)
```

The **language reference** (syntax of every feature, all types/functions,
operator and scoping rules, all diagnostics) lives in [`LANGUAGE.md`](LANGUAGE.md);
this document covers the compiler pipeline and the binary format.

* **Languages/tools**: C++17, standard library only. Hand-rolled lexer +
  recursive-descent parser (no Bison/Flex/ANTLR). No third-party dependencies.
* **Build**: CMake ≥ 3.16. Targets: `fsmc` (CLI), `fsmc_tests` (tests via CTest).
* **Diagnistics**: `file:line:col: error: message`, exit code `2` on any error,
  `1` on usage/IO problems, `0` on success (warnings allowed). A binary is only
  written after every pass — including the link-back pass — succeeds, so no
  partial output ever exists.

---

## 1. Pipeline

```
tokens = lex::tokenize(source)          -- hand-rolled lexer (src/lexer.cpp)
src    = Parser(tokens).parse()         -- recursive descent (src/parser.cpp)
sym    = pass1_symbolTable(src)         -- src/passes.cpp
forest = pass2_buildAstForest(src, sym)
gotos  = pass3_collectGotos(src, forest)
fsm    = pass4_buildFsmAdjacency(src, gotos, sym)
bytes  = pass5_serialize(src, forest, fsm, sym)     -- src/serializer.cpp
link   = pass6_link(bytes)               -- reads bytes back, validates (src/module_reader.cpp)
```

The six passes are six distinct named functions with a visible boundary; none
are merged. Parser-level validations (C block scoping through the scope stack,
the mandatory `Start{}` / `Update{}` phase blocks inside every `Actions`, no
nested conditionals, no bare blocks, Traversals structure, overload resolution,
operator type rules, Tier-2/Tier-3 semantics) run during parsing, so the passes
operate on a well-formed forest.

| Pass | Name | Boundary |
|---|---|---|
| 1 | `pass1_symbolTable` | names + entry state → global/state tables, resolved `isEntry` |
| 2 | `pass2_buildAstForest` | forest + tables → temp canonical order, reference integrity |
| 3 | `pass3_collectGotos` | forest → per-state ordered goto lists |
| 4 | `pass4_buildFsmAdjacency` | goto lists + state table → adjacency matrix |
| 5 | `pass5_serialize` | everything → bytes (two sub-passes: layout, then emit) |
| 6 | `pass6_link` | bytes → read-back module; every address, child pointer, function id and operand must resolve, and every `ACTIONS` token must carry its `START`/`UPDATE` phase containers (§2.4) |

### Determinism

* Single source of truth for names/IDs: `BuiltinTypes` (22 types) and
  `BuiltinFunctions` (179 overloads) — `lib/`. Every type name is resolved via
  `BuiltinTypes::find`, every function via `BuiltinFunctions::find`; no list is
  duplicated elsewhere.
* The AST is an ordered forest; every container keeps declaration/execution
  order. No timestamps, no environment input, no hash-map iteration on the
  output path (the only `unordered_map` is a name→index lookup table).
* Output is byte-identical across runs and machines; verified by
  `golden_bytes.minimal_module_exact_bytes` (378 pinned bytes) and
  `golden_bytes.byte_identical_across_runs`.

---

## 2. Binary layout

All integers little-endian, written with explicit `writeU8/16/32/64` (no struct
memcpy, no undefined behavior). `std::vector<uint8_t>` buffers throughout.

### 2.1 Header (36 bytes)

| Offset | Size | Content |
|---|---|---|
| 0 | 4 | magic `0x46534D44` ("FSMD") |
| 4 | 2 | version major `0` |
| 6 | 2 | version minor `5` |
| 8..35 | 8×4 | section start offsets: Global, Runtime, Temporary, State, Token, AST, FSM |

Section offsets are absolute file offsets and always tile the file exactly:
`36 ≤ global ≤ runtime ≤ temp ≤ state ≤ token ≤ ast ≤ fsm ≤ filesize`, with the
FSM section ending at EOF.

**v0.1 → v0.2**: the scope-depth bytes were removed — one from every Temporary
Variable entry and one from every AST token (deviation 8).

**v0.2 → v0.3**: claim/ownership encoding was removed — the `owner` and
`dirty` bytes of every Runtime Variable entry (2 bytes × entry count) and the
`CLAIM`/`RELEASE` instructions bracketing Tier 3 calls (deviation 4).

**v0.3 → v0.4**: the `string` type (tag `0x05`) was added, and with it the
format's first **variable-width** value. No entry layout changed shape; a
string value simply occupies `[4] byte length` + UTF-8 bytes wherever a value
of that type is stored (§2.2).

**v0.4 → v0.5**: `Actions` gained the two mandatory phase blocks, encoded as
two new AST **container** tokens — `START` (`0x07`) and `UPDATE` (`0x08`) —
that hang off the state's `ACTIONS` token alongside its `TEMP_VAR_DECL`
children (§2.4). No existing entry changed shape: the tokens carry no data, so
each costs `[1] type [2] child-count` plus `[4]` per child, and `ACTIONS`
simply grew by two child addresses. The Temporary, Token and instruction
layouts are untouched, and the instruction stream stays flat and in source
order (§2.8).

The entry layouts are version-specific, so `readModule` rejects any other
major/minor pair (`unsupported module version X.Y (this fsmc reads v0.5)`)
instead of misparsing an older module.

### 2.2 Section IDs and entry formats

| ID | Name | Contents | Entry |
|---|---|---|---|
| 0 | null | (unused) | — |
| 1 | Global Variable | static constants | `[4] self-addr [1] type tag [N] value bytes [2] len [·] name` (`N` = the type's `sizeBytes`, or `4 + byte length` for `string`) |
| 2 | Runtime Variable | runtime variables | `[4] self-addr [1] type tag [4] binding slot [2] len [·] name` |
| 3 | Temporary Variable | temps (canonical order) | `[4] self-addr [1] type tag [2] len [·] name` |
| 4 | State | state directory | `[4] count` then `[4] AST-root addr [2] len [·] name` |
| 5 | Token / Instruction | per-state instruction frames | `[4] state-addr [2] instr-count`, then `[1] opcode [1] operand-count [4]×operand` |
| 6 | AST Adjacency | flat token array (pre-order DFS order) | `[1] type [2] child-count [4]×child` + type-specific data |
| 7 | FSM Adjacency | adjacency list | `[4] count` then `[4] src-state-addr [2] target-count [4]×target-state-addr` |

Neither the Temporary entry nor the AST token carries a scope depth: the block
that owns a temporary is **structural** — its `TEMP_VAR_DECL` token is a child
of the AST token of the `{ … }` body that declared it (see §6).

Self-addresses are packed addresses that point back at the entry itself; the
State entry's address field points at the state's AST root token instead (the
state is *defined by* its AST root).

**Value width.** Every type stores its value in exactly `sizeBytes` bytes —
except `string` (tag `0x05`, added in v0.4), whose width is per value:

```
[4] byte length (little-endian, UTF-8 bytes, no terminator)  then  [length] payload
```

That encoding is used in both places a value is stored: a Global Variable
entry's `[N] value bytes` and a `LITERAL` AST token's data after the tag byte.
`TypeDefinition::isVariableSize` marks the type, and a reader must branch on it
before trusting `sizeBytes` (which is `0` for `string`); `validateModule`
additionally requires the stored length to agree with the payload it is
followed by, in both places. Runtime and Temporary entries store no value at
all, so a `string` variable or temp is unaffected — only its tag byte differs.

### 2.3 32-bit address packing

```
[3 bits: section ID][29 bits: byte offset from section start]
```

**Deviation from spec draft**: the draft sketches a 2-bit section field, which
cannot represent section IDs 4..7 (e.g. `4 << 30` overflows a 32-bit word and
silently wraps to section 0). With 3 bits all 8 sections fit, at the cost of a
29-bit offset (~536 MiB per section — far beyond any module).

### 2.4 AST token types

| 0x.. | Token | Data |
|---|---|---|
| 0x01 | STATE | `[1] is-entry flag` |
| 0x02 | ACTIONS | (none) |
| 0x03 | TRAVERSALS | (none) |
| 0x04 | IF | `[4] condition expr address` |
| 0x05 | ELSE_IF | `[4] condition expr address` |
| 0x06 | ELSE | (none) |
| 0x07 | START | (none) — container for the `Start{}` phase block (v0.5) |
| 0x08 | UPDATE | (none) — container for the `Update{}` phase block (v0.5) |
| 0x10 | FUNCTION_CALL | `[2] function id [1] arg-count [4]×arg-expr-addr` |
| 0x11 | GOTO | `[4] state entry address` |
| 0x12 | TEMP_VAR_DECL | `[1] type tag [4] temp entry address` |
| 0x13 | ASSIGN | `[4] target var address [4] value expr address` |
| 0x14 | RETURN | `[4] value expr address` — **reserved**: no source construct emits it |
| 0x20 | BINARY_OP | `[1] op id [4] left [4] right` |
| 0x21 | UNARY_OP | `[1] op id [4] operand` |
| 0x22 | LITERAL | `[1] type tag [N] value bytes` (N = type size; for `string`, `[4] byte length` + UTF-8 bytes) |
| 0x23 | VAR_REF | `[4] variable entry address` |

AST emission order is pre-order DFS: a container/statement entry comes first,
then its condition, then its children in source order. The child edges are the
only nesting information in the module: a container token (`STATE`, `ACTIONS`,
`START`, `UPDATE`, `TRAVERSALS`, `IF`, `ELSE_IF`, `ELSE`) *is* a `{ … }` body,
so it is exactly one scope for temporaries.

`START` and `UPDATE` are always both present, in that order, among the
children of `ACTIONS` — the parser rejects a source that omits, reorders or
duplicates either one (§3.2, deviation 9). An `ACTIONS` child list therefore
reads: the body's own `TEMP_VAR_DECL` (and its initialising `ASSIGN`) tokens in
source order, then `START`, then `UPDATE`. A phase block with no statements is
a container with `child-count 0`; it still occupies its token, so a reader can
tell "empty phase" from "older module" without looking at the version.
`validateModule` (Pass 6) enforces the invariant: an `ACTIONS` token with no
`START` child, no `UPDATE` child, two of either, or `UPDATE` before `START` is
rejected (`ACTIONS token is missing its START/UPDATE phase blocks`, `ACTIONS has
more than one START child`, `START phase block does not precede UPDATE under
ACTIONS`), so a runtime can rely on the structure without re-deriving it. The Token section is framed per state:
`[4] state-addr [2] instr-count` — **documented deviation** from the
flat-instruction-list sketch, chosen so each state's stream is self-locating.

Variable-reference addresses may point into any of the Global (section 1),
Runtime (2) or Temporary (3) sections — the section bits identify the kind.

### 2.5 Instruction set

| 0x.. | Opcode | Operands | Notes |
|---|---|---|---|
| 0x01 | OpCall | `[AST FUNCTION_CALL address]` | |
| 0x02 | OpAssign | `[var address, value-expr AST address]` | |
| 0x03 | OpGoto | `[state entry address]` | |
| 0x04 | OpEval | `[AST address]` | **reserved** (evaluate, discard); never emitted |
| 0x05–0x13 | arithmetic/comparison | 0–3 operands | reserved runtime opcodes; never emitted by the current compiler (all arithmetic lives in the AST) |
| 0x14 | *(retired — was OpClaim)* | — | removed in v0.3; never reused, invalid in a module |
| 0x15 | *(retired — was OpRelease)* | — | removed in v0.3; never reused, invalid in a module |

**Every call compiles to exactly one `OpCall` at every tier.** Tier 3 used to
be bracketed by `CLAIM…; CALL; RELEASE…`; that is gone (§2.6).

### 2.6 Retired encodings (never reused)

Removed in module v0.3 — the format no longer says anything about who owns a
runtime variable or which of its fields a function drives:

* `OpClaim` (0x14) / `OpRelease` (0x15), with operands
  `[var address, field index]` where `position = 0`, `velocity = 1`,
  `rotation = 2`.
* The Runtime Variable entry's `owner` byte (`0x00` external / `0x01` DSL,
  compiler-derived in Pass 2) and its `dirty` byte (always `0x00` at compile
  time).

Per the ID policy these opcode values stay retired: `readModule`/
`validateModule` reject them as unknown opcodes, so a v0.2 module fails
cleanly instead of being misread.

What survives of the claim system is a **source-level rule only**: a Tier 3
call that drives an object takes it as its first argument, and that argument
must be a runtime or temporary variable — the parser rejects static constants,
literals and call results there (§3.7). Nothing about the choice is recorded in
the module.

### 2.7 Runtime-variable fields

* `type tag` — the variable's type (§4).
* `binding slot` — declaration order among runtime variables (0-based). This is
  the slot the Controller uses to push external values in.

### 2.8 Execution-order conventions (encoded in the streams)

1. **Entry phase** — state-level temp initializers (declaration order).
2. **Actions**, in source order:
   a. the `Actions`-body temp initializers (declaration order);
   b. the `Start{}` statements — **once, when the state is entered**;
   c. the `Update{}` statements — **every tick**.
3. **Traversals** — in source order; the runtime evaluates ifs in order and
   takes the first goto that fires (a trailing bare goto is the default
   transition).

The per-state instruction frame is one flat list in exactly that order and
carries no phase marker of its own: which phase an instruction belongs to is
**structural**, recovered the same way a temp's owning block is. `OpCall`
points at its `FUNCTION_CALL` token and `OpAssign` at its value-expression
token; walking that token's parent edges leads to `START` or `UPDATE` (or to
`ACTIONS` / `STATE` for a temp initializer, `TRAVERSALS` for a goto). A runtime therefore
executes the `START` subtree on entry only, the `UPDATE` subtree every tick,
and the temp initializers per the frame they belong to.

State-level temps are legal and **user-confirmed**: initialized once on state
entry (before Actions), destroyed on state exit, visible across the whole
State body (Actions and Traversals), *not* visible in other states.

---

## 3. Language semantics implemented

### 3.1 Global section

* `const <T> name = <const-expr>;` — static constant; initializer must be a
  compile-time constant expression (evaluated by `src/const_fold.cpp`),
  constants may reference earlier constants only, no forward references.
* `var <T> name;` — runtime variable; initializers are a **compile error**
  (values arrive through the binding slot).
* `@ENTRY <State>;` — exactly one per file, must name a declared state.

### 3.2 Temp variables and scope (C block scoping)

* `temp <T> name [= <expr>];` may appear in any `{ … }` body that accepts
  statements: the State body, the Actions body, a `Start{}` / `Update{}` phase
  body, or an if / else-if / else body.
* **`Actions` is split into two mandatory phase blocks** (module v0.5):
  `Start{}` — runs once when the state is entered — and `Update{}` — runs every
  tick. Both must be present, exactly once each, `Start{}` first, and they hold
  *all* action statements; the only thing allowed directly in the `Actions` body
  is a `temp` declaration, which both phases then share. The parser enforces
  each half of that rule with its own diagnostic (`Actions must contain a
  Start{} block …`, `Start{} must come before Update{} in the Actions body`,
  `duplicate Update{} block in the Actions body`, `action logic must be inside
  Start{} or Update{} …`); see LANGUAGE.md §8.
* **Scope stack, no fixed depth levels**: push a frame on every `{`, pop it on
  the matching `}`; declarations write to the top frame (the declaration's
  *direct parent block*), lookup walks the open frames top-to-bottom
  (innermost first), shadowing is allowed, redeclaration in the *same* block is
  an error, and a name is visible only **from its declaration onward**.
* Nothing is serialized about the scope: no depth byte in the Temporary
  Variable Section, none on AST tokens. Ownership is the parent edge of the
  temp's `TEMP_VAR_DECL` token (§6).
* The two phase bodies are **sibling frames**, not nested ones: a temp declared
  in `Start{}` is invisible in `Update{}` and vice versa, and — since `Start{}`
  is compiled first — an `Update{}` temp cannot be referenced from `Start{}`
  either.
* Traversals cannot declare temps (only `if`/`goto` statements are allowed
  there).

### 3.3 Conditionals

* `if (bool) {} else if (bool) {} else {}` — an else-if chain is one
  conditional (sibling IF / ELSE_IF / ELSE AST nodes sharing the chain).
* **Nested conditionals are a compile error.**
* Conditions must be `bool`; non-bool is a compile error.
* **Bare blocks** (`{ ... }` not attached to a keyword) are a compile error.

### 3.4 Traversals

* Body: any number of `if (bool) { goto <State>; }` plus at most one bare
  `goto <State>;` (which must be the last statement to be live).
* Each if body must contain **exactly one** goto.
* At least one goto in the body, `else` is not allowed, gotos in Actions are a
  compile error.
* A bare goto **before** any if emits a **warning** (dead code: that transition
  always fires) — the state still compiles.

### 3.5 Expressions and operators (C precedence)

`||` < `&&` < `==` `!=` < `<` `>` `<=` `>=` < `+` `-` < `*` `/` `//` < `**`
(right-associative) < unary `!` `-` < primary.

Type rules (no implicit conversions anywhere):

| Op | Rule |
|---|---|
| `&&` `||` | bool + bool → bool |
| `==` `!=` | exact same type (numeric, bool or string) → bool; handles rejected |
| `<` `>` `<=` `>=` | numeric scalars, either promoted (int→float→double); vectors **rejected** (even same-type); bool rejected |
| `+` `-` | same-type vectors component-wise; same-type scalars promoted; scalar `*` vector is the only mixed case |
| `*` | same-type vectors rejected (`vector * vector` — use `dot()`); scalar × vector both orders OK; scalars promoted |
| `/` | int / int → **float** (true division); otherwise promoted scalar |
| `//` | int // int → int, **floor** (toward −∞: `-7 // 2 == -4`) |
| `**` | scalar numeric only; same-type stays, mixed promotes |
| unary `!` | bool → bool |
| unary `-` | numeric (scalars and vectors, component-wise) |
| `+` on strings | `string` + `string` → `string` (concatenation) — the **only** arithmetic-shaped operator defined for strings; mixing a string with a number is an error, there is no conversion |
| other ops on strings | `<` `>` `<=` `>=` `-` `*` `/` `//` `**` `&&` `||` `!` unary `-` all rejected: `'<' is not defined for strings` |

* **Literals**: decimal/exponent literals default to **double**; the `f`
  suffix makes them **float**. Integers are 32-bit (out-of-range is an error).
* **Vector literals** are compile-time only, via typed-ctor syntax:
  `Vector3(1.0f, 2.0f, 3.0f)` — components must be constant float expressions
  (earlier `const` values allowed). Wrong arity or non-constant components are
  errors. Vectors have no runtime constructor.
* **String literals** (`"…"`, escapes `\n \t \r \" \\`) are full
  expressions of type `string`: they may initialize a `const` or a `temp`, be
  assigned to a runtime `var`, appear in conditions, and be concatenated.
  Constant concatenation is folded at compile time (`"he" + "llo"` emits one
  literal); a runtime operand emits a `BINARY_OP` with `OpPlus`.

### 3.6 Overload resolution (spec 0.4)

**Full-tuple exact-type matching, no implicit conversions.** For a call
`f(a₁, …, aₙ)`:

1. Look up all overloads of `f` in `BuiltinFunctions` (empty → `unknown
   function`).
2. Filter by arity.
3. Filter by **exact** type match of every argument (no promotion, no
   widening, no handle substitution).
4. Exactly one survivor → resolved; zero → error listing the argument types
   and **all candidates** with full signatures; two or more → ambiguous
   error, same candidate list.

Tier 2: the mutating argument (first parameter) cannot be a static constant.
Tier 3: the driven object (first parameter) must be a variable — see §3.7.

### 3.7 Tiers

* **Tier 1** — read-only queries; may appear in expressions and conditions.
* **Tier 2** — mutation of engine state; statement level; first argument is
  the mutated handle (must be a runtime variable or temp).
* **Tier 3** — Controller-driven (navigation/steering/control). Statement
  level only; compiles to one `OpCall` like every other call. The overloads
  that drive an object (`goTo`, `followTarget`, `findShortestPathAndMove`,
  `follow`, `sprintTowards`, `moveTowards`, `stopMovement`, `lookAt`) take it
  as their first argument, which must therefore be a runtime variable or a
  temp (`requiresVariableTarget` in the registry). `wait` / `waitUntil` are
  Tier 3 but drive no object, so any expression is accepted.

---

## 4. Type registry (`BuiltinTypes`, 22 entries)

| Name | Tag | Bytes | Kind |
|---|---|---|---|
| void | 0x00 | 0 | primitive (no storage) |
| int | 0x01 | 4 | primitive, numeric |
| float | 0x02 | 4 | primitive, numeric |
| double | 0x03 | 8 | primitive, numeric |
| bool | 0x04 | 1 | primitive |
| string | 0x05 | variable | primitive, **variable width**: `[4] byte length` + UTF-8 bytes |
| Vector2 | 0x10 | 8 | vector, numeric |
| Vector3 | 0x11 | 12 | vector, numeric |
| Quaternion | 0x12 | 16 | vector, numeric |
| Object2D / Object3D | 0x20 / 0x21 | 4 | object (handle) |
| Transform2D / Transform3D | 0x22 / 0x23 | 4 | object (handle) |
| Camera2D / Camera3D | 0x30 / 0x31 | 4 | camera (handle) |
| Sprite2D / Sprite3D | 0x40 / 0x41 | 4 | sprite (handle) |
| AnimationController2D / 3D | 0x50 / 0x51 | 4 | animation (handle) |
| PhysicsObject2D / 3D | 0x60 / 0x61 | 4 | physics (handle) |
| NavMeshAgent | 0x70 | 4 | navigation (handle) |

Every type reference in the compiler goes through
`BuiltinTypes::instance().find(name)` — the table in `lib/builtin_types.cpp`
is the only copy.

---

## 5. Function registry (`BuiltinFunctions`, 179 overloads)

IDs are **assigned once**, sequentially within each category's range, and are
stable forever: adding an overload appends at the end of the category's used
IDs; existing IDs are never renumbered, and retired IDs are never reused.

Category ranges:

| Category | Range | Overloads |
|---|---|---|
| Math | 0x0000–0x0018 | 25 |
| Object | 0x0100–0x0115 | 22 |
| Sprite | 0x0200–0x020B | 12 |
| Animation | 0x0300–0x0311 | 18 |
| Physics | 0x0400–0x0411 | 18 |
| Camera | 0x0500–0x0509 | 10 |
| Navigation | 0x0600–0x062C | 45 |
| Perception | 0x0700–0x070D | 14 |
| Steering | 0x0800–0x0809 | 10 |
| Sensing | 0x0900–0x0901 | 2 |
| Control | 0x0A00–0x0A02 | 3 |

Full ID table (name, id, tier, signature → return):

| ID | Name | Tier | Signature → Return |
|---|---|---|---|
| 0x0000 | sin | 1 | (float x) → float |
| 0x0001 | cos | 1 | (float x) → float |
| 0x0002 | tan | 1 | (float x) → float |
| 0x0003 | asin | 1 | (float x) → float |
| 0x0004 | acos | 1 | (float x) → float |
| 0x0005 | atan | 1 | (float x) → float |
| 0x0006 | atan2 | 1 | (float y, float x) → float |
| 0x0007 | sqrt | 1 | (float x) → float |
| 0x0008 | pow | 1 | (float base, float exp) → float |
| 0x0009 | abs | 1 | (float x) → float |
| 0x000A | sign | 1 | (float x) → float |
| 0x000B | clamp | 1 | (float x, float lo, float hi) → float |
| 0x000C | lerp | 1 | (float a, float b, float t) → float |
| 0x000D | min | 1 | (float a, float b) → float |
| 0x000E | max | 1 | (float a, float b) → float |
| 0x000F | floor | 1 | (float x) → float |
| 0x0010 | ceil | 1 | (float x) → float |
| 0x0011 | round | 1 | (float x) → float |
| 0x0012 | normalize | 1 | (Vector3 v) → Vector3 |
| 0x0013 | dot | 1 | (Vector3 a, Vector3 b) → float |
| 0x0014 | cross | 1 | (Vector3 a, Vector3 b) → Vector3 |
| 0x0015 | distance | 1 | (Vector3 a, Vector3 b) → float |
| 0x0016 | magnitude | 1 | (Vector3 v) → float |
| 0x0017 | random | 1 | () → float |
| 0x0018 | randomRange | 1 | (float lo, float hi) → float |
| 0x0100 | getPosition | 1 | (Object3D obj) → Vector3 |
| 0x0101 | getPosition | 1 | (Object2D obj) → Vector2 |
| 0x0102 | getRotation | 1 | (Object3D obj) → Quaternion |
| 0x0103 | getScale | 1 | (Object3D obj) → Vector3 |
| 0x0104 | getScale | 1 | (Object2D obj) → Vector2 |
| 0x0105 | setPosition | 2 | (Object3D obj, Vector3 pos) → void |
| 0x0106 | setPosition | 2 | (Object2D obj, Vector2 pos) → void |
| 0x0107 | setRotation | 2 | (Object3D obj, Quaternion rot) → void |
| 0x0108 | setScale | 2 | (Object3D obj, Vector3 scale) → void |
| 0x0109 | setScale | 2 | (Object2D obj, Vector2 scale) → void |
| 0x010A | distanceTo | 1 | (Object3D a, Object3D b) → float |
| 0x010B | distanceTo | 1 | (Object2D a, Object2D b) → float |
| 0x010C | directionTo | 1 | (Object3D a, Object3D b) → Vector3 |
| 0x010D | directionTo | 1 | (Object2D a, Object2D b) → Vector2 |
| 0x010E | isActive | 1 | (Object3D obj) → bool |
| 0x010F | isActive | 1 | (Object2D obj) → bool |
| 0x0110 | setActive | 2 | (Object3D obj, bool active) → void |
| 0x0111 | setActive | 2 | (Object2D obj, bool active) → void |
| 0x0112 | getTag | 1 | (Object3D obj) → int |
| 0x0113 | getTag | 1 | (Object2D obj) → int |
| 0x0114 | getLayer | 1 | (Object3D obj) → int |
| 0x0115 | getLayer | 1 | (Object2D obj) → int |
| 0x0200 | getColor | 1 | (Sprite3D spr) → Vector3 |
| 0x0201 | getColor | 1 | (Sprite2D spr) → Vector3 |
| 0x0202 | setColor | 2 | (Sprite3D spr, Vector3 rgb) → void |
| 0x0203 | setColor | 2 | (Sprite2D spr, Vector3 rgb) → void |
| 0x0204 | isVisible | 1 | (Sprite3D spr) → bool |
| 0x0205 | isVisible | 1 | (Sprite2D spr) → bool |
| 0x0206 | setVisible | 2 | (Sprite3D spr, bool visible) → void |
| 0x0207 | setVisible | 2 | (Sprite2D spr, bool visible) → void |
| 0x0208 | getBounds | 1 | (Sprite3D spr) → Vector3 |
| 0x0209 | getBounds | 1 | (Sprite2D spr) → Vector2 |
| 0x020A | getSize | 1 | (Sprite3D spr) → Vector2 |
| 0x020B | getSize | 1 | (Sprite2D spr) → Vector2 |
| 0x0300 | play | 2 | (AnimationController3D ctrl) → void |
| 0x0301 | play | 2 | (AnimationController2D ctrl) → void |
| 0x0302 | stop | 2 | (AnimationController3D ctrl) → void |
| 0x0303 | stop | 2 | (AnimationController2D ctrl) → void |
| 0x0304 | pause | 2 | (AnimationController3D ctrl) → void |
| 0x0305 | pause | 2 | (AnimationController2D ctrl) → void |
| 0x0306 | resume | 2 | (AnimationController3D ctrl) → void |
| 0x0307 | resume | 2 | (AnimationController2D ctrl) → void |
| 0x0308 | isPlaying | 1 | (AnimationController3D ctrl) → bool |
| 0x0309 | isPlaying | 1 | (AnimationController2D ctrl) → bool |
| 0x030A | getCurrentClip | 1 | (AnimationController3D ctrl) → int |
| 0x030B | getCurrentClip | 1 | (AnimationController2D ctrl) → int |
| 0x030C | setSpeed | 2 | (AnimationController3D ctrl, float speed) → void |
| 0x030D | setSpeed | 2 | (AnimationController2D ctrl, float speed) → void |
| 0x030E | getProgress | 1 | (AnimationController3D ctrl) → float |
| 0x030F | getProgress | 1 | (AnimationController2D ctrl) → float |
| 0x0310 | setAnimation | 2 | (AnimationController3D ctrl, int clip) → void |
| 0x0311 | setAnimation | 2 | (AnimationController2D ctrl, int clip) → void |
| 0x0400 | getVelocity | 1 | (PhysicsObject3D phys) → Vector3 |
| 0x0401 | getVelocity | 1 | (PhysicsObject2D phys) → Vector2 |
| 0x0402 | setVelocity | 2 | (PhysicsObject3D phys, Vector3 v) → void |
| 0x0403 | setVelocity | 2 | (PhysicsObject2D phys, Vector2 v) → void |
| 0x0404 | getMass | 1 | (PhysicsObject3D phys) → float |
| 0x0405 | getMass | 1 | (PhysicsObject2D phys) → float |
| 0x0406 | applyForce | 2 | (PhysicsObject3D phys, Vector3 force) → void |
| 0x0407 | applyForce | 2 | (PhysicsObject2D phys, Vector2 force) → void |
| 0x0408 | applyImpulse | 2 | (PhysicsObject3D phys, Vector3 impulse) → void |
| 0x0409 | applyImpulse | 2 | (PhysicsObject2D phys, Vector2 impulse) → void |
| 0x040A | isGrounded | 1 | (PhysicsObject3D phys) → bool |
| 0x040B | isGrounded | 1 | (PhysicsObject2D phys) → bool |
| 0x040C | isColliding | 1 | (PhysicsObject3D phys) → bool |
| 0x040D | isColliding | 1 | (PhysicsObject2D phys) → bool |
| 0x040E | getCollisionNormal | 1 | (PhysicsObject3D phys) → Vector3 |
| 0x040F | getCollisionNormal | 1 | (PhysicsObject2D phys) → Vector2 |
| 0x0410 | raycast | 1 | (Vector3 origin, Vector3 dir, float dist) → bool |
| 0x0411 | raycast | 1 | (Vector2 origin, Vector2 dir, float dist) → bool |
| 0x0500 | getPosition | 1 | (Camera3D cam) → Vector3 |
| 0x0501 | getPosition | 1 | (Camera2D cam) → Vector2 |
| 0x0502 | isInView | 1 | (Camera3D cam, Object3D obj) → bool |
| 0x0503 | isInView | 1 | (Camera2D cam, Object2D obj) → bool |
| 0x0504 | screenToWorld | 1 | (Camera3D cam, Vector2 screen) → Vector3 |
| 0x0505 | screenToWorld | 1 | (Camera2D cam, Vector2 screen) → Vector2 |
| 0x0506 | worldToScreen | 1 | (Camera3D cam, Vector3 world) → Vector2 |
| 0x0507 | worldToScreen | 1 | (Camera2D cam, Vector3 world) → Vector2 |
| 0x0508 | getViewport | 1 | (Camera3D cam) → Vector2 |
| 0x0509 | getViewport | 1 | (Camera2D cam) → Vector2 |
| 0x0600 | findPath | 1 | (Vector3 from, Vector3 to) → int |
| 0x0601 | findPath | 1 | (Vector2 from, Vector2 to) → int |
| 0x0602 | getNextWaypoint | 1 | (int path) → Vector3 |
| 0x0603 | getPathLength | 1 | (int path) → float |
| 0x0604 | hasReachedDestination | 1 | (NavMeshAgent agent, Vector3 tgt) → bool |
| 0x0605 | hasReachedDestination | 1 | (NavMeshAgent agent, Object3D tgt) → bool |
| 0x0606 | hasReachedDestination | 1 | (Object3D agent, Vector3 tgt) → bool |
| 0x0607 | hasReachedDestination | 1 | (Object3D agent, Object3D tgt) → bool |
| 0x0608 | hasReachedDestination | 1 | (Object2D agent, Vector2 tgt) → bool |
| 0x0609 | hasReachedDestination | 1 | (Object2D agent, Object2D tgt) → bool |
| 0x060A | goTo | 3 | (NavMeshAgent agent, Vector3 dest) → void — drives `agent` |
| 0x060B | goTo | 3 | (NavMeshAgent agent, Object3D dest) → void — drives `agent` |
| 0x060C | goTo | 3 | (Object3D agent, Vector3 dest) → void — drives `agent` |
| 0x060D | goTo | 3 | (Object3D agent, Object3D dest) → void — drives `agent` |
| 0x060E | goTo | 3 | (Object2D agent, Vector2 dest) → void — drives `agent` |
| 0x060F | goTo | 3 | (Object2D agent, Object2D dest) → void — drives `agent` |
| 0x0610 | followTarget | 3 | (NavMeshAgent agent, Object3D tgt) → void — drives `agent` |
| 0x0611 | followTarget | 3 | (NavMeshAgent agent, Object2D tgt) → void — drives `agent` |
| 0x0612 | followTarget | 3 | (Object3D agent, Object3D tgt) → void — drives `agent` |
| 0x0613 | followTarget | 3 | (Object2D agent, Object2D tgt) → void — drives `agent` |
| 0x0614 | findShortestPathAndMove | 3 | (NavMeshAgent agent, Vector3 tgt) → void — drives `agent` |
| 0x0615 | findShortestPathAndMove | 3 | (NavMeshAgent agent, Object3D tgt) → void — drives `agent` |
| 0x0616 | findShortestPathAndMove | 3 | (Object3D agent, Vector3 tgt) → void — drives `agent` |
| 0x0617 | findShortestPathAndMove | 3 | (Object3D agent, Object3D tgt) → void — drives `agent` |
| 0x0618 | findShortestPathAndMove | 3 | (Object2D agent, Vector2 tgt) → void — drives `agent` |
| 0x0619 | findShortestPathAndMove | 3 | (Object2D agent, Object2D tgt) → void — drives `agent` |
| 0x061A | follow | 3 | (NavMeshAgent agent, Object3D tgt) → void — drives `agent` |
| 0x061B | follow | 3 | (NavMeshAgent agent, Object2D tgt) → void — drives `agent` |
| 0x061C | follow | 3 | (Object3D agent, Object3D tgt) → void — drives `agent` |
| 0x061D | follow | 3 | (Object2D agent, Object2D tgt) → void — drives `agent` |
| 0x061E | sprintTowards | 3 | (NavMeshAgent agent, Vector3 dest, float speedMult) → void — drives `agent` |
| 0x061F | sprintTowards | 3 | (NavMeshAgent agent, Object3D dest, float speedMult) → void — drives `agent` |
| 0x0620 | sprintTowards | 3 | (Object3D agent, Vector3 dest, float speedMult) → void — drives `agent` |
| 0x0621 | sprintTowards | 3 | (Object3D agent, Object3D dest, float speedMult) → void — drives `agent` |
| 0x0622 | sprintTowards | 3 | (Object2D agent, Vector2 dest, float speedMult) → void — drives `agent` |
| 0x0623 | sprintTowards | 3 | (Object2D agent, Object2D dest, float speedMult) → void — drives `agent` |
| 0x0624 | moveTowards | 3 | (NavMeshAgent agent, Vector3 dest, float speed) → void — drives `agent` |
| 0x0625 | moveTowards | 3 | (NavMeshAgent agent, Object3D dest, float speed) → void — drives `agent` |
| 0x0626 | moveTowards | 3 | (Object3D agent, Vector3 dest, float speed) → void — drives `agent` |
| 0x0627 | moveTowards | 3 | (Object3D agent, Object3D dest, float speed) → void — drives `agent` |
| 0x0628 | moveTowards | 3 | (Object2D agent, Vector2 dest, float speed) → void — drives `agent` |
| 0x0629 | moveTowards | 3 | (Object2D agent, Object2D dest, float speed) → void — drives `agent` |
| 0x062A | stopMovement | 3 | (NavMeshAgent agent) → void — drives `agent` |
| 0x062B | stopMovement | 3 | (Object3D agent) → void — drives `agent` |
| 0x062C | stopMovement | 3 | (Object2D agent) → void — drives `agent` |
| 0x0700 | lookAt | 3 | (Object3D src, Object3D tgt) → void — drives `src` |
| 0x0701 | lookAt | 3 | (Object2D src, Object2D tgt) → void — drives `src` |
| 0x0702 | isInLineOfSight | 1 | (Object3D src, Object3D tgt) → bool |
| 0x0703 | isInLineOfSight | 1 | (Object2D src, Object2D tgt) → bool |
| 0x0704 | isInRange | 1 | (Object3D src, Object3D tgt, float radius) → bool |
| 0x0705 | isInRange | 1 | (Object2D src, Object2D tgt, float radius) → bool |
| 0x0706 | getAngleTo | 1 | (Object3D src, Object3D tgt) → float |
| 0x0707 | getAngleTo | 1 | (Object2D src, Object2D tgt) → float |
| 0x0708 | getDistanceTo | 1 | (Object3D src, Object3D tgt) → float |
| 0x0709 | getDistanceTo | 1 | (Object2D src, Object2D tgt) → float |
| 0x070A | getNearestOfTag | 1 | (Vector3 pos, int tag, float radius) → Object3D |
| 0x070B | getNearestOfTag | 1 | (Vector2 pos, int tag, float radius) → Object2D |
| 0x070C | getAllInRadius | 1 | (Vector3 pos, float radius) → int |
| 0x070D | getAllInRadius | 1 | (Vector2 pos, float radius) → int |
| 0x0800 | getFleeDirection | 1 | (Vector3 from, Vector3 threat) → Vector3 |
| 0x0801 | getFleeDirection | 1 | (Vector2 from, Vector2 threat) → Vector2 |
| 0x0802 | getPursuitPosition | 1 | (Object3D tgt, float speed) → Vector3 |
| 0x0803 | getPursuitPosition | 1 | (Object2D tgt, float speed) → Vector2 |
| 0x0804 | getSeparationVector | 1 | (Object3D agent, int neighbors) → Vector3 |
| 0x0805 | getSeparationVector | 1 | (Object2D agent, int neighbors) → Vector2 |
| 0x0806 | getArrivalVector | 1 | (Vector3 pos, Vector3 dest, float slowRadius) → Vector3 |
| 0x0807 | getArrivalVector | 1 | (Vector2 pos, Vector2 dest, float slowRadius) → Vector2 |
| 0x0808 | getWanderVector | 1 | (Vector3 pos, float radius) → Vector3 |
| 0x0809 | getWanderVector | 1 | (Vector2 pos, float radius) → Vector2 |
| 0x0900 | getRaycastHit | 1 | (Vector3 origin, Vector3 dir, float dist) → Object3D |
| 0x0901 | getRaycastHit | 1 | (Vector2 origin, Vector2 dir, float dist) → Object2D |
| 0x0A00 | wait | 3 | (float seconds) → void |
| 0x0A01 | waitUntil | 3 | (bool condition) → void |
| 0x0A02 | emit | 2 | (int eventId) → void |

### ID stability policy

* IDs are allocated sequentially inside category ranges and **never
  renumbered**: a new overload takes the next free ID in its category range;
  removed overloads leave a permanent gap.
* The category base + range bounds above are part of the module ABI: readers
  may reject out-of-range IDs.
* `lib_functions` tests enforce: 179 overloads, unique IDs, reachability by
  name, per-category sequential layout, and the per-overload
  `requiresVariableTarget` flags (37 Tier 3 overloads set, `wait`/`waitUntil`
  and every Tier 1/2 clear).

In the table above, "drives `X`" marks a Tier 3 overload whose first parameter
is the object it drives: the source must pass a runtime variable or a temp
there. It is a parse-time rule; no byte of the module records it.

---

## 6. Scope model (scope stack, no depth levels)

Scoping is C block scoping implemented as a pure scope stack (`include/scope.hpp`):
one frame per `{ … }` body, pushed on `{` and popped — with all of its
declarations discarded — on the matching `}`. A frame has an id (handed out on
push, never reused); a `temp` records the id of the frame it was declared in
(`TempVar::scopeId`) as internal bookkeeping, and lookup resolves innermost
frame first. There is **no cap on the number of open frames and no numbered
depth level anywhere** — not in the AST (`Stmt` / `TempVar` have no depth
field), not in the binary (§2.2).

Block lifetimes at runtime:

| Block (AST token) | Lifetime of the temps declared in it |
|---|---|
| State body (`STATE`) | created once on state entry, destroyed on state exit; visible in Actions **and** Traversals, and in every block nested inside the state body |
| Actions body (`ACTIONS`) | created when the block runs each tick, destroyed at block end; visible in **both** phase blocks |
| `Start{}` body (`START`) | created when the entry phase runs, destroyed when it finishes; not visible in `Update{}` or in Traversals |
| `Update{}` body (`UPDATE`) | created when that tick's phase runs, destroyed when it finishes; not visible in `Start{}` (which already ran) or in Traversals |
| Traversals body (`TRAVERSALS`) | (no temps are allowed here) |
| if / else-if / else body (`IF` / `ELSE_IF` / `ELSE`) | created while that branch is active, destroyed at branch end; each branch of a chain is its own frame |

Rules: temps only inside `{ … }` bodies (file-level `temp` is an error); a name
is visible from its declaration to the end of its block, including in nested
blocks (where it may be shadowed); same-block redeclaration is an error; a use
outside the declaring block — after it closed, in a sibling branch, or before
the declaration — is `unknown variable 'x'`.

**Ownership is structural.** Because a temp's scope is exactly the block whose
child its declaration is, a reader recovers the scope of any temporary from the
AST alone: walk a state's tree and remember the nearest enclosing container
token (`STATE` / `ACTIONS` / `START` / `UPDATE` / `TRAVERSALS` / `IF` /
`ELSE_IF` / `ELSE`) of each `TEMP_VAR_DECL`. `fsmc -d` does exactly that and prints it as the temp's
`scope` (the disassembler's `tempScope` map).

---

## 7. Overload-resolution rule (restated, normative)

Given argument types `T₁..Tₙ`: a candidate matches iff `argc == n` and for
every `i`, `Tᵢ == paramTypeᵢ` **exactly** (same type tag). No implicit
conversions, no promotion at the call boundary, no handle polymorphism.
Zero matches → error with the argument list and every candidate's full
signature; >1 match → ambiguous error, same list.

---

## 8. Deviations from spec v0.3 (and why)

1. **Address section field is 3 bits (29-bit offset), not 2 bits.** The spec
   defines 8 section IDs (0..7) but sketches a 2-bit section field; IDs ≥ 4
   overflow the 32-bit word (`4 << 30` wraps to section 0) — the format as
   sketched cannot encode its own State/Token/AST/FSM sections.
2. **Token/Instruction section is framed per state** —
   `[4] state-addr [2] instr-count` per state — instead of one flat
   instruction list; makes each state's stream self-locating and the frame
   count cross-checkable against the state directory.
3. **`goTo` has 6 overloads** — `{NavMeshAgent × {Vector3, Object3D}}`,
   `{Object3D × {Vector3, Object3D}}`, `{Object2D × {Vector2, Object2D}}` —
   i.e. `NavMeshAgent` (a 3D-only type) is not paired with `Object2D`. This
   keeps Navigation at 45 overloads (0x0600–0x062C) and mirrors the
   `hasReachedDestination`/`followTarget`/`follow` overload shapes.
4. **Runtime-variable ownership is not encoded at all** (module v0.3). The
   spec's handoff — an `owner` byte per runtime variable plus `CLAIM`/
   `RELEASE` instructions around Tier 3 calls — was dropped: the DSL makes no
   statement about who owns a variable or which fields a function touches, so
   Pass 2 has nothing to derive and a call is a single `OpCall`. What remains
   is the source-level rule that a Tier 3 call's driven first argument must be
   a variable (§2.6, §3.7).
5. **`//` (floor division) vs `//` (line comment)** are disambiguated by
   lexer context, as in C-like languages needing both: `//` is floor
   division only when it directly follows a value token (digit, letter, `)`,
   `]`, `"` — no intervening whitespace or newline); otherwise it starts a
   line comment. `x; // c` is a comment; `7 // 2` is floor division.
6. **`string` is a first-class but deliberately narrow type** (module v0.4).
   The draft reserved it and rejected every literal; it is now registered as
   tag `0x05` and usable in constants, runtime variables, temps, conditions
   and assignments, with `+` (concatenation) and `==` / `!=` as its only
   operators. Two things the draft did not settle are decided here: (a) the
   spelling is lowercase `string`, matching the other primitives — `String`
   remains an unknown type name; (b) **no built-in function takes a string**,
   so the 179-entry registry is untouched and a string can only reach the
   engine through a runtime variable's binding slot. Because a string's width
   is per value, `TypeDefinition` gained an `isVariableSize` flag and every
   reader branches on it before trusting `sizeBytes` (§2.2).
7. **State-level temps are legal** (declared in the State body's own scope
   frame) — initialized once on state entry, destroyed on state exit — per the
   user-confirmed correction to the draft's "no temp vars at State level"
   wording.
8. **No scope-depth field** (module v0.2). The draft's `[1] scope depth` byte
   in the Temporary Variable Section and the `[1] depth` byte on every AST
   token assumed three fixed depth levels (State body / block body / if body).
   Temporaries are C block-scoped through a scope stack instead: a temp lives
   in its direct parent `{ … }` and dies at that block's `}`, at any nesting
   level, so a fixed level cannot describe it — and the nesting is already
   encoded by the AST child edges. Both bytes were dropped (the format minor
   version was bumped 1 → 2); the owning block of a temp is recovered from the
   AST parent edge (§6).
9. **`Actions` requires a `Start{}` and an `Update{}` block** (module v0.5).
   The draft put all action statements directly in the `Actions` body and left
   the entry-once / every-tick split to the runtime's discretion. Per the
   user-confirmed change, the split is now part of the language: both blocks are
   mandatory, `Start{}` comes first, each appears exactly once, and only `temp`
   declarations may sit directly in the `Actions` body (shared by both phases).
   Encoded as two new AST container tokens, `START` (`0x07`) and `UPDATE`
   (`0x08`), under `ACTIONS` — the instruction frame layout and every other
   entry are unchanged, so the format minor version was bumped 4 → 5 (§2.1,
   §2.4, §2.8).

---

## 9. Test coverage

CTest suites (hand-rolled framework, `fsmc_tests [filter]`):

| Suite | Covers |
|---|---|
| `lib_types` | 22 types, name reachability, tag uniqueness, spec table (incl. `string` at 0x05 and `String` still unknown) |
| `lib_functions` | 179 overloads, unique/stable IDs, sequential category ranges, spec overload counts, Tier-3 `requiresVariableTarget` flags |
| `lexer` | keywords (case-sensitive, `Start`/`Update` included — and `Startx`/`xUpdate` staying identifiers), literal kinds (`int`/`float`/`double`), operators, `//` disambiguation, comments, strings, `@ENTRY`, line/col tracking |
| `parser` | AST shape, unknown types/variables, const folding (precedence, `//` floor toward −∞, int/int→float), vector literals, operator type rules, string rejection, runtime-init rejection |
| `scope` | in/out of scope, shadowing, same-block redeclaration, visible-from-declaration-onward, one frame per block (same block shares a frame; sibling branches do not), state-level temps (visible in Actions + Traversals; **not** visible in other states), file-level temp rejection, plus direct `ScopeStack` unit tests (unbounded nesting, innermost-first lookup, pop discards, unique frame ids) |
| `phases` | the `Start{}`/`Update{}` rule set end to end: both blocks mandatory (and the two diagnostics when neither is present), each exactly once, `Start{}` first, empty phases allowed, every non-`temp` statement rejected directly in the `Actions` body (call / assignment / `if` / `goto`), `temp` declarations allowed there and visible in both phases, phase bodies as sibling frames (a `Start{}` temp unknown in `Update{}` and vice versa, one name reusable in both), `Start`/`Update` reserved as identifiers, `START`/`UPDATE` as `ACTIONS` children in the AST with no data and per-phase child lists, instruction order `Start` before `Update`, disassembly phase labels, the two `errors/` phase fixtures, validation rejecting a module that lost a phase container, and every fixture declaring both blocks |
| `overloads` | `goTo` success/failure with candidate lists, arity failure, unknown function, Tier-2 const rejection, Tier-3 driven-argument-must-be-a-variable (and `wait`/`waitUntil` accepting literals), temp targets, nested-call type propagation |
| `traversals` | full failure matrix (empty body, no goto, extra statements, two gotos, else, else-if, temps) + bare-goto warning + adjacency with duplicates preserved |
| `passes` | per-pass outputs (goto order, adjacency), single-entry rules, duplicate detection, nested-conditional fail / else-if chain succeed, bare-block fail, no CLAIM/RELEASE emitted for Tier-3 calls, structural temp ownership in the AST (each temp's declaration is a child of its owning block token — `STATE`, `ACTIONS` or `IF`), round-trip read-back & compare, corruption rejection, determinism |
| `golden_bytes` | **pinned 392-byte module** for `tests/fixtures/minimal.fsm` (hand-verified; v0.5 adds the `START`/`UPDATE` container tokens to v0.4's 378 bytes), module version pinned to v0.5 + rejection of v0.1–v0.4 by name, header/offset tiling by independent walk, two-state module contents, byte-identical reruns |
| `strings` | the `string` type end to end: registry entry, literals as expressions, lexer escape decoding, folded constant concatenation, runtime `BINARY_OP` concatenation, `==`/`!=` yielding bool, every rejected operator, type mismatches, `String` still unknown, length-prefixed bytes in the Global section and in LITERAL tokens, fixture round-trip + validation, quoted/re-escaped disassembly, corrupt-length rejection |
| `disassemble` | `-d` path: read+validate fixtures, name resolution (states/vars/functions), absence of the claim/ownership vocabulary, decoded literals (scalars + vectors), the temps' owning-block column (recovered from the AST parent edge), the `START (runs once, on state entry)` / `UPDATE (runs every tick)` phase labels, corrupted/truncated/wrong-version module rejection, deterministic dump output |

Every fixture and every `samples/*.fsm` file declares both phase blocks.

Fixtures: `tests/fixtures/minimal.fsm` (one state, a self-loop, an `Actions`-body
temp and an empty `Start{}`), `tests/fixtures/two_state.fsm` (if/else-if/else,
temps in the state body / Actions body / if body, a seeding `Start{}`, Traversals
with two ifs + bare default goto, Tier 3 driving calls),
`tests/fixtures/strings.fsm` (string constants + folding, a runtime string,
string temps, escapes, comparisons driving transitions), and
`tests/fixtures/errors/*.fsm` (one file per major error class).
