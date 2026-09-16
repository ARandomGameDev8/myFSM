// myFSM Unity Runtime — incremental movement system.
//
// Navigation / lookAt calls never teleport: they post a goal for the agent,
// and Advance() (once per tick, before the AI's Update round) moves the goal
// a step closer. 3D agents with a live NavMeshAgent on the NavMesh steer via
// SetDestination (NavMesh finds the shortest path); everything else moves
// manually: position += direction * speed * dt. Movement is translation-only;
// facing changes only through lookAt goals.

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

            // Manual fallback: position += direction * speed * dt.
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
            Vector3 next = step >= dist ? dest : pos + to / dist * step;
            if (goal.Is2D) next.z = pos.z;
            t.position = next;
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
