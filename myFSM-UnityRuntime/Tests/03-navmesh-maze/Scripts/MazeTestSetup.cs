// Test 03 — builds the whole test scene from one component, so the test can be
// dropped onto an empty GameObject and played.
//
// It makes sure a maze exists (adding a MazeGeneratorController if the scene has
// none), then creates the runner — capsule + NavMeshAgent + the pre-compiled TestAI —
// and adds the recorder. The maze is rebuilt with a fresh random seed, so
// every Play is a NEW maze; whatever the layout, the carver guarantees at
// least one path from entry to exit (a spanning tree; braiding only removes
// walls), and the build proves it with a BFS before the NavMesh is baked.
//
// The movement is entirely the runtime's: TestAI posts a persistent chase goal
// with moveTowards(agent, target, speed); the runtime re-reads the target every
// tick and drives the NavMeshAgent with SetDestination. Nothing here touches a
// transform during the run. The AI's dest and speed slots are bound in
// TestAI.Manual.cs (target = the maze's ActiveTarget: the assigned target, or
// the static marker the maze creates on the exit).

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
        public TestAI runner;
        public bool createRunnerIfMissing = true;

        [Header("Runner setup")]
        [Tooltip("The chase speed bound into TestAI.speed (units/second).")]
        public float runnerSpeed = 3.5f;
        public float agentRadius = 0.4f;
        public float agentHeight = 2f;
        public float agentAcceleration = 12f;
        [Tooltip("Height of the capsule's centre above the ground.")]
        public float runnerCentreHeight = 1f;

        private void Awake()
        {
            Current = this;
            if (maze == null) maze = FindObjectOfType<MazeGeneratorController>();
            if (maze == null)
            {
                GameObject mazeObject = new GameObject("Maze");
                maze = mazeObject.AddComponent<MazeGeneratorController>();
                Debug.Log("[maze-03] no MazeGeneratorController found — created one with the default grid.", this);
            }

            if (runner == null) runner = FindObjectOfType<TestAI>();
            if (runner == null && createRunnerIfMissing) runner = CreateRunner();

            if (runner != null)
            {
                maze.runner = runner;
                if (runner.GetComponent<MazePathRecorder>() == null)
                    runner.gameObject.AddComponent<MazePathRecorder>();
            }

            // Camera: park the Main Camera over the maze. Unity spawns it at the
            // origin, eye height — which is inside the maze, staring at a wall.
            AttachCameraRig();
        }

        /// <summary>The one setup in the scene, for the manual bindings to read
        /// (TestAI.Manual.cs takes runnerSpeed from here).</summary>
        public static MazeTestSetup Current { get; private set; }

        private void OnDestroy()
        {
            if (Current == this) Current = null;
        }

        private void Start()
        {
            if (maze == null || runner == null) return;
            // Placement happens in Start: every Awake — including the maze's own
            // build — has finished, so the entry marker is where it belongs.
            Vector3 entry = maze.EntryPosition;
            Place(runner.transform, new Vector3(entry.x, maze.GroundY + runnerCentreHeight, entry.z));
            Debug.Log("[maze-03] runner placed at the entry " + runner.transform.position
                      + " — it should chase the target to "
                      + (maze.ActiveTarget != null ? maze.ActiveTarget.position : maze.ExitPosition)
                      + " and stop within the agent's stoppingDistance.", this);
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

        /// <summary>
        /// Puts the overview rig on the scene's Main Camera (creating a camera
        /// first if the scene has none — a bare scene is meant to just work).
        /// The rig frames the whole maze, reframes after G rebuilds it, and
        /// follows the runner overhead while F is toggled.
        /// </summary>
        private void AttachCameraRig()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                GameObject go = new GameObject("Main Camera");
                go.tag = "MainCamera";
                cam = go.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.12f, 0.14f, 0.18f);
                Debug.Log("[maze-03] no Main Camera in the scene — created one.", go);
            }
            if (cam.GetComponent<MazeOverviewCamera>() == null)
                cam.gameObject.AddComponent<MazeOverviewCamera>();
        }

        private TestAI CreateRunner()
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

            TestAI ai = go.AddComponent<TestAI>();
            Debug.Log("[maze-03] created a runner (capsule + NavMeshAgent + TestAI).", go);
            return ai;
        }
    }
}
