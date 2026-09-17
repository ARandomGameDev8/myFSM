// myFSM Unity Runtime — incremental movement system.
//
// Navigation / lookAt calls never teleport: they post a goal for the agent and
// Advance() (once per tick, before the AI's Update round) takes one step
// towards it. The step is handed to whatever the agent's GameObject actually
// has on it, so movement obeys the same collision world as the rest of the
// game instead of a private one:
//
//   NavMeshAgent         SetDestination — the mesh steers and pathfinds.
//   CharacterController  Move() — the controller resolves slopes, steps and
//                        walls itself (the engine's capsule sweep).
//   Rigidbody(2D),       dynamic  -> velocity: the solver resolves every
//                        collision, and mass/drag/gravity keep working (the
//                        3D vertical velocity is left alone). Movement is a
//                        request to the physics engine, not a teleport.
//                        kinematic-> Physics.SphereCast / Physics2D.CircleCast
//                        sweep, then MovePosition to the swept point
//                        (kinematic bodies are not collided by the solver).
//   Collider(2D) only    engine sphere/circle cast sweep + slide along the
//                        surface — Unity's cast does the detection, the code
//                        only projects the leftover onto the hit plane.
//   nothing at all       position += direction * speed * dt.
//
// So a wall between an agent and its target stops it — or bends it into a
// slide — in every one of those shapes, because the detection is the engine's.
// Movement is translation-only; facing changes only through lookAt goals.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using MyFSM.Core;

namespace MyFSM.Unity
{
    /// <summary>
    /// What actually moves the agent this tick, picked from the components the
    /// agent's GameObject (or one of its parents) carries:
    /// 3D: NavMeshAgent &gt; CharacterController &gt; Rigidbody &gt; Collider &gt; none;
    /// 2D: Rigidbody2D &gt; Collider2D &gt; none.
    /// </summary>
    public enum MotionDriver
    {
        /// <summary>
        /// No physics components: the step is swept with the default body
        /// radius and written to the transform. It still cannot walk through
        /// walls — it just has no collider to take its size from.
        /// </summary>
        Transform = 0,
        /// <summary>Collider but no body: engine sphere/circle cast sweep + slide.</summary>
        ColliderSweep = 1,
        /// <summary>CharacterController.Move — the controller resolves contacts.</summary>
        CharacterController = 2,
        /// <summary>Rigidbody: velocity when dynamic, swept MovePosition when kinematic.</summary>
        Rigidbody = 3,
        /// <summary>Rigidbody2D: velocity when dynamic, swept MovePosition when kinematic.</summary>
        Rigidbody2D = 4,
        /// <summary>NavMeshAgent.SetDestination — the engine pathfinds and steers.</summary>
        NavMeshAgent = 5,
    }

    /// <summary>
    /// The components movement resolved for one agent. <see cref="Agent"/> is
    /// the transform the goal was posted for (positions are measured there);
    /// <see cref="Owner"/> is the transform the chosen driver actually moves —
    /// the same one unless the component lives on a parent.
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

    /// <summary>
    /// One swept sphere against the engine's collision world. Unity's physics
    /// implements it (PhysicsMotionProbe) with SphereCast/CircleCast; the
    /// headless sandbox injects a fake so the stop/slide behaviour can be
    /// tested without an engine. The cast must not see the agent itself, which
    /// is what a cast starting inside a collider naturally does.
    /// </summary>
    public interface IMotionProbe
    {
        /// <summary>
        /// Sweeps a sphere of <paramref name="radius"/> from
        /// <paramref name="origin"/> along <paramref name="direction"/> (unit
        /// length) for at most <paramref name="maxDistance"/>. Returns true on
        /// a hit; <paramref name="distance"/> is then how far the CENTRE may
        /// travel before contact and <paramref name="normal"/> the surface
        /// normal there.
        /// </summary>
        bool Sphere(Vector3 origin, float radius, Vector3 direction, float maxDistance,
                    bool is2D, out float distance, out Vector3 normal);
    }

    /// <summary>The real thing: Physics.SphereCast / Physics2D.CircleCast.</summary>
    public sealed class PhysicsMotionProbe : IMotionProbe
    {
        public bool Sphere(Vector3 origin, float radius, Vector3 direction, float maxDistance,
                           bool is2D, out float distance, out Vector3 normal)
        {
            distance = 0f;
            normal = Vector3.zero;
            if (maxDistance < 0f) return false;
            if (is2D)
            {
                // CircleCast ignores colliders the circle already overlaps, so
                // an agent standing against a wall does not hit itself.
                RaycastHit2D h = Physics2D.CircleCast(
                    new Vector2(origin.x, origin.y), radius,
                    new Vector2(direction.x, direction.y), maxDistance);
                if (h.collider == null) return false;
                distance = h.distance;
                normal = new Vector3(h.normal.x, h.normal.y, 0f);
                return true;
            }
            RaycastHit hit;
            if (!Physics.SphereCast(origin, radius, direction, out hit, maxDistance,
                                    ~0, QueryTriggerInteraction.Ignore))
                return false;
            distance = hit.distance;
            normal = hit.normal;
            return true;
        }
    }

    public sealed class MovementSystem
    {
        private readonly Dictionary<int, MoveGoal> _goals = new Dictionary<int, MoveGoal>();

        public void SetGoal(int agentHandleId, MoveGoal goal)
        {
            _goals[agentHandleId] = goal;
        }

        /// <summary>
        /// Drops the goal and stops whatever was driving it: a velocity-driven
        /// body gets its planar velocity zeroed, a NavMeshAgent is stopped.
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
        // Which movement the environment gets
        //
        // The agent is never assumed to be a bare transform: the components it
        // (or a parent) carries decide how the step is applied. Nothing here
        // re-implements collision detection — every contact is found by the
        // engine (SphereCast/CircleCast, Move(), the physics solver) and the
        // code only decides where to ask and what to do with the answer.
        // ----------------------------------------------------------

        /// <summary>
        /// When false the environment is ignored and steps are written
        /// straight to the transform (the escape hatch for objects whose
        /// movement something else owns, e.g. an animation or a character
        /// controller script). NavMeshAgent pathing still applies.
        /// </summary>
        public bool CollisionAware = true;

        /// <summary>Cast source. Swap it to test movement without an engine.</summary>
        public IMotionProbe Probe = new PhysicsMotionProbe();

        /// <summary>Downward acceleration applied to an airborne CharacterController.</summary>
        public float CharacterGravity = 20f;

        /// <summary>Body radius used when the agent has no collider.</summary>
        public const float DefaultBodyRadius = 0.5f;

        /// <summary>Kept between the body and a surface, to stop it creeping in.</summary>
        public const float CollisionSkin = 0.02f;

        private const int MaxSlideIterations = 3;
        private const float MinStep = 1e-5f;

        /// <summary>
        /// Resolves what will move <paramref name="t"/>, from the components on
        /// its GameObject or the nearest parent carrying them.
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

            // A live NavMeshAgent is engine pathing, not collision handling, so
            // it keeps steering even with CollisionAware off (the toggle only
            // governs how a manual step is applied).
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
                    // A kinematic body is swept before MovePosition, so its
                    // collider is what decides when it has touched something.
                    c.Shape2D = t.GetComponentInParent<Collider2D>();
                    c.Owner = body2d.transform;
                    return c;
                }
                Collider2D shape2d = t.GetComponentInParent<Collider2D>();
                if (shape2d != null && shape2d.enabled)
                {
                    c.Driver = MotionDriver.ColliderSweep;
                    c.Shape2D = shape2d;
                    c.Owner = shape2d.transform;
                }
                return c;
            }

            // CharacterController derives from Collider, so it must be tested
            // before the plain-collider case.
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
                // Used when the body is kinematic (swept before MovePosition).
                c.Shape = t.GetComponentInParent<Collider>();
                c.Owner = body.transform;
                return c;
            }
            Collider shape = t.GetComponentInParent<Collider>();
            if (shape != null && shape.enabled)
            {
                c.Driver = MotionDriver.ColliderSweep;
                c.Shape = shape;
                c.Owner = shape.transform;
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
                case MotionDriver.ColliderSweep: return "collider sweep";
                default: return "transform";
            }
        }

        /// <summary>
        /// The agent's half-width, taken from its collider when it has one and
        /// falling back to <see cref="DefaultBodyRadius"/>.
        /// </summary>
        public static float BodyRadius(Transform t, bool is2D)
        {
            if (t != null)
            {
                Collider c = t.GetComponent<Collider>();
                if (c != null && c.enabled)
                {
                    // Horizontal half-extents only: a tall capsule is not a
                    // wide one, and using y would park it a body-height away
                    // from every wall.
                    Vector3 e = c.bounds.extents;
                    return Mathf.Max(e.x, e.z);
                }
                Collider2D c2 = t.GetComponent<Collider2D>();
                if (c2 != null && c2.enabled)
                {
                    Vector2 e2 = c2.bounds.extents;
                    return Mathf.Max(e2.x, e2.y);
                }
            }
            return DefaultBodyRadius;
        }

        private static float RadiusOf(MotionContext ctx)
        {
            if (ctx.Shape != null)
            {
                Vector3 e = ctx.Shape.bounds.extents;
                return Mathf.Max(e.x, e.z);
            }
            if (ctx.Shape2D != null)
            {
                Vector2 e = ctx.Shape2D.bounds.extents;
                return Mathf.Max(e.x, e.y);
            }
            return DefaultBodyRadius;
        }

        /// <summary>
        /// Centre of the sweep for this agent: the collider's bounds centre
        /// when it has one (its pivot need not be its middle), else the given
        /// position.
        /// </summary>
        private static Vector3 SweepCentre(MotionContext ctx, Vector3 fallback)
        {
            if (ctx.Shape != null) return ctx.Shape.bounds.center;
            if (ctx.Shape2D != null)
            {
                Vector3 c = ctx.Shape2D.bounds.center;
                return new Vector3(c.x, c.y, fallback.z);
            }
            return fallback;
        }

        /// <summary>
        /// Moves a sphere of <paramref name="radius"/> from
        /// <paramref name="origin"/> by <paramref name="delta"/>, stopping at
        /// obstacles and sliding along them. Returns the position the CENTRE
        /// reached and sets <paramref name="blocked"/> when anything got in
        /// the way. Every contact comes from the engine cast
        /// (<see cref="IMotionProbe.Sphere"/>); the only arithmetic here is
        /// projecting what is left of the step onto the hit plane.
        /// </summary>
        public static Vector3 SlideStep(Vector3 origin, Vector3 delta, float radius, bool is2D,
                                        IMotionProbe probe, out bool blocked)
        {
            blocked = false;
            Vector3 pos = origin;
            Vector3 remaining = is2D ? new Vector3(delta.x, delta.y, 0f) : delta;
            if (probe == null) return pos + remaining;

            for (int i = 0; i < MaxSlideIterations; i++)
            {
                float len = remaining.magnitude;
                if (len <= MinStep) break;
                Vector3 dir = remaining / len;

                float distance;
                Vector3 normal;
                if (!probe.Sphere(pos, radius, dir, len, is2D, out distance, out normal))
                {
                    pos += dir * len;
                    break;
                }

                blocked = true;
                float travel = distance - CollisionSkin;
                if (travel < 0f) travel = 0f;          // already touching: cannot advance
                if (travel > len) travel = len;
                pos += dir * travel;

                Vector3 leftover = dir * (len - travel);
                normal = normal.normalized;
                if (is2D) normal.z = 0f;
                // Overlapping at the start of the cast reports no usable
                // surface: stop here rather than creep into the obstacle.
                if (normal.sqrMagnitude <= 1e-8f) break;
                remaining = Vector3.ProjectOnPlane(leftover, normal);
                if (is2D) remaining.z = 0f;
            }
            return pos;
        }

        /// <summary>Where the agent's body would end up after a swept step.</summary>
        private Vector3 SweptOwnerPosition(MotionContext ctx, Vector3 ownerPos, Vector3 delta)
        {
            Vector3 origin = SweepCentre(ctx, ownerPos);
            bool blocked;
            Vector3 swept = SlideStep(origin, delta, RadiusOf(ctx), ctx.Is2D, Probe, out blocked);
            Vector3 moved = swept - origin;
            if (ctx.Is2D) moved.z = 0f;
            return ownerPos + moved;
        }

        /// <summary>
        /// Asks a dynamic body's solver for this tick's motion. Unity's physics
        /// resolves every contact from here on, so the agent is stopped by
        /// walls (and slowed by drag/gravity) exactly like any other body.
        /// </summary>
        private static void DriveVelocity(MotionContext ctx, Vector3 dir, float dist, float dt,
                                          float speed)
        {
            float want = Mathf.Min(speed, dist / Mathf.Max(dt, 1e-6f));
            if (ctx.Body != null)
            {
                Vector3 v = ctx.Body.velocity;
                ctx.Body.velocity = new Vector3(dir.x * want, v.y, dir.z * want);
                return;
            }
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

            // NavMesh fast path: a live agent steers itself.
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
                    // Unreachable on the mesh: use whatever component the
                    // object has instead of pretending it can walk there.
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
                    return; // engine is steering; nothing more this tick
                }
            }

            Vector3 pos = t.position;
            Vector3 ownerPos = ctx.Owner != null ? ctx.Owner.position : pos;
            Vector3 to = dest - pos;
            if (goal.Is2D) to.z = 0f;
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
                    // The controller does its own capsule sweep, slope and step
                    // handling; only gravity is ours to apply.
                    Vector3 motion = delta;
                    if (!ctx.Controller.isGrounded) motion.y -= CharacterGravity * dt;
                    ctx.Controller.Move(motion);
                    return;
                }
                case MotionDriver.Rigidbody:
                case MotionDriver.Rigidbody2D:
                {
                    bool kinematic = ctx.Driver == MotionDriver.Rigidbody
                        ? ctx.Body.isKinematic
                        : ctx.Body2D.isKinematic;
                    if (kinematic)
                    {
                        // Kinematic bodies are not collided by the solver, so
                        // sweep first and then hand the result to the engine.
                        Vector3 next = SweptOwnerPosition(ctx, ownerPos, delta);
                        if (ctx.Driver == MotionDriver.Rigidbody)
                            ctx.Body.MovePosition(next);
                        else
                            ctx.Body2D.MovePosition(new Vector2(next.x, next.y));
                        return;
                    }
                    DriveVelocity(ctx, to / dist, dist, dt, goal.Speed);
                    return;
                }
                case MotionDriver.ColliderSweep:
                {
                    ctx.Owner.position = SweptOwnerPosition(ctx, ownerPos, delta);
                    return;
                }
                default:
                {
                    // Nothing to drive the object, but "no rigidbody" still
                    // means collisions are respected: sweep with the collider's
                    // geometry when there is one, else the default body radius.
                    // Only CollisionAware=false writes a bare position.
                    if (CollisionAware)
                        ctx.Owner.position = SweptOwnerPosition(ctx, ownerPos, delta);
                    else
                        ctx.Owner.position = ownerPos + delta;
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
        /// Applies a new facing through the engine when the object has a body
        /// (so the physics step and interpolation stay in sync), and straight
        /// to the transform otherwise.
        /// </summary>
        private static void ApplyRotation(MotionContext ctx, Quaternion rotation)
        {
            Transform agent = ctx.Agent;
            // Only the object being faced can carry the body: if the rigidbody
            // is on a parent, rotating it would not be the agent's facing.
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
