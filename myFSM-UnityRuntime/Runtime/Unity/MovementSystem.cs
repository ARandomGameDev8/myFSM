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
//                        surface, then an overlap push-out.
//   nothing at all       the same sweep with the default body radius, then
//                        position += the result.
//
// How the step is applied is never guessed from a radius: an object that HAS a
// collider is moved with the engine's own overlap resolution — move a substep,
// ask Physics.ComputePenetration (3D) / Collider2D.Distance (2D) for the
// minimal translation that separates it from whatever it now overlaps, apply
// that, and slide the leftover along the contact. Unity computes all the
// geometry; no shape has to be approximated, which is exactly what the docs
// recommend for movement without a rigidbody ("first query for the colliders
// nearby using OverlapSphere and then adjust the character's position using the
// data returned by ComputePenetration"). Substep size is a fraction of the
// body's own smallest dimension, so a fast body cannot jump a thin wall.
//
// An object with NO collider has no shape for the engine to resolve, so that
// case (and only that case) sweeps a probe sphere instead: Physics.SphereCast /
// Physics2D.CircleCast, with the true surface normal taken from a ray (the docs
// warn a sphere cast's normal "does not always represent the surface normal...
// misleading if you're using it for sliding") and a ray fan standing in when the
// shape cast is blind — it "will not detect colliders for which the sphere
// overlaps the collider", and never sees a non-convex MeshCollider. The engine
// overlap query is the final net in every case: a tick never ends with the body
// inside something.
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
    /// Everything movement asks about the collision world. Unity's physics
    /// implements it (PhysicsMotionProbe); the headless sandbox injects a fake
    /// so stop/slide/push-out behaviour can be tested without an engine.
    /// Every member is a query the engine answers — movement never computes
    /// contact geometry itself, it only applies the answers.
    /// </summary>
    public interface IMotionProbe
    {
        /// <summary>
        /// The minimal translation that separates the owner's own collider
        /// from everything it currently overlaps, or false when it is clear.
        /// This is the shape-accurate path: Unity's ComputePenetration (3D) /
        /// Collider2D.Distance (2D) does the geometry, so no radius has to be
        /// assumed and colliders a shape cast cannot see still count.
        /// <paramref name="centre"/> and <paramref name="radius"/> are only
        /// used when the owner has no collider at all, as a sphere stand-in.
        /// </summary>
        bool ResolvePenetration(Transform owner, Vector3 centre, float radius, bool is2D,
                                out Vector3 push);

        /// <summary>
        /// Sweeps a sphere of <paramref name="radius"/> from
        /// <paramref name="origin"/> along <paramref name="direction"/> (unit
        /// length) for at most <paramref name="maxDistance"/>. Only used for
        /// objects with no collider. <paramref name="distance"/> is how far the
        /// CENTRE may travel before the volume contacts something (Unity's own
        /// meaning for a swept volume); <paramref name="normal"/> is the
        /// engine's normal, which for SphereCast may be the contact-to-centre
        /// direction rather than the surface normal.
        /// </summary>
        bool SphereCast(Vector3 origin, float radius, Vector3 direction, float maxDistance,
                        bool is2D, out float distance, out Vector3 normal);

        /// <summary>
        /// A plain ray, for the two things a shape cast cannot do: give the
        /// true surface normal (Unity's documented workaround for sliding) and
        /// see colliders the sphere already overlaps or non-convex meshes.
        /// <paramref name="point"/> is the surface point that was hit.
        /// </summary>
        bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, bool is2D,
                     out float distance, out Vector3 point, out Vector3 normal);

        /// <summary>Name of the collider behind the last positive answer.</summary>
        string LastHitName { get; }
    }

    /// <summary>The real thing: Physics and Physics2D.</summary>
    public sealed class PhysicsMotionProbe : IMotionProbe
    {
        /// <summary>How far outside the body to look when pushing it out.</summary>
        private const float OverlapPadding = 0.05f;

        public string LastHitName { get; private set; }

        public bool SphereCast(Vector3 origin, float radius, Vector3 direction, float maxDistance,
                               bool is2D, out float distance, out Vector3 normal)
        {
            distance = 0f;
            normal = Vector3.zero;
            LastHitName = null;
            if (maxDistance < 0f) return false;
            if (is2D)
            {
                // A circle cast ignores colliders it already overlaps, so the
                // agent standing against a wall does not hit itself.
                RaycastHit2D h = Physics2D.CircleCast(
                    new Vector2(origin.x, origin.y), radius,
                    new Vector2(direction.x, direction.y), maxDistance);
                if (h.collider == null) return false;
                distance = h.distance;
                normal = new Vector3(h.normal.x, h.normal.y, 0f);
                LastHitName = h.collider.name;
                return true;
            }
            RaycastHit hit;
            if (!Physics.SphereCast(origin, radius, direction, out hit, maxDistance,
                                    ~0, QueryTriggerInteraction.Ignore))
                return false;
            distance = hit.distance;
            normal = hit.normal;
            LastHitName = hit.collider != null ? hit.collider.name : null;
            return true;
        }

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, bool is2D,
                            out float distance, out Vector3 point, out Vector3 normal)
        {
            distance = 0f;
            point = Vector3.zero;
            normal = Vector3.zero;
            if (maxDistance < 0f) return false;
            if (is2D)
            {
                RaycastHit2D h = Physics2D.Raycast(new Vector2(origin.x, origin.y),
                                                   new Vector2(direction.x, direction.y),
                                                   maxDistance);
                if (h.collider == null) return false;
                distance = h.distance;
                point = new Vector3(h.point.x, h.point.y, origin.z);
                normal = new Vector3(h.normal.x, h.normal.y, 0f);
                LastHitName = h.collider.name;
                return true;
            }
            RaycastHit hit;
            if (!Physics.Raycast(origin, direction, out hit, maxDistance,
                                 ~0, QueryTriggerInteraction.Ignore))
                return false;
            distance = hit.distance;
            point = hit.point;
            normal = hit.normal;
            LastHitName = hit.collider != null ? hit.collider.name : null;
            return true;
        }

        public bool ResolvePenetration(Transform owner, Vector3 centre, float radius, bool is2D,
                                       out Vector3 push)
        {
            push = Vector3.zero;
            LastHitName = null;
            float best = 0f;

            if (is2D)
            {
                Collider2D self2 = owner != null ? owner.GetComponentInParent<Collider2D>() : null;
                Vector2 c = new Vector2(centre.x, centre.y);
                // Broad phase only: the query has to reach everything the body
                // could touch, ComputePenetration/Distance then decide exactly.
                float query = self2 != null
                    ? self2.bounds.extents.magnitude + OverlapPadding
                    : radius + OverlapPadding;
                Collider2D[] hits = Physics2D.OverlapCircleAll(c, query);
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider2D h = hits[i];
                    if (h == null || h == self2 || IsSelf(h.transform, owner)) continue;
                    Vector2 want;
                    if (self2 != null)
                    {
                        // Unity's own 2D separation: distance is negative while
                        // the two overlap. The direction is taken from the two
                        // bounds centres so the normal's sign convention never
                        // matters; the DEPTH is Unity's exact value.
                        ColliderDistance2D gap = self2.Distance(h);
                        if (!gap.isValid || gap.distance >= 0f) continue;
                        Vector2 away = (Vector2)self2.bounds.center - (Vector2)h.bounds.center;
                        if (away.sqrMagnitude < 1e-8f) away = Vector2.up;
                        want = away.normalized * (-gap.distance + CollisionSkin);
                    }
                    else
                    {
                        // No collider to resolve: treat the agent as a sphere.
                        Vector2 p = h.ClosestPoint(c);
                        Vector2 away = c - p;
                        float d = away.magnitude;
                        if (d >= radius - 1e-4f) continue;
                        if (d <= 1e-4f)
                        {
                            // Buried: leave through the side we came from.
                            Vector2 out2 = c - (Vector2)h.bounds.center;
                            if (out2.sqrMagnitude < 1e-8f) out2 = Vector2.up;
                            want = out2.normalized * (radius + CollisionSkin);
                        }
                        else want = away / d * (radius - d + CollisionSkin);
                    }
                    if (want.sqrMagnitude > best)
                    {
                        best = want.sqrMagnitude;
                        push = new Vector3(want.x, want.y, 0f);
                        LastHitName = h.name;
                    }
                }
                return best > 0f;
            }

            Collider self = owner != null ? owner.GetComponentInParent<Collider>() : null;
            float query = self != null
                ? self.bounds.extents.magnitude + OverlapPadding
                : radius + OverlapPadding;
            Collider[] solids = Physics.OverlapSphere(centre, query);
            for (int i = 0; i < solids.Length; i++)
            {
                Collider h = solids[i];
                if (h == null || h == self || IsSelf(h.transform, owner)) continue;
                Vector3 want;
                if (self != null)
                {
                    // Unity's own minimal translation vector for these two
                    // colliders at their current poses.
                    Vector3 dir;
                    float dist;
                    if (!Physics.ComputePenetration(self, self.transform.position,
                                                    self.transform.rotation,
                                                    h, h.transform.position, h.transform.rotation,
                                                    out dir, out dist)) continue;
                    want = dir * (dist + CollisionSkin);
                }
                else
                {
                    Vector3 p = h.ClosestPoint(centre);
                    Vector3 away = centre - p;
                    float d = away.magnitude;
                    if (d >= radius - 1e-4f) continue;
                    if (d <= 1e-4f)
                    {
                        // Buried: leave through the side we came from.
                        Vector3 out3 = centre - h.bounds.center;
                        if (out3.sqrMagnitude < 1e-8f) out3 = Vector3.up;
                        want = out3.normalized * (radius + CollisionSkin);
                    }
                    else want = away / d * (radius - d + CollisionSkin);
                }
                if (want.sqrMagnitude > best)
                {
                    best = want.sqrMagnitude;
                    push = want;
                    LastHitName = h.name;
                }
            }
            return best > 0f;
        }

        /// <summary>True when <paramref name="t"/> is the agent or part of its hierarchy.</summary>
        private static bool IsSelf(Transform t, Transform ignore)
        {
            if (t == null || ignore == null) return false;
            for (Transform p = t; p != null; p = p.parent)
            {
                if (p == ignore) return true;
            }
            for (Transform p = ignore; p != null; p = p.parent)
            {
                if (p == t) return true;
            }
            return false;
        }
    }

    public sealed class MovementSystem
    {
        private readonly Dictionary<int, MoveGoal> _goals = new Dictionary<int, MoveGoal>();

        // Diagnostics: one line per agent when its driver or blocked state
        // changes, so "why did it walk through that wall?" is answerable from
        // the console instead of guesswork.
        private readonly Dictionary<int, string> _reportedDriver = new Dictionary<int, string>();
        private readonly HashSet<int> _blocked = new HashSet<int>();

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
                _blocked.Remove(agentHandleId);
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
            _blocked.Clear();
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
        // engine (SphereCast/CircleCast, Raycast, ClosestPoint, Move(), the
        // physics solver) and the code only decides where to ask and what to do
        // with the answer.
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
        private const int MaxOverlapIterations = 3;
        private const int MaxSubSteps = 12;
        private const float MinStep = 1e-5f;
        private const float MinSubStep = 0.02f;

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
        /// the way. Detection and normals come from the engine (the probe);
        /// the only arithmetic here is projecting what is left of the step
        /// onto the surface.
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

                float travel;
                Vector3 normal;
                if (!FindContact(probe, pos, radius, dir, len, is2D, out travel, out normal))
                {
                    pos += dir * len;
                    break;
                }

                blocked = true;
                pos += dir * travel;
                Vector3 leftover = dir * (len - travel);
                if (normal.sqrMagnitude <= 1e-8f) break;
                remaining = Vector3.ProjectOnPlane(leftover, normal);
                if (is2D) remaining.z = 0f;
            }
            return pos;
        }

        /// <summary>
        /// How far the sphere's centre may travel along <paramref name="dir"/>
        /// before it touches something, and the normal to slide on. The engine
        /// shape cast answers first; when it is blind — a non-convex
        /// MeshCollider, or anything the sphere already overlaps — rays answer
        /// instead, and the centre travel is derived from the surface plane.
        /// </summary>
        private static bool FindContact(IMotionProbe probe, Vector3 pos, float radius,
                                        Vector3 dir, float len, bool is2D,
                                        out float travel, out Vector3 normal)
        {
            travel = len;
            normal = Vector3.zero;

            float distance;
            Vector3 castNormal;
            if (probe.SphereCast(pos, radius, dir, len, is2D, out distance, out castNormal))
            {
                travel = distance - CollisionSkin;
                if (travel < 0f) travel = 0f;
                if (travel > len) travel = len;

                // Unity's docs: a sphere cast's normal "does not always
                // represent the surface normal ... misleading if you're using
                // it for sliding ... consider using a Physics.Raycast". So ask
                // a ray for the real one and keep the cast's as a fallback.
                normal = castNormal;
                float rd;
                Vector3 rp, rn;
                if (probe.Raycast(pos, dir, distance + radius + CollisionSkin * 4f, is2D,
                                  out rd, out rp, out rn) && rn.sqrMagnitude > 1e-8f)
                    normal = rn;
                return true;
            }

            // Shape cast blind. Rays from the centre and from a ring at the
            // body radius cover the surface the sphere would have touched.
            bool hit = false;
            float best = len;
            Vector3 bestNormal = Vector3.zero;
            Vector3 perp = Perpendicular(dir, is2D);
            Vector3 up2 = is2D ? Vector3.zero : Vector3.Cross(dir, perp).normalized;
            ConsiderRay(probe, pos, pos, radius, dir, len, is2D,
                        ref hit, ref best, ref bestNormal);
            ConsiderRay(probe, pos, pos + perp * radius, radius, dir, len, is2D,
                        ref hit, ref best, ref bestNormal);
            ConsiderRay(probe, pos, pos - perp * radius, radius, dir, len, is2D,
                        ref hit, ref best, ref bestNormal);
            if (!is2D)
            {
                ConsiderRay(probe, pos, pos + up2 * radius, radius, dir, len, is2D,
                            ref hit, ref best, ref bestNormal);
                ConsiderRay(probe, pos, pos - up2 * radius, radius, dir, len, is2D,
                            ref hit, ref best, ref bestNormal);
            }
            if (!hit) return false;
            travel = best;
            normal = bestNormal;
            return true;
        }

        /// <summary>
        /// One fallback ray. Its hit point is a point on the surface, so the
        /// centre travel is the plane-contact formula
        /// <c>t = (radius - dot(pos - point, normal)) / dot(dir, normal)</c>:
        /// how far the centre goes before the sphere's edge reaches that
        /// plane. (Subtracting the radius from the ray distance instead would
        /// only be right head-on.) The running minimum wins.
        /// </summary>
        private static void ConsiderRay(IMotionProbe probe, Vector3 pos, Vector3 from, float radius,
                                        Vector3 dir, float len, bool is2D,
                                        ref bool hit, ref float best, ref Vector3 bestNormal)
        {
            float rd;
            Vector3 point, normal;
            if (!probe.Raycast(from, dir, len + radius, is2D, out rd, out point, out normal))
                return;
            float denom = Dot(dir, normal);
            if (denom > -MinStep) return;              // parallel or facing away
            float t = (radius - Dot(pos - point, normal)) / denom;
            if (t < 0f) t = 0f;
            t -= CollisionSkin;
            if (t < 0f) t = 0f;
            if (t > len) t = len;
            if (!hit || t < best)
            {
                best = t;
                bestNormal = normal;
                hit = true;
            }
        }

        private static Vector3 Perpendicular(Vector3 dir, bool is2D)
        {
            Vector3 perp;
            if (is2D)
            {
                perp = new Vector3(-dir.y, dir.x, 0f);
            }
            else
            {
                Vector3 axis = Mathf.Abs(dir.y) > 0.9f
                    ? new Vector3(1f, 0f, 0f)     // moving vertically: any axis works
                    : new Vector3(0f, 1f, 0f);
                perp = Vector3.Cross(dir, axis).normalized;
            }
            if (perp.sqrMagnitude <= MinStep * MinStep) perp = new Vector3(1f, 0f, 0f);
            return perp;
        }

        private static float Dot(Vector3 a, Vector3 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }

        /// <summary>
        /// The move for an object that HAS a collider: move a substep, let the
        /// engine's overlap query say how far it is inside whatever it now
        /// touches, back it out and slide the leftover along the contact. No
        /// shape is approximated and no cast can be blind to it, because
        /// ComputePenetration/Collider2D.Distance answer for the real collider.
        /// </summary>
        private Vector3 ResolveStep(MotionContext ctx, Vector3 ownerPos, Vector3 delta,
                                    out bool blocked)
        {
            blocked = false;
            Transform owner = ctx.Owner;
            if (owner == null || Probe == null) return ownerPos + delta;

            float cap = SubStepCap(ctx);
            Vector3 pos = ownerPos;
            Vector3 remaining = ctx.Is2D ? new Vector3(delta.x, delta.y, 0f) : delta;
            int slides = 0;

            for (int i = 0; i < MaxSubSteps && slides < MaxSlideIterations; i++)
            {
                float len = remaining.magnitude;
                if (len <= MinStep) break;
                Vector3 dir = remaining / len;
                float stepLen = Mathf.Min(cap, len);
                Vector3 tentative = pos + dir * stepLen;
                owner.position = tentative;

                Vector3 push;
                if (Probe.ResolvePenetration(owner, SweepCentre(ctx, tentative), RadiusOf(ctx),
                                             ctx.Is2D, out push))
                {
                    blocked = true;
                    slides++;
                    // Back out along the contact as far as it takes, plus a
                    // skin so the next step does not start inside.
                    pos = tentative + push;
                    owner.position = pos;
                    remaining -= dir * stepLen;
                    Vector3 normal = push.normalized;
                    if (normal.sqrMagnitude <= 1e-8f) break;
                    remaining = Vector3.ProjectOnPlane(remaining, normal);
                    if (ctx.Is2D) remaining.z = 0f;
                }
                else
                {
                    pos = tentative;
                    remaining -= dir * stepLen;
                }
            }

            if (ctx.Is2D)
            {
                Vector3 fixedPos = new Vector3(pos.x, pos.y, ownerPos.z);
                owner.position = fixedPos;
                return fixedPos;
            }
            owner.position = pos;
            return pos;
        }

        /// <summary>
        /// How far the body may travel before the engine is asked again: half
        /// its own smallest dimension, so it can never step past something
        /// thinner than itself without overlapping it at least once.
        /// </summary>
        private static float SubStepCap(MotionContext ctx)
        {
            Vector3 e;
            if (ctx.Shape != null) e = ctx.Shape.bounds.extents;
            else if (ctx.Shape2D != null)
            {
                Vector2 e2 = ctx.Shape2D.bounds.extents;
                e = new Vector3(e2.x, e2.y, e2.x);
            }
            else e = new Vector3(DefaultBodyRadius, DefaultBodyRadius, DefaultBodyRadius);

            float smallest = Mathf.Min(Mathf.Min(e.x, e.y), e.z);
            float cap = smallest * 0.5f;
            if (cap < MinSubStep) cap = MinSubStep;
            return cap;
        }

        /// <summary>
        /// The move for an object with NO collider: there is no shape for the
        /// engine to resolve, so a probe sphere is swept with
        /// Physics.SphereCast / Physics2D.CircleCast, the true surface normal is
        /// taken from a ray, and the running position is pushed out of anything
        /// it ended up inside.
        /// </summary>
        private Vector3 SweptSpherePosition(MotionContext ctx, Vector3 ownerPos, Vector3 delta,
                                            out bool blocked)
        {
            Vector3 origin = SweepCentre(ctx, ownerPos);
            float radius = RadiusOf(ctx);
            Vector3 swept = SlideStep(origin, delta, radius, ctx.Is2D, Probe, out blocked);

            // "SphereCast will not detect colliders for which the sphere
            // overlaps the collider": if anything did get the body inside a
            // collider, leave it outside at the end of the tick.
            if (Probe != null)
            {
                for (int i = 0; i < MaxOverlapIterations; i++)
                {
                    Vector3 push;
                    if (!Probe.ResolvePenetration(null, swept, radius, ctx.Is2D, out push))
                        break;
                    swept += push;
                    blocked = true;
                }
            }

            Vector3 moved = swept - origin;
            if (ctx.Is2D) moved.z = 0f;
            return ownerPos + moved;
        }

        /// <summary>
        /// One step through whichever strategy fits the object: the engine's
        /// overlap resolution when it has a collider, the swept probe sphere
        /// when it has none.
        /// </summary>
        private Vector3 StepPosition(MotionContext ctx, Vector3 ownerPos, Vector3 delta,
                                     out bool blocked)
        {
            if (ctx.Shape != null || ctx.Shape2D != null)
                return ResolveStep(ctx, ownerPos, delta, out blocked);
            return SweptSpherePosition(ctx, ownerPos, delta, out blocked);
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

        /// <summary>
        /// One console line per agent when its driver changes or it first gets
        /// stopped by something, so "should this object be colliding?" is
        /// answerable at a glance instead of by guesswork. Logs on the
        /// transition only, so a goal re-posted every tick stays quiet.
        /// </summary>
        private void Report(int handleId, MotionContext ctx, string who, bool blocked,
                            AiExecution exec)
        {
            if (exec == null || exec.Log == null) return;
            string drv = DriverName(ctx.Driver);
            string before;
            bool known = _reportedDriver.TryGetValue(handleId, out before);
            if (!known || before != drv) _reportedDriver[handleId] = drv;

            if (blocked)
            {
                if (_blocked.Add(handleId))
                {
                    string what = Probe != null && !string.IsNullOrEmpty(Probe.LastHitName)
                        ? Probe.LastHitName
                        : "a collider";
                    exec.Log.Info("movement: " + who + " stopped by " + what + " (" + drv + ")");
                }
                return;
            }
            _blocked.Remove(handleId);
            if (!known || before != drv)
                exec.Log.Info("movement: " + who + " driven by " + drv);
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
                    Report(handleId, ctx, t.name, false, exec);
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
                    Report(handleId, ctx, t.name, false, exec);
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
                        // resolve the move here and hand the result to the
                        // engine as a pose it will apply.
                        bool touched;
                        Vector3 next = StepPosition(ctx, ownerPos, delta, out touched);
                        if (ctx.Driver == MotionDriver.Rigidbody)
                            ctx.Body.MovePosition(next);
                        else
                            ctx.Body2D.MovePosition(new Vector2(next.x, next.y));
                        Report(handleId, ctx, t.name, touched, exec);
                        return;
                    }
                    DriveVelocity(ctx, to / dist, dist, dt, goal.Speed);
                    Report(handleId, ctx, t.name, false, exec);
                    return;
                }
                case MotionDriver.ColliderSweep:
                {
                    bool touched;
                    Vector3 next = StepPosition(ctx, ownerPos, delta, out touched);
                    ctx.Owner.position = next;
                    Report(handleId, ctx, t.name, touched, exec);
                    return;
                }
                default:
                {
                    // Nothing to drive the object, but "no rigidbody" still
                    // means collisions are respected: resolve against the real
                    // collider when there is one, else sweep the default body
                    // radius. Only CollisionAware=false writes a bare position.
                    if (CollisionAware)
                    {
                        bool touched;
                        Vector3 next = StepPosition(ctx, ownerPos, delta, out touched);
                        ctx.Owner.position = next;
                        Report(handleId, ctx, t.name, touched, exec);
                    }
                    else
                    {
                        ctx.Owner.position = ownerPos + delta;
                    }
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
