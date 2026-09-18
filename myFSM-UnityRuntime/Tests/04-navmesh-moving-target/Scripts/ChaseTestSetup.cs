// Test 04 — builds the whole test scene from one component: maze + chaser +
// walking target + recorder.
//
// Both actors are separate objects with their own NavMeshAgent:
//
//   Chaser  capsule + NavMeshAgent + the generated PathChaserAI. The runtime
//           drives its agent with SetDestination, re-planned every tick.
//   Walker  capsule + NavMeshAgent + MazeTargetWalker. It walks itself (its own
//           SetDestination / Move calls), so it always stands on valid NavMesh.
//
// The chaser is deliberately a little faster than the walker: the catch has to
// come from the re-planning, not from the chase being impossible.
//
// Everything the test needs is created here, so an empty GameObject with this
// component on it is a complete test scene.

using UnityEngine;
using UnityEngine.AI;
using MyFSM.Unity;

namespace MyFSM.Tests
{
    public class ChaseTestSetup : MonoBehaviour
    {
        [Header("Scene")]
        public MazeGeneratorController maze;
        public PathChaserAI chaser;
        public MazeTargetWalker walker;
        public bool createActorsIfMissing = true;

        [Header("Chaser setup")]
        public float chaserSpeed = 4.5f;
        public float agentRadius = 0.4f;
        public float agentHeight = 2f;
        public float agentAcceleration = 12f;

        [Header("Target setup")]
        [Tooltip("Must stay below chaserSpeed, or the catch never happens.")]
        public float walkerSpeed = 3.2f;
        public MazeTargetWalker.Mode walkerMode = MazeTargetWalker.Mode.PingPong;
        [Tooltip("Height of a capsule's centre above the ground.")]
        public float centreHeight = 1f;

        private void Awake()
        {
            if (maze == null) maze = FindObjectOfType<MazeGeneratorController>();
            if (maze == null)
            {
                GameObject mazeObject = new GameObject("Maze");
                maze = mazeObject.AddComponent<MazeGeneratorController>();
                Debug.Log("[maze-04] no MazeGeneratorController found — created one with the default grid.", this);
            }

            if (chaser == null) chaser = FindObjectOfType<PathChaserAI>();
            if (walker == null) walker = FindObjectOfType<MazeTargetWalker>();

            if (chaser == null && createActorsIfMissing) chaser = CreateChaser();
            if (walker == null && createActorsIfMissing) walker = CreateWalker();

            if (chaser != null)
            {
                maze.runner = null; // test 03's slot, unused here
                if (chaser.GetComponent<MovingTargetRecorder>() == null)
                {
                    MovingTargetRecorder recorder = chaser.gameObject.AddComponent<MovingTargetRecorder>();
                    recorder.target = walker;
                }
            }
            if (walker != null) maze.targetWalker = walker.transform;
        }

        private void Start()
        {
            if (maze == null || chaser == null || walker == null) return;
            // Every Awake has run by now, so the entry/exit markers exist.
            Vector3 entry = maze.EntryPosition;
            Vector3 stand = new Vector3(entry.x, maze.GroundY + centreHeight, entry.z);
            Place(chaser.transform, stand);
            Place(walker.transform, stand);
            Debug.Log("[maze-04] chaser (speed " + chaserSpeed + ") starts at the entry; target "
                      + "(speed " + walkerSpeed + ") walks " + walkerMode + " between the entry and "
                      + "the exit. Watching for the catch.", this);
        }

        /// <summary>
        /// Puts an agent-driven object at a position. A NavMeshAgent owns its
        /// position, so writing transform.position would be undone on the next
        /// agent update: Warp is Unity's call for "move this agent here".
        /// </summary>
        private static void Place(Transform target, Vector3 position)
        {
            if (target == null) return;
            NavMeshAgent agent = target.GetComponent<NavMeshAgent>();
            if (agent != null && agent.enabled && agent.isOnNavMesh)
            {
                agent.Warp(position);
                return;
            }
            target.position = position;
        }

        private PathChaserAI CreateChaser()
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "PathChaser";
            NavMeshAgent agent = go.AddComponent<NavMeshAgent>();
            agent.radius = agentRadius;
            agent.height = agentHeight;
            agent.speed = chaserSpeed;
            agent.acceleration = agentAcceleration;
            agent.angularSpeed = 720f;
            agent.stoppingDistance = 0.2f;
            agent.autoBraking = false;

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = new Color(0.9f, 0.3f, 0.25f);

            PathChaserAI ai = go.AddComponent<PathChaserAI>();
            Debug.Log("[maze-04] created the chaser (capsule + NavMeshAgent + PathChaserAI).", go);
            return ai;
        }

        private MazeTargetWalker CreateWalker()
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "TargetWalker";
            NavMeshAgent agent = go.AddComponent<NavMeshAgent>();
            agent.radius = agentRadius;
            agent.height = agentHeight;
            agent.speed = walkerSpeed;
            agent.acceleration = agentAcceleration;
            agent.angularSpeed = 720f;
            agent.stoppingDistance = 0.4f;

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = new Color(0.25f, 0.6f, 1f);

            MazeTargetWalker created = go.AddComponent<MazeTargetWalker>();
            created.speed = walkerSpeed;
            created.mode = walkerMode;
            created.maze = maze;
            Debug.Log("[maze-04] created the walking target (capsule + NavMeshAgent + MazeTargetWalker).", go);
            return created;
        }
    }
}
