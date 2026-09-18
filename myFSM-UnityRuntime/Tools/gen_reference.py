#!/usr/bin/env python3
"""Regenerates Docs/Reference.md from the runtime's own built-in table.

Usage:  python3 myFSM-UnityRuntime/Tools/gen_reference.py

Reads Runtime/Core/FunctionCatalog.cs (the table the runtime dispatches on) and
emits the function reference: every overload with its ID, signature, tier and
behaviour note, plus the override points and host API. The behaviour notes are
curated from the Unity implementations in Runtime/Unity/Functions/ and live in
NOTES below -- when you add a function to the catalog, add its note here too or
the generator will tell you it is missing.
"""
import collections, sys, os
# One-line behaviour notes per built-in function name (92 names), derived from
# the Unity implementations in Runtime/Unity/Functions/.
NOTES = {
# --- Math (0x0000-0x0018) ---
"sin":"Mathf.Sin. Radians in, float out.","cos":"Mathf.Cos. Radians.",
"tan":"Mathf.Tan. Radians.","asin":"Mathf.Asin. Radians out.","acos":"Mathf.Acos. Radians out.",
"atan":"Mathf.Atan. Radians out.","atan2":"Mathf.Atan2(y, x) — **y first**, as in Unity/Math.",
"sqrt":"Mathf.Sqrt. Negative input yields NaN (no clamp).","pow":"Mathf.Pow(b, exp).",
"abs":"Mathf.Abs.","sign":"-1, 0 or 1.","clamp":"clamp(x, lo, hi); lo > hi is not corrected.",
"lerp":"lerp(a, b, t) with **t unclamped** — t outside [0,1] extrapolates.",
"min":"Smaller of two floats.","max":"Larger of two floats.",
"floor":"Rounds toward -infinity (float in / float out).","ceil":"Rounds toward +infinity.",
"round":"Rounds to nearest (banker's rounding via Math.Round).",
"normalize":"Vector3.normalized. A zero vector stays zero (no NaN, no error).",
"dot":"Vector3.Dot.","cross":"Vector3.Cross (right-handed, as Unity).",
"distance":"Vector3.Distance between two points.",
"magnitude":"Length of a vector.","random":"UnityEngine.Random.value — [0, 1).",
"randomRange":"UnityEngine.Random.Range(lo, hi) — [lo, hi).",
# --- Object (0x0100-0x0115) ---
"getPosition":"transform.position. The 2D overload drops z (Vector2).",
"getRotation":"transform.rotation as a Quaternion (3D only).",
"getScale":"transform.localScale; 2D overload returns (x, y).",
"setPosition":"Writes transform.position (Tier 2). Use it sparingly — it teleports.",
"setRotation":"Writes transform.rotation (Tier 2).",
"setScale":"Writes transform.localScale (Tier 2).",
"distanceTo":"Distance between two objects; 2D compares only x/y.",
"directionTo":"**Normalized** (b − a). Combine with a distance/speed when you need a point.",
"isActive":"GameObject.activeSelf.",
"setActive":"GameObject.SetActive (Tier 2).",
"getTag":"FNV-1a 32-bit hash of the tag string as an int — compare it with another getTag result, not with a literal.",
"getLayer":"GameObject.layer index.",
# --- Sprite (0x0200-0x020B) ---
"getColor":"SpriteRenderer colour as (r, g, b) — **alpha is dropped**; the result is a Vector3, not a colour type.","setColor":"Writes r/g/b from a Vector3 and leaves **alpha untouched** (Tier 2) — the mirror of getColor.",
"isVisible":"Whether the renderer is enabled/visible.",
"setVisible":"Toggles renderer visibility (Tier 2).",
"getBounds":"World-space **size** of the sprite bounds (Vector3).",
"getSize":"Sprite size in world units: rect.size / pixelsPerUnit (Vector2). Logs an error when no sprite is assigned.",
# --- Animation (0x0300-0x0311) ---
"play":"Enables the Animator and restores its channel speed (resumes from wherever it is).",
"stop":"Animator.Rebind + disable — rewinds to the default pose.",
"pause":"Remembers the current speed and sets it to 0.","resume":"Restores the speed saved by pause.",
"isPlaying":"True when the animator is enabled **and** speed > 0 (so a paused animator reads false).",
"getCurrentClip":"fullPathHash of the animator's layer-0 state id (an int, stable per clip).",
"setSpeed":"Sets the animator's playback speed (and remembers it on the channel).",
"getProgress":"Normalized time of layer 0, wrapped into [0, 1).",
"setAnimation":"Animator.Play(stateId, 0, 0) — jumps straight to a state by its integer id (Tier 2).",
# --- Physics (0x0400-0x0411) ---
"getVelocity":"Rigidbody(2D).velocity; zero when there is no rigidbody.",
"setVelocity":"Assigns the rigidbody velocity (Tier 2) — instant, frame-rate independent motion.",
"getMass":"Rigidbody(2D).mass; **0** when there is no rigidbody (not 1).",
"applyForce":"AddForce — accumulates, meant for the physics step.",
"applyImpulse":"AddForce(..., ForceMode.Impulse) — an instant kick.",
"isGrounded":"Downward raycast from the collider centre, length = extents.y + 0.2 (0.35 with no collider).",
"isColliding":"OverlapSphere around the body (radius = bounds extents magnitude, min 0.1; 0.3 with no collider) — true if anything else overlaps.",
"getCollisionNormal":"Normal pointing away from the nearest overlapping collider's closest point; falls back to up.",
"raycast":"Returns only a bool. Use `getRaycastHit` (Sensing) when you need the object that was hit.",
# --- Camera (0x0500-0x0509) ---
"isInView":"WorldToViewportPoint: in front of the camera (z > 0) **and** inside [0,1]².",
"screenToWorld":"Screen pixels → a world point at a **fixed depth** (nearClipPlane + 1), not a caller-supplied one.",
"worldToScreen":"Camera.WorldToScreenPoint — a world point → screen pixels (z carries depth).",
"getViewport":"Camera pixelWidth/pixelHeight as a Vector2.",
# --- Navigation (0x0600-0x062C) ---
"findPath":"Stores a corner list and returns its **path id** (int). NavMesh corners when available, straight line otherwise. Ids are per-AI; 0 is invalid.",
"getNextWaypoint":"Pops the next corner of a path id. Past the end it returns the last corner forever (no error).",
"getPathLength":"Total length of the stored polyline; 0 for an unknown path (logs an error).",
"hasReachedDestination":"Within the stopping distance of a point/object: the posted goal's stop distance if there is one, else the NavMeshAgent's, else 0.2. On a dynamic body with gravity on, the distance is measured in the horizontal plane (see §2) — a grounded agent is \"there\" when it is under the target.",
"goTo":"Posts a Point goal at the agent's navigation speed. The destination is read **once, now**: an object argument is snapshotted, not chased — use `follow` to track something that moves.",
"follow":"Posts a FollowObject goal: re-reads the target's position every tick, so it chases a moving object forever. Stops at **half** the agent's stopping distance — the tighter of the two follow calls.",
"findShortestPathAndMove":"Posts a corner queue (PathCorners) and walks it.",
"followTarget":"Same as `follow` but stops at the **full** stopping distance, so it keeps the agent's normal stand-off instead of closing in.",
"sprintTowards":"Point goal at **base speed × multiplier** (multiplier is the 3rd argument; negative clamps to 0).",
"moveTowards":"Point goal at an **absolute** speed (units/second). Pass a destination POINT, not a direction. On a dynamic body with gravity ON, only the horizontal plane is driven — the vertical axis belongs to the solver (see §2).",
"stopMovement":"Clears the goal (and stops a NavMeshAgent).",
# --- Perception (0x0700-0x070D) ---
"lookAt":"Posts a LookAt goal: gradual rotation toward the target. The goal is **retired within 0.5°**, so re-post it every tick to track a moving target. 2D uses +X as forward and rotates around Z.",
"isInLineOfSight":"Raycast a → b: true when nothing blocks, or when the first thing hit **is** b.",
"isInRange":"Squared-distance test against the radius (cheaper than a raycast).",
"getAngleTo":"Degrees between the object's forward (+X in 2D) and the direction to the target.",
"getDistanceTo":"Distance between two objects (the perception overload; same maths as Object.distanceTo).",
"getNearestOfTag":"OverlapSphere, keeps objects whose tag HASH matches, returns the nearest as a handle (null handle when none).",
"getAllInRadius":"Returns an **int count** of colliders in the radius — it cannot be iterated. Use getNearestOfTag for an object.",
# --- Steering (0x0800-0x0809) ---
"getFleeDirection":"Normalized (from − threat) — the direction to run.",
"getPursuitPosition":"Target position + its rigidbody velocity (a one-second lead) when the `speed` argument is positive **and** the target is actually moving; otherwise just the target's position. Aim at the returned point to intercept.",
"getSeparationVector":"Normalized sum of away-vectors to the N nearest neighbours inside the default separation radius (N is the 2nd argument, ≤ 0 returns zero).",
"getArrivalVector":"Direction to the target scaled by min(dist / slowRadius, 1) — feed it to setVelocity or moveTowards for a smooth stop.",
"getWanderVector":"Random planar **offset** of the given radius; it ignores its `pos` argument, so add it to a position.",
# --- Sensing (0x0900-0x0901) ---
"getRaycastHit":"Raycast that returns the hit object as a handle (null handle when nothing is hit) — the object-returning half of `raycast`.",
# --- Control (0x0A00-0x0A02) ---
"wait":"Suspends Update + Traversals for N seconds. Movement keeps advancing, so in-flight motion continues.",
"waitUntil":"Suspends Update + Traversals until the condition is true (re-evaluated every tick).",
"emit":"Records an event id into the main database, where queries and the broadcast servers can see it.",
}


def parse_catalog(path):
    """Reads the built-in table straight out of FunctionCatalog.cs."""
    src = open(path, encoding="utf-8").read()
    rows, pos = [], 0
    while True:
        at = src.find("O(0x", pos)
        if at < 0:
            break
        depth, j = 0, at + 1
        while j < len(src):
            if src[j] == "(":
                depth += 1
            elif src[j] == ")":
                depth -= 1
                if depth == 0:
                    break
            j += 1
        body, pos = src[at + 2:j], j
        parts, depth, cur = [], 0, ""
        for ch in body:
            if ch == '"':
                cur += ch
                continue
            if ch == "(":
                depth += 1
            if ch == ")":
                depth -= 1
            if ch == "," and depth == 0:
                parts.append(cur.strip())
                cur = ""
            else:
                cur += ch
        parts.append(cur.strip())
        unq = lambda s: s.strip().strip('"')
        rows.append({
            "id": parts[0], "name": unq(parts[1]), "category": unq(parts[2]),
            "tier": int(parts[3]), "ret": unq(parts[4]),
            "varTarget": parts[5].strip().lower() == "true",
            "params": [unq(p) for p in parts[6:] if p.strip()],
        })
    return rows

import os
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # myFSM-UnityRuntime/
CATALOG = os.path.join(ROOT, "Runtime", "Core", "FunctionCatalog.cs")
OUT = os.path.join(ROOT, "Docs", "Reference.md")
rows = parse_catalog(CATALOG)
by = collections.OrderedDict()
for r in rows:
    by.setdefault(r["category"], []).append(r)

def sig(r):
    ps = ", ".join(r["params"]) if r["params"] else ""
    return "%s(%s) -> %s" % (r["name"], ps, r["ret"])

def table(cat):
    out = []
    out.append("| ID | Signature | Tier | Notes |")
    out.append("|---|---|---|---|")
    seen = set()
    for r in by[cat]:
        note = ""
        if r["name"] not in seen:
            seen.add(r["name"])
            note = NOTES.get(r["name"], "")
        out.append("| `%s` | `%s` | %d | %s |" % (r["id"], sig(r), r["tier"], note))
    return "\n".join(out)

CAT_TITLE = {
 "Math": ("Math", "Pure computation over `Mathf` / `Vector3`. No engine state is read or written."),
 "Object": ("Object", "The base object model: position, rotation, scale, activeness, tag, layer. This is where the 2D/3D overload pairs start."),
 "Sprite": ("Sprite", "Sprite + renderer state: colour, visibility, bounds, sprite size."),
 "Animation": ("Animation", "Animator channel: play/stop/pause/resume, speed, progress, and jumping to a state by id. Each handle keeps its own speed memory, so `pause`/`resume` round-trip."),
 "Physics": ("Physics", "Rigidbody state and collision queries. `applyForce` accumulates for the physics step; `applyImpulse` is an instant kick; `raycast` answers only yes/no — use `getRaycastHit` for the object."),
 "Camera": ("Camera", "Camera-space helpers: view tests and screen/world/viewport conversion."),
 "Navigation": ("Navigation", "Path queries and goal-posting movement — the largest category, and the only one that spans all three tiers. Posting a goal never teleports: the movement system advances it a step per tick, and HOW that step is applied is decided by the components on the agent (NavMeshAgent, CharacterController, Rigidbody(2D), a bare Collider, or nothing — see §2 and the movement section of the README)."),
 "Perception": ("Perception", "What the AI can sense about other objects: facing, line of sight, range, angle, distance, and tag/radius scans."),
 "Steering": ("Steering", "Pure vector maths for classic steering behaviours: flee, pursuit, separation, arrival, wander. These **return** a vector; they do not move anything — feed the result to `moveTowards`/`setVelocity`."),
 "Sensing": ("Sensing", "Scene queries that return an object rather than a number."),
 "Control": ("Control", "Flow control inside the DSL: suspension (`wait`/`waitUntil`) and event emission."),
}

doc = []
A = doc.append

A("# myFSM Unity Runtime — Function & Override Reference")
A("")
A("Every built-in function the FSM language can call, every override point the")
A("runtime offers, and the host API around them. Generated from")
A("`Runtime/Core/FunctionCatalog.cs` (the same table the runtime dispatches on),")
A("so the IDs, tiers and signatures here cannot drift from the implementation.")
A("")
A("Counts are asserted by `Sandbox/check.py`: **179 overloads, 92 distinct")
A("functions, 11 categories**. Each catalog ID maps to exactly one `case` in the")
A("Unity dispatchers, and every ID sits in exactly one category range.")
A("")
A("---")
A("")
A("## 1. How to read the tables")
A("")
A("| Column | Meaning |")
A("|---|---|")
A("| **ID** | The stable ABI. The compiled module stores this number, the runtime dispatches on it. IDs are never renumbered or reused. |")
A("| **Signature** | DSL types, not C#: `Object3D`, `Vector3`, `float`… `-> void` means it is a statement, anything else is an expression you can assign. |")
A("| **Tier** | Where the call is allowed (see §2). |")
A("| **Notes** | What the Unity implementation actually does, including the defaults it falls back to. |")
A("")
A("Tier 3 rows drive an object over time, so their first argument must be a")
A("runtime `var`/`temp` (see §2). Everything else is call-site free.")
A("")
A("Overloads of one name are grouped so the note is written once, on the first")
A("row of that name: the difference between them is normally 2D vs 3D")
A("(`Object3D`/`Vector3` vs `Object2D`/`Vector2`), a point vs an object")
A("argument, or a fixed vs a caller-supplied speed. `getPosition` is the one")
A("name that appears in two categories (Object and Camera), so it carries a")
A("note in each.")
A("")
A("## 2. Tiers — where a call may appear")
A("")
A("| Tier | Kind | Can appear in | Constraint |")
A("|---|---|---|---|")
A("| **1** | Query — reads engine state | expressions, conditions, initializers, call arguments | none |")
A("| **2** | Mutate — writes engine state | statements only | the mutated object must be a runtime `var`/`temp`, not a `const` |")
A("| **3** | Controller-driven — navigation, steering, control that plays out over time | statements only | the driven object must be a runtime `var`/`temp` (this is the `varTarget` flag in the catalog) |")
A("")
A("37 of the 39 Tier 3 overloads require a variable target (the catalog's")
A("`varTarget` flag). The two that do not are `wait` and `waitUntil`: they are")
A("Tier 3 because they suspend the AI over time, but they drive no object, so any")
A("expression is allowed there.")
A("")
A("Tier 3 calls never do the work themselves: they post a **goal** onto the AI's")
A("`MovementSystem`, which advances it once per tick, before the state's `Update`")
A("round. That is why `waitUntil(hasReachedDestination(agent, point))` can come")
A("true, and why movement keeps advancing while the AI is suspended.")
A("")
A("The step is then handed to Unity. The component the agent carries decides")
A("which Unity function moves it — this is the environment check, and there is no")
A("collision maths in the runtime at all:")
A("")
A("| On the agent | Unity call | Stopped by walls? |")
A("|---|---|---|")
A("| `NavMeshAgent` (enabled, on a mesh) | `SetDestination` | yes, by the NavMesh |")
A("| `CharacterController` | `Move` | yes — the controller's own capsule sweep |")
A("| `Rigidbody`/`Rigidbody2D`, dynamic | `velocity` is set (or `AddForce`) | yes — the physics solver |")
A("| `Rigidbody`/`Rigidbody2D`, kinematic | `MovePosition` | **no** — Unity: \"collisions won't affect the rigidbody itself\" |")
A("| `Collider`/`Collider2D` with no body | none exists | **no** — a bare collider is static geometry; a warning names the missing component |")
A("| nothing at all | none | no — the step goes to the transform |")
A("")
A("Dynamic bodies split the axes: **gravity keeps the vertical, the goal keeps")
A("the horizontal.** A body with gravity on (`useGravity`, or a non-zero")
A("`gravityScale` in 2D) has its XZ velocity driven towards the goal at the")
A("requested speed while its vertical velocity is left exactly as the solver")
A("left it — a body dropped from the air falls at Unity's gravity (9.81 m/s²")
A("by default), lands, and is never lifted to the goal's height. That is the")
A("same split a `NavMeshAgent` uses walking the ground, and arrival is measured")
A("in that same plane: a gravity-driven body has arrived when it is under the")
A("goal horizontally, so a grounded chaser settles around its target instead of")
A("pressing into its centre. With gravity off nothing else owns the vertical")
A("axis, so the goal drives all three — what a flying or hovering agent wants.")
A("")
A("Direct position changes are untouched: `setPosition`, `setRotation` and")
A("`setScale` still write the transform and teleport, exactly like")
A("`transform.position` does in Unity. Only goal-driven movement is routed")
A("through the calls above.")
A("")
A("Each agent logs the call it uses when that changes")
A("(`movement: Marcher -> Rigidbody.velocity (physics resolves collisions)`), and")
A("a collider with no body logs one warning naming the component to add.")
A("`Movement.CollisionAware = false` skips the environment and writes to the")
A("transform (the escape hatch for objects whose movement something else owns);")
A("a NavMeshAgent steers itself either way.")
A("")
A("---")
A("")
A("## 3. Functions by category")
A("")

for cat, (title, blurb) in CAT_TITLE.items():
    rs = by[cat]
    ids = [int(r["id"], 16) for r in rs]
    names = len(set(r["name"] for r in rs))
    A("### %s — %d overloads, %d function%s (`0x%04X`–`0x%04X`)" % (
        title, len(rs), names, "" if names == 1 else "s", min(ids), max(ids)))
    A("")
    A(blurb)
    A("")
    A(table(cat))
    A("")

A("---")
A("")
A("## 4. Overrides — the C# side you extend")
A("")
A("### 4.1 `AIInstance` virtuals")
A("")
A("`AIInstance` is the MonoBehaviour every AI derives from. All of these are")
A("optional: the defaults are exactly what a plain AI needs.")
A("")
A("| Member | Kind | Default | When it runs / is used | Override to |")
A("|---|---|---|---|---|")
A("| `ModuleResourcePath` | `string` | `null` | Read during boot, after the embedded bytes | Legacy only: point at a `TextAsset` under a `Resources/` folder. Self-contained classes leave this null. |")
A("| `GeneratedModuleName` | `string` | `null` | Read during boot when no inspector name override is set | Rename the module in logs/DB/query responses. Generated classes return the module stem. |")
A("| `EmbeddedModule` | `byte[]` | `null` | Read during boot, before the Resources path | Return compiled module bytes so the component needs no asset. Generated classes return their embedded blob; hand-written ones can load from anywhere. |")
A("| `AssignedModuleAsset` | `TextAsset` | `null` | Read first during boot; wins over everything | Expose an inspector module slot. Only `FsmbAIInstance` does — that is why generated classes show no module field. |")
A("| `Start()` | `protected virtual void` | boots from asset, disables the component on failure | Unity, once, first frame | Pre-seed state before boot. Call `base.Start()` or boot yourself, or the AI never runs. |")
A("| `Update()` | `protected virtual void` | `TickInternal()` | Unity, every frame | Rarely. Overriding **without** calling `base.Update()` silently stops the AI (Unity only calls the most-derived `Update`). Prefer `Paused` or `enabled = false`. |")
A("| `OnDestroy()` | `protected virtual void` | unregisters from the main server | Unity, on destroy | Extra teardown. Call `base.OnDestroy()` to unregister. |")
A("| `OnBindingsRequired()` | `protected virtual void` | empty | During boot, **after** the inspector list, **before** the entry state's `Start{}` | Bind slots in code. Generated classes override it with per-slot comments and a call to `OnBindingsManual()`. |")
A("| `AutoBindHandles` | `bool` | `true` | Read by the auto-bind base layer of every boot | Kill switch: return `false` to bind every handle slot by hand. |")
A("")
A("### 4.2 Lifecycle, precisely")
A("")
A("```")
A("Unity Start()")
A("  └─ BootFromAsset()                        fail => BootError + enabled = false")
A("       module source order: AssignedModuleAsset -> EmbeddedModule -> Resources")
A("       └─ BootCore()")
A("            read + validate the module (fail fast, one error line)")
A("            MainServer.EnsureExists(); alloc instance id")
A("            build Handles / Movement / Paths / Dispatcher / AiExecution")
A("            auto-bind base layer (unique handle tags -> this GameObject)")
A("            inspector slot list, then OnBindingsRequired()   [fresh boot]")
A("            ...or journal replay                            [hot reload]")
A("            Execution.Boot()  (selects the entry head; enters nothing)")
A("            register asset + instance")
A("")
A("Unity Update()  (every frame, for every AI)")
A("  └─ TickInternal()")
A("       Paused        -> DB sample only, nothing advances")
A("       Movement.Advance(dt)      fresh positions for this tick's decisions")
A("       Execution.Tick()          suspension -> external -> Update{} -> Traversals{}")
A("       head changed  -> DB row + ordered/priority broadcast + tick sample")
A("```")
A("")
A("The entry state's `Start{}` runs on the **first tick**, not in Unity's")
A("`Start()` — which is why bindings are applied before boot.")
A("")
A("### 4.3 Generated-class overrides")
A("")
A("A generated class is a `sealed partial class XxxAI : AIInstance` with:")
A("")
A("| Emitted | Purpose |")
A("|---|---|")
A("| `GeneratedModuleName` | The module stem, for logs/DB. |")
A("| `EmbeddedModule` (+ `ModuleData`) | The module bytes, base64, at the bottom of the file. This is what makes the class self-contained. |")
A("| `State_*` consts | State names as strings — never hardcode them. |")
A("| `Slot_*` consts | Binding slot indices, with a comment naming the DSL type and whether it auto-binds. |")
A("| `OnBindingsRequired()` | Emits `base.OnBindingsRequired()`, per-slot comments, then calls `OnBindingsManual()`. |")
A("| `partial void OnBindingsManual();` | Declaration only. **Never rewritten** by the compiler. |")
A("")
A("A generated class deliberately has **no** module asset field and emits no")
A("`AssetResourcePath`/`ModuleResourcePath`: it carries its own module. The one")
A("exception is the legacy `GenerateSource` overload that receives no bytes —")
A("that shape still emits the Resources path, because it has nothing else.")
A("")
A("### 4.4 Interfaces you can implement")
A("")
A("| Interface | Implement to | Notes |")
A("|---|---|---|")
A("| `ITimeProvider` | Feed your own clock | `Time` + `DeltaTime`. Default `UnityTimeProvider` uses `UnityEngine.Time`. |")
A("| `IExecutionLog` | Route runtime logging | `Info` / `Warn` / `Error`. Default logs through `Debug` with a `[myFSM name@object]` prefix. |")
A("| `IFunctionDispatcher` | Serve calls yourself | `Dispatch(id, args, exec)`. Default resolves handles against the scene; the sandbox harness substitutes a stub. |")
A("| `IQueryBackend` | Answer external queries/commands | Implemented by `MainServer`; the query server is the only consumer. |")
A("")
A("---")
A("")
A("## 5. Host API")
A("")
A("### 5.1 `AIInstance` — public members")
A("")
A("| Member | Type | Meaning |")
A("|---|---|---|")
A("| `Execution` | `AiExecution` | The booted module + variable tables + state handler. `null` before boot. |")
A("| `Handles` | `HandleTable` | Handle-id ↔ Unity object map for this AI. |")
A("| `Movement` | `MovementSystem` | Posted goals, and the environment check: `Movement.Resolve(t, is2D)` reports the `MotionDriver` chosen for an object (NavMeshAgent / CharacterController / Rigidbody(2D) / collider-without-a-body / transform), without running anything. `Movement.CollisionAware` lives here too. |")
A("| `Paths` | `PathTable` | Path ids handed out by `findPath`. |")
A("| `Dispatcher` | `FunctionDispatcher` | Resolves handles/components for built-in calls. |")
A("| `InstanceId` / `ModuleName` / `Booted` / `BootError` | — | Registry identity and boot outcome. |")
A("| `Paused` | `bool` | Soft pause: DB keeps sampling, nothing ticks. |")
A("| `CurrentStateName` / `DisplayName` | `string` | `\"<none>\"` before the first tick; `\"Module@GameObject\"`. |")
A("| `BoundSlotCount` | `int` | Distinct slots the journal currently binds. |")
A("| `BootFromAsset()` | `bool` | Resolve + boot from the module source chain. |")
A("| `BootWithBytes(bytes, moduleName)` | `bool` | Boot a module you loaded yourself (the CLI/sandbox path). |")
A("| `RebootWithBytes(bytes)` | `bool` | Hot-reload: same instance id, journal replayed so bindings survive. |")
A("| `Bind(slot, obj)` | `bool` | Bind a Unity object to a handle slot (journaled). |")
A("| `SetBoundValue(slot, value)` | `bool` | Push a value into a value slot (journaled, tag-strict). |")
A("| `TryGetVariable(name, out v)` | `bool` | Read consts → runtime vars → live temps. |")
A("| `TrySetVariable(name, v, out error)` | `bool` | Write a runtime var (read-only consts refuse). |")
A("| `TickInternal()` | `StateChangeInfo` | One frame step. Returns `null` when nothing changed. |")
A("")
A("### 5.2 `AiExecution`")
A("")
A("`Module`, `ModuleName`, `InstanceId`, `InstanceName`, `Vars`")
A("(`VariableTable`), `Evaluator`, `States` (`StateHandler`), `StartServer`,")
A("`UpdateServer`, `Traversal`, `Dispatcher`, `Time`, `Log`, counters")
A("(`TickCount`, `CallCount`), suspension state (`IsSuspended`, `ResumeAtTime`,")
A("`HasWaitCondition`), and the pending transition (`HasTransitionRequest`,")
A("`RequestedState`, `TransitionReason`). `RequestTransition(stateIndex, reason)` " + 
  "is how host code forces a state change from outside.")
A("")
A("### 5.3 `MainServer` (singleton MonoBehaviour)")
A("")
A("Registry and services: `Instance`, `Db`, `OrderedBroadcast`,")
A("`PriorityBroadcast`, `Queries`, `TimeProvider`, `EnsureExists()`,")
A("`AllocInstanceId()`, `RegisterAsset()`, `Register()`, `Unregister()`,")
A("`TryGetAI(instanceId, out ai)`, `ConnectExternal(name)`, and `Execute(request)`")
A("— the single entry point the query server uses.")
A("")
A("### 5.4 Values, handles, compilation")
A("")
A("| Type | Use |")
A("|---|---|")
A("| `FsmValue` | The runtime value: `Kind`, `TypeTag`, `I`/`F`/`D`/`B`/`S`, `X`/`Y`/`Z`/`W`, `HandleId`, and the `Make*` factories (`MakeInt`, `MakeFloat`, `MakeBool`, `MakeVec3`, `MakeHandle`, `NullHandle`, `Void`). |")
A("| `HandleTable` | `Alloc(obj, tag)`, `Resolve(id)`, `TagOf(id)`, `Clear()`, `Count`. Id 0 is the null handle. |")
A("| `FsmCompiler` | `IsAvailable`, `NativeVersion`, `CompileSource(src, name)`, `CompileFile(fsm, fsmb)` → `FsmCompileResult { Ok, Module, Diagnostics, Error }`. |")
A("| `ClassGenerator` | `SanitizeIdentifier`, `GenerateSource(module, name, class, resourcePath)` (legacy) and `GenerateSource(module, bytes, name, class, resourcePath)` (self-contained). |")
A("| `FsmBurstCompiler` | MonoBehaviour: `Entries`, `DefaultOutputFolder`, `DefaultScriptFolder`, `InputFolder`, `SweepFolder()`, `CompileEntry(e)`, `CompileAll()`, plus `ResolveProjectPath` / `ToAssetPath` / `ResourcePathOf` / `FindStaleGeneratedClass`. |")
A("")
A("---")
A("")
A("## 6. Binding model")
A("")
A("A module's `var` declarations become **slots**, numbered in declaration order.")
A("`Slot_*` constants in the generated class name them.")
A("")
A("1. **Auto-bind base layer** (every boot, never journaled): each handle slot")
A("   whose type tag appears exactly once is bound to the host GameObject —")
A("   `Object2D/3D → gameObject`, `Transform2D/3D → transform`,")
A("   `Camera → GetComponent<Camera>()`, `Sprite → SpriteRenderer`,")
A("   `AnimationController → Animator`, `PhysicsObject → Rigidbody(2D)` else")
A("   `Collider(2D)`, `NavMeshAgent → GetComponent<NavMeshAgent>()`.")
A("2. **Inspector slot list** — index = slot. Handles only, one line of setup per slot.")
A("3. **`OnBindingsRequired()`** — code bindings, then `OnBindingsManual()` from")
A("   your hand-written partial file, which survives regeneration.")
A("")
A("Everything in 2 and 3 is **journaled**: `RebootWithBytes` replays the journal,")
A("so a hot-reloaded brain keeps its bindings and its instance id.")
A("")
A("Ambiguous tags (two or more slots of the same type) are deliberately left")
A("unbound with a warning — bind them explicitly, or the first call that touches")
A("them logs `null handle` and yields a default.")
A("")
A("---")
A("")
A("## 7. Appendix")
A("")
A("### 7.1 Category ranges")
A("")
A("| Range | Category | Overloads |")
A("|---|---|---|")
for cat, rs in by.items():
    ids = [int(r["id"], 16) for r in rs]
    A("| `0x%04X`–`0x%04X` | %s | %d |" % (min(ids), max(ids), cat, len(rs)))
A("| | **total** | **%d** |" % len(rows))
A("")
A("### 7.2 2D / 3D pairing")
A("")
A("Almost every engine function has a 2D and a 3D overload. The rule:")
A("")
A("- pick by the **first argument's type** — `Object2D`/`Vector2` selects the 2D one;")
A("- 2D movement ignores `z` and rotates around `Z` with **+X as forward**;")
A("- 3D uses `transform.forward` and full 3-axis rotation.")
A("")
A("### 7.3 Failure policy")
A("")
A("Built-ins never throw. A null handle, a missing component or an unbound slot")
A("logs one `Error` line and returns a default:")
A("")
A("| Failure | Returns |")
A("|---|---|")
A("| Unresolvable object/transform | `Vector3.zero` / `Vector2.zero` / `0f` / `false`, or a **null handle** |")
A("| No `Rigidbody` for `getMass` | `0` |")
A("| No `Collider` for `isColliding` | probe radius `0.3`, centred on the transform |")
A("| Empty direction argument to a raycast | `false` / null handle (logged) |")
A("| Unknown path id | `0f` / a zero vector (logged) |")
A("| Unknown function id at runtime | `Void` + `unknown <Category> function id XXXX` |")
A("")
A("This keeps a mis-bound AI observably broken (log spam + defaults) instead of")
A("crashing the frame.")

named = sorted(set(r["name"] for r in rows))
missing = [n for n in named if n not in NOTES]
if missing:
    sys.stderr.write("WARNING: no behaviour note for: %s\n"
                     "         (add one to NOTES in this script)\n" % ", ".join(missing))
stale = [n for n in NOTES if n not in named]
if stale:
    sys.stderr.write("WARNING: NOTES has entries with no catalog function: %s\n"
                     % ", ".join(sorted(stale)))

open(OUT, "w", encoding="utf-8").write("\n".join(doc) + "\n")
print("wrote Docs/Reference.md")
print("lines:", len("\n".join(doc).split("\n")))
print("tables:", len(CAT_TITLE), "categories,", len(rows), "overload rows")
