# AIInstance — the base class, part by part

`Runtime/Unity/AIInstance.cs`. The abstract `MonoBehaviour` every AI extends:
one `.fsmb` module loaded onto one GameObject is one AI. Generated classes
(`XxxAI : AIInstance`) pin the module path + constants; `FsmbAIInstance`
(sealed, empty, same file) is the generic inspector-driven version — assign
a `.fsmb`, no codegen needed. Both tick themselves; the main server never
ticks AIs (see `MainServer.md`).

## File tour (top to bottom)

**Tiny helpers.** `UnityTimeProvider` (`Time.time`/`deltaTime` behind
`ITimeProvider`, so tests drive time by hand) and `UnityExecutionLog`
(prefixes every line with `[myFSM Name@GameObject]`).

**Inspector fields** (private, serialized): `_moduleAsset` (the `.fsmb`
`TextAsset`, optional for generated classes), `_slotBindings`
(`List<UnityEngine.Object>`, list index = binding slot — handles only),
`_moduleNameOverride` (renames the asset).

**Public state** (read-only except `Paused`): `Execution` (the per-instance
`AiExecution`: state handler, 3 servers, variable table, dispatcher),
`Handles` (id → Unity object table), `Movement` (in-flight motion goals),
`Paths`, `Dispatcher` (the 179 built-ins), `InstanceId` (1, 2, 3…; stable
across hot reloads), `Booted`, `Paused` (soft pause — still records DB
samples, idles), `ModuleName`, `BootError` (set when boot fails).

**Module source** (virtuals, `null` by default): `EmbeddedModule` (the module
bytes compiled into the class — a generated class is self-contained and needs
no asset at all), `AssignedModuleAsset` (a module picked in the inspector,
overridden by `FsmbAIInstance` — generated classes deliberately have no such
field, so their inspector shows no module slot), `ModuleResourcePath` (legacy
Resources path fallback) and `GeneratedModuleName` (module-name fallback).
Resolution order at boot: `AssignedModuleAsset` (explicit override) →
`EmbeddedModule` → `Resources.Load<TextAsset>(ModuleResourcePath)` → fail. The
failure message says which source was missing, and what to do about it. `CurrentStateName`
(`"<none>"` before the first tick) and `DisplayName` (`Module@GameObject`).

**Journal + count.** `BindingEntry` (`Slot` + object-or-value) and `_journal`
(every explicit `Bind`/`SetBoundValue`, in order — replayed across hot
reloads). `BoundSlotCount` = distinct journaled slots (auto-binds are NOT
journaled, so this counts explicit bindings).

**Unity messages.** `Start()` boots once (`BootFromAsset`; on failure sets
`enabled = false`, which also stops `Update`). `OnDestroy()` unregisters
from the server. `OnBindingsRequired()` is the code-bindings hook (empty in
base; generated classes override it — runs after the inspector list, before
boot completes, i.e. before the entry `Start{}` on the first Update).

**Boot.** `BootFromAsset()` (inspector asset → else Resources path → else
fail) and `BootWithBytes(bytes, name)` (dynamic path) both funnel into
`BootCore`, which: rejects double-boot, reads + validates the bytes
(fail-fast with `BootError`), ensures the main server, allocates the id,
builds tables/dispatcher/`AiExecution`, runs the **auto-bind base layer**,
then inspector list + `OnBindingsRequired()` (fresh boot) or journal replay
(reboot), then `Execution.Boot` (selects the entry head — enters nothing;
first Update performs `none → head`), then registers asset + instance.
`RebootWithBytes` (query `ReloadModule`) unregisters, rebuilds on the same
id, replays the journal. `FailBoot` clears state + logs one error line.

**Explicit bindings.** `Bind(slot, obj)` (handle slots only: validates slot,
tag, non-null; journals; allocates a handle id) and
`SetBoundValue(slot, value)` (value slots only: tag-strict via
`ValueMatchesTag`; journals). `ApplyBinding` is the unjournaled applier both
use — and the only thing auto-bind calls.

**Auto-bind.** `AutoBindHandles` (virtual kill switch, default true).
`AutoBindUnambiguousHandles` groups runtime slots by type tag: value slots
skipped (no rule); tags claimed by 2+ slots skipped with one warning
("ambiguous, bind manually"); unique handle slots bound via
`ResolveAutoBindTarget` (below), missing components skipped with an info
log. Mapping: `Object2D/3D → gameObject`, `Transform2D/3D → transform`,
`Camera → GetComponent<Camera>()`, `Sprite → SpriteRenderer`,
`AnimationController → Animator`, `PhysicsObject2D/3D → Rigidbody(2D)` else
`Collider(2D)`, `NavMeshAgent → GetComponent<NavMeshAgent>()`.

**Variable access.** `TryGetVariable` (consts → runtime → live temps, first
match) and `TrySetVariable` (runtime slots only, tag-strict) — what queries,
game code, and the debugger use.

**Per-frame.** `Update()` (virtual) → `TickInternal()`: skip unbooted; soft
`Paused` records a paused sample and idles; else `Movement.Advance` (fresh
positions) → `Execution.Tick` (first tick: initial entry + Start; then
suspension → external → Update → Traversals → transition) → on head change:
DB timetable row + publish to both broadcast servers → tick-sample. Returns
the change (null when none). Overriding `Update()` without
`base.Update()` silently stops the AI — prefer `Paused` / `enabled=false`.

**Per physics step.** `FixedUpdate()` (virtual) → `FixedTickInternal()`: skip
unbooted or `Paused`; else `Movement.FixedAdvance(Time.fixedDeltaTime)`, which
takes the step of every goal whose agent is a `Rigidbody`(2D) — one
`MovePosition` per physics step (see below). The FSM never runs here.
Overriding `FixedUpdate()` without `base.FixedUpdate()` stops rigidbody agents.

**Movement is component-aware.** Tier-3 goals never move a bare transform:
each tick the movement system looks at what the agent's GameObject (or its
nearest parent) actually has on it and calls the Unity function meant for that
component. There is no collision maths in the runtime — the component resolves
everything:

| On the agent | Unity call | Stopped by walls? |
|---|---|---|
| `NavMeshAgent` (enabled, on the mesh) | `SetDestination` | yes — via the NavMesh |
| `CharacterController` | `Move(motion)` | **yes** — its own capsule sweep handles slopes, steps and walls |
| `Rigidbody` / `Rigidbody2D`, **dynamic** | `MovePosition`, once per physics step from `FixedUpdate` | **yes** — the solver resolves every contact, so walls, piles and other bodies all matter |
| `Rigidbody` / `Rigidbody2D`, **kinematic** | `MovePosition`, once per physics step from `FixedUpdate` | **no** — Unity's docs: *"If the rigidbody is kinematic then any collisions won't affect the rigidbody itself"*; use a dynamic body when walls must stop it |
| `Collider` / `Collider2D`, no body | *(none exists)* | **no** — a collider on its own is static geometry; the runtime warns once and says what to add |
| nothing at all | *(none)* | no — the step is written to the transform |

**What to use for an AI that must be stopped by walls:** a dynamic
`Rigidbody`(2D) with gravity off (`useGravity = false` / `gravityScale = 0`) and
rotation frozen, or a `CharacterController`. Those are the component sets Unity
provides a collision-resolving move for. The dynamic body is the closest to "it
just works": the runtime moves it exactly as the hand-written follower below
does, and Unity resolves the contacts.

**On a Rigidbody the goal is this script, verbatim**, run from the AI's own
`FixedUpdate`:

```csharp
void FixedUpdate()
{
    var dir = (target.position - rb.position).normalized;
    rb.MovePosition(rb.position + dir * speed * Time.fixedDeltaTime);
}
```

`speed` is the goal's speed (the call's argument for `moveTowards`; the agent's
base speed — `NavMeshAgent.speed` or 3.5 — for `goTo` / `follow`, times the
multiplier for `sprintTowards`), `target.position` is the goal's destination
(re-read every physics step for an object target, a fixed point for a Vector3),
and both are measured from the body's own `rb.position`. Nothing is layered on
top: no velocity is written, the step is not clamped to the remaining distance,
not flattened onto the ground plane and not stopped short by a stop distance,
and the goal is **not dropped on arrival** — the follower has no notion of
"arrived" either. A body standing on its destination gets a zero direction
(Unity's `normalized` of a zero vector) and so a zero-length move, until the next
call replaces the goal or `stopMovement` drops it. `MovePosition` is a move
rather than a teleport (Unity: use `Rigidbody.position` to teleport), so
interpolation stays smooth and a dynamic body's contacts are resolved by the
physics step. Because the step runs once per physics step with
`Time.fixedDeltaTime`, the body travels at exactly the requested speed whatever
the frame rate is doing. A path goal (`findShortestPathAndMove`) still advances
from corner to corner and ends past the last one; each corner is walked with the
same step.

**Everything else steps per frame** in `Update`, one step of `position +=
direction x speed x time`, clamped so it never overshoots, and the goal ends
within the agent's stop distance. A CharacterController's step is taken in the
ground plane and its gravity is added by the runtime, so a walker falls, lands
and runs instead of hovering at the goal's height; a transform-driven object has
nothing else moving it, so the goal drives all three axes. A goal posted towards
an **object** re-reads that object's position every step, so the agent tracks a
target that moves.

`setPosition` / `setRotation` / `setScale` are **not** affected by any of this —
they write the transform directly and teleport, exactly like `transform.position`
in Unity. Only the goal-driven tier-3 calls are routed through the moves above.

The `Collision Aware` inspector toggle (default on) is the escape hatch for
objects whose movement something else owns: with it off, steps go straight to
the transform and no component is consulted. A `NavMeshAgent` steers itself
either way.

**Diagnostics.** The first time an agent is driven — and whenever its driver
changes — one line goes to the log:

```
movement: Marcher -> Rigidbody.MovePosition in FixedUpdate (physics resolves collisions)
movement: Marcher -> transform (collider without a body: nothing can stop it)
```

and a collider with no body also gets one warning:

```
movement: Marcher has a Collider but no Rigidbody / CharacterController. Unity
cannot move a body-less object against collisions (a bare Collider is static
geometry), so movement is applied to the transform and nothing will stop it.
Add a dynamic Rigidbody (gravity off) or a CharacterController to make walls matter.
```

`Movement.Resolve(transform, is2D)` reports the driver chosen for an object
without running anything — handy from your own code, and what the sandbox tests
assert against.

## The generated child (what `ClassGenerator` emits)

One module → one `public sealed partial class XxxAI : AIInstance`
(`Samples/GuardAI.cs` is real output for `guard.fsmb`):

1. `AssetResourcePath` + the two pin overrides (module loading).
2. `State_*` string constants (one per state) and `Slot_*` int constants
   (one per runtime slot, DSL type as comment) — no magic strings/ints.
3. An `OnBindingsRequired()` override: per-slot comments marking each slot
   **auto-bound (unique type)** vs **AMBIGUOUS (Nx)** vs **value** (with the
   exact `SetBoundValue` + `FsmValue.Make*` call), then an
   `OnBindingsManual()` call.
4. A `partial void OnBindingsManual();` declaration — zero-cost when
   unimplemented (the compiler erases the call).

## Where to manually bind (when auto doesn't cover you)

Ranked, best first:

1. **Inspector list** (`Slot Bindings`, index = slot) — handles only, no
   code, survives recompiles. Can't set value slots.
2. **Partial file (durable code)** — add `XxxAI.Manual.cs` (see
   `Samples/PatrolAI.Manual.cs`): same `partial class XxxAI`, implement
   `partial void OnBindingsManual()` with `Bind(Slot_*, …)` /
   `SetBoundValue(Slot_*, FsmValue.Make*…)` calls. Never rewritten by the
   burst compiler. This is the home for ambiguous-type slots and value
   overrides:
   ```csharp
   using UnityEngine;
   using MyFSM.Core;   // FsmValue lives here - a using is per FILE, so
                       // `using MyFSM.Unity;` alone does not bring it in
   using MyFSM.Unity;

   public sealed partial class PatrolAI
   {
       partial void OnBindingsManual()
       {
           Bind(Slot_waypointA, GameObject.Find("WaypointA")); // ambiguous: manual
           Bind(Slot_waypointB, GameObject.Find("WaypointB"));
           SetBoundValue(Slot_speed, FsmValue.MakeFloat(3.5f)); // value override
       }
   }
   ```
   Only the `Bind(...)` calls need no extra using: reading a value slot
   (`TryGetVariable(name, out FsmValue value)`) and writing one
   (`SetBoundValue(slot, FsmValue.Make…)`) both name `FsmValue`, so a manual
   file that touches ANY value slot needs `using MyFSM.Core;` — that is the
   `CS0103: The name 'FsmValue' does not exist in the current context` error.
3. **Inline in the generated file** — works (same calls inside the generated
   override), but the burst compiler rewrites the file on every compile, so
   treat it as scratch only.

Value makers: `MakeInt`, `MakeFloat`, `MakeDouble`, `MakeBool`,
`MakeString`, `MakeVec2`, `MakeVec3`, `MakeQuat` (quaternion example uses
identity `0,0,0,1`).

## Binding precedence + lifecycle timeline

Weakest → strongest: **auto-bind** (fresh every boot, unjournaled) →
**inspector list** (fresh boot, journaled) → **journal replay** (reboot only)
→ **`OnBindingsRequired` / `OnBindingsManual`** (fresh boot, journaled).
Last writer wins per slot.

Frames: Edit (compile → scripts → GameObjects, see `Compiler.md`) → Play:
`Start()` boots (load → auto-bind → explicit binds → select head →
register) → every `Update()`: move → think → maybe transition → journal →
`LateUpdate()`: queries observe it all.
