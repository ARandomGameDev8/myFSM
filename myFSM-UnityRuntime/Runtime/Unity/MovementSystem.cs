// myFSM Unity Runtime — incremental movement system.
//
// Navigation / lookAt calls never teleport: they post a goal for the agent, and
// Advance() (once per tick, before the AI's Update round) takes one step towards
// it. The step is handed to Unity — this file contains no collision maths at
// all. It picks the component that should receive the movement and calls the
// Unity function meant for that component:
//
//   NavMeshAgent         SetDestination() — the mesh pathfinds and steers.
//   CharacterController  Move() — the controller's own capsule sweep resolves
//                        slopes, steps and walls.
//   Rigidbody(2D)        dynamic   -> velocity is set; the physics solver
//                        resolves every contact, and drag/mass keep working.
//                        GRAVITY KEEPS ITS AXIS: on a body with gravity on
//                        (useGravity / gravityScale != 0) the goal drives the
//                        HORIZONTAL plane only and velocity.y is left to the
//                        solver, so a dropped body falls at Unity's gravity
//                        (9.81 m/s^2 by default) and lands — it is never
//                        lifted to the goal's height, and ARRIVAL is measured
//                        in that same plane (a grounded chaser settles around
//                        its target instead of pressing into its centre). With
//                        gravity off nothing else drives the vertical axis, so
//                        the goal drives all three (flying/hovering agents).
//                        kinematic -> MovePosition(), Unity's kinematic move
//                        API (docs: "Moves the kinematic Rigidbody towards
//                        position"). Unity also documents that collisions do
//                        not affect a kinematic body — "If the rigidbody is
//                        kinematic then any collisions won't affect the
//                        rigidbody itself" — so nothing is layered on top to
//                        hide that. Ask for a dynamic body if walls must stop
//                        it.
//   Collider, no body    No Unity function can move it: a collider on its own
//                        is static geometry. Movement is written to the
//                        transform and a warning names the missing component.
//   nothing at all       Same: the step goes to the transform.
//
// Direct position changes are deliberately untouched: setPosition() and
// friends still write the transform (or the body's position) and teleport, the
// same way transform.position and Rigidbody.position do in Unity. Only the
// goal-driven tier-3 movements are routed through the moves above.
//
// Movement is translation-only; facing changes only through lookAt goals.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using MyFSM.Core;

namespace MyFSM.Unity
{
    /// <summary>
    /// Which Unity function moves the agent this tick, picked from the
    /// components the agent's GameObject (or one of its parents) carries:
    /// 3D: NavMeshAgent &gt; CharacterController &gt; Rigidbody &gt; collider-only &gt; none;
    /// 2D: Rigidbody2D &gt; collider-only &gt; none.
    /// </summary>
    public enum MotionDriver
    {
        /// <summary>No components: the step is written to the transform.</summary>
        Transform = 0,
        /// <summary>
        /// A collider with no Rigidbody/CharacterController. Unity has no
        /// collision-resolving move for it, so the step goes to the transform
        /// and a warning names the missing component.
        /// </summary>
        ColliderNoBody = 1,
        /// <summary>CharacterController.Move().</summary>
        CharacterController = 2,
        /// <summary>Rigidbody: velocity when dynamic, MovePosition when kinematic.</summary>
        Rigidbody = 3,
        /// <summary>Rigidbody2D: velocity when dynamic, MovePosition when kinematic.</summary>
        Rigidbody2D = 4,
        /// <summary>NavMeshAgent.SetDestination().</summary>
        NavMeshAgent = 5,
    }

    /// <summary>
    /// The components movement resolved for one agent. <see cref="Agent"/> is
    /// the transform the goal was posted for (positions are measured there);
    /// <see cref="Owner"/> is the transform the chosen component actually moves
    /// — the same one unless the component lives on a parent.
    /// </summary>
    public struct MotionContext
    {
        public MotionDriver Driver;
        public bool Is2D;
        public Transform Agent;
        public Transform Owner;
        public NavMeshAgent Nav;
        public CharacterController Controller;
        public Rigidbody Body;
        public Rigidbody2D Body2D;
        public Collider Shape;
        public Collider2D Shape2D;

        /// <summary>True when the body is kinematic (moved with MovePosition).</summary>
        public bool Kinematic;
    }

    public enum MoveMode
    {
        None,
        Point,        // fixed destination (goTo, sprintTowards, moveTowards)
        FollowObject, // live re-target (follow, followTarget)
        PathCorners,  // corner queue (findShortestPathAndMove)
        LookAt        // gradual rotation (lookAt)
    }

    public sealed class MoveGoal
    {
        public MoveMode Mode = MoveMode.None;
        public FsmValue Agent;
        public FsmValue Target; // FollowObject / LookAt target, else null handle
        public Vector3 Destination;
        public float Speed = FunctionDispatcher.DefaultMoveSpeed;
        public float StopDistance = FunctionDispatcher.DefaultStopDistance;
        public List<Vector3> Corners;
        public int CornerIndex;
        public bool Is2D;

        // Cached while the goal advances, so stopMovement/Clear() can stop a
        // body that was already set in motion (a velocity-driven rigidbody
        // would otherwise keep coasting after its goal is gone).
        public Transform AgentTransform;
    }

    public sealed class StoredPath
    {
        public readonly List<Vector3> Corners = new List<Vector3>();
        public int NextIndex;
    }

    /// <summary>
    /// Path ids for findPath/getNextWaypoint/getPathLength. Id 0 is invalid.
    /// </summary>
    public sealed class PathTable
    {
        private readonly List<StoredPath> _paths = new List<StoredPath>();

        public int Alloc(List<Vector3> corners)
        {
            StoredPath p = new StoredPath();
            p.Corners.AddRange(corners);
            _paths.Add(p);
            return _paths.Count;
        }

        public bool TryGet(int id, out StoredPath path)
        {
            if (id <= 0 || id > _paths.Count)
            {
                path = null;
                return false;
            }
            path = _paths[id - 1];
            return true;
        }

        public void Clear()
        {
            _paths.Clear();
        }
    }

    public sealed class MovementSystem
    {
        private readonly Dictionary<int, MoveGoal> _goals = new Dictionary<int, MoveGoal>();

        // Diagnostics: one line per agent when its driver changes, plus a
        // one-off warning when a collider has no body to move it, so "why did
        // it walk through that wall?" is answerable from the console.
        private readonly Dictionary<int, string> _reportedDriver = new Dictionary<int, string>();
        private readonly HashSet<int> _warnedNoBody = new HashSet<int>();

        public void SetGoal(int agentHandleId, MoveGoal goal)
        {
            _goals[agentHandleId] = goal;
        }

        /// <summary>
        /// Drops the goal and stops whatever was driving it: a velocity-driven
        /// body gets its planar velocity zeroed, a NavMeshAgent is stopped.
        /// (A kinematic body stops because MovePosition simply stops being
        /// called, and a transform move stops the same way.)
        /// </summary>
        public void ClearGoal(int agentHandleId)
        {
            MoveGoal goal;
            if (_goals.TryGetValue(agentHandleId, out goal))
            {
                if (goal.AgentTransform != null)
                    StopMotion(Resolve(goal.AgentTransform, goal.Is2D));
                _goals.Remove(agentHandleId);
            }
        }

        public bool HasGoal(int agentHandleId)
        {
            return _goals.ContainsKey(agentHandleId);
        }

        public bool TryGetGoal(int agentHandleId, out MoveGoal goal)
        {
            return _goals.TryGetValue(agentHandleId, out goal);
        }

        public void Clear()
        {
            foreach (MoveGoal goal in _goals.Values)
            {
                if (goal.AgentTransform != null)
                    StopMotion(Resolve(goal.AgentTransform, goal.Is2D));
            }
            _goals.Clear();
            _reportedDriver.Clear();
            _warnedNoBody.Clear();
        }

        /// <summary>
        /// Advances every goal one step. Runs even while an AI is suspended by
        /// wait()/waitUntil(): in-flight motion is engine-level, so conditions
        /// like waitUntil(hasReachedDestination(...)) can still become true.
        /// </summary>
        public void Advance(float dt, FunctionDispatcher d, AiExecution exec)
        {
            if (_goals.Count == 0 || dt <= 0f) return;
            int[] keys = new int[_goals.Count];
            _goals.Keys.CopyTo(keys, 0);
            for (int i = 0; i < keys.Length; i++)
            {
                MoveGoal goal;
                if (!_goals.TryGetValue(keys[i], out goal)) continue;
                if (goal.Mode == MoveMode.LookAt)
                    AdvanceLook(keys[i], goal, dt, d, exec);
                else
                    AdvanceMove(keys[i], goal, dt, d, exec);
            }
        }

        // ----------------------------------------------------------
        // The environment check
        //
        // Which Unity component gets the movement. Nothing here detects or
        // resolves collisions — that is the component's job once it is called.
        // ----------------------------------------------------------

        /// <summary>
        /// When false the environment is ignored: steps are written straight to
        /// the transform and no component is consulted (the escape hatch for
        /// objects whose movement something else owns). NavMeshAgent pathing
        /// is unaffected — a mesh agent steers itself either way.
        /// </summary>
        public bool CollisionAware = true;

        /// <summary>Downward acceleration applied to an airborne CharacterController.</summary>
        public float CharacterGravity = 20f;

        /// <summary>
        /// Resolves which component will move <paramref name="t"/>, from the
        /// components on its GameObject or the nearest parent carrying them.
        /// </summary>
        public MotionContext Resolve(Transform t, bool is2D)
        {
            return ResolveCore(t, is2D, false);
        }

        private MotionContext ResolveCore(Transform t, bool is2D, bool skipNav)
        {
            MotionContext c = new MotionContext();
            c.Driver = MotionDriver.Transform;
            c.Agent = t;
            c.Owner = t;
            c.Is2D = is2D;
            if (t == null) return c;

            if (!is2D && !skipNav)
            {
                NavMeshAgent nav = t.GetComponentInParent<NavMeshAgent>();
                if (nav != null && nav.enabled && nav.isOnNavMesh)
                {
                    c.Driver = MotionDriver.NavMeshAgent;
                    c.Nav = nav;
                    c.Owner = nav.transform;
                    return c;
                }
            }
            if (!CollisionAware) return c;

            if (is2D)
            {
                Rigidbody2D body2d = t.GetComponentInParent<Rigidbody2D>();
                if (body2d != null)
                {
                    c.Driver = MotionDriver.Rigidbody2D;
                    c.Body2D = body2d;
                    c.Kinematic = body2d.isKinematic;
                    c.Owner = body2d.transform;
                    return c;
                }
                Collider2D shape2d = t.GetComponent<Collider2D>();
                if (shape2d != null && shape2d.enabled)
                {
                    c.Driver = MotionDriver.ColliderNoBody;
                    c.Shape2D = shape2d;
                }
                return c;
            }

            // CharacterController derives from Collider, so it must be tested
            // before the bare-collider case.
            CharacterController cc = t.GetComponentInParent<CharacterController>();
            if (cc != null && cc.enabled)
            {
                c.Driver = MotionDriver.CharacterController;
                c.Controller = cc;
                c.Owner = cc.transform;
                return c;
            }
            Rigidbody body = t.GetComponentInParent<Rigidbody>();
            if (body != null)
            {
                c.Driver = MotionDriver.Rigidbody;
                c.Body = body;
                c.Kinematic = body.isKinematic;
                c.Owner = body.transform;
                return c;
            }
            // A collider on this object, but nothing to move it. A collider on
            // a PARENT is not this object's body (in Unity it belongs to the
            // parent's own rigidbody), so only the object itself is asked.
            Collider shape = t.GetComponent<Collider>();
            if (shape != null && shape.enabled)
            {
                c.Driver = MotionDriver.ColliderNoBody;
                c.Shape = shape;
            }
            return c;
        }

        /// <summary>Human-readable driver name, for logs and tooling.</summary>
        public static string DriverName(MotionDriver driver)
        {
            switch (driver)
            {
                case MotionDriver.NavMeshAgent: return "NavMeshAgent";
                case MotionDriver.CharacterController: return "CharacterController";
                case MotionDriver.Rigidbody: return "Rigidbody";
                case MotionDriver.Rigidbody2D: return "Rigidbody2D";
                case MotionDriver.ColliderNoBody: return "collider without a body";
                default: return "transform";
            }
        }

        /// <summary>The driver plus the Unity call it stands for.</summary>
        private static string DriverLabel(MotionContext ctx)
        {
            switch (ctx.Driver)
            {
                case MotionDriver.NavMeshAgent: return "NavMeshAgent.SetDestination";
                case MotionDriver.CharacterController: return "CharacterController.Move";
                case MotionDriver.Rigidbody:
                    return ctx.Kinematic
                        ? "Rigidbody.MovePosition (kinematic: Unity does not stop it at colliders)"
                        : "Rigidbody.velocity (physics resolves collisions)";
                case MotionDriver.Rigidbody2D:
                    return ctx.Kinematic
                        ? "Rigidbody2D.MovePosition (kinematic: Unity does not stop it at colliders)"
                        : "Rigidbody2D.velocity (physics resolves collisions)";
                case MotionDriver.ColliderNoBody:
                    return "transform (collider without a body: nothing can stop it)";
                default:
                    return "transform";
            }
        }

        /// <summary>
        /// Logs the driver when it changes for an agent, so which Unity call is
        /// being used is visible instead of guessed.
        /// </summary>
        private void Report(int handleId, MotionContext ctx, string who, AiExecution exec)
        {
            if (exec == null || exec.Log == null) return;
            string now = DriverLabel(ctx);
            string before;
            if (_reportedDriver.TryGetValue(handleId, out before) && before == now) return;
            _reportedDriver[handleId] = now;
            exec.Log.Info("movement: " + who + " -> " + now);
        }

        /// <summary>
        /// One-off warning for the case Unity cannot help with: a collider with
        /// no Rigidbody/CharacterController is static geometry, so nothing
        /// stops it. Says exactly what to add.
        /// </summary>
        private void WarnNoBody(int handleId, string who, AiExecution exec)
        {
            if (exec == null || exec.Log == null) return;
            if (!_warnedNoBody.Add(handleId)) return;
            exec.Log.Warn("movement: " + who + " has a Collider but no Rigidbody / " +
                          "CharacterController. Unity cannot move a body-less object " +
                          "against collisions (a bare Collider is static geometry), so " +
                          "movement is applied to the transform and nothing will stop " +
                          "it. Add a dynamic Rigidbody (gravity off) or a " +
                          "CharacterController to make walls matter.");
        }

        /// <summary>
        /// Asks a dynamic body's solver for this tick's motion. Unity's physics
        /// resolves every contact from here on, so the agent is stopped by
        /// walls (and slowed by drag/gravity) exactly like any other body.
        /// </summary>
        private static void DriveVelocity(MotionContext ctx, Vector3 dir, float dist, float dt,
                                          float speed)
        {
            if (ctx.Body != null)
            {
                if (ctx.Body.useGravity)
                {
                    // Gravity owns the vertical axis. The goal drives the target's
                    // XZ only and velocity.y is left exactly as the solver left it,
                    // so the body falls at Unity's gravity and lands instead of
                    // being lifted to (or held at) the goal's height — the same
                    // split a NavMeshAgent uses walking the ground.
                    Vector3 planar = new Vector3(dir.x, 0f, dir.z);
                    float planarMag = planar.magnitude;   // sin(angle from vertical)
                    if (planarMag < 1e-4f)
                    {
                        // Directly above/below the goal: gravity does all of it.
                        ctx.Body.velocity = new Vector3(0f, ctx.Body.velocity.y, 0f);
                        return;
                    }
                    float planarWant = Mathf.Min(speed, dist * planarMag / Mathf.Max(dt, 1e-6f));
                    Vector3 planarDir = planar / planarMag;
                    ctx.Body.velocity = new Vector3(planarDir.x * planarWant,
                                                    ctx.Body.velocity.y,
                                                    planarDir.z * planarWant);
                    return;
                }
                // Gravity off: nothing else drives the vertical axis, so the goal
                // drives all three (a flying or hovering agent).
                ctx.Body.velocity = dir * Mathf.Min(speed, dist / Mathf.Max(dt, 1e-6f));
                return;
            }
            // 2D twin of the same rule: gravityScale 0 is the usual top-down case
            // (the goal drives both axes); with gravity on, Unity keeps the
            // vertical axis and the goal drives X.
            if (ctx.Body2D.gravityScale != 0f)
            {
                float magX = Mathf.Abs(dir.x);
                if (magX < 1e-4f)
                {
                    ctx.Body2D.velocity = new Vector2(0f, ctx.Body2D.velocity.y);
                    return;
                }
                float planarWant2 = Mathf.Min(speed, dist * magX / Mathf.Max(dt, 1e-6f));
                ctx.Body2D.velocity = new Vector2(dir.x / magX * planarWant2,
                                                  ctx.Body2D.velocity.y);
                return;
            }
            float want = Mathf.Min(speed, dist / Mathf.Max(dt, 1e-6f));
            ctx.Body2D.velocity = new Vector2(dir.x * want, dir.y * want);
        }

        /// <summary>Stops a body the goal was driving, keeping pose and gravity.</summary>
        private static void StopMotion(MotionContext ctx)
        {
            if (ctx.Driver == MotionDriver.Rigidbody && ctx.Body != null && !ctx.Body.isKinematic)
            {
                Vector3 v = ctx.Body.velocity;
                ctx.Body.velocity = new Vector3(0f, v.y, 0f);
                return;
            }
            if (ctx.Driver == MotionDriver.Rigidbody2D && ctx.Body2D != null &&
                !ctx.Body2D.isKinematic)
            {
                ctx.Body2D.velocity = Vector2.zero;
                return;
            }
            if (ctx.Driver == MotionDriver.NavMeshAgent && ctx.Nav != null)
                ctx.Nav.isStopped = true;
        }

        /// <summary>
        /// True when the goal must be treated as a horizontal-plane problem: a
        /// dynamic body whose gravity is on cannot be lifted or held by the goal,
        /// so the vertical component of the step is not the goal's to measure.
        /// </summary>
        private static bool GravityOwnsVertical(MotionContext ctx, MoveGoal goal)
        {
            if (goal.Is2D)
                return ctx.Driver == MotionDriver.Rigidbody2D && ctx.Body2D != null &&
                       !ctx.Kinematic && ctx.Body2D.gravityScale != 0f;
            return ctx.Driver == MotionDriver.Rigidbody && ctx.Body != null &&
                   !ctx.Kinematic && ctx.Body.useGravity;
        }

        private static bool CloseEnough(Vector3 a, Vector3 b, float within, bool is2D)
        {
            if (is2D)
            {
                float dx = a.x - b.x;
                float dy = a.y - b.y;
                return dx * dx + dy * dy <= within * within;
            }
            return (a - b).sqrMagnitude <= within * within;
        }

        private void AdvanceMove(int handleId, MoveGoal goal, float dt,
                                 FunctionDispatcher d, AiExecution exec)
        {
            Transform t = d.ResolveTransform(goal.Agent, exec, "movement");
            if (t == null)
            {
                _goals.Remove(handleId);
                return;
            }
            goal.AgentTransform = t;

            Vector3 dest = goal.Destination;
            if (goal.Mode == MoveMode.FollowObject)
            {
                Transform tt = d.ResolveTransform(goal.Target, exec, "movement");
                if (tt == null)
                {
                    _goals.Remove(handleId);
                    return;
                }
                dest = tt.position;
                goal.Destination = dest;
            }
            else if (goal.Mode == MoveMode.PathCorners)
            {
                if (goal.Corners == null || goal.Corners.Count == 0)
                {
                    _goals.Remove(handleId);
                    return;
                }
                while (goal.CornerIndex < goal.Corners.Count &&
                       CloseEnough(t.position, goal.Corners[goal.CornerIndex],
                                   goal.StopDistance, goal.Is2D))
                {
                    goal.CornerIndex++;
                }
                if (goal.CornerIndex >= goal.Corners.Count)
                {
                    StopMotion(Resolve(t, goal.Is2D));
                    _goals.Remove(handleId);
                    return;
                }
                dest = goal.Corners[goal.CornerIndex];
            }

            MotionContext ctx = Resolve(t, goal.Is2D);

            // NavMesh: the engine pathfinds and steers.
            if (ctx.Driver == MotionDriver.NavMeshAgent)
            {
                NavMeshAgent agent = ctx.Nav;
                agent.isStopped = false;
                agent.speed = goal.Speed;
                agent.stoppingDistance = goal.StopDistance;
                agent.SetDestination(dest);
                if (!agent.pathPending &&
                    agent.pathStatus == NavMeshPathStatus.PathInvalid)
                {
                    // No route on the mesh: hand the step to whatever component
                    // the object does have instead of pretending it can walk.
                    ctx = ResolveCore(t, goal.Is2D, true);
                }
                else if (!agent.pathPending && agent.remainingDistance <= goal.StopDistance)
                {
                    StopMotion(ctx);
                    if (goal.Mode != MoveMode.FollowObject) _goals.Remove(handleId);
                    return;
                }
                else
                {
                    Report(handleId, ctx, t.name, exec);
                    return; // the mesh is steering; nothing more this tick
                }
            }

            Vector3 pos = t.position;
            Vector3 ownerPos = ctx.Owner != null ? ctx.Owner.position : pos;
            Vector3 to = dest - pos;
            if (goal.Is2D) to.z = 0f;
            // Gravity-driven dynamic body: the goal cannot own the vertical axis
            // (gravity does — see DriveVelocity), so the step is measured in the
            // horizontal plane. Arrival then means "under the goal": a grounded
            // chaser settles around its target instead of driving into the
            // target's centre forever, and the FSM's hasReachedDestination turns
            // true when it has actually arrived in the plane it can move in.
            if (GravityOwnsVertical(ctx, goal)) to.y = 0f;
            float dist = to.magnitude;
            if (dist <= goal.StopDistance)
            {
                StopMotion(ctx);
                if (goal.Mode != MoveMode.FollowObject) _goals.Remove(handleId);
                return;
            }

            float step = goal.Speed * dt;
            Vector3 delta = step >= dist ? to : to / dist * step;
            if (goal.Is2D) delta.z = 0f;
            if (delta.sqrMagnitude <= 0f) return;   // zero speed: goal stays posted

            switch (ctx.Driver)
            {
                case MotionDriver.CharacterController:
                {
                    // Unity's controller move: it does its own capsule sweep,
                    // slope and step handling, and stops at walls. Only gravity
                    // is ours to add.
                    Vector3 motion = delta;
                    if (!ctx.Controller.isGrounded) motion.y -= CharacterGravity * dt;
                    ctx.Controller.Move(motion);
                    Report(handleId, ctx, t.name, exec);
                    return;
                }
                case MotionDriver.Rigidbody:
                case MotionDriver.Rigidbody2D:
                {
                    if (ctx.Kinematic)
                    {
                        // Unity's kinematic move. Its docs are explicit that
                        // collisions do not affect a kinematic body, so this is
                        // passed through as-is; nothing is layered on top.
                        Vector3 next = ownerPos + delta;
                        if (ctx.Driver == MotionDriver.Rigidbody)
                            ctx.Body.MovePosition(next);
                        else
                            ctx.Body2D.MovePosition(new Vector2(next.x, next.y));
                        Report(handleId, ctx, t.name, exec);
                        return;
                    }
                    DriveVelocity(ctx, to / dist, dist, dt, goal.Speed);
                    Report(handleId, ctx, t.name, exec);
                    return;
                }
                default:
                {
                    // No body: Unity has nothing to move here, so the step goes
                    // to the transform (and the first time, the warning above).
                    if (ctx.Driver == MotionDriver.ColliderNoBody)
                        WarnNoBody(handleId, t.name, exec);
                    ctx.Owner.position = ownerPos + delta;
                    Report(handleId, ctx, t.name, exec);
                    return;
                }
            }
        }

        private void AdvanceLook(int handleId, MoveGoal goal, float dt,
                                 FunctionDispatcher d, AiExecution exec)
        {
            Transform t = d.ResolveTransform(goal.Agent, exec, "lookAt");
            Transform tt = d.ResolveTransform(goal.Target, exec, "lookAt");
            if (t == null || tt == null)
            {
                _goals.Remove(handleId);
                return;
            }
            goal.AgentTransform = t;
            MotionContext ctx = Resolve(t, goal.Is2D);

            Vector3 dir = tt.position - t.position;
            if (goal.Is2D)
            {
                if (dir.x == 0f && dir.y == 0f)
                {
                    _goals.Remove(handleId);
                    return;
                }
                float target = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
                float next = Mathf.MoveTowardsAngle(t.eulerAngles.z, target,
                    FunctionDispatcher.DefaultLookSpeed * dt);
                ApplyRotation(ctx, Quaternion.Euler(0f, 0f, next));
                if (Mathf.Abs(Mathf.DeltaAngle(next, target)) < 0.5f)
                    _goals.Remove(handleId);
                return;
            }
            if (dir.sqrMagnitude < 1e-8f)
            {
                _goals.Remove(handleId);
                return;
            }
            Quaternion want = Quaternion.LookRotation(dir);
            Quaternion turned = Quaternion.RotateTowards(t.rotation, want,
                FunctionDispatcher.DefaultLookSpeed * dt);
            ApplyRotation(ctx, turned);
            if (Quaternion.Angle(turned, want) < 0.5f)
                _goals.Remove(handleId);
        }

        /// <summary>
        /// Applies a new facing through the body when there is one (MoveRotation
        /// is Unity's rotation counterpart to MovePosition), and straight to the
        /// transform otherwise.
        /// </summary>
        private static void ApplyRotation(MotionContext ctx, Quaternion rotation)
        {
            Transform agent = ctx.Agent;
            // Only the object being faced can carry the body: if the body is on
            // a parent, rotating it would not be the agent's facing.
            if (agent != null && ctx.Owner == agent)
            {
                if (ctx.Driver == MotionDriver.Rigidbody && ctx.Body != null)
                {
                    ctx.Body.MoveRotation(rotation);
                    return;
                }
                if (ctx.Driver == MotionDriver.Rigidbody2D && ctx.Body2D != null)
                {
                    ctx.Body2D.MoveRotation(rotation.eulerAngles.z);
                    return;
                }
            }
            if (agent != null) agent.rotation = rotation;
        }
    }
}
