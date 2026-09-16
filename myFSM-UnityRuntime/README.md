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
  manual `position += direction * speed * dt` otherwise) — never teleports.

Language level is **C# 7.3**, UnityEngine only, no packages — compiles in
Unity 2019+ and in the sandbox harness (`Sandbox/`).

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
    HandleTable.cs        per-AI handle id -> Unity object
    FunctionDispatcher.cs ID router + resolution helpers
    MovementSystem.cs     incremental goals (NavMesh or manual) + look goals
    ClassGenerator.cs     .fsmb -> named C# class source (editor-time)
    Functions/            Math Object Sprite Animation Physics Camera
                          Navigation Perception Steering Sensing Control
Samples/   generated-class example (what ClassGenerator emits)
Sandbox/   compile/run harness WITHOUT Unity (stubs + headless smoke test);
           NEVER installed into a Unity project (excluded in the manifest)
```

## Quickstart (Unity)

1. Compile a model with the myFSM compiler: `fsmc patrol.fsm -o patrol.fsmb`.
2. Copy this package's `Runtime/` into your project (default `Assets/MyFSM`;
   the installer does this — see below).
3. Drop the `.fsmb` anywhere (e.g. `Assets/MyFSM/Modules/patrol.fsmb`).
4. Add `FsmbAIInstance` to a GameObject, assign the `.fsmb`, fill the binding
   slots with scene objects (index = binding slot).
5. Play. The main server boots automatically; the AI enters its `@ENTRY` state.

Generated classes (optional, nicer): run `ClassGenerator.GenerateSource`
(in the editor) on the module bytes, save the file, use `PatrolAI` instead of
`FsmbAIInstance` — same behavior plus `State_*` / `Slot_*` constants.

## Queries, commands, broadcasts

```csharp
MainServer main = MainServer.EnsureExists();

// Use the AI system as a service:
QueryClient cli = main.ConnectExternal("ui");
main.Queries.Enqueue(cli.ClientId, new ServerRequest {
    IsCommand = false, Code = (int)QueryCode.GetStats });
main.Queries.Enqueue(cli.ClientId, new ServerRequest {
    IsCommand = true, Code = (int)CommandCode.TransitionTo,
    TargetInstanceId = ai.InstanceId, StringArg = PatrolAI.State_Chase });
// ...next frame(s)...
ServerResponse r;
while (cli.TryTakeResponse(out r)) Debug.Log(r.Ok + " " + r.Text);

// Live state changes, no polling:
main.OrderedBroadcast.Subscribe(new ActionWithStateSubscriber(
    ai.InstanceId, state => Debug.Log("now in " + state)));
```

Queries: `GetInstanceState ListInstances GetAssetInfo ListAssets
GetStateHistory GetVariable GetStats GetEmits`.
Commands: `TransitionTo SetVariable Emit PauseAI ResumeAI ReloadModule`
(ReloadModule re-parses bytes, requires an identical state/slot schema, then
reboots every instance of the asset with bindings replayed).

Priority subscribers subclass `PriorityStateSubscriber` and set
`NextVerdict` (`ServeRest` / `SkipRestThisTick` / `DeregisterLower`); plain
`Action` subscribers always behave as `ServeRest`.

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
  (0.5x stop distance). `goTo`/`sprintTowards`/`moveTowards` snapshot object
  destinations at call time.
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

## Installer (next)

The traditional installer (detect Unity, discover projects, pick project +
in-project folder, payload = this repo) is specified and comes second, in
this repo. Engine ports later follow the same package format
(`myfsm-package.json` + manifest validation + file map).

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
