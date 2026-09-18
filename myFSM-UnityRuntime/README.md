# myFSM-UnityRuntime

Pure C# Unity runtime for myFSM: loads compiler-produced `.fsmb` modules
(module v0.5) and runs each one as an AI on a GameObject.

- **Loader**: byte-exact port of the compiler's module reader + validator.
- **AI model**: one loaded module <=> one AI. Generated classes inherit the
  abstract `AIInstance` (a MonoBehaviour); `FsmbAIInstance` is the generic,
  inspector-driven version (assign a `.fsmb`, no codegen needed).
- **Serving**: three logical callback servers per AI (Start / Update /
  Traversals). Statements are clients with callbacks, served in source order.
- **Variables**: constants + runtime binding slots + temporaries with C-like
  block lifetimes (a temp dies when its parent `{}` closes).
- **Backend servers** (independent): main server (registry + DB + timetable),
  query server (OS-scheduler-like service desk for external systems) and two
  live broadcast servers (ordered + priority) for state changes.
- **Functions**: Unity implementations of all 179 built-in overloads.
  Motion/rotation calls are incremental across ticks (NavMesh when available,
  manual `position += direction * speed * dt` otherwise) — never teleports,
  and manual movement is swept against colliders so bodies stop at walls and
  slide along them instead of passing through.

Language level is **C# 7.3**, UnityEngine only, no packages — compiles in
Unity 2019+ and in the sandbox harness (`Sandbox/`).

## Docs (start here)

| Guide | Covers |
|---|---|
| [`Docs/Setup.md`](Docs/Setup.md) | Requirements, install, wiring an AI (inspector + generated class), multiple AIs, troubleshooting, uninstall |
| [`Docs/MainServer.md`](Docs/MainServer.md) | Registry, frame tick, DB ownership, query-backend routing, public API, driving it from game code |
| [`Docs/QueryServer.md`](Docs/QueryServer.md) | Client lifecycle, all 8 queries + 6 commands, scheduling algorithm, caps & tuning, full error catalog |
| [`Docs/BroadcastServers.md`](Docs/BroadcastServers.md) | Subscribing, ordered vs priority delivery, verdicts A/B/C, re-entrancy rules, pitfalls |
| [`Docs/AIInstance.md`](Docs/AIInstance.md) | Base class part-by-part, generated child anatomy, where/how to bind manually, precedence |
| [`Docs/Compiler.md`](Docs/Compiler.md) | Native lib + wrapper, manual/dynamic/burst compile, inspector workflow, failure modes |
| [`Docs/Reference.md`](Docs/Reference.md) | **All 179 built-in functions** (ID, signature, tier, behaviour) + every override point and the host API |

## Layout

```
Runtime/
  Core/    no UnityEngine dependency (loader, values, tables, servers, DB)
    FsmbFormat.cs         .fsmb v0.5 constants (mirrors binary_format.hpp)
    FsmbReader.cs         decoder + validator (mirrors module_reader.cpp)
    FunctionCatalog.cs    the 179 overloads (mirrors builtin_functions)
    FsmValue.cs           tagged runtime values, literal decoding
    VariableTable.cs      consts + runtime slots + scoped temps
    AiExecution.cs        per-AI context (servers, suspension, transitions)
    ExpressionEvaluator.cs DSL operator semantics (no implicit conversions)
    Callbacks.cs          statement clients + AiCallback + block serving
    CallbackServers.cs    Start / Update / Traversal servers
    StateHandler.cs       head state, entry/exit, per-tick flow, goto rules
    Db.cs                 asset/instance records, timetable, emit log
    QueryServer.cs        scheduler (cap 15, N/M bounds, congestion, aging)
    Broadcast.cs          ordered + priority state-change servers
  Unity/   MonoBehaviour glue + function implementations
    AIInstance.cs         abstract base + FsmbAIInstance + bindings
    MainServer.cs         registry, DB, tick loop, query backend
    HandleTable.cs        per-AI handle-id -> Unity object
    FunctionDispatcher.cs ID router + resolution helpers
    MovementSystem.cs     incremental goals (NavMesh or manual) + look goals
    ClassGenerator.cs     .fsmb -> named C# class source (editor-time)
    Functions/            Math Object Sprite Animation Physics Camera
                          Navigation Perception Steering Sensing Control
Docs/      Setup, Reference (all functions + overrides), MainServer, QueryServer,
           BroadcastServers, AIInstance, Compiler guides
Samples/   generated-class examples (what ClassGenerator emits)
Sandbox/   compile/run harness WITHOUT Unity (stubs + headless smoke test);
           NEVER installed into a Unity project (excluded in the manifest)
myfsm-package.json   package manifest (name, version, install dir, file map)
```

## Concepts & tick flow

One `.fsmb` module loaded onto one GameObject is one AI. The module's states,
phases (`Start{}`/`Update{}`), transitions and variables all come from the
compiled file; the Unity side supplies bound scene objects, time, and the
engine services behind the 179 built-in functions.

Every frame, each AI ticks itself in its own `Update()` (Unity component
order — the main server never ticks AIs, it only keeps the registry). One
AI's frame:

1. `MovementSystem.Advance` — in-flight motion/rotation goals step closer
   (fresh positions for this tick's decisions; runs even while suspended).
2. `StateHandler.Tick` — on the very first tick the head is entered
   (none → entry) and its Start round runs; then the suspension check
   (`wait`/`waitUntil`), then any externally commanded transition, then the
   Update round, then the Traversals round, then the requested transition
   (if any).
3. If the head state changed: record it in the DB timetable and publish
   one `StateChangeEvent` to **both** broadcast servers.

After every AI has ticked, the server's `LateUpdate()` runs `Db.TotalTicks++`,
then `Queries.Tick()` — the query scheduler serves external clients.

Errors during serving never throw inside the player loop: they log (with the
AI's `[myFSM name]` prefix) and yield default values. Only boot/structural
problems fail fast.

## Quickstart (Unity)

1. Compile a model: `fsmc patrol.fsm -o patrol.fsmb`.
2. Copy `Runtime/` into your project (default `Assets/MyFSM`).
3. Add `FsmbAIInstance` to a GameObject, assign the `.fsmb`, fill the binding
   slots (list index = slot).
4. Play — the main server boots automatically; the AI enters `@ENTRY`.

Full detail (generated classes, Resources paths, bindings, troubleshooting):
[`Docs/Setup.md`](Docs/Setup.md).

```csharp
// Queries/commands from any system:
QueryClient cli = MainServer.EnsureExists().ConnectExternal("ui");
MainServer.EnsureExists().Queries.Enqueue(cli.ClientId, new ServerRequest {
    IsCommand = false, Code = (int)QueryCode.GetStats });

// Live state changes, no polling:
MainServer.EnsureExists().OrderedBroadcast.Subscribe(
    new ActionWithStateSubscriber(ai.InstanceId, s => Debug.Log("now in " + s)));
```

## Variables & bindings

- **Constants** (`const`): decoded once at boot, read-only.
- **Runtime slots** (`var`): Controller-facing state. Handle slots hold bound
  scene objects (`Bind`), value slots hold plain values (`SetBoundValue`,
  tag-strict). Unbound slots read zero defaults; null handles fail soft.
- **Temporaries** (`temp`): C-like block lifetimes — state-body temps live
  for the visit, Actions-body temps re-initialize every round, branch temps
  die at the branch end.

Bindings are journaled, so hot reloads replay them. Bind before boot so
`Start{}` sees values; late binds apply immediately. See
[`Docs/Setup.md`](Docs/Setup.md) for the wiring walkthrough.

## Internal DB

`MainServer.Db` is the system's in-memory memory (scene-play lifetime, no
persistence): per-module **asset records** (states, entry, slot schema,
source hash), per-AI **instance records** (path, head state, counters,
pause/suspend flags), a **timetable** of every transition (capped ring,
1024) and an **emit log** (capped ring, 256), plus frame/transition totals.
External systems never touch it directly — the query server answers from
snapshots (copies) and applies commands with validation. See
[`Docs/MainServer.md`](Docs/MainServer.md) (writers/tick) and
[`Docs/QueryServer.md`](Docs/QueryServer.md) (snapshot readers).

## Movement & functions

Tier-3 calls post goals, never teleport. Each tick the movement system looks at
what the agent's GameObject (or a parent) carries and **calls the Unity function
meant for that component** — there is no collision maths in the runtime:

| On the agent | Unity call | Stopped by walls? |
|---|---|---|
| `NavMeshAgent` (enabled, on a mesh) | `SetDestination` | yes — via the NavMesh |
| `CharacterController` | `Move(motion)` | **yes** — the controller's own sweep |
| `Rigidbody`/`Rigidbody2D`, **dynamic** | `velocity` is set each tick | **yes** — the physics solver (mass, drag, gravity keep working) |
| `Rigidbody`/`Rigidbody2D`, **kinematic** | `MovePosition` | **no** — Unity's docs: *"If the rigidbody is kinematic then any collisions won't affect the rigidbody itself"* |
| `Collider`/`Collider2D`, no body | *(none exists)* | **no** — a bare collider is static geometry; one warning names the missing component |
| nothing at all | *(none)* | no — the step goes to the transform |

So: **if the AI must be stopped by walls, give it a dynamic `Rigidbody`(2D)
(gravity off, rotation frozen) or a `CharacterController`.** Those are the
component sets Unity resolves collisions for. `setPosition` / `setRotation` /
`setScale` are untouched — they write the transform and teleport, exactly like
`transform.position` in Unity; only goal-driven movement is routed through the
calls above. `AIInstance`'s **Collision Aware** toggle is the escape hatch for
objects whose transform something else owns.

Each agent logs the call it uses when that changes
(`movement: Marcher -> Rigidbody.MovePosition (physics resolves collisions)`), which
makes the path taken visible instead of guessed. `moveTowards` with an OBJECT
destination re-reads it every tick, as `follow`/`followTarget` do; `goTo` and
`sprintTowards` take a Vector3 (or an object's position at call time); `lookAt`
rotates gradually (through `MoveRotation` when the object has a body); movement
never changes facing. The other 150+ overloads are direct engine mappings;
engine-state failures (null handles, missing components) log and yield defaults,
never exceptions.

### If an AI walks through a wall

The runtime calls Unity's move for the component it finds, so a wall can only be
respected if Unity has something to resolve with:

1. **Give the agent a body.** A dynamic `Rigidbody`(2D) with gravity off and
   rotation frozen, or a `CharacterController`. Without one, Unity has nothing
   to move against collisions and the console says so.
2. **The collider has to be on the object that should collide** — the agent's own
   collider. A collider on a parent belongs to the parent.
3. **The wall needs a collider, and Is Trigger must be off.** Triggers block
   nothing in Unity, for any body type.
4. **Kinematic bodies are not stopped by collisions** — Unity's rule, not the
   runtime's. Switch it to dynamic, or use a `CharacterController`.
5. **A `NavMeshAgent`** follows the baked NavMesh and ignores colliders that are
   not part of it: bake the obstacle, or put a `NavMeshObstacle` on it.
6. **`Collision Aware` off in the inspector?** Then steps go to the transform and
   nothing is consulted at all.
7. **Current code?** The console line names the call on the first tick. If it
   never appears, the project's copy of `Runtime/` is not the one that logs.

## Interpretation notes (spec decisions)

Ambiguities in the design brief, resolved as follows:

- **If-chains in Actions are C-like**: the first true branch runs, the other
  *branches* are skipped, statements *after* the chain still run.
- **Self-goto stays**: `goto CurrentState` performs no exit/enter (no Start
  re-run, no broadcast); otherwise trailing default gotos would reset state
  temps every tick and time-based transitions could never fire.
- **Suspension starts next tick**: `wait`/`waitUntil` take effect after the
  current tick finishes serving; in-flight motion continues while suspended
  (so `waitUntil(hasReachedDestination(...))` can become true). `waitUntil`
  re-evaluates its condition AST every tick.
- **`follow` vs `followTarget`**: both live-track; `follow` stops closer
  (0.5x stop distance). `moveTowards` given an OBJECT also live-tracks it (the
  goal re-reads the object each tick); given a Vector3 it walks to that fixed
  point. `goTo`/`sprintTowards` always take a point.
- **`getPursuitPosition`**: one-second lead on the target's rigidbody
  velocity (`pos + vel`), else the current position.
- **`getSeparationVector`**: samples up to `neighbors` nearby colliders
  (radius 5) with 1/distance weighting, normalized.
- **Tags are FNV-1a ints** (`getTag`, `getNearestOfTag`); layers are Unity
  layer ints. `getRaycastHit`/`getNearestOfTag` miss => null handle (id 0),
  which fails soft (log + default) when used.
- **`screenToWorld`** unprojects at `nearClipPlane + 1`. **`getSize`** is
  sprite units (`rect.size / pixelsPerUnit`). **2D facing** is +X for
  `getAngleTo`; 2D motion keeps Z.
- **Animation**: `play` = enable + channel speed; `stop` = Rebind + disable;
  `pause`/`resume` zero/restore speed; clips are state `fullPathHash`;
  progress is loop-normalized time.
- **`isGrounded`/`isColliding`/`getCollisionNormal`** are query-based
  approximations (down-ray / overlap sampling / nearest-contact normal) —
  no collision-event tracking, no auto-attached components.
- **`emit`** records into the main DB (tick, time, instance, event id) and is
  retrievable via `GetEmits`; it does not touch the broadcast servers.
- **Scheduler defaults**: M = 2 (clampable 1..3); N adapts ±2/tick between
  8 and 20 (soft 15); long-term cap 64; starvation eviction after 300 ticks
  for back-half waiters under congestion.
- **Execution is AST-driven**; the Token section is validated (frame count,
  operand shapes) but the instruction stream is not interpreted — phase and
  scope structure come from the AST parent edges, per DESIGN.md §2.8/§6.

## Type mapping

| DSL | Unity |
|---|---|
| Object2D/3D | GameObject |
| Transform2D/3D | Transform |
| Camera2D/3D | Camera |
| Sprite2D/3D | SpriteRenderer |
| AnimationController2D/3D | Animator |
| PhysicsObject2D/3D | Rigidbody2D / Rigidbody |
| NavMeshAgent | UnityEngine.AI.NavMeshAgent |

Binding is forgiving: bind a GameObject and components are found with
`GetComponent`; bind a component and siblings resolve through it.

## Sandbox harness

`Sandbox/` holds UnityEngine stubs, `SmokeTest` (headless: parses real
`.fsmb` vectors, ticks AIs, exercises the query/broadcast servers) and a
`dotnet` console project. With a .NET SDK:

```sh
cd Sandbox
dotnet run -- Vectors/
```

`Sandbox/check.py` needs only Python + pip packages (`tree-sitter`,
`tree-sitter-c-sharp`) and validates .cs syntax plus catalog/dispatcher
ID consistency (179/179 both directions).

## Installer (next)

The traditional installer (detect Unity, discover projects, pick project +
in-project folder, payload = this repo) is specified and comes second, in
this repo. Engine ports later follow the same package format
(`myfsm-package.json` + manifest validation + file map).
