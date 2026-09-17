# myFSM Unity Runtime — Function & Override Reference

Every built-in function the FSM language can call, every override point the
runtime offers, and the host API around them. Generated from
`Runtime/Core/FunctionCatalog.cs` (the same table the runtime dispatches on),
so the IDs, tiers and signatures here cannot drift from the implementation.

Counts are asserted by `Sandbox/check.py`: **179 overloads, 92 distinct
functions, 11 categories**. Each catalog ID maps to exactly one `case` in the
Unity dispatchers, and every ID sits in exactly one category range.

---

## 1. How to read the tables

| Column | Meaning |
|---|---|
| **ID** | The stable ABI. The compiled module stores this number, the runtime dispatches on it. IDs are never renumbered or reused. |
| **Signature** | DSL types, not C#: `Object3D`, `Vector3`, `float`… `-> void` means it is a statement, anything else is an expression you can assign. |
| **Tier** | Where the call is allowed (see §2). |
| **Notes** | What the Unity implementation actually does, including the defaults it falls back to. |

Tier 3 rows drive an object over time, so their first argument must be a
runtime `var`/`temp` (see §2). Everything else is call-site free.

Overloads of one name are grouped so the note is written once, on the first
row of that name: the difference between them is normally 2D vs 3D
(`Object3D`/`Vector3` vs `Object2D`/`Vector2`), a point vs an object
argument, or a fixed vs a caller-supplied speed. `getPosition` is the one
name that appears in two categories (Object and Camera), so it carries a
note in each.

## 2. Tiers — where a call may appear

| Tier | Kind | Can appear in | Constraint |
|---|---|---|---|
| **1** | Query — reads engine state | expressions, conditions, initializers, call arguments | none |
| **2** | Mutate — writes engine state | statements only | the mutated object must be a runtime `var`/`temp`, not a `const` |
| **3** | Controller-driven — navigation, steering, control that plays out over time | statements only | the driven object must be a runtime `var`/`temp` (this is the `varTarget` flag in the catalog) |

37 of the 39 Tier 3 overloads require a variable target (the catalog's
`varTarget` flag). The two that do not are `wait` and `waitUntil`: they are
Tier 3 because they suspend the AI over time, but they drive no object, so any
expression is allowed there.

Tier 3 calls never do the work themselves: they post a **goal** onto the AI's
`MovementSystem`, which advances it once per tick, before the state's `Update`
round. That is why `waitUntil(hasReachedDestination(agent, point))` can come
true, and why movement keeps advancing while the AI is suspended.

The step is then applied through the components the agent actually carries —
this is the environment check, and it is why walls stop an AI instead of
letting it walk through:

| On the agent | How the step is applied |
|---|---|
| `NavMeshAgent` (enabled, on a mesh) | `SetDestination` — the mesh pathfinds and steers |
| `CharacterController` | `Move()` — the controller's own capsule sweep handles slopes, steps and walls |
| `Rigidbody`/`Rigidbody2D`, dynamic | velocity is set each tick; the physics solver resolves the contacts (mass, drag, gravity keep working) |
| `Rigidbody`/`Rigidbody2D`, kinematic | moved here (overlap resolution), then `MovePosition` — the solver does not collide kinematic bodies |
| `Collider`/`Collider2D` only | substeps through `Physics.ComputePenetration` / `Collider2D.Distance`: the engine supplies the minimal translation that separates the body, and the leftover step slides along the contact |
| no components | the same substep loop with `SphereCast`/`CircleCast` and a ray for the true surface normal, at the default body radius |

Detection and resolution are Unity's — `ComputePenetration`/`Collider2D.Distance`
for a body with a collider, `SphereCast`/`CircleCast` + a ray for one without,
`Move()`, and the solver. Movement only chooses where to ask and what to do
with the answer, and it logs the driver once per agent
(`movement: <object> driven by collider sweep`) plus the first time something
stops it (`movement: <object> stopped by Wall`). `Movement.CollisionAware = false`
skips the environment entirely and writes to the transform (the escape hatch
for objects whose movement something else owns); NavMeshAgent pathing is
unaffected either way.

---

## 3. Functions by category

### Math — 25 overloads, 25 functions (`0x0000`–`0x0018`)

Pure computation over `Mathf` / `Vector3`. No engine state is read or written.

| ID | Signature | Tier | Notes |
|---|---|---|---|
| `0x0000` | `sin(float x) -> float` | 1 | Mathf.Sin. Radians in, float out. |
| `0x0001` | `cos(float x) -> float` | 1 | Mathf.Cos. Radians. |
| `0x0002` | `tan(float x) -> float` | 1 | Mathf.Tan. Radians. |
| `0x0003` | `asin(float x) -> float` | 1 | Mathf.Asin. Radians out. |
| `0x0004` | `acos(float x) -> float` | 1 | Mathf.Acos. Radians out. |
| `0x0005` | `atan(float x) -> float` | 1 | Mathf.Atan. Radians out. |
| `0x0006` | `atan2(float y, float x) -> float` | 1 | Mathf.Atan2(y, x) — **y first**, as in Unity/Math. |
| `0x0007` | `sqrt(float x) -> float` | 1 | Mathf.Sqrt. Negative input yields NaN (no clamp). |
| `0x0008` | `pow(float b, float exp) -> float` | 1 | Mathf.Pow(b, exp). |
| `0x0009` | `abs(float x) -> float` | 1 | Mathf.Abs. |
| `0x000A` | `sign(float x) -> float` | 1 | -1, 0 or 1. |
| `0x000B` | `clamp(float x, float lo, float hi) -> float` | 1 | clamp(x, lo, hi); lo > hi is not corrected. |
| `0x000C` | `lerp(float a, float b, float t) -> float` | 1 | lerp(a, b, t) with **t unclamped** — t outside [0,1] extrapolates. |
| `0x000D` | `min(float a, float b) -> float` | 1 | Smaller of two floats. |
| `0x000E` | `max(float a, float b) -> float` | 1 | Larger of two floats. |
| `0x000F` | `floor(float x) -> float` | 1 | Rounds toward -infinity (float in / float out). |
| `0x0010` | `ceil(float x) -> float` | 1 | Rounds toward +infinity. |
| `0x0011` | `round(float x) -> float` | 1 | Rounds to nearest (banker's rounding via Math.Round). |
| `0x0012` | `normalize(Vector3 v) -> Vector3` | 1 | Vector3.normalized. A zero vector stays zero (no NaN, no error). |
| `0x0013` | `dot(Vector3 a, Vector3 b) -> float` | 1 | Vector3.Dot. |
| `0x0014` | `cross(Vector3 a, Vector3 b) -> Vector3` | 1 | Vector3.Cross (right-handed, as Unity). |
| `0x0015` | `distance(Vector3 a, Vector3 b) -> float` | 1 | Vector3.Distance between two points. |
| `0x0016` | `magnitude(Vector3 v) -> float` | 1 | Length of a vector. |
| `0x0017` | `random() -> float` | 1 | UnityEngine.Random.value — [0, 1). |
| `0x0018` | `randomRange(float lo, float hi) -> float` | 1 | UnityEngine.Random.Range(lo, hi) — [lo, hi). |

### Object — 22 overloads, 12 functions (`0x0100`–`0x0115`)

The base object model: position, rotation, scale, activeness, tag, layer. This is where the 2D/3D overload pairs start.

| ID | Signature | Tier | Notes |
|---|---|---|---|
| `0x0100` | `getPosition(Object3D obj) -> Vector3` | 1 | transform.position. The 2D overload drops z (Vector2). |
| `0x0101` | `getPosition(Object2D obj) -> Vector2` | 1 |  |
| `0x0102` | `getRotation(Object3D obj) -> Quaternion` | 1 | transform.rotation as a Quaternion (3D only). |
| `0x0103` | `getScale(Object3D obj) -> Vector3` | 1 | transform.localScale; 2D overload returns (x, y). |
| `0x0104` | `getScale(Object2D obj) -> Vector2` | 1 |  |
| `0x0105` | `setPosition(Object3D obj, Vector3 pos) -> void` | 2 | Writes transform.position (Tier 2). Use it sparingly — it teleports. |
| `0x0106` | `setPosition(Object2D obj, Vector2 pos) -> void` | 2 |  |
| `0x0107` | `setRotation(Object3D obj, Quaternion rot) -> void` | 2 | Writes transform.rotation (Tier 2). |
| `0x0108` | `setScale(Object3D obj, Vector3 scale) -> void` | 2 | Writes transform.localScale (Tier 2). |
| `0x0109` | `setScale(Object2D obj, Vector2 scale) -> void` | 2 |  |
| `0x010A` | `distanceTo(Object3D a, Object3D b) -> float` | 1 | Distance between two objects; 2D compares only x/y. |
| `0x010B` | `distanceTo(Object2D a, Object2D b) -> float` | 1 |  |
| `0x010C` | `directionTo(Object3D a, Object3D b) -> Vector3` | 1 | **Normalized** (b − a). Combine with a distance/speed when you need a point. |
| `0x010D` | `directionTo(Object2D a, Object2D b) -> Vector2` | 1 |  |
| `0x010E` | `isActive(Object3D obj) -> bool` | 1 | GameObject.activeSelf. |
| `0x010F` | `isActive(Object2D obj) -> bool` | 1 |  |
| `0x0110` | `setActive(Object3D obj, bool active) -> void` | 2 | GameObject.SetActive (Tier 2). |
| `0x0111` | `setActive(Object2D obj, bool active) -> void` | 2 |  |
| `0x0112` | `getTag(Object3D obj) -> int` | 1 | FNV-1a 32-bit hash of the tag string as an int — compare it with another getTag result, not with a literal. |
| `0x0113` | `getTag(Object2D obj) -> int` | 1 |  |
| `0x0114` | `getLayer(Object3D obj) -> int` | 1 | GameObject.layer index. |
| `0x0115` | `getLayer(Object2D obj) -> int` | 1 |  |

### Sprite — 12 overloads, 6 functions (`0x0200`–`0x020B`)

Sprite + renderer state: colour, visibility, bounds, sprite size.

| ID | Signature | Tier | Notes |
|---|---|---|---|
| `0x0200` | `getColor(Sprite3D spr) -> Vector3` | 1 | SpriteRenderer colour as (r, g, b) — **alpha is dropped**; the result is a Vector3, not a colour type. |
| `0x0201` | `getColor(Sprite2D spr) -> Vector3` | 1 |  |
| `0x0202` | `setColor(Sprite3D spr, Vector3 rgb) -> void` | 2 | Writes r/g/b from a Vector3 and leaves **alpha untouched** (Tier 2) — the mirror of getColor. |
| `0x0203` | `setColor(Sprite2D spr, Vector3 rgb) -> void` | 2 |  |
| `0x0204` | `isVisible(Sprite3D spr) -> bool` | 1 | Whether the renderer is enabled/visible. |
| `0x0205` | `isVisible(Sprite2D spr) -> bool` | 1 |  |
| `0x0206` | `setVisible(Sprite3D spr, bool visible) -> void` | 2 | Toggles renderer visibility (Tier 2). |
| `0x0207` | `setVisible(Sprite2D spr, bool visible) -> void` | 2 |  |
| `0x0208` | `getBounds(Sprite3D spr) -> Vector3` | 1 | World-space **size** of the sprite bounds (Vector3). |
| `0x0209` | `getBounds(Sprite2D spr) -> Vector2` | 1 |  |
| `0x020A` | `getSize(Sprite3D spr) -> Vector2` | 1 | Sprite size in world units: rect.size / pixelsPerUnit (Vector2). Logs an error when no sprite is assigned. |
| `0x020B` | `getSize(Sprite2D spr) -> Vector2` | 1 |  |

### Animation — 18 overloads, 9 functions (`0x0300`–`0x0311`)

Animator channel: play/stop/pause/resume, speed, progress, and jumping to a state by id. Each handle keeps its own speed memory, so `pause`/`resume` round-trip.

| ID | Signature | Tier | Notes |
|---|---|---|---|
| `0x0300` | `play(AnimationController3D ctrl) -> void` | 2 | Enables the Animator and restores its channel speed (resumes from wherever it is). |
| `0x0301` | `play(AnimationController2D ctrl) -> void` | 2 |  |
| `0x0302` | `stop(AnimationController3D ctrl) -> void` | 2 | Animator.Rebind + disable — rewinds to the default pose. |
| `0x0303` | `stop(AnimationController2D ctrl) -> void` | 2 |  |
| `0x0304` | `pause(AnimationController3D ctrl) -> void` | 2 | Remembers the current speed and sets it to 0. |
| `0x0305` | `pause(AnimationController2D ctrl) -> void` | 2 |  |
| `0x0306` | `resume(AnimationController3D ctrl) -> void` | 2 | Restores the speed saved by pause. |
| `0x0307` | `resume(AnimationController2D ctrl) -> void` | 2 |  |
| `0x0308` | `isPlaying(AnimationController3D ctrl) -> bool` | 1 | True when the animator is enabled **and** speed > 0 (so a paused animator reads false). |
| `0x0309` | `isPlaying(AnimationController2D ctrl) -> bool` | 1 |  |
| `0x030A` | `getCurrentClip(AnimationController3D ctrl) -> int` | 1 | fullPathHash of the animator's layer-0 state id (an int, stable per clip). |
| `0x030B` | `getCurrentClip(AnimationController2D ctrl) -> int` | 1 |  |
| `0x030C` | `setSpeed(AnimationController3D ctrl, float speed) -> void` | 2 | Sets the animator's playback speed (and remembers it on the channel). |
| `0x030D` | `setSpeed(AnimationController2D ctrl, float speed) -> void` | 2 |  |
| `0x030E` | `getProgress(AnimationController3D ctrl) -> float` | 1 | Normalized time of layer 0, wrapped into [0, 1). |
| `0x030F` | `getProgress(AnimationController2D ctrl) -> float` | 1 |  |
| `0x0310` | `setAnimation(AnimationController3D ctrl, int clip) -> void` | 2 | Animator.Play(stateId, 0, 0) — jumps straight to a state by its integer id (Tier 2). |
| `0x0311` | `setAnimation(AnimationController2D ctrl, int clip) -> void` | 2 |  |

### Physics — 18 overloads, 9 functions (`0x0400`–`0x0411`)

Rigidbody state and collision queries. `applyForce` accumulates for the physics step; `applyImpulse` is an instant kick; `raycast` answers only yes/no — use `getRaycastHit` for the object.

| ID | Signature | Tier | Notes |
|---|---|---|---|
| `0x0400` | `getVelocity(PhysicsObject3D phys) -> Vector3` | 1 | Rigidbody(2D).velocity; zero when there is no rigidbody. |
| `0x0401` | `getVelocity(PhysicsObject2D phys) -> Vector2` | 1 |  |
| `0x0402` | `setVelocity(PhysicsObject3D phys, Vector3 v) -> void` | 2 | Assigns the rigidbody velocity (Tier 2) — instant, frame-rate independent motion. |
| `0x0403` | `setVelocity(PhysicsObject2D phys, Vector2 v) -> void` | 2 |  |
| `0x0404` | `getMass(PhysicsObject3D phys) -> float` | 1 | Rigidbody(2D).mass; **0** when there is no rigidbody (not 1). |
| `0x0405` | `getMass(PhysicsObject2D phys) -> float` | 1 |  |
| `0x0406` | `applyForce(PhysicsObject3D phys, Vector3 force) -> void` | 2 | AddForce — accumulates, meant for the physics step. |
| `0x0407` | `applyForce(PhysicsObject2D phys, Vector2 force) -> void` | 2 |  |
| `0x0408` | `applyImpulse(PhysicsObject3D phys, Vector3 impulse) -> void` | 2 | AddForce(..., ForceMode.Impulse) — an instant kick. |
| `0x0409` | `applyImpulse(PhysicsObject2D phys, Vector2 impulse) -> void` | 2 |  |
| `0x040A` | `isGrounded(PhysicsObject3D phys) -> bool` | 1 | Downward raycast from the collider centre, length = extents.y + 0.2 (0.35 with no collider). |
| `0x040B` | `isGrounded(PhysicsObject2D phys) -> bool` | 1 |  |
| `0x040C` | `isColliding(PhysicsObject3D phys) -> bool` | 1 | OverlapSphere around the body (radius = bounds extents magnitude, min 0.1; 0.3 with no collider) — true if anything else overlaps. |
| `0x040D` | `isColliding(PhysicsObject2D phys) -> bool` | 1 |  |
| `0x040E` | `getCollisionNormal(PhysicsObject3D phys) -> Vector3` | 1 | Normal pointing away from the nearest overlapping collider's closest point; falls back to up. |
| `0x040F` | `getCollisionNormal(PhysicsObject2D phys) -> Vector2` | 1 |  |
| `0x0410` | `raycast(Vector3 origin, Vector3 dir, float dist) -> bool` | 1 | Returns only a bool. Use `getRaycastHit` (Sensing) when you need the object that was hit. |
| `0x0411` | `raycast(Vector2 origin, Vector2 dir, float dist) -> bool` | 1 |  |

### Camera — 10 overloads, 5 functions (`0x0500`–`0x0509`)

Camera-space helpers: view tests and screen/world/viewport conversion.

| ID | Signature | Tier | Notes |
|---|---|---|---|
| `0x0500` | `getPosition(Camera3D cam) -> Vector3` | 1 | transform.position. The 2D overload drops z (Vector2). |
| `0x0501` | `getPosition(Camera2D cam) -> Vector2` | 1 |  |
| `0x0502` | `isInView(Camera3D cam, Object3D obj) -> bool` | 1 | WorldToViewportPoint: in front of the camera (z > 0) **and** inside [0,1]². |
| `0x0503` | `isInView(Camera2D cam, Object2D obj) -> bool` | 1 |  |
| `0x0504` | `screenToWorld(Camera3D cam, Vector2 screen) -> Vector3` | 1 | Screen pixels → a world point at a **fixed depth** (nearClipPlane + 1), not a caller-supplied one. |
| `0x0505` | `screenToWorld(Camera2D cam, Vector2 screen) -> Vector2` | 1 |  |
| `0x0506` | `worldToScreen(Camera3D cam, Vector3 world) -> Vector2` | 1 | Camera.WorldToScreenPoint — a world point → screen pixels (z carries depth). |
| `0x0507` | `worldToScreen(Camera2D cam, Vector3 world) -> Vector2` | 1 |  |
| `0x0508` | `getViewport(Camera3D cam) -> Vector2` | 1 | Camera pixelWidth/pixelHeight as a Vector2. |
| `0x0509` | `getViewport(Camera2D cam) -> Vector2` | 1 |  |

### Navigation — 45 overloads, 11 functions (`0x0600`–`0x062C`)

Path queries and goal-posting movement — the largest category, and the only one that spans all three tiers. Posting a goal never teleports: the movement system advances it a step per tick, and HOW that step is applied is decided by the components on the agent (NavMeshAgent, CharacterController, Rigidbody(2D), a bare Collider, or nothing — see §2 and the movement section of the README).

| ID | Signature | Tier | Notes |
|---|---|---|---|
| `0x0600` | `findPath(Vector3 from, Vector3 to) -> int` | 1 | Stores a corner list and returns its **path id** (int). NavMesh corners when available, straight line otherwise. Ids are per-AI; 0 is invalid. |
| `0x0601` | `findPath(Vector2 from, Vector2 to) -> int` | 1 |  |
| `0x0602` | `getNextWaypoint(int path) -> Vector3` | 1 | Pops the next corner of a path id. Past the end it returns the last corner forever (no error). |
| `0x0603` | `getPathLength(int path) -> float` | 1 | Total length of the stored polyline; 0 for an unknown path (logs an error). |
| `0x0604` | `hasReachedDestination(NavMeshAgent agent, Vector3 tgt) -> bool` | 1 | Within the stopping distance of a point/object: the posted goal's stop distance if there is one, else the NavMeshAgent's, else 0.2. |
| `0x0605` | `hasReachedDestination(NavMeshAgent agent, Object3D tgt) -> bool` | 1 |  |
| `0x0606` | `hasReachedDestination(Object3D agent, Vector3 tgt) -> bool` | 1 |  |
| `0x0607` | `hasReachedDestination(Object3D agent, Object3D tgt) -> bool` | 1 |  |
| `0x0608` | `hasReachedDestination(Object2D agent, Vector2 tgt) -> bool` | 1 |  |
| `0x0609` | `hasReachedDestination(Object2D agent, Object2D tgt) -> bool` | 1 |  |
| `0x060A` | `goTo(NavMeshAgent agent, Vector3 dest) -> void` | 3 | Posts a Point goal at the agent's navigation speed. The destination is read **once, now**: an object argument is snapshotted, not chased — use `follow` to track something that moves. |
| `0x060B` | `goTo(NavMeshAgent agent, Object3D dest) -> void` | 3 |  |
| `0x060C` | `goTo(Object3D agent, Vector3 dest) -> void` | 3 |  |
| `0x060D` | `goTo(Object3D agent, Object3D dest) -> void` | 3 |  |
| `0x060E` | `goTo(Object2D agent, Vector2 dest) -> void` | 3 |  |
| `0x060F` | `goTo(Object2D agent, Object2D dest) -> void` | 3 |  |
| `0x0610` | `followTarget(NavMeshAgent agent, Object3D tgt) -> void` | 3 | Same as `follow` but stops at the **full** stopping distance, so it keeps the agent's normal stand-off instead of closing in. |
| `0x0611` | `followTarget(NavMeshAgent agent, Object2D tgt) -> void` | 3 |  |
| `0x0612` | `followTarget(Object3D agent, Object3D tgt) -> void` | 3 |  |
| `0x0613` | `followTarget(Object2D agent, Object2D tgt) -> void` | 3 |  |
| `0x0614` | `findShortestPathAndMove(NavMeshAgent agent, Vector3 tgt) -> void` | 3 | Posts a corner queue (PathCorners) and walks it. |
| `0x0615` | `findShortestPathAndMove(NavMeshAgent agent, Object3D tgt) -> void` | 3 |  |
| `0x0616` | `findShortestPathAndMove(Object3D agent, Vector3 tgt) -> void` | 3 |  |
| `0x0617` | `findShortestPathAndMove(Object3D agent, Object3D tgt) -> void` | 3 |  |
| `0x0618` | `findShortestPathAndMove(Object2D agent, Vector2 tgt) -> void` | 3 |  |
| `0x0619` | `findShortestPathAndMove(Object2D agent, Object2D tgt) -> void` | 3 |  |
| `0x061A` | `follow(NavMeshAgent agent, Object3D tgt) -> void` | 3 | Posts a FollowObject goal: re-reads the target's position every tick, so it chases a moving object forever. Stops at **half** the agent's stopping distance — the tighter of the two follow calls. |
| `0x061B` | `follow(NavMeshAgent agent, Object2D tgt) -> void` | 3 |  |
| `0x061C` | `follow(Object3D agent, Object3D tgt) -> void` | 3 |  |
| `0x061D` | `follow(Object2D agent, Object2D tgt) -> void` | 3 |  |
| `0x061E` | `sprintTowards(NavMeshAgent agent, Vector3 dest, float speedMult) -> void` | 3 | Point goal at **base speed × multiplier** (multiplier is the 3rd argument; negative clamps to 0). |
| `0x061F` | `sprintTowards(NavMeshAgent agent, Object3D dest, float speedMult) -> void` | 3 |  |
| `0x0620` | `sprintTowards(Object3D agent, Vector3 dest, float speedMult) -> void` | 3 |  |
| `0x0621` | `sprintTowards(Object3D agent, Object3D dest, float speedMult) -> void` | 3 |  |
| `0x0622` | `sprintTowards(Object2D agent, Vector2 dest, float speedMult) -> void` | 3 |  |
| `0x0623` | `sprintTowards(Object2D agent, Object2D dest, float speedMult) -> void` | 3 |  |
| `0x0624` | `moveTowards(NavMeshAgent agent, Vector3 dest, float speed) -> void` | 3 | Point goal at an **absolute** speed (units/second). Pass a destination POINT, not a direction. |
| `0x0625` | `moveTowards(NavMeshAgent agent, Object3D dest, float speed) -> void` | 3 |  |
| `0x0626` | `moveTowards(Object3D agent, Vector3 dest, float speed) -> void` | 3 |  |
| `0x0627` | `moveTowards(Object3D agent, Object3D dest, float speed) -> void` | 3 |  |
| `0x0628` | `moveTowards(Object2D agent, Vector2 dest, float speed) -> void` | 3 |  |
| `0x0629` | `moveTowards(Object2D agent, Object2D dest, float speed) -> void` | 3 |  |
| `0x062A` | `stopMovement(NavMeshAgent agent) -> void` | 3 | Clears the goal (and stops a NavMeshAgent). |
| `0x062B` | `stopMovement(Object3D agent) -> void` | 3 |  |
| `0x062C` | `stopMovement(Object2D agent) -> void` | 3 |  |

### Perception — 14 overloads, 7 functions (`0x0700`–`0x070D`)

What the AI can sense about other objects: facing, line of sight, range, angle, distance, and tag/radius scans.

| ID | Signature | Tier | Notes |
|---|---|---|---|
| `0x0700` | `lookAt(Object3D src, Object3D tgt) -> void` | 3 | Posts a LookAt goal: gradual rotation toward the target. The goal is **retired within 0.5°**, so re-post it every tick to track a moving target. 2D uses +X as forward and rotates around Z. |
| `0x0701` | `lookAt(Object2D src, Object2D tgt) -> void` | 3 |  |
| `0x0702` | `isInLineOfSight(Object3D src, Object3D tgt) -> bool` | 1 | Raycast a → b: true when nothing blocks, or when the first thing hit **is** b. |
| `0x0703` | `isInLineOfSight(Object2D src, Object2D tgt) -> bool` | 1 |  |
| `0x0704` | `isInRange(Object3D src, Object3D tgt, float radius) -> bool` | 1 | Squared-distance test against the radius (cheaper than a raycast). |
| `0x0705` | `isInRange(Object2D src, Object2D tgt, float radius) -> bool` | 1 |  |
| `0x0706` | `getAngleTo(Object3D src, Object3D tgt) -> float` | 1 | Degrees between the object's forward (+X in 2D) and the direction to the target. |
| `0x0707` | `getAngleTo(Object2D src, Object2D tgt) -> float` | 1 |  |
| `0x0708` | `getDistanceTo(Object3D src, Object3D tgt) -> float` | 1 | Distance between two objects (the perception overload; same maths as Object.distanceTo). |
| `0x0709` | `getDistanceTo(Object2D src, Object2D tgt) -> float` | 1 |  |
| `0x070A` | `getNearestOfTag(Vector3 pos, int tag, float radius) -> Object3D` | 1 | OverlapSphere, keeps objects whose tag HASH matches, returns the nearest as a handle (null handle when none). |
| `0x070B` | `getNearestOfTag(Vector2 pos, int tag, float radius) -> Object2D` | 1 |  |
| `0x070C` | `getAllInRadius(Vector3 pos, float radius) -> int` | 1 | Returns an **int count** of colliders in the radius — it cannot be iterated. Use getNearestOfTag for an object. |
| `0x070D` | `getAllInRadius(Vector2 pos, float radius) -> int` | 1 |  |

### Steering — 10 overloads, 5 functions (`0x0800`–`0x0809`)

Pure vector maths for classic steering behaviours: flee, pursuit, separation, arrival, wander. These **return** a vector; they do not move anything — feed the result to `moveTowards`/`setVelocity`.

| ID | Signature | Tier | Notes |
|---|---|---|---|
| `0x0800` | `getFleeDirection(Vector3 from, Vector3 threat) -> Vector3` | 1 | Normalized (from − threat) — the direction to run. |
| `0x0801` | `getFleeDirection(Vector2 from, Vector2 threat) -> Vector2` | 1 |  |
| `0x0802` | `getPursuitPosition(Object3D tgt, float speed) -> Vector3` | 1 | Target position + its rigidbody velocity (a one-second lead) when the `speed` argument is positive **and** the target is actually moving; otherwise just the target's position. Aim at the returned point to intercept. |
| `0x0803` | `getPursuitPosition(Object2D tgt, float speed) -> Vector2` | 1 |  |
| `0x0804` | `getSeparationVector(Object3D agent, int neighbors) -> Vector3` | 1 | Normalized sum of away-vectors to the N nearest neighbours inside the default separation radius (N is the 2nd argument, ≤ 0 returns zero). |
| `0x0805` | `getSeparationVector(Object2D agent, int neighbors) -> Vector2` | 1 |  |
| `0x0806` | `getArrivalVector(Vector3 pos, Vector3 dest, float slowRadius) -> Vector3` | 1 | Direction to the target scaled by min(dist / slowRadius, 1) — feed it to setVelocity or moveTowards for a smooth stop. |
| `0x0807` | `getArrivalVector(Vector2 pos, Vector2 dest, float slowRadius) -> Vector2` | 1 |  |
| `0x0808` | `getWanderVector(Vector3 pos, float radius) -> Vector3` | 1 | Random planar **offset** of the given radius; it ignores its `pos` argument, so add it to a position. |
| `0x0809` | `getWanderVector(Vector2 pos, float radius) -> Vector2` | 1 |  |

### Sensing — 2 overloads, 1 function (`0x0900`–`0x0901`)

Scene queries that return an object rather than a number.

| ID | Signature | Tier | Notes |
|---|---|---|---|
| `0x0900` | `getRaycastHit(Vector3 origin, Vector3 dir, float dist) -> Object3D` | 1 | Raycast that returns the hit object as a handle (null handle when nothing is hit) — the object-returning half of `raycast`. |
| `0x0901` | `getRaycastHit(Vector2 origin, Vector2 dir, float dist) -> Object2D` | 1 |  |

### Control — 3 overloads, 3 functions (`0x0A00`–`0x0A02`)

Flow control inside the DSL: suspension (`wait`/`waitUntil`) and event emission.

| ID | Signature | Tier | Notes |
|---|---|---|---|
| `0x0A00` | `wait(float seconds) -> void` | 3 | Suspends Update + Traversals for N seconds. Movement keeps advancing, so in-flight motion continues. |
| `0x0A01` | `waitUntil(bool condition) -> void` | 3 | Suspends Update + Traversals until the condition is true (re-evaluated every tick). |
| `0x0A02` | `emit(int eventId) -> void` | 2 | Records an event id into the main database, where queries and the broadcast servers can see it. |

---

## 4. Overrides — the C# side you extend

### 4.1 `AIInstance` virtuals

`AIInstance` is the MonoBehaviour every AI derives from. All of these are
optional: the defaults are exactly what a plain AI needs.

| Member | Kind | Default | When it runs / is used | Override to |
|---|---|---|---|---|
| `ModuleResourcePath` | `string` | `null` | Read during boot, after the embedded bytes | Legacy only: point at a `TextAsset` under a `Resources/` folder. Self-contained classes leave this null. |
| `GeneratedModuleName` | `string` | `null` | Read during boot when no inspector name override is set | Rename the module in logs/DB/query responses. Generated classes return the module stem. |
| `EmbeddedModule` | `byte[]` | `null` | Read during boot, before the Resources path | Return compiled module bytes so the component needs no asset. Generated classes return their embedded blob; hand-written ones can load from anywhere. |
| `AssignedModuleAsset` | `TextAsset` | `null` | Read first during boot; wins over everything | Expose an inspector module slot. Only `FsmbAIInstance` does — that is why generated classes show no module field. |
| `Start()` | `protected virtual void` | boots from asset, disables the component on failure | Unity, once, first frame | Pre-seed state before boot. Call `base.Start()` or boot yourself, or the AI never runs. |
| `Update()` | `protected virtual void` | `TickInternal()` | Unity, every frame | Rarely. Overriding **without** calling `base.Update()` silently stops the AI (Unity only calls the most-derived `Update`). Prefer `Paused` or `enabled = false`. |
| `OnDestroy()` | `protected virtual void` | unregisters from the main server | Unity, on destroy | Extra teardown. Call `base.OnDestroy()` to unregister. |
| `OnBindingsRequired()` | `protected virtual void` | empty | During boot, **after** the inspector list, **before** the entry state's `Start{}` | Bind slots in code. Generated classes override it with per-slot comments and a call to `OnBindingsManual()`. |
| `AutoBindHandles` | `bool` | `true` | Read by the auto-bind base layer of every boot | Kill switch: return `false` to bind every handle slot by hand. |

### 4.2 Lifecycle, precisely

```
Unity Start()
  └─ BootFromAsset()                        fail => BootError + enabled = false
       module source order: AssignedModuleAsset -> EmbeddedModule -> Resources
       └─ BootCore()
            read + validate the module (fail fast, one error line)
            MainServer.EnsureExists(); alloc instance id
            build Handles / Movement / Paths / Dispatcher / AiExecution
            auto-bind base layer (unique handle tags -> this GameObject)
            inspector slot list, then OnBindingsRequired()   [fresh boot]
            ...or journal replay                            [hot reload]
            Execution.Boot()  (selects the entry head; enters nothing)
            register asset + instance

Unity Update()  (every frame, for every AI)
  └─ TickInternal()
       Paused        -> DB sample only, nothing advances
       Movement.Advance(dt)      fresh positions for this tick's decisions
       Execution.Tick()          suspension -> external -> Update{} -> Traversals{}
       head changed  -> DB row + ordered/priority broadcast + tick sample
```

The entry state's `Start{}` runs on the **first tick**, not in Unity's
`Start()` — which is why bindings are applied before boot.

### 4.3 Generated-class overrides

A generated class is a `sealed partial class XxxAI : AIInstance` with:

| Emitted | Purpose |
|---|---|
| `GeneratedModuleName` | The module stem, for logs/DB. |
| `EmbeddedModule` (+ `ModuleData`) | The module bytes, base64, at the bottom of the file. This is what makes the class self-contained. |
| `State_*` consts | State names as strings — never hardcode them. |
| `Slot_*` consts | Binding slot indices, with a comment naming the DSL type and whether it auto-binds. |
| `OnBindingsRequired()` | Emits `base.OnBindingsRequired()`, per-slot comments, then calls `OnBindingsManual()`. |
| `partial void OnBindingsManual();` | Declaration only. **Never rewritten** by the compiler. |

A generated class deliberately has **no** module asset field and emits no
`AssetResourcePath`/`ModuleResourcePath`: it carries its own module. The one
exception is the legacy `GenerateSource` overload that receives no bytes —
that shape still emits the Resources path, because it has nothing else.

### 4.4 Interfaces you can implement

| Interface | Implement to | Notes |
|---|---|---|
| `ITimeProvider` | Feed your own clock | `Time` + `DeltaTime`. Default `UnityTimeProvider` uses `UnityEngine.Time`. |
| `IExecutionLog` | Route runtime logging | `Info` / `Warn` / `Error`. Default logs through `Debug` with a `[myFSM name@object]` prefix. |
| `IFunctionDispatcher` | Serve calls yourself | `Dispatch(id, args, exec)`. Default resolves handles against the scene; the sandbox harness substitutes a stub. |
| `IMotionProbe` | Replace movement's collision queries | `ResolvePenetration` (the engine's minimal translation for the body's own collider: `Physics.ComputePenetration` / `Collider2D.Distance`), `SphereCast` and `Raycast` (used when the object has no collider). `MovementSystem.Probe` is swappable at runtime — that is how the stop/slide/push-out tests run engine-free. |
| `IQueryBackend` | Answer external queries/commands | Implemented by `MainServer`; the query server is the only consumer. |

---

## 5. Host API

### 5.1 `AIInstance` — public members

| Member | Type | Meaning |
|---|---|---|
| `Execution` | `AiExecution` | The booted module + variable tables + state handler. `null` before boot. |
| `Handles` | `HandleTable` | Handle-id ↔ Unity object map for this AI. |
| `Movement` | `MovementSystem` | Posted goals, and the environment check: `Movement.Resolve(t, is2D)` reports the `MotionDriver` chosen for an object. `Movement.Probe` and `Movement.CollisionAware` live here too. |
| `Paths` | `PathTable` | Path ids handed out by `findPath`. |
| `Dispatcher` | `FunctionDispatcher` | Resolves handles/components for built-in calls. |
| `InstanceId` / `ModuleName` / `Booted` / `BootError` | — | Registry identity and boot outcome. |
| `Paused` | `bool` | Soft pause: DB keeps sampling, nothing ticks. |
| `CurrentStateName` / `DisplayName` | `string` | `"<none>"` before the first tick; `"Module@GameObject"`. |
| `BoundSlotCount` | `int` | Distinct slots the journal currently binds. |
| `BootFromAsset()` | `bool` | Resolve + boot from the module source chain. |
| `BootWithBytes(bytes, moduleName)` | `bool` | Boot a module you loaded yourself (the CLI/sandbox path). |
| `RebootWithBytes(bytes)` | `bool` | Hot-reload: same instance id, journal replayed so bindings survive. |
| `Bind(slot, obj)` | `bool` | Bind a Unity object to a handle slot (journaled). |
| `SetBoundValue(slot, value)` | `bool` | Push a value into a value slot (journaled, tag-strict). |
| `TryGetVariable(name, out v)` | `bool` | Read consts → runtime vars → live temps. |
| `TrySetVariable(name, v, out error)` | `bool` | Write a runtime var (read-only consts refuse). |
| `TickInternal()` | `StateChangeInfo` | One frame step. Returns `null` when nothing changed. |

### 5.2 `AiExecution`

`Module`, `ModuleName`, `InstanceId`, `InstanceName`, `Vars`
(`VariableTable`), `Evaluator`, `States` (`StateHandler`), `StartServer`,
`UpdateServer`, `Traversal`, `Dispatcher`, `Time`, `Log`, counters
(`TickCount`, `CallCount`), suspension state (`IsSuspended`, `ResumeAtTime`,
`HasWaitCondition`), and the pending transition (`HasTransitionRequest`,
`RequestedState`, `TransitionReason`). `RequestTransition(stateIndex, reason)` is how host code forces a state change from outside.

### 5.3 `MainServer` (singleton MonoBehaviour)

Registry and services: `Instance`, `Db`, `OrderedBroadcast`,
`PriorityBroadcast`, `Queries`, `TimeProvider`, `EnsureExists()`,
`AllocInstanceId()`, `RegisterAsset()`, `Register()`, `Unregister()`,
`TryGetAI(instanceId, out ai)`, `ConnectExternal(name)`, and `Execute(request)`
— the single entry point the query server uses.

### 5.4 Values, handles, compilation

| Type | Use |
|---|---|
| `FsmValue` | The runtime value: `Kind`, `TypeTag`, `I`/`F`/`D`/`B`/`S`, `X`/`Y`/`Z`/`W`, `HandleId`, and the `Make*` factories (`MakeInt`, `MakeFloat`, `MakeBool`, `MakeVec3`, `MakeHandle`, `NullHandle`, `Void`). |
| `HandleTable` | `Alloc(obj, tag)`, `Resolve(id)`, `TagOf(id)`, `Clear()`, `Count`. Id 0 is the null handle. |
| `FsmCompiler` | `IsAvailable`, `NativeVersion`, `CompileSource(src, name)`, `CompileFile(fsm, fsmb)` → `FsmCompileResult { Ok, Module, Diagnostics, Error }`. |
| `ClassGenerator` | `SanitizeIdentifier`, `GenerateSource(module, name, class, resourcePath)` (legacy) and `GenerateSource(module, bytes, name, class, resourcePath)` (self-contained). |
| `FsmBurstCompiler` | MonoBehaviour: `Entries`, `DefaultOutputFolder`, `DefaultScriptFolder`, `InputFolder`, `SweepFolder()`, `CompileEntry(e)`, `CompileAll()`, plus `ResolveProjectPath` / `ToAssetPath` / `ResourcePathOf` / `FindStaleGeneratedClass`. |

---

## 6. Binding model

A module's `var` declarations become **slots**, numbered in declaration order.
`Slot_*` constants in the generated class name them.

1. **Auto-bind base layer** (every boot, never journaled): each handle slot
   whose type tag appears exactly once is bound to the host GameObject —
   `Object2D/3D → gameObject`, `Transform2D/3D → transform`,
   `Camera → GetComponent<Camera>()`, `Sprite → SpriteRenderer`,
   `AnimationController → Animator`, `PhysicsObject → Rigidbody(2D)` else
   `Collider(2D)`, `NavMeshAgent → GetComponent<NavMeshAgent>()`.
2. **Inspector slot list** — index = slot. Handles only, one line of setup per slot.
3. **`OnBindingsRequired()`** — code bindings, then `OnBindingsManual()` from
   your hand-written partial file, which survives regeneration.

Everything in 2 and 3 is **journaled**: `RebootWithBytes` replays the journal,
so a hot-reloaded brain keeps its bindings and its instance id.

Ambiguous tags (two or more slots of the same type) are deliberately left
unbound with a warning — bind them explicitly, or the first call that touches
them logs `null handle` and yields a default.

---

## 7. Appendix

### 7.1 Category ranges

| Range | Category | Overloads |
|---|---|---|
| `0x0000`–`0x0018` | Math | 25 |
| `0x0100`–`0x0115` | Object | 22 |
| `0x0200`–`0x020B` | Sprite | 12 |
| `0x0300`–`0x0311` | Animation | 18 |
| `0x0400`–`0x0411` | Physics | 18 |
| `0x0500`–`0x0509` | Camera | 10 |
| `0x0600`–`0x062C` | Navigation | 45 |
| `0x0700`–`0x070D` | Perception | 14 |
| `0x0800`–`0x0809` | Steering | 10 |
| `0x0900`–`0x0901` | Sensing | 2 |
| `0x0A00`–`0x0A02` | Control | 3 |
| | **total** | **179** |

### 7.2 2D / 3D pairing

Almost every engine function has a 2D and a 3D overload. The rule:

- pick by the **first argument's type** — `Object2D`/`Vector2` selects the 2D one;
- 2D movement ignores `z` and rotates around `Z` with **+X as forward**;
- 3D uses `transform.forward` and full 3-axis rotation.

### 7.3 Failure policy

Built-ins never throw. A null handle, a missing component or an unbound slot
logs one `Error` line and returns a default:

| Failure | Returns |
|---|---|
| Unresolvable object/transform | `Vector3.zero` / `Vector2.zero` / `0f` / `false`, or a **null handle** |
| No `Rigidbody` for `getMass` | `0` |
| No `Collider` for `isColliding` | probe radius `0.3`, centred on the transform |
| Empty direction argument to a raycast | `false` / null handle (logged) |
| Unknown path id | `0f` / a zero vector (logged) |
| Unknown function id at runtime | `Void` + `unknown <Category> function id XXXX` |

This keeps a mis-bound AI observably broken (log spam + defaults) instead of
crashing the frame.
