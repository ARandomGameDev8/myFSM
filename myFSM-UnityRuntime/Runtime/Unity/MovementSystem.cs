// myFSM Unity Runtime — incremental movement system.
//
// Navigation / lookAt calls never teleport: they post a goal for the agent,
// and Advance() (once per tick, before the AI's Update round) moves the goal
// a step closer. 3D agents with a live NavMeshAgent on the NavMesh steer via
// SetDestination (NavMesh finds the shortest path); everything else moves
// manually: position += direction * speed * dt, swept against colliders (see
// the collision-aware stepping block below) so bodies stop at walls and slide
// along them. Movement is translation-only; facing changes only through
// lookAt goals.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using MyFSM.Core;

namespace MyFSM.Unity
{
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
    /// One ray against the engine's collision world. Unity's physics implements
    /// it (PhysicsMotionProbe); the headless sandbox injects a fake so the
    /// slide behaviour can be tested without an engine.
    /// </summary>
    public interface IMotionProbe
    {
        /// <summary>
        /// Casts a ray. Returns true when something is hit within
        /// <paramref name="maxDistance"/>; <paramref name="distance"/> is then
        /// the distance from the origin to the impact point and
        /// <paramref name="normal"/> the surface normal there. Rays starting
        /// inside a collider do not report it, which is what keeps an agent
        /// from colliding with itself.
        /// </summary>
        bool Ray(Vector3 origin, Vector3 direction, float maxDistance, bool is2D,
                 out float distance, out Vector3 normal);
    }

    /// <summary>The real thing: Physics.Raycast / Physics2D.Raycast.</summary>
    public sealed class PhysicsMotionProbe : IMotionProbe
    {
        public bool Ray(Vector3 origin, Vector3 direction, float maxDistance, bool is2D,
                        out float distance, out Vector3 normal)
        {
            distance = 0f;
            normal = Vector3.zero;
            if (maxDistance < 0f) return false;
            if (is2D)
            {
                RaycastHit2D h = Physics2D.Raycast(
                    new Vector2(origin.x, origin.y),
                    new Vector2(direction.x, direction.y), maxDistance);
                if (h.collider == null) return false;
                distance = h.distance;
                normal = new Vector3(h.normal.x, h.normal.y, 0f);
                return true;
            }
            RaycastHit hit;
            if (!Physics.Raycast(origin, direction, out hit, maxDistance)) return false;
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

        public void ClearGoal(int agentHandleId)
        {
            _goals.Remove(agentHandleId);
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
        // Collision-aware stepping
        //
        // The manual fallback used to be position += direction * speed * dt,
        // which walks straight through walls. Instead the step is swept
        // against the engine's colliders with a few rays around the body
        // radius (centre + a ring perpendicular to the motion, so corners are
        // not clipped), and what is left after the first contact is projected
        // onto the surface and re-swept: the object slides along walls
        // instead of stopping dead or tunnelling.
        // ----------------------------------------------------------

        /// <summary>Manual movement collision-checks every step when true.</summary>
        public bool CollisionAware = true;

        /// <summary>Ray source. Swap it to test movement without an engine.</summary>
        public IMotionProbe Probe = new PhysicsMotionProbe();

        /// <summary>Body radius used when the agent has no collider.</summary>
        public const float DefaultBodyRadius = 0.5f;

        /// <summary>Kept between the body and a surface, to stop it creeping in.</summary>
        public const float CollisionSkin = 0.02f;

        private const int MaxSlideIterations = 3;
        private const float MinStep = 1e-5f;

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

        /// <summary>Point the sweep starts from: the collider's centre if any.</summary>
        private static Vector3 SweepOrigin(Transform t)
        {
            Collider c = t.GetComponent<Collider>();
            if (c != null && c.enabled) return c.bounds.center;
            return t.position;
        }

        private static void ApplyPosition(Transform t, Vector3 next)
        {
            Rigidbody rb = t.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.MovePosition(next);   // physics owns the transform
                return;
            }
            Rigidbody2D rb2 = t.GetComponent<Rigidbody2D>();
            if (rb2 != null)
            {
                rb2.MovePosition(new Vector2(next.x, next.y));
                return;
            }
            t.position = next;
        }

        /// <summary>
        /// Moves a body of <paramref name="radius"/> from
        /// <paramref name="origin"/> by <paramref name="delta"/>, stopping at
        /// obstacles and sliding along them. Returns the position reached and
        /// sets <paramref name="blocked"/> when anything got in the way.
        /// </summary>
        public static Vector3 SlideStep(Vector3 origin, Vector3 delta, float radius,
                                        bool is2D, IMotionProbe probe, out bool blocked)
        {
            blocked = false;
            Vector3 pos = origin;
            Vector3 remaining = delta;
            for (int i = 0; i < MaxSlideIterations; i++)
            {
                float len = remaining.magnitude;
                if (len <= MinStep) break;
                Vector3 dir = remaining / len;
                float travel;
                Vector3 normal;
                if (!SweepAround(pos, dir, len, radius, is2D, probe, out travel, out normal))
                {
                    pos += remaining;
                    break;
                }
                blocked = true;
                float advance = travel - CollisionSkin;
                if (advance < 0f) advance = 0f;
                pos += dir * advance;
                // Whatever is left of this step, minus its component into the
                // surface: the body keeps moving along the wall.
                Vector3 rest = dir * (len - advance);
                Vector3 slide = rest - normal * Dot(rest, normal);
                if (is2D) slide.z = 0f;
                if (slide.sqrMagnitude <= MinStep * MinStep) break;
                remaining = slide;
            }
            return pos;
        }

        private static float Dot(Vector3 a, Vector3 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }

        /// <summary>
        /// How far the body's CENTRE may travel before it touches something.
        /// Rays leave the centre and a ring of offsets perpendicular to the
        /// motion (so a corner is caught before the centre ray alone would
        /// see it), and each hit is turned into a centre travel with the
        /// plane-contact formula
        /// <c>t = (radius - dot(pos - hitPoint, normal)) / dot(dir, normal)</c>
        /// — subtracting the radius from the ray distance instead would be
        /// right only head-on, and would let an obliquely moving body overlap
        /// the surface. Reports the smallest travel and that surface's normal.
        /// </summary>
        private static bool SweepAround(Vector3 pos, Vector3 dir, float len, float radius,
                                        bool is2D, IMotionProbe probe,
                                        out float travel, out Vector3 normal)
        {
            travel = len;
            normal = Vector3.zero;
            bool hit = false;

            // Perpendicular to the motion, in the plane the body moves in.
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
                perp = Cross(dir, axis).normalized;
            }
            if (perp.sqrMagnitude <= MinStep * MinStep) perp = new Vector3(1f, 0f, 0f);
            Vector3 up2 = is2D ? Vector3.zero : Cross(dir, perp).normalized;

            // The surface may already be touching, so the rays reach a body
            // radius past the step itself.
            float cap = len + radius;
            ConsiderHit(pos, pos, dir, cap, radius, is2D, probe,
                        ref hit, ref travel, ref normal);
            ConsiderHit(pos, pos + perp * radius, dir, cap, radius, is2D, probe,
                        ref hit, ref travel, ref normal);
            ConsiderHit(pos, pos - perp * radius, dir, cap, radius, is2D, probe,
                        ref hit, ref travel, ref normal);
            if (!is2D)
            {
                ConsiderHit(pos, pos + up2 * radius, dir, cap, radius, is2D, probe,
                            ref hit, ref travel, ref normal);
                ConsiderHit(pos, pos - up2 * radius, dir, cap, radius, is2D, probe,
                            ref hit, ref travel, ref normal);
            }
            return hit;
        }

        /// <summary>
        /// Casts one ray and folds its hit into the running minimum. The
        /// travel is always derived for the CENTRE (<paramref name="pos"/>),
        /// whichever offset the ray started from, since that is the quantity
        /// the caller moves.
        /// </summary>
        private static void ConsiderHit(Vector3 pos, Vector3 from, Vector3 dir, float cap,
                                        float radius, bool is2D, IMotionProbe probe,
                                        ref bool hit, ref float travel, ref Vector3 normal)
        {
            float d;
            Vector3 n;
            if (!probe.Ray(from, dir, cap, is2D, out d, out n)) return;
            float denom = Dot(dir, n);
            if (denom > -MinStep) return;   // parallel or facing away: not blocking
            Vector3 hitPoint = from + dir * d;
            float t = (radius - Dot(pos - hitPoint, n)) / denom;
            if (t < 0f) t = 0f;             // already touching/inside: cannot advance
            if (!hit || t < travel)
            {
                travel = t;
                normal = n;
                hit = true;
            }
        }

        private static Vector3 Cross(Vector3 a, Vector3 b)
        {
            return new Vector3(a.y * b.z - a.z * b.y,
                               a.z * b.x - a.x * b.z,
                               a.x * b.y - a.y * b.x);
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
                    _goals.Remove(handleId);
                    return;
                }
                dest = goal.Corners[goal.CornerIndex];
            }

            // NavMesh fast path (3D only): a live agent steers itself.
            NavMeshAgent agent = null;
            if (!goal.Is2D)
            {
                NavMeshAgent found = t.gameObject.GetComponent<NavMeshAgent>();
                if (found != null && found.enabled && found.isOnNavMesh)
                    agent = found;
            }
            if (agent != null)
            {
                agent.isStopped = false;
                agent.speed = goal.Speed;
                agent.stoppingDistance = goal.StopDistance;
                agent.SetDestination(dest);
                if (!agent.pathPending &&
                    agent.pathStatus == NavMeshPathStatus.PathInvalid)
                {
                    // Unreachable on the mesh: fall through to manual motion.
                    agent = null;
                }
                else if (!agent.pathPending &&
                         agent.remainingDistance <= goal.StopDistance)
                {
                    if (goal.Mode != MoveMode.FollowObject)
                        _goals.Remove(handleId);
                    return;
                }
                else
                {
                    return; // engine is steering; nothing more this tick
                }
            }

            // Manual fallback: position += direction * speed * dt, swept
            // against colliders so the body stops at walls and slides along
            // them instead of passing through.
            Vector3 pos = t.position;
            Vector3 to = dest - pos;
            if (goal.Is2D) to.z = 0f;
            float dist = to.magnitude;
            if (dist <= goal.StopDistance)
            {
                if (goal.Mode != MoveMode.FollowObject)
                    _goals.Remove(handleId);
                return;
            }
            float step = goal.Speed * dt;
            Vector3 delta = step >= dist ? to : to / dist * step;
            if (goal.Is2D) delta.z = 0f;
            Vector3 next = pos + delta;
            if (CollisionAware && Probe != null && delta.sqrMagnitude > 0f)
            {
                Vector3 origin = SweepOrigin(t);
                bool blocked;
                Vector3 swept = SlideStep(origin, delta, BodyRadius(t, goal.Is2D),
                                          goal.Is2D, Probe, out blocked);
                next = pos + (swept - origin);
            }
            if (goal.Is2D) next.z = pos.z;
            ApplyPosition(t, next);
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
                t.rotation = Quaternion.Euler(0f, 0f, next);
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
            t.rotation = Quaternion.RotateTowards(t.rotation, want,
                FunctionDispatcher.DefaultLookSpeed * dt);
            if (Quaternion.Angle(t.rotation, want) < 0.5f)
                _goals.Remove(handleId);
        }
    }
}
