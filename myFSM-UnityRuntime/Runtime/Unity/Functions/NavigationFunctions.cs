// myFSM Unity Runtime — Navigation category (0x0600-0x062C, 45 overloads).
//
// Path queries over NavMesh (3D) with straight-line fallback, arrival tests,
// and goal-posting movement: goTo / followTarget / findShortestPathAndMove /
// follow / sprintTowards / moveTowards never teleport — they set a goal the
// MovementSystem advances incrementally (NavMeshAgent when on a mesh,
// manual position += direction * speed * dt otherwise, swept against
// colliders so the body slides along walls). Object destinations
// are snapshotted at call time (goTo/sprint/move); follow* re-target live.

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
            List<Vector3> corners = ComputeCorners3(FsmConvert.ToV3(args[0]), FsmConvert.ToV3(args[1]));
            return FsmValue.MakeInt(d.Paths.Alloc(corners));
        }

        private static FsmValue FindPath2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
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
                exec.Log.Error("getNextWaypoint: unknown path");
                return FsmValue.MakeVec3(0f, 0f, 0f);
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

        private static void PostPoint(FunctionDispatcher d, FsmValue agent, Vector3 dest,
                                      float speed, bool is2D)
        {
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
            PostPoint(d, args[0], dest, BaseSpeed(d, args[0]), is2D);
            return FsmValue.Void;
        }

        private static FsmValue GoToObject(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            if (d.ResolveTransform(args[0], exec, "goTo") == null) return FsmValue.Void;
            Transform tt = d.ResolveTransform(args[1], exec, "goTo");
            if (tt == null) return FsmValue.Void;
            PostPoint(d, args[0], tt.position, BaseSpeed(d, args[0]), Is2DAgent(args[0]));
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
            PostPoint(d, args[0], dest, BaseSpeed(d, args[0]) * mult, is2D);
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
            PostPoint(d, args[0], tt.position, BaseSpeed(d, args[0]) * mult, Is2DAgent(args[0]));
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
            PostPoint(d, args[0], dest, speed, is2D);
            return FsmValue.Void;
        }

        private static FsmValue MoveToObject(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            if (d.ResolveTransform(args[0], exec, "moveTowards") == null) return FsmValue.Void;
            Transform tt = d.ResolveTransform(args[1], exec, "moveTowards");
            if (tt == null) return FsmValue.Void;
            float speed = args[2].F;
            if (speed < 0f)
            {
                exec.Log.Warn("moveTowards: negative speed clamped to 0");
                speed = 0f;
            }
            PostPoint(d, args[0], tt.position, speed, Is2DAgent(args[0]));
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
