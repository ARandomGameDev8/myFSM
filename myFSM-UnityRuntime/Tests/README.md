# myFSM runtime tests (Play mode)

Four hand-runnable test cases that exercise the Unity runtime end to end. Each
one ships with a **pre-compiled module** (`.fsmb`), the **generated class** that
carries that module inside a C# file, and a **manual-binding half**, because the
two halves of an FSM's life are different jobs: the module is compiled from the
`.fsm`, and the object bindings are hand-written and must survive recompiles.

These are scene tests, not unit tests. They print to the console and write CSVs;
nothing here fails a build. The property-style checks live in
`Sandbox/SmokeTest.cs`, and `Sandbox/check.py` syntax-parses everything under
`Tests/` (along with Runtime/Sandbox/Samples) plus the catalog/dispatch
invariants, so a malformed test script is caught without opening Unity.

```
Tests/
  README.md                          this file
  Shared/
    StateTransitionRecorder.cs       logs every state change + watched variables
  Tools/
    gen_class.py                     emits the generated class (mirrors ClassGenerator)
    verify_classes.py                module/class consistency check for all four cases
  01-state-transitions/
    Fsm/zonebridge.fsm               source
    Fsm/zonebridge.fsmb              compiled module (950 bytes)
    Fsm/zonebridge.fsmd              disassembly (states, slots, types)
    Scripts/ZoneBridgeAI.cs          GENERATED  - module blob + State_/Slot_ constants
    Scripts/ZoneBridgeAI.Manual.cs   MANUAL     - OnBindingsManual() bindings
    Scripts/ZoneController.cs        drives `zone`, verifies the outcome
    Scripts/ZoneTestSetup.cs         one component that finishes the scene
  02-spawn-stress/                   chaser.fsm (232 bytes) + ChaserAI(.Manual) + ...
  03-navmesh-maze/                   mazerunner.fsm (475 bytes) + MazeRunnerAI(.Manual) + ...
  04-navmesh-moving-target/          pathchaser.fsm (513 bytes) + PathChaserAI(.Manual) + ...
```

## How an FSM becomes a running test

```bash
# from the repository root (`bin/fsmc` ships with the repo; `make` rebuilds it)

# 1. compile the module, then disassemble it to inspect what was emitted
bin/fsmc myFSM-UnityRuntime/Tests/01-state-transitions/Fsm/zonebridge.fsm \
         -o myFSM-UnityRuntime/Tests/01-state-transitions/Fsm/zonebridge.fsmb
bin/fsmc -d myFSM-UnityRuntime/Tests/01-state-transitions/Fsm/zonebridge.fsmb \
         -o myFSM-UnityRuntime/Tests/01-state-transitions/Fsm/zonebridge.fsmd

# 2. regenerate the class from the module + its disassembly, straight into Scripts/
cd myFSM-UnityRuntime
python3 Tests/Tools/gen_class.py Tests/01-state-transitions/Fsm/zonebridge.fsmd \
        Tests/01-state-transitions/Fsm/zonebridge.fsmb ZoneBridge ZoneBridgeAI \
        Tests/01-state-transitions/Scripts
```

### Checking that the committed pieces agree

```bash
python3 myFSM-UnityRuntime/Tests/Tools/verify_classes.py       # from the repo root
```

It recompiles each `.fsm` with `fsmc` and compares byte for byte with the
committed `.fsmb` (catching "edited the FSM, forgot to recompile"), re-runs the
disassembler against the committed `.fsmb`, and then checks the generated class
against that disassembly: version, state consts, slot consts (number + type), and
that the embedded base64 blob decodes to the same bytes as the `.fsmb`. Current
state:

```
ok   01-state-transitions/zonebridge: module 950 bytes in sync, class ZoneBridgeAI.cs matches (3 states, 4 runtime slots, blob identical)
ok   02-spawn-stress/chaser:         module 232 bytes in sync, class ChaserAI.cs matches   (1 state,  2 runtime slots, blob identical)
ok   03-navmesh-maze/mazerunner:      module 475 bytes in sync, class MazeRunnerAI.cs matches (2 states, 4 runtime slots, blob identical)
ok   04-navmesh-moving-target/pathchaser: module 513 bytes in sync, class PathChaserAI.cs matches (2 states, 4 runtime slots, blob identical)
```

`Tools/gen_class.py` is a line-for-line mirror of
`Runtime/Unity/ClassGenerator.cs` (`GenerateSource` with the module bytes):
same header, same `State_*`/`Slot_*` constants, same per-slot comments, same
`partial void OnBindingsManual();` hook, same base64 blob wrapped at 96 columns.
It exists because the modules here are compiled on the command line, where the
editor-time C# generator cannot run. Regenerating a class is always safe — which
is exactly why the bindings live in the other file.

### The two files, and why both are needed

| file | produced by | contains | edit it? |
| --- | --- | --- | --- |
| `<Name>AI.cs` | the class generator | `GeneratedModuleName`, `State_*`, `Slot_*`, the slot table as comments, and the base64 `.fsmb` (`ModuleData` → `EmbeddedModule`) | **no** — it is rewritten on every recompile |
| `<Name>AI.Manual.cs` | you | `partial void OnBindingsManual()`: `Bind(slot, object)` for handles, `SetBoundValue(slot, value)` for values | yes |

`AIInstance` auto-binds a handle slot **only when its type tag is unique** in the
module (an `Object3D` slot becomes this `GameObject`, a `Transform3D` slot this
`transform`, a component type a `GetComponent`). Every module here has two slots
of the same type (`self` + a target), so the generator prints
`AMBIGUOUS (2x) - Bind(Slot_x, ...)` and the manual file decides which object is
which. Delete the `.Manual.cs` file and the project still compiles (the partial
hook simply does nothing) — it just runs with those slots unbound.

## Test 01 — variable-driven state transitions

**Goal**: a runtime variable drives the state, and per-state Traversal evaluation
puts the head in the state that variable asks for.

Scene: an empty GameObject with **`ZoneTestSetup`** on it. It adds `ZoneBridgeAI`
(the generated class), `ZoneController`, and the shared `StateTransitionRecorder`
(told to log `zone` with every transition). `ZoneController` creates the marker
object (`ZoneMarker`) the module teleports.

| `zone` | state | what the state does on entry |
| --- | --- | --- |
| 0 – 10 | `AtHome` | `setPosition(self, home)`, marker at home |
| 11 – 20 | `Above10` | `setPosition(self, home + (0,10,0))` |
| 21 – … | `Above40` | `setPosition(self, home + (0,40,0))` |

The ranges as specified ("0-10 / 11-20 / >=20") overlap at 20, so the module lets
the lower band own the boundary: **20 means +10 m**, 21 is the first value that
means +40 m. Change one comparison in the `.fsm` if you want 20 in the upper band.

Controls: `W` / `Up` = +1, `S` / `Down` = −1 (a tap steps once, a hold repeats
after 0.4 s and then every 0.15 s), `R` = reset to 0, and the value never drops
below 0.

What it records:

* `state_transitions.csv` — one row per state change, straight from the runtime's
  ordered broadcast server (`time, tick, from, to, reason, zone`), i.e. the
  runtime's own account of what happened.
* `zone_checks.csv` — the controller's verdict a few frames after each change:
  expected state vs actual state, expected height vs actual height, and whether
  the marker is back at home. Any mismatch is a `FAIL` row plus a console warning.

## Test 02 — spawn stress: how many AIs before the frame budget breaks

**Goal**: find the practical maximum number of ticking AIs on this machine.

Scene: an empty GameObject with **`SpawnStressTest`** on it. It creates the
ground plane, a WASD `PlayerController` object if the scene has none, and one
dynamic Rigidbody cube per spawn (gravity off, Y frozen, rotations frozen) that
carries `ChaserAI` — the pre-compiled `chaser.fsm`, which re-posts
`moveTowards(self, getPosition(player), 6)` every tick.

Controls: `Space` = +`spawnPerPress` AI (default 1), `B` = +100, `P` = pause
spawning, `L` = write the reports now, `Backspace` = clear. Move the player with
WASD: the chasers follow, so the load is real movement, not idle ticking.

Files:

| file | row per | columns |
| --- | --- | --- |
| `spawn_stress_spawns.csv` | spawn | index, frame, `instantiateMs`, the frame the spawn landed in, the frame **after** it (when the new AI has booted and ticked), alive-after, instance id |
| `spawn_stress_frames.csv` | every 10 frames | frame, time, delta, smoothed delta, fps, alive AIs, **AIs registered in the runtime's DB**, total FSM calls served |
| `spawn_stress_summary.txt` | run | the one-line answer: spawned, alive, practical maximum, final fps, registry size |

The practical maximum is declared by the test itself: when the smoothed frame
time stays above `slowdownFrameMs` (33.3 ms = 30 fps) for a full second with at
least `minimumCountForStop` (25) AIs alive, it stops spawning and logs
`PRACTICAL MAXIMUM reached: N AIs`. Raise `maxAlive` if your machine takes more
than the default 5000.

## Test 03 — shortest path through a maze

**Goal**: an AI in a generated maze walks the shortest path out, and the test
proves afterwards that it did.

Scene: an empty GameObject with **`MazeTestSetup`** on it. It finds or creates
the `MazeGeneratorController` (ground plane + random maze + entry/exit markers +
runtime-baked NavMesh) and creates the runner: capsule + `NavMeshAgent` +
`MazeRunnerAI` + the recorder.

The maze is a **perfect maze** from an iterative depth-first search, so every cell
is reachable and entry → exit always has a route; `MazeGeneratorController` also
proves that at runtime with a BFS over the same grid and logs the corridor length
it found. Walls are cubes with colliders, laid on the ground plane's own height
(`GroundY`), so raising the plane moves the maze with it. `G` rebuilds a new
random maze (new NavMesh), `B` re-bakes the NavMesh.

The module (`mazerunner.fsm`) runs once on entry:
`plannedLength = getPathLength(findPath(getPosition(self), getPosition(exit)))`
then `findShortestPathAndMove(self, exit)`; its Traversals watch
`hasReachedDestination(self, exit)` and stop in the terminal `Done` state.
Because the runner carries a `NavMeshAgent`, the movement system drives it with
Unity's own `NavMeshAgent.SetDestination`.

Three numbers, from three places, decide the verdict:

| number | source |
| --- | --- |
| `plannedLength` | the engine's own `findPath` + `getPathLength` inside the module |
| `travelled` | the recorder's per-frame accumulation of the runner's real movement |
| `straightLine` | entry → exit in a straight line, measured before the run starts |

**PASS** needs: arrival within `arrivalTolerance` (1 m), `travelled` ≥ straight
line (you cannot cross a maze shorter than the direct line), and `travelled`
inside `[0.85, 1.25] × plannedLength` (a wanderer or a teleporting shortcut fails
this). The **only** skip: `findPath` returned no route at all (no NavMesh under
the runner) — then the row says `SKIPPED — findPath returned no route`.

Files: `maze_run.csv` (the verdict row and its numbers) and `maze_trail.csv`
(per-sample position, distance to the exit, FSM state). The walked route is also
drawn in the scene with a LineRenderer.

Requires the **AI Navigation** package (`com.unity.ai.navigation`) for the runtime
bake; with a NavMesh baked in the editor instead, clear `buildNavMesh`.

## Test 04 — shortest path to a *moving* target

**Goal**: the same maze, but the target walks, so the route has to be re-planned
while it is being followed.

Scene: an empty GameObject with **`ChaseTestSetup`** on it: maze + chaser
(capsule + `NavMeshAgent` + `PathChaserAI`, speed 4.5) + walking target (capsule
+ `NavMeshAgent` + `MazeTargetWalker`, speed 3.2) + `MovingTargetRecorder`. The
chaser is deliberately faster, so the catch comes from re-planning rather than
from the chase being impossible.

The target walks itself with its own NavMeshAgent (so it always stands on valid
NavMesh): `PingPong` between the entry and the exit by default, `RandomWalk` for
unpredictable routes, or `Manual` — press `M` and drive it with WASD (the agent's
own `Move()` keeps it on the navmesh).

The module (`pathchaser.fsm`) re-plans and re-posts **every tick**:
`plannedLength = getPathLength(findPath(self, target))` +
`findShortestPathAndMove(self, target)`, and flips `caught` inside
`isInRange(self, target, 2.0 m)` (terminal `Caught` state).

Four facts, all measured, decide the verdict:

* `caught` — the module's flag flipped;
* separation at the catch ≤ `catchRadius + catchTolerance` (2.0 + 0.5 m);
* the target **actually travelled** ≥ `minTargetTravel` (5 m) — otherwise this
  quietly became test 03;
* the route **changed** — the recorder counts how often `plannedLength` moved;
  a constant value would mean re-planning never happened.

**PASS** needs all four plus the final state `Caught`. Files: `pursuit.csv`
(verdict row) and `pursuit_samples.csv` (per-sample separation, both positions,
the live route length and the chaser's state — the curve that shows the chase).

## Reading the output

Every CSV lands in `Application.persistentDataPath` (Unity logs the full path
each time it writes one; press `L` in test 02 to force it early). The runtime's
own bookkeeping is also queryable while playing: `MainServer.Instance.Db`
(`SnapshotInstances()`, `SnapshotHistory(instanceId, n)`), which is what the
stress test reads its "registered AIs" column from.

Where a check can only be skipped, the CSV says so in the verdict column rather
than silently passing: test 03 skips when the navigation built-in cannot produce
a route, and test 04 flags a target that barely moved.

## Requirements

* The runtime scripts (`myFSM-UnityRuntime/Runtime/Unity/...`) in the project —
  `installers/unity/install.py` does that (use `--branch <this branch>`; the
  default `test` branch predates several fixes).
* `com.unity.ai.navigation` for tests 03 and 04 (runtime NavMesh baking).
* No editor-time step: the modules are already compiled into the generated class
  files, so nothing needs assigning in the inspector — attach the setup
  component and press Play.
