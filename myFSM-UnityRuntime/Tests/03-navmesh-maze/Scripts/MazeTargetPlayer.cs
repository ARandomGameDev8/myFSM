// Test 03 — the chase target as a PLAYABLE character: WASD/arrows move it
// through the maze, exactly the way test 04's walker is driven by hand.
//
// Movement is NavMeshAgent.Move: Unity's own "walk this much, stay on the
// navmesh" call — walls constrain it, no custom collision code anywhere.
// Without input the agent stands still (no auto-patrol); M (or clicking a
// gamepad-less build) is not needed — WASD always works.
//
// This component IS the default chase target: MazeTestSetup creates it when
// no `target` was assigned, so the runner's moveTowards goal — which re-reads
// the destination object every tick — chases you while you run. The old
// static exit marker is still available by leaving the player off in the
// setup (`spawnPlayerTarget = false`).
//
// Run it with the overview camera (F toggles follow mode): in follow mode you
// are the camera's anchor, so you can literally outrun the AI through your own
// maze.

using UnityEngine;
using UnityEngine.AI;

namespace MyFSM.Tests
{
    [DefaultExecutionOrder(20)] // move before the runner's AI ticks (order 50+)
    public class MazeTargetPlayer : MonoBehaviour
    {
        [Header("Movement")]
        public float moveSpeed = 4.5f;
        [Tooltip("Rotates the body toward the walk direction (cosmetics only).")]
        public float turnSpeed = 540f;
        [Tooltip("Also accept arrow keys alongside WASD.")]
        public bool arrowsToo = true;

        private NavMeshAgent _agent;
        private float _totalMoved;

        /// <summary>Metres this target has actually moved (proof it moved).</summary>
        public float TotalMoved { get { return _totalMoved; } }

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
        }

        private void Update()
        {
            float x = 0f;
            float z = 0f;
            // MazeInput: works with either Unity input backend (see MazeInput.cs).
            if (MazeInput.GetKey(KeyCode.W) || (arrowsToo && MazeInput.GetKey(KeyCode.UpArrow))) z += 1f;
            if (MazeInput.GetKey(KeyCode.S) || (arrowsToo && MazeInput.GetKey(KeyCode.DownArrow))) z -= 1f;
            if (MazeInput.GetKey(KeyCode.A) || (arrowsToo && MazeInput.GetKey(KeyCode.LeftArrow))) x -= 1f;
            if (MazeInput.GetKey(KeyCode.D) || (arrowsToo && MazeInput.GetKey(KeyCode.RightArrow))) x += 1f;

            Vector3 direction = new Vector3(x, 0f, z);
            if (direction.sqrMagnitude < 0.0001f) return;
            direction.Normalize();

            // NavMeshAgent.Move: walk this much, stay on the navmesh. Walls
            // constrain the agent; nothing else is needed.
            _agent.Move(direction * moveSpeed * Time.deltaTime);

            // Cosmetics: face where you walk.
            Quaternion look = Quaternion.LookRotation(direction);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, look,
                                                          turnSpeed * Time.deltaTime);

            _totalMoved += moveSpeed * Time.deltaTime;
        }
    }
}
