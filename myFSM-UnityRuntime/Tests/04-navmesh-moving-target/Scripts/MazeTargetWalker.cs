// Test 04 — the object the time-varying target test chases.
//
// It walks the maze itself (its own NavMeshAgent, set in this script, not by
// myFSM) so that it always stands on a valid NavMesh position: a target that
// wandered off the navmesh would make the chaser's findPath fail for reasons
// that have nothing to do with the test.
//
// Three ways to move it:
//   PingPong    walks back and forth between two points — the default, and the
//               reproducible way to make the route change under the chaser
//   RandomWalk  keeps picking a far away point on the navmesh and walking there
//   Manual      you drive it with WASD (the agent's own Move() keeps it on the
//               navmesh); press M at any time to switch to manual
//
// PingPong starts at the maze entry and turns around at the exit, so it always
// travels through the same corridors the chaser has to re-plan through.

using UnityEngine;
using UnityEngine.AI;

namespace MyFSM.Tests
{
    [RequireComponent(typeof(NavMeshAgent))]
    public class MazeTargetWalker : MonoBehaviour
    {
        public enum Mode
        {
            PingPong,
            RandomWalk,
            Manual
        }

        [Header("How it moves")]
        public Mode mode = Mode.PingPong;
        public float speed = 4.5f;
        [Tooltip("0.5 m away from a waypoint counts as 'there'.")]
        public float arriveDistance = 0.5f;

        [Header("Ping-pong")]
        [Tooltip("One end of the walk. Empty: the maze entry.")]
        public Transform endA;
        [Tooltip("The other end. Empty: the maze exit.")]
        public Transform endB;
        public float pauseAtEnds = 0.25f;

        [Header("Random walk")]
        [Tooltip("Bounds to pick points in. Empty: the maze controller's grid.")]
        public MazeGeneratorController maze;
        public float minLegDistance = 6f;
        public int sampleAttempts = 30;
        public float pauseBetweenLegs = 0.25f;

        [Header("Manual")]
        public float manualSpeed = 5f;
        public KeyCode manualKey = KeyCode.M;

        /// <summary>Metres this object has actually travelled (proof the target moved).</summary>
        public float TotalMoved { get; private set; }

        private NavMeshAgent _agent;
        private Vector3 _lastPosition;
        private Transform _currentEnd;
        private float _pauseUntil;
        private MazeRunnerAI _runner;
        private bool _warnedNoEnds;

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _agent.speed = speed;
            _agent.autoBraking = true;
            _agent.stoppingDistance = arriveDistance;
            _lastPosition = transform.position;

            ResolveEnds();
        }

        private void Start()
        {
            ResolveEnds();
            if (endA == null && endB == null && !_warnedNoEnds)
            {
                _warnedNoEnds = true;
                Debug.LogWarning("[walker] no maze/entry/exit found — set endA and endB, or use "
                                 + "MazeGeneratorController. Falling back to a 10 m line.", this);
            }
            if (mode == Mode.Manual) return;
            // SetDestination is only legal on an agent that is on a NavMesh: if
            // this object has not been placed yet, Update() keeps retrying.
            if (_agent.isOnNavMesh) GoToNextEnd();
        }

        /// <summary>
        /// Ends are resolved lazily: this walker may be created before the maze
        /// has finished building (both happen in Awake, in scene order), so the
        /// lookup is retried until the entry/exit markers exist.
        /// </summary>
        private void ResolveEnds()
        {
            if (maze == null) maze = FindObjectOfType<MazeGeneratorController>();
            if (endA == null && maze != null) endA = maze.Entry;
            if (endB == null && maze != null) endB = maze.Exit;
            if (endA == null && endB == null && _runner == null)
            {
                // No maze in the scene: fall back to the runner of test 03.
                _runner = FindObjectOfType<MazeRunnerAI>();
                if (_runner != null) endA = _runner.transform;
            }
        }

        private void Update()
        {
            if (ChaseInput.GetKeyDown(manualKey))
            {
                mode = mode == Mode.Manual ? Mode.PingPong : Mode.Manual;
                if (mode == Mode.Manual) _agent.ResetPath();
                else GoToNextEnd();
                Debug.Log("[walker] mode " + mode, this);
            }

            if (mode == Mode.Manual) UpdateManual();
            else if (mode == Mode.PingPong) UpdatePingPong();
            else UpdateRandomWalk();

            TotalMoved += HorizontalDistance(_lastPosition, transform.position);
            _lastPosition = transform.position;
        }

        // ------------------------------------------------------------------

        private void UpdateManual()
        {
            float x = 0f;
            float z = 0f;
            if (ChaseInput.GetKey(KeyCode.A) || ChaseInput.GetKey(KeyCode.LeftArrow)) x -= 1f;
            if (ChaseInput.GetKey(KeyCode.D) || ChaseInput.GetKey(KeyCode.RightArrow)) x += 1f;
            if (ChaseInput.GetKey(KeyCode.S) || ChaseInput.GetKey(KeyCode.DownArrow)) z -= 1f;
            if (ChaseInput.GetKey(KeyCode.W) || ChaseInput.GetKey(KeyCode.UpArrow)) z += 1f;

            Vector3 direction = new Vector3(x, 0f, z);
            if (direction.sqrMagnitude < 0.0001f) return;
            direction.Normalize();

            // NavMeshAgent.Move is Unity's own "walk this much, stay on the
            // navmesh" call: no custom collision handling anywhere in this test.
            _agent.Move(direction * manualSpeed * Time.deltaTime);
        }

        private void UpdatePingPong()
        {
            if (endA == null && endB == null) ResolveEnds();
            if (Time.time < _pauseUntil) return;
            if (_agent.pathPending) return;
            if (_agent.remainingDistance > arriveDistance) return;

            _pauseUntil = Time.time + pauseAtEnds;
            GoToNextEnd();
        }

        private void GoToNextEnd()
        {
            if (!_agent.isOnNavMesh) return;
            Vector3 target;
            if (endA == null && endB == null)
            {
                target = transform.position + new Vector3(10f, 0f, 0f);
            }
            else if (_currentEnd == endA || _currentEnd == null)
            {
                _currentEnd = endB != null ? endB : endA;
                target = _currentEnd.position;
            }
            else
            {
                _currentEnd = endA != null ? endA : endB;
                target = _currentEnd.position;
            }
            _agent.SetDestination(target);
        }

        private void UpdateRandomWalk()
        {
            if (Time.time < _pauseUntil) return;
            if (_agent.pathPending || _agent.remainingDistance > arriveDistance) return;

            Vector3 candidate;
            if (TryRandomPoint(out candidate))
            {
                _pauseUntil = Time.time + pauseBetweenLegs;
                _agent.SetDestination(candidate);
            }
            else
            {
                _pauseUntil = Time.time + 0.5f; // nothing valid on the navmesh, retry shortly
            }
        }

        private bool TryRandomPoint(out Vector3 point)
        {
            point = transform.position;
            Vector3 min = maze != null ? maze.CellCentre(0, 0) : transform.position - new Vector3(10f, 0f, 10f);
            Vector3 max = maze != null
                ? maze.CellCentre(maze.cellsX - 1, maze.cellsY - 1)
                : transform.position + new Vector3(10f, 0f, 10f);

            for (int attempt = 0; attempt < sampleAttempts; attempt++)
            {
                Vector3 candidate = new Vector3(Random.Range(min.x, max.x),
                                                min.y + 0.5f,
                                                Random.Range(min.z, max.z));
                if (HorizontalDistance(candidate, transform.position) < minLegDistance) continue;

                NavMeshHit hit;
                if (!NavMesh.SamplePosition(candidate, out hit, 4f, NavMesh.AllAreas)) continue;
                point = hit.position;
                return true;
            }
            return false;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
