// Test 03 — builds the whole test scene from one component, so the test can be
// dropped onto an empty GameObject and played.
//
// It makes sure a maze exists (adding a MazeGeneratorController if the scene has
// none), creates the runner — capsule + NavMeshAgent + the generated
// MazeRunnerAI — and adds the recorder. The maze bakes its NavMesh in its own
// Awake, so by the time this component's Start runs there is an entry to place
// the runner on.
//
// The movement is entirely the runtime's: the runner's NavMeshAgent is the
// component the movement system detects, and it drives it with SetDestination.
// Nothing in this test touches a transform directly.

using UnityEngine;
using UnityEngine.AI;
using MyFSM.Unity;

namespace MyFSM.Tests
{
    public class MazeTestSetup : MonoBehaviour
    {
        [Header("Scene")]
        [Tooltip("The maze. Left empty: found, or created on an empty GameObject.")]
        public MazeGeneratorController maze;
        [Tooltip("The runner. Left empty: found, or created here.")]
        public MazeRunnerAI runner;
        public bool createRunnerIfMissing = true;

        [Header("Runner setup")]
        public float runnerSpeed = 3.5f;
        public float agentRadius = 0.4f;
        public float agentHeight = 2f;
        public float agentAcceleration = 12f;
        [Tooltip("Height of the capsule's centre above the ground.")]
        public float runnerCentreHeight = 1f;

        private void Awake()
        {
            if (maze == null) maze = FindObjectOfType<MazeGeneratorController>();
            if (maze == null)
            {
                GameObject mazeObject = new GameObject("Maze");
                maze = mazeObject.AddComponent<MazeGeneratorController>();
                Debug.Log("[maze-03] no MazeGeneratorController found — created one with the default grid.", this);
            }

            if (runner == null) runner = FindObjectOfType<MazeRunnerAI>();
            if (runner == null && createRunnerIfMissing) runner = CreateRunner();

            if (runner != null)
            {
                maze.runner = runner;
                if (runner.GetComponent<MazePathRecorder>() == null)
                    runner.gameObject.AddComponent<MazePathRecorder>();
            }
        }

        private void Start()
        {
            if (maze == null || runner == null) return;
            // Placement happens in Start: every Awake — including the maze's own
            // build — has finished, so the entry marker is where it belongs.
            Vector3 entry = maze.EntryPosition;
            Place(runner.transform, new Vector3(entry.x, maze.GroundY + runnerCentreHeight, entry.z));
            Debug.Log("[maze-03] runner placed at the entry " + runner.transform.position
                      + " — it should walk the shortest corridor route to " + maze.ExitPosition
                      + " and stop there.", this);
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

        private MazeRunnerAI CreateRunner()
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "MazeRunner";

            NavMeshAgent agent = go.AddComponent<NavMeshAgent>();
            agent.radius = agentRadius;
            agent.height = agentHeight;
            agent.speed = runnerSpeed;
            agent.acceleration = agentAcceleration;
            agent.angularSpeed = 720f;
            agent.stoppingDistance = 0.2f;
            agent.autoBraking = true;

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = new Color(0.95f, 0.85f, 0.2f);

            MazeRunnerAI ai = go.AddComponent<MazeRunnerAI>();
            Debug.Log("[maze-03] created a runner (capsule + NavMeshAgent + MazeRunnerAI).", go);
            return ai;
        }
    }
}
