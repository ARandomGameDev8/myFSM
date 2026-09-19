// myFSM Unity Runtime — Navigation category (0x0600-0x062C, 45 overloads).
//
// Path queries over NavMesh (3D) with straight-line fallback, arrival tests,
// and goal-posting movement: goTo / followTarget / findShortestPathAndMove /
// follow / sprintTowards / moveTowards never teleport — they set a goal the
// MovementSystem advances incrementally through the Unity call for the
// agent's component: NavMeshAgent.SetDestination on a mesh,
// Rigidbody(2D).MovePosition from FixedUpdate (rb.position + normalized
// direction * speed * Time.fixedDeltaTime, the plain follower script),
// CharacterController.Move, or a transform write when nothing can move it.
// Object destinations are snapshotted at call time (goTo/sprint); follow*
// and moveTowards(object) re-target live.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using MyFSM.Core;

namespace MyFSM.Unity
{
    public static class NavigationFunctions
    {
        public static FsmValue Run(FunctionDispatcher d, ushort id, FsmValue[] args, AiExecution exec)
        {
            switch (id)
            {
                case 0x0600: return FindPath3(d, args, exec);
                case 0x0601: return FindPath2(d, args, exec);
                case 0x0602: return NextWaypoint(d, args, exec);
                case 0x0603: return PathLength(d, args, exec);
                case 0x0604: return HasReachedPoint(d, args, exec, false);
                case 0x0605: return HasReachedObject(d, args, exec, false);
                case 0x0606: return HasReachedPoint(d, args, exec, false);
                case 0x0607: return HasReachedObject(d, args, exec, false);
                case 0x0608: return HasReachedPoint(d, args, exec, true);
                case 0x0609: return HasReachedObject(d, args, exec, true);
                case 0x060A: return GoToPoint(d, args, exec);
                case 0x060B: return GoToObject(d, args, exec);
                case 0x060C: return GoToPoint(d, args, exec);
                case 0x060D: return GoToObject(d, args, exec);
                case 0x060E: return GoToPoint(d, args, exec);
                case 0x060F: return GoToObject(d, args, exec);
                case 0x0610: return Follow(d, args, exec, 1f, "followTarget");
                case 0x0611: return Follow(d, args, exec, 1f, "followTarget");
                case 0x0612: return Follow(d, args, exec, 1f, "followTarget");
                case 0x0613: return Follow(d, args, exec, 1f, "followTarget");
                case 0x0614: return ShortestPathMove(d, args, exec, false);
                case 0x0615: return ShortestPathMove(d, args, exec, true);
                case 0x0616: return ShortestPathMove(d, args, exec, false);
                case 0x0617: return ShortestPathMove(d, args, exec, true);
                case 0x0618: return ShortestPathMove(d, args, exec, false);
                case 0x0619: return ShortestPathMove(d, args, exec, true);
                case 0x061A: return Follow(d, args, exec, 0.5f, "follow");
                case 0x061B: return Follow(d, args, exec, 0.5f, "follow");
                case 0x061C: return Follow(d, args, exec, 0.5f, "follow");
                case 0x061D: return Follow(d, args, exec, 0.5f, "follow");
                case 0x061E: return SprintToPoint(d, args, exec);
                case 0x061F: return SprintToObject(d, args, exec);
                case 0x0620: return SprintToPoint(d, args, exec);
                case 0x0621: return SprintToObject(d, args, exec);
                case 0x0622: return SprintToPoint(d, args, exec);
                case 0x0623: return SprintToObject(d, args, exec);
                case 0x0624: return MoveToPoint(d, args, exec);
                case 0x0625: return MoveToObject(d, args, exec);
                case 0x0626: return MoveToPoint(d, args, exec);
                case 0x0627: return MoveToObject(d, args, exec);
                case 0x0628: return MoveToPoint(d, args, exec);
                case 0x0629: return MoveToObject(d, args, exec);
                case 0x062A: return Stop(d, args, exec);
                case 0x062B: return Stop(d, args, exec);
                case 0x062C: return Stop(d, args, exec);
                default:
                    exec.Log.Error("unknown Navigation function id " + id.ToString("X4"));
                    return FsmValue.Void;
            }
        }

        private static bool Is2DAgent(FsmValue h)
        {
            return h.TypeTag == FsmbType.Object2D;
        }

        private static bool TryGetAgent(FunctionDispatcher d, FsmValue h, out NavMeshAgent nav)
        {
            nav = null;
            UnityEngine.Object o = d.Handles.Resolve(h.HandleId);
            if (o == null) return false;
            nav = o as NavMeshAgent;
            if (nav != null) return true;
            GameObject go = o as GameObject;
            if (go == null)
            {
                Component c = o as Component;
                if (c != null) go = c.gameObject;
            }
            if (go == null) return false;
            nav = go.GetComponent<NavMeshAgent>();
            return nav != null;
        }

        private static float BaseSpeed(FunctionDispatcher d, FsmValue agent)
        {
            NavMeshAgent nav;
            if (TryGetAgent(d, agent, out nav) && nav != null) return nav.speed;
            return FunctionDispatcher.DefaultMoveSpeed;
        }

        private static float StopForNew(FunctionDispatcher d, FsmValue agent)
        {
            NavMeshAgent nav;
            if (TryGetAgent(d, agent, out nav) && nav != null) return nav.stoppingDistance;
            return FunctionDispatcher.DefaultStopDistance;
        }

        private static float StopForCheck(FunctionDispatcher d, FsmValue agent)
        {
            MoveGoal g;
            if (d.Movement.TryGetGoal(agent.HandleId, out g)) return g.StopDistance;
            return StopForNew(d, agent);
        }

        private static List<Vector3> ComputeCorners3(Vector3 from, Vector3 to)
        {
            List<Vector3> corners = new List<Vector3>();
            NavMeshPath path = new NavMeshPath();
            if (NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path) &&
                path.status != NavMeshPathStatus.PathInvalid &&
                path.corners != null && path.corners.Length > 0)
            {
                corners.AddRange(path.corners);
            }
            else
            {
                corners.Add(to); // straight-line fallback
            }
            return corners;
        }

        // ----------------------------------------------------------
        // Path queries
        // ----------------------------------------------------------

        private static FsmValue FindPath3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            if (!Finite3(FsmConvert.ToV3(args[0])) || !Finite3(FsmConvert.ToV3(args[1])))
            {
                // NaN reaches here when a start/end was read from an unbound slot
                // (see ObjectFunctions / FunctionDispatcher.InvalidPosition3). Asking
                // the engine to path-find between imaginary points would be worse
                // than refusing: no path is stored, and the id 0 is already invalid
                // for getPathLength / findShortestPathAndMove.
                exec.ErrorOnce("findpath:invalid",
                               "findPath: start or end is not a finite position (NaN/Infinity) — "
                               + "it was most likely read from an unbound slot. No path stored "
                               + "(returns 0, which the path functions reject).");
                return FsmValue.MakeInt(0);
            }
            List<Vector3> corners = ComputeCorners3(FsmConvert.ToV3(args[0]), FsmConvert.ToV3(args[1]));
            return FsmValue.MakeInt(d.Paths.Alloc(corners));
        }

        /// <summary>Every component a real number (the guard for engine-bound math).</summary>
        private static bool Finite3(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        private static FsmValue FindPath2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            if (!Finite3(new Vector3(args[0].X, args[0].Y, 0f)) ||
                !Finite3(new Vector3(args[1].X, args[1].Y, 0f)))
            {
                exec.ErrorOnce("findpath:invalid",
                               "findPath: start or end is not a finite position (NaN/Infinity) — "
                               + "it was most likely read from an unbound slot. No path stored "
                               + "(returns 0, which the path functions reject).");
                return FsmValue.MakeInt(0);
            }
            List<Vector3> corners = new List<Vector3>();
            corners.Add(new Vector3(args[0].X, args[0].Y, 0f));
            corners.Add(new Vector3(args[1].X, args[1].Y, 0f));
            return FsmValue.MakeInt(d.Paths.Alloc(corners));
        }

        private static FsmValue NextWaypoint(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            StoredPath p;
            if (!d.Paths.TryGet(args[0].I, out p) || p.Corners.Count == 0)
            {
                exec.ErrorOnce("waypoint:unknownpath",
                               "getNextWaypoint: unknown path id (findPath was never called, "
                               + "or the id is 0). Returning an invalid (NaN) position instead of "
                               + "(0,0,0) — the world origin is the centre of the scene, and a "
                               + "caller moving towards it would look pulled there.");
                return FunctionDispatcher.InvalidPosition3(exec, "getNextWaypoint");
            }
            int i = p.NextIndex;
            if (i >= p.Corners.Count) i = p.Corners.Count - 1; // exhausted: hold last
            else p.NextIndex++;
            return FsmConvert.FromV3(p.Corners[i]);
        }

        private static FsmValue PathLength(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            StoredPath p;
            if (!d.Paths.TryGet(args[0].I, out p))
            {
                exec.Log.Error("getPathLength: unknown path");
                return FsmValue.MakeFloat(0f);
            }
            float total = 0f;
            for (int i = 1; i < p.Corners.Count; i++)
                total += Vector3.Distance(p.Corners[i - 1], p.Corners[i]);
            return FsmValue.MakeFloat(total);
        }

        // ----------------------------------------------------------
        // Arrival
        // ----------------------------------------------------------

        private static FsmValue HasReachedPoint(FunctionDispatcher d, FsmValue[] args,
                                                AiExecution exec, bool is2D)
        {
            Transform at = d.ResolveTransform(args[0], exec, "hasReachedDestination");
            if (at == null) return FsmValue.MakeBool(false);
            float tol = StopForCheck(d, args[0]);
            bool reached;
            if (is2D)
            {
                Vector2 a = new Vector2(at.position.x, at.position.y);
                Vector2 q = FsmConvert.ToV2(args[1]);
                reached = (a - q).sqrMagnitude <= tol * tol;
            }
            else
            {
                reached = (at.position - FsmConvert.ToV3(args[1])).sqrMagnitude <= tol * tol;
            }
            return FsmValue.MakeBool(reached);
        }

        private static FsmValue HasReachedObject(FunctionDispatcher d, FsmValue[] args,
                                                 AiExecution exec, bool is2D)
        {
            Transform at = d.ResolveTransform(args[0], exec, "hasReachedDestination");
            Transform tt = d.ResolveTransform(args[1], exec, "hasReachedDestination");
            if (at == null || tt == null) return FsmValue.MakeBool(false);
            float tol = StopForCheck(d, args[0]);
            bool reached;
            if (is2D)
            {
                Vector2 a = new Vector2(at.position.x, at.position.y);
                Vector2 q = new Vector2(tt.position.x, tt.position.y);
                reached = (a - q).sqrMagnitude <= tol * tol;
            }
            else
            {
                reached = (at.position - tt.position).sqrMagnitude <= tol * tol;
            }
            return FsmValue.MakeBool(reached);
        }

        // ----------------------------------------------------------
        // Goals
        // ----------------------------------------------------------

        /// <summary>
        /// Posts a Point goal, refusing an invalid destination.
        ///
        /// A destination that is not a finite position can only come from a slot
        /// that was never bound (see ObjectFunctions.InvalidPosition). Posting it
        /// would have the movement system chase garbage; the old behaviour of
        /// reading (0,0,0) out of an unbound slot sent every such agent to the
        /// WORLD ORIGIN — the centre of the scene — which is exactly the "why is
        /// everything pulled to the middle" symptom. Nothing is posted, and the
        /// agent stays where it is.
        /// </summary>
        private static void PostPoint(FunctionDispatcher d, FsmValue agent, Vector3 dest,
                                      float speed, bool is2D, string fn, AiExecution exec)
        {
            if (float.IsNaN(dest.x) || float.IsInfinity(dest.x) ||
                float.IsNaN(dest.y) || float.IsInfinity(dest.y) ||
                float.IsNaN(dest.z) || float.IsInfinity(dest.z))
            {
                exec.ErrorOnce("postpoint:" + fn,
                               fn + ": the destination is not a finite position (NaN/Infinity), "
                               + "so no goal was posted and the agent is NOT sent anywhere. The "
                               + "usual cause is a destination read from an unbound slot with "
                               + "getPosition — bind that slot, or check it before using it.");
                return;
            }
            MoveGoal g = new MoveGoal();
            g.Mode = MoveMode.Point;
            g.Agent = agent;
            g.Destination = dest;
            g.Speed = speed;
            g.StopDistance = StopForNew(d, agent);
            g.Is2D = is2D;
            d.Movement.SetGoal(agent.HandleId, g);
        }

        private static FsmValue GoToPoint(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            if (d.ResolveTransform(args[0], exec, "goTo") == null) return FsmValue.Void;
            bool is2D = Is2DAgent(args[0]);
            Vector3 dest = is2D
                ? new Vector3(args[1].X, args[1].Y, 0f)
                : FsmConvert.ToV3(args[1]);
            PostPoint(d, args[0], dest, BaseSpeed(d, args[0]), is2D, "goTo", exec);
            return FsmValue.Void;
        }

        private static FsmValue GoToObject(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            if (d.ResolveTransform(args[0], exec, "goTo") == null) return FsmValue.Void;
            Transform tt = d.ResolveTransform(args[1], exec, "goTo");
            if (tt == null) return FsmValue.Void;
            PostPoint(d, args[0], tt.position, BaseSpeed(d, args[0]), Is2DAgent(args[0]),
                      "goTo", exec);
            return FsmValue.Void;
        }

        private static FsmValue Follow(FunctionDispatcher d, FsmValue[] args, AiExecution exec,
                                       float stopScale, string fn)
        {
            if (d.ResolveTransform(args[0], exec, fn) == null) return FsmValue.Void;
            if (d.ResolveTransform(args[1], exec, fn) == null) return FsmValue.Void;
            MoveGoal g = new MoveGoal();
            g.Mode = MoveMode.FollowObject;
            g.Agent = args[0];
            g.Target = args[1];
            g.Speed = BaseSpeed(d, args[0]);
            g.StopDistance = StopForNew(d, args[0]) * stopScale;
            g.Is2D = Is2DAgent(args[0]);
            d.Movement.SetGoal(args[0].HandleId, g);
            return FsmValue.Void;
        }

        private static FsmValue ShortestPathMove(FunctionDispatcher d, FsmValue[] args,
                                                 AiExecution exec, bool targetIsObject)
        {
            Transform at = d.ResolveTransform(args[0], exec, "findShortestPathAndMove");
            if (at == null) return FsmValue.Void;
            bool is2D = Is2DAgent(args[0]);
            Vector3 from = at.position;
            Vector3 to;
            if (targetIsObject)
            {
                Transform tt = d.ResolveTransform(args[1], exec, "findShortestPathAndMove");
                if (tt == null) return FsmValue.Void;
                to = tt.position;
            }
            else
            {
                to = is2D
                    ? new Vector3(args[1].X, args[1].Y, from.z)
                    : FsmConvert.ToV3(args[1]);
            }
            List<Vector3> corners = new List<Vector3>();
            if (is2D)
            {
                corners.Add(to);
            }
            else
            {
                corners = ComputeCorners3(from, to);
            }
            MoveGoal g = new MoveGoal();
            g.Mode = MoveMode.PathCorners;
            g.Agent = args[0];
            g.Speed = BaseSpeed(d, args[0]);
            g.StopDistance = StopForNew(d, args[0]);
            g.Is2D = is2D;
            g.Corners = corners;
            g.CornerIndex = 0;
            d.Movement.SetGoal(args[0].HandleId, g);
            return FsmValue.Void;
        }

        private static FsmValue SprintToPoint(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            if (d.ResolveTransform(args[0], exec, "sprintTowards") == null) return FsmValue.Void;
            float mult = args[2].F;
            if (mult < 0f)
            {
                exec.Log.Warn("sprintTowards: negative multiplier clamped to 0");
                mult = 0f;
            }
            bool is2D = Is2DAgent(args[0]);
            Vector3 dest = is2D
                ? new Vector3(args[1].X, args[1].Y, 0f)
                : FsmConvert.ToV3(args[1]);
            PostPoint(d, args[0], dest, BaseSpeed(d, args[0]) * mult, is2D, "sprintTowards", exec);
            return FsmValue.Void;
        }

        private static FsmValue SprintToObject(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            if (d.ResolveTransform(args[0], exec, "sprintTowards") == null) return FsmValue.Void;
            Transform tt = d.ResolveTransform(args[1], exec, "sprintTowards");
            if (tt == null) return FsmValue.Void;
            float mult = args[2].F;
            if (mult < 0f)
            {
                exec.Log.Warn("sprintTowards: negative multiplier clamped to 0");
                mult = 0f;
            }
            PostPoint(d, args[0], tt.position, BaseSpeed(d, args[0]) * mult, Is2DAgent(args[0]),
                      "sprintTowards", exec);
            return FsmValue.Void;
        }

        private static FsmValue MoveToPoint(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            if (d.ResolveTransform(args[0], exec, "moveTowards") == null) return FsmValue.Void;
            float speed = args[2].F;
            if (speed < 0f)
            {
                exec.Log.Warn("moveTowards: negative speed clamped to 0");
                speed = 0f;
            }
            bool is2D = Is2DAgent(args[0]);
            Vector3 dest = is2D
                ? new Vector3(args[1].X, args[1].Y, 0f)
                : FsmConvert.ToV3(args[1]);
            PostPoint(d, args[0], dest, speed, is2D, "moveTowards", exec);
            return FsmValue.Void;
        }

        private static FsmValue MoveToObject(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            if (d.ResolveTransform(args[0], exec, "moveTowards") == null) return FsmValue.Void;
            if (d.ResolveTransform(args[1], exec, "moveTowards") == null) return FsmValue.Void;
            float speed = args[2].F;
            if (speed < 0f)
            {
                exec.Log.Warn("moveTowards: negative speed clamped to 0");
                speed = 0f;
            }
            // An OBJECT target is tracked, not snapshotted: the destination is
            // re-read every tick, so the agent keeps moving towards it whether it
            // stands still or wanders off. (Pass a Vector3 — e.g.
            // getPosition(target) — for a one-off point instead.)
            MoveGoal g = new MoveGoal();
            g.Mode = MoveMode.FollowObject;
            g.Agent = args[0];
            g.Target = args[1];
            g.Speed = speed;
            g.StopDistance = StopForNew(d, args[0]);
            g.Is2D = Is2DAgent(args[0]);
            d.Movement.SetGoal(args[0].HandleId, g);
            return FsmValue.Void;
        }

        private static FsmValue Stop(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            d.Movement.ClearGoal(args[0].HandleId);
            NavMeshAgent nav;
            if (TryGetAgent(d, args[0], out nav) && nav != null)
            {
                nav.isStopped = true;
                nav.ResetPath();
            }
            return FsmValue.Void;
        }
    }
}
