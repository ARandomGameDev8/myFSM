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
