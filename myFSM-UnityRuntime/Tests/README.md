# myFSM runtime tests (Play mode)

Four hand-runnable test cases that exercise the Unity runtime end to end. Each
one ships with a **pre-compiled module** (`.fsmb`), the **generated class** that
carries that module inside a C# file, and a **manual-binding half**, because the
two halves of an FSM's life are different jobs: the module is compiled from the
`.fsm`, and the object bindings are hand-written and must survive recompiles.

## Quick start (attach one component, press Play)

You do **not** wire anything manually. Each test has a setup component that
builds the whole scene for you — ground, player, maze, actors, markers — and the
object bindings are C# in each `*.Manual.cs` file (compiled in, not dragged in the
inspector). So the whole procedure is:

1. **Install once.** Put the runtime *and* these tests in the project:
   ```bash
   # from the repo root; Tests/ is part of the payload
   python3 installers/unity/install.py --source local --local-path . \
       --project "/path/to/YourUnityProject" --yes
   # (or: --source git --git-url https://github.com/ARandomGameDev8/myFSM.git \
   #      --branch arena/01a0af62-myfsm --project ... --yes)
   ```
   This lands them in `Assets/MyFSM/` — `Assets/MyFSM/Tests/01-...` etc.
   (verified locally: 79 files copied, 34 of them under `Tests/`). The
   installer's default source is the `test` branch, which predates the runtime
   fixes and these tests, so pass `--source local` or the branch above.
2. **Let Unity compile** (a few seconds; the console should be clean). Nothing
   to add in the inspector, no scene asset to open — the setup component builds
   its own scene at Play time.
3. **New scene** (or any scene) → `GameObject > Create Empty` → **Add
   Component** → the one component of the test you want:

   | test | component to add | it builds, on its own |
   | --- | --- | --- |
   | 01 | `ZoneTestSetup` | a **visible cube with the AI on it** (or the AI you already placed), a 45 m height ruler with ticks at +10 m/+40 m, the keyboard controller, the marker pin, the recorder, and a camera framing that fits the whole column. It drives nothing: the cube moves when you press a key |
   | 02 | `SpawnStressTest` | a 400 m ground plane, a **cylinder** player on a CharacterController you drive with WASD, a **top-down camera that follows it**, and one falling chaser cube per `Space` press (nothing spawns by itself) |
   | 03 | `MazeTestSetup` | ground plane, random maze + entry/exit, baked NavMesh, runner capsule + agent + AI + recorder |
   | 04 | `ChaseTestSetup` | the same maze, plus a walked-out chaser and a walking target |

4. **Press Play.** The component logs what it built and what to watch for; the
   CSVs appear under `Application.persistentDataPath` (Unity logs the full path
   whenever it writes one).

If you copied the folders by hand instead of running the installer, verify the
copy first — a single missing script shows up as a `CS0246` in whatever file
uses it, which is a confusing way to learn a file is missing:

```bash
python3 Tests/Tools/check_project.py "/path/to/YourUnityProject"
# -> every test and runtime file the tests need is present and up to date
```

It compares by file NAME (renamed folders are fine) and reports what is missing,
what is stale (an older copy of a file that has since been fixed), and which
public types a missing file declares — i.e. the names Unity will fail to find.

Nothing else is required — no prefabs, no inspector references, no `.fsmb`
assets to assign (each generated class carries its module bytes), no
`com.unity.*` packages (`NavMeshAgent`/`NavMeshBuilder` are built-in modules),
and not even the `fsmc` compiler: the modules are already compiled into the
generated classes. `fsmc` is only needed if you edit a `.fsm` and want to
recompile it.

Test 02 is the exception to "nothing spawns by itself": the *scene* (plane, player,
camera) is automatic, but the **cubes are always your call** — `Space`, `B`, or
`spawnOnStart` if you set it.

### When you *would* wire things manually

Only to replace the defaults, never to make a test run. Each setup component
looks for the objects it needs first and only creates what is missing, so you can
drop your own in: a `MazeGeneratorController` with your own grid/seed, a runner
you built yourself (capsule + `NavMeshAgent` + the generated AI class), a
`cubePrefab` for test 02, or a marker/exit/target assigned in the inspector. The
`*.Manual.cs` files are the place for bindings that need code — test 02, for
example, hands the player reference to every runtime-spawned chaser through a
static field, which no inspector could do.

These are scene tests, not unit tests. They print to the console and write CSVs;
nothing here fails a build. The property-style checks live in
`Sandbox/SmokeTest.cs`, and `Sandbox/check.py` syntax-parses everything under
`Tests/` (along with Runtime/Sandbox/Samples) plus the catalog/dispatch
invariants and the **names a type is not allowed to hold twice** (`CS0102`: a
nested type sharing a name with one of its own methods, fields or properties,
duplicate members, `CS0111`/`CS0542`) — errors Unity only reports when it
compiles, which is why the checker looks for them first. It is not a compiler:
signature-level type errors still need Unity or `dotnet`.

Every folder is SELF-CONTAINED — a test never compiles against another test's
scripts, so you can copy one case into a project on its own.

```
Tests/
  README.md                          this file
  Tools/                             dev helpers (not needed to run a test)
    gen_class.py                     emits the generated class (mirrors ClassGenerator)
    verify_classes.py                module/class consistency check for all four cases
    check_project.py                 verifies a Unity project holds the files it needs
  01-state-transitions/
    Fsm/zonebridge.fsm               source
    Fsm/zonebridge.fsmb              compiled module (950 bytes)
    Fsm/zonebridge.fsmd              disassembly (states, slots, types)
    Scripts/ZoneBridgeAI.cs          GENERATED  - module blob + State_/Slot_ constants
    Scripts/ZoneBridgeAI.Manual.cs   MANUAL     - OnBindingsManual() bindings
    Scripts/StateTransitionRecorder.cs  logs every state change + watched variables
    Scripts/ZoneController.cs        drives `zone`, verifies the outcome
    Scripts/ZoneTestSetup.cs         one component that finishes the scene
  02-spawn-stress/                   chaser.fsm (232 bytes) + ChaserAI(.Manual)
    Scripts/StressInput.cs           keyboard reads, either Unity input backend
    Scripts/TopDownFollowCamera.cs   the follow rig the test adds to the Main Camera
  03-navmesh-maze/                   mazerunner.fsm (475 bytes) + MazeRunnerAI(.Manual)
    Scripts/MazeInput.cs             keyboard reads (G rebuild, B re-bake)
  04-navmesh-moving-target/          pathchaser.fsm (513 bytes) + PathChaserAI(.Manual)
    Scripts/ChaseInput.cs            keyboard reads (M manual mode, WASD)
  01-state-transitions/Scripts/ZoneInput.cs   the same helper for test 01
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

### Movement on a Rigidbody (what "normal" means here)

Every goal-driven move is one step of the plain formula — **position += direction
x speed x time** — handed to Unity's move call for the component the object
carries. Nothing writes `velocity`, and the runtime contains no collision maths:

* **Rigidbody / Rigidbody2D** → `MovePosition` (Unity's documented way to move a
  body by a step; its own example is
  `rb.MovePosition(transform.position + input * dt * speed)`), so a **dynamic**
  body is stopped by walls, piles and other bodies, and interpolation stays
  smooth. A **kinematic** body is moved the same way but Unity documents that
  collisions do not affect it — use a dynamic body when walls must stop it.
* **CharacterController** → `Move(step)` (its own capsule sweep).
* **NavMeshAgent** → `SetDestination` (the mesh pathfinds).
* **Nothing to move it** → the step is written to the transform, and the first
  time a bare `Collider` is found the runtime warns and names what to add.

Rigidbodies are moved by Unity in **physics steps** while an AI ticks once per
frame, and `MovePosition` keeps the last request of a step — so the steps of the
frames inside one physics step are summed and requested as one move. Without
that a body would travel at (render fps / physics fps) of the requested speed.

`moveTowards` towards an **object** re-reads that object every tick, so a target
that keeps moving is tracked and one that stands still is reached normally.
**Gravity keeps the vertical axis.** A body that gravity is holding down (dynamic
with gravity on, or a CharacterController) gets its step in the ground plane, so
the goal never fights the fall; a body with gravity off or a kinematic body flies
to the target on all three axes.

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

Scene: an empty GameObject with **`ZoneTestSetup`** on it. **Nothing is filled in
anywhere**, and you do not create the cube either. The setup finds the AI — on the
object it is attached to, else anywhere in the scene (an AI you added by hand is
used, not duplicated) — and if the scene has no AI at all it adds `ZoneBridgeAI`
to the object you attached it to. Then, when that object has no mesh of its own,
it adds a visible cube child, so an "empty GameObject" still gives you something
you can watch. `ZoneController` (keyboard) and `StateTransitionRecorder` go on
the AI's own object.

**Nothing moves until you drive it.** `zone` changes only from your keys
(`W`/`Up` +1, `S`/`Down` −1, `R` reset), from editing `zone` on the controller in
the Inspector while playing (the value is pushed to the FSM on the next frame), or
from your own code calling `SetZone()`. There is no demo cycle, no timer, no
automatic stepping.

Every accepted change is logged with its source and a read-back from the FSM:

```
[zone] zone = 15  (from: increase key (+1))  ->  FSM reads 15, expecting Above10 (10 m up)
```

That single line answers "the variable isn't changing": if it appears, the variable did
change and the machine is what to look at next (the same line names the
state it expects, and a `[zone] FAIL …` warning follows if the machine disagrees);
if it never appears, the key press is not reaching the game — see the input rows
in Troubleshooting.

The scene also builds a 45 m height ruler (thin pole, tick at +10 m and +40 m) and
frames the Main Camera on the column, because the machine teleports the cube 10 m
and 40 m up — which a default camera at eye height cannot show. Set
`frameCameraOnStart` to false to keep your own camera; `C` re-frames at any time.

### What the `marker` slot is (and why it needs nothing from you)

The module declares two `Object3D` slots: `self` (the object that rises) and
`marker` (slot 1). Every state teleports the marker to `home`, so it is a visible
pin showing where home is while `self` moves to +10 m or +40 m — scenery that
makes the current state obvious, and something the controller can verify (the pin
must be back at home in every state, because the FSM says so).

Both fields that mention it are **optional overrides**:

| field | where | leave empty ⇒ |
| --- | --- | --- |
| `marker` (Transform) | `ZoneBridgeAI.Manual.cs` | the binding finds or creates an object named `ZoneMarker` (small green sphere, no collider) |
| `marker` (Transform) | `ZoneController` | the controller reads slot 1 back out of the module and verifies *that* object |

Fill either one only if you want a specific object (any GameObject, cube, empty,
your own prop) to be the pin. There is deliberately **no fallback to `self`**: the
states run `setPosition(self, …lift…)` and then `setPosition(marker, home)`, so a
marker bound to the same object would drag the lifted object back down and make
the test look broken.

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

The visible cube, the ruler and the camera framing are not decoration: with an AI
on an empty GameObject the state machine transitions and logs perfectly while
*nothing on screen changes*, which reads as a broken test. They exist so the
screen agrees with the console.

What it records:

* `state_transitions.csv` — one row per state change, straight from the runtime's
  ordered broadcast server (`time, tick, from, to, reason, zone`), i.e. the
  runtime's own account of what happened.
* `zone_checks.csv` — the controller's verdict a few frames after each change:
  expected state vs actual state, expected height vs actual height, and whether
  the marker is back at home. Any mismatch is a `FAIL` row plus a console warning.

## Test 02 — spawn stress: how many AIs before the frame budget breaks

**Goal**: find the practical maximum number of ticking AIs on this machine.

Scene: an empty GameObject with **`SpawnStressTest`** on it. Everything is built
for you:

| what | how it is made |
| --- | --- |
| ground | a **400 × 400 m** plane (Unity's plane primitive scaled), centred on the origin — room for thousands of cubes |
| player | a **2 m cylinder** with a **CharacterController** and `PlayerController`: ordinary WASD movement (`Move` + gravity, keys read in `Update`, applied in `FixedUpdate`). Rigidbodies cannot push a controller, so the crowd blocks the player and never drags it; walking into the crowd pushes cubes, because that is what a solid object moving through them does |
| camera | `TopDownFollowCamera` on the Main Camera: parked 45 m above and 18 m behind the player, looking down, following it in `LateUpdate` |
| cubes | only when you ask: `Space` = +1, `B` = +100 |

**Each cube is a dynamic Rigidbody with gravity on** (rotations locked to X/Z so
it stays upright), dropped from `spawnHeight` = 15 m over the plane. The default
placement spreads the drop points over the whole plane (`spreadFraction` 0.45), so
the crowd arrives from every direction — no pile at the middle to mistake for a
gravity well. `CentreInAir` and `AroundPlayer` are there if you want the older
shapes.

The chaser FSM walks the cube at the player's position. The runtime turns that
into one step of `position += direction x speed x time` per tick through Unity's
`MovePosition` — and because the cube has gravity, **the runtime aims the step
along the ground plane and lets Unity keep the vertical axis**: the cube falls,
lands, runs, and is never lifted or held at the player's height. Overlapping
spawns or a pile-up can push a cube up; it falls back instead of hovering.

The target is bound on the spot: `SpawnStressTest` sets `ChaserAI.Target` right
after `AddComponent`, before the component's `Start` binds its slots — per cube,
no static state. One line in the console then says what the first cube ended up
chasing (`[stress] cube #1 chases 'Player' …`), so "why is the crowd going the
wrong way?" is answered instead of guessed.

Unity's solver does the falling, the colliding and the pile-ups: **that physics
load is part of the number this test reports**, which is why the summary records
`cube gravity`, `spawn placement` and the plane size — runs with different
settings are not comparable.

Controls: `Space` = +`batchSmall` cube (1), `B` = +`batchLarge` (100), `P` = pause
spawning, `L` = write the reports now, `Backspace` = clear, `R` = player back to
the origin. Move the player with WASD: the chasers follow, so the load is real
movement, not idle ticking. Nothing spawns on its own (`spawnOnStart` is 0); keys
are read through `StressInput`, so they work with either Unity input backend.

Files:

| file | row per | columns |
| --- | --- | --- |
| `spawn_stress_spawns.csv` | spawn | index, frame, `instantiateMs`, the frame the spawn landed in, alive-after, instance id |
| `spawn_stress_frames.csv` | every 10 frames | frame, time, delta, smoothed delta, fps, alive cubes, **AIs registered in the runtime's DB**, total FSM calls served, **mean and nearest distance from the crowd to the player** (-1 = no crowd to measure) |
| `spawn_stress_summary.txt` | run | player and its body, spawned, alive, practical maximum, final fps, `crowd to player`, registry size, total FSM calls |

The last two columns of `spawn_stress_frames.csv` answer "is the crowd chasing me
or stuck?": a working chase keeps the mean distance small however far the player
walks, while a crowd that never got a target (or is jammed) shows the mean growing
with the player's walk.

The practical maximum is declared by the test itself: when the smoothed frame time
stays above `slowdownFrameMs` (33.3 ms = 30 fps) for `slowdownHoldSeconds` (1.5 s)
with at least `minimumCountForStop` cubes alive, spawning pauses and the console
says so. Set `stopWhenSlow` off to keep spawning regardless.

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

Needs no extra package: the bake is `UnityEngine.AI.NavMeshBuilder`, part of the
built-in `UnityEngine.AIModule`. With a NavMesh baked in the editor instead,
clear `buildNavMesh`.

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

## Troubleshooting compile errors

| error | meaning | fix |
| --- | --- | --- |
| `CS0246: The type or namespace name 'StateTransitionRecorder' could not be found` in `ZoneTestSetup.cs` | the recorder script is not in the project — test 01 needs it | copy `Tests/01-state-transitions/Scripts/StateTransitionRecorder.cs` somewhere under `Assets/` (any folder; Unity compiles every script under `Assets/`) |
| `CS0103: The name 'FsmValue' does not exist in the current context` in a `*.Manual.cs` | that file writes value slots, and `FsmValue` lives in `MyFSM.Core` — a `using` applies to ONE file, so `using MyFSM.Unity;` does not bring it in | add `using MyFSM.Core;` (fixed in the shipped files; if you copied them earlier, re-copy or add the line) |
| `CS0103: The name 'StressInput' / 'MazeInput' / 'ChaseInput' does not exist` | that script is from this repo but its per-folder input helper is missing from the project (each test folder carries its own copy) | copy the helper file from the same folder (`Tests/<case>/Scripts/<Name>Input.cs`) — `Tests/Tools/check_project.py` lists exactly what is missing. All four helpers are in namespace `MyFSM.Tests`, so no `using` is involved any more |
| `CS0103: The name 'X' does not exist` where `X` does not exist ANYWHERE in the current checkout (for example `InputCompat`) | your project mixes two versions: that script is from an older commit than the helper it calls. This shipped once — `SpawnStressTest.cs` kept calling `InputCompat` after the helper became `StressInput`. `Tests/Tools/check_project.py` reports the file as `STALE` because it compares contents, but only when run from the fixed checkout — and if the checkout itself holds the mistake, no content comparison can see it | re-copy the whole folder (`Tests/<case>/Scripts/`) or re-run the installer, then prove it without opening Unity: `python3 Sandbox/check.py --baseline <project>/Assets/MyFSM` names the exact missing type |
| test 02: the **player** slides on its own, or the crowd pushes him to the middle | something other than the keys is driving the player: either a dynamic Rigidbody (physics can then shove it) or an FSM AI standing on the player object (an AI ticks every frame and drags its own object towards its own goal) | the setup forces a **kinematic** body on the player (Unity: collisions do not affect a kinematic body, so the crowd blocks it but cannot move it) and prints `[stress] player '<name>': …`. If an AI is on the player it logs an error naming the component — remove it; the player is driven by keys only |
| every cube (or an object the FSM drives) walks to the **centre of the plane** | the AI's target slot is unbound. World-space reads of an unbound handle used to return `(0,0,0)`, and `(0,0,0)` is the world origin — the middle of the plane — so every such AI marched there | fixed in the runtime: such a read is now an **invalid (NaN)** position and the movement calls refuse it, so the agent stays where it is and logs `getPosition: no live source to read a position from …` once per AI. Bind the slot (or create the object it expects) — in test 02 the chaser's `player` target comes from `ChaserAI.Player` / an object named `Player` |
| an agent hovers, or climbs towards a target above it | a body whose gravity is on has its step taken in the ground plane: Unity keeps the vertical axis (`GravityOwnsVertical`). A body with gravity **off** or a kinematic body has nothing else driving it, so the goal owns all three axes and flies to the target's height | that is the walker/flyer split: `useGravity` on = ground agent (test 02), off = flyer |
| `CS0246: The type or namespace name 'AIInstance' ...` / `MainServer` | the runtime folder is missing or not under `Assets/` | install the runtime (`installers/unity/install.py`) — `Runtime/Core`, `Runtime/Unity` and `Runtime/Compiler` all have to be present |
| test 01: console shows `zone = 15 -> expecting Above10` and transitions, but **nothing visibly moves** | the AI sits on an object with no mesh (an empty GameObject): the machine is working perfectly on an invisible object | attach `ZoneTestSetup` — it adds a visible cube child to an empty object, or use a Cube primitive as the AI host. Before that change, this looked exactly like a broken test |
| test 01: **nothing happens at all**, no log lines from `[zone]` | you added `ZoneBridgeAI` on its own: the module never reads the keyboard, only its `zone` variable, and nothing writes it. The binding logs this as a warning at boot | add `ZoneTestSetup` to any GameObject — it wires the controller, the recorder and the cube; the keys then drive it (nothing moves by itself) |
| test 01: keys do nothing, console shows no `[zone] zone = …` line | input is not reaching the game | check the startup line `[zone] input backend: …`. If it reports the Input System package, or says NONE, your project's **Active Input Handling** excludes the legacy `UnityEngine.Input` API the tests used to call (that API throws in that mode, so no key ever arrives) — the controller now reads whichever backend is compiled in; the line tells you which. Anything unmapped logs `[input] <key> has no Input System mapping` |
| test 01: keys do nothing and the backend line looks fine | Unity only delivers input to a **focused** window | click inside the Game view before pressing keys — and note that pressing WASD while the mouse is over the **Scene** view flies the Scene camera instead of driving the game |
| test 01: `[zone] SetBoundValue(…) failed` or `[myFSM] SetBoundValue before execution exists` | the FSM could not be written at that moment | fixed: the first push now waits for the AI to boot. If it still appears, the AI never booted — the controller then reports `the AI never booted after N s: <BootError>` |
| test 01: `zone` changes in the Inspector but the FSM does not follow | the field was edited before the AI booted | the controller pushes any changed `zone` on the next frame and logs it; if nothing is logged at all, look for the boot error row above |

`Tests/Tools/check_project.py` finds the first three situations before Unity does.
`Sandbox/check.py` (repo-side, needs tree-sitter) fails on two `CS0103` shapes:

- **a name is used but its namespace is never imported** — it reads the top-level
  types of every shipped `MyFSM.*` namespace from the files themselves and fails
  any file that names one without importing its namespace (nested types and
  global-namespace types are ignored — the generated AI classes live in the global
  namespace and are importable by anyone). Proved by reintroducing the bug:
  `USING FAIL … names TmpProbe but never imports MyFSM.Tests.Stress; … -> RESULT: FAIL`.
- **a name is used as a type or static receiver and is declared nowhere** — the
  shape Unity reported for `InputCompat` and earlier for `FsmValue`. Everything the
  repository uses that lives outside it (UnityEngine, UnityEditor, `System`) is
  listed in `Sandbox/known_external_names.txt`; the check fails any other
  `Foo.Bar()`, `new Foo(…)` or `: Foo` whose `Foo` no folder declares. Proved the
  same way: a file calling `InputCompat.GetKeyDown(...)` → `UNDEFINED NAME
  InputCompat … -> RESULT: FAIL`, `RESULT: OK` once removed.

Before the `InputCompat` round the second shape did not exist, which is exactly how
a renamed helper shipped with a caller still using the old name.

### Checking a project you already copied

The second check also runs against a *project*, which is how to tell "my copy is
from the broken commit" from "the FSM itself is misbehaving":

```
python3 Sandbox/check.py --baseline <unity-project>/Assets/MyFSM
```

It parses the project's `Assets/MyFSM` instead of the repository and treats the
repository's own files as the baseline, so each name the project uses that the
current repository does not declare is printed with `CS0103` next to it. On a
current copy: `names ok … RESULT: OK`. On a copy that still holds the old helper
name, the name is listed. Naming the wrong directory (say the project root) is
reported as a usage error rather than passing quietly.

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
  `installers/unity/install.py` does that (`Tests/` ships with the payload, so
  the installer puts these test folders under `Assets/MyFSM/Tests/`). Use
  `--branch arena/01a0af62-myfsm` or `--source local`: the installer's default
  `test` branch predates several fixes and these tests.
* Nothing else. `NavMeshAgent`/`NavMeshBuilder` are built-in Unity modules.
* No editor-time step: the modules are already compiled into the generated class
  files, so nothing needs assigning in the inspector — attach the setup
  component and press Play.
