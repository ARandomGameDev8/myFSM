// Test 03 — camera rig: a bird's-eye view of the whole maze, or a top-down
// follow of the runner.
//
// Without this rig the Main Camera sits where Unity puts it by default — at
// the origin, eye height — which is inside the maze: walls on every side, no
// sense of the maze, no sense of the route. This rig parks the camera above
// the maze centre instead, high enough that the whole grid fits in frame, and
// looks down at it (tilt configurable, default 25° — enough top-down to read
// the corridors as a grid, enough angle to see the walls standing up). The
// height is computed from the maze extent and the
// camera's own vertical FOV, so it works for any grid size and aspect.
//
// Press F to toggle follow mode: the camera then tracks the runner from above
// (like test 02's rig, but straight down — corridors stay readable). F again
// returns to the overhead shot, which snaps to the new maze if G rebuilt it.
//
// The rig attaches itself to the scene's Main Camera from MazeTestSetup; no
// scene wiring needed.

using UnityEngine;

namespace MyFSM.Tests
{
    [DefaultExecutionOrder(200)] // after the AI/recorder moved things, before rendering
    public class MazeOverviewCamera : MonoBehaviour
    {
        [Header("What to watch")]
        public MazeGeneratorController maze;
        [Tooltip("Follow target in follow mode. Empty: the scene's runner.")]
        public Transform followTarget;

        [Header("Overhead shot")]
        [Tooltip("Extra metres of margin around the maze in frame.")]
        public float margin = 4f;
        [Tooltip("Tilt from straight down, degrees (0 = top-down orthographic feel, " +
                 "30 = you also see the far walls standing up).")]
        [Range(0f, 60f)] public float tiltDegrees = 25f;

        [Header("Follow mode (F)")]
        [Tooltip("Metres above the runner while following.")]
        public float followHeight = 18f;

        [Header("Keys")]
        public KeyCode toggleKey = KeyCode.F;

        private bool _following;
        private Vector3 _velocity;
        private Transform _target;
        private Camera _camera;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        private void Start()
        {
            if (maze == null) maze = MazeGeneratorController.Current;
            if (followTarget == null && maze != null && maze.runner != null)
                followTarget = maze.runner.transform;
            SnapToMaze();
        }

        private void Update()
        {
            if (MazeInput.GetKeyDown(toggleKey)) ToggleMode();
        }

        /// <summary>Overhead <-> follow. The switch snaps (no fly-in) so the
        /// picture is always one you can read immediately.</summary>
        public void ToggleMode()
        {
            _following = !_following;
            _velocity = Vector3.zero;
            if (_following)
            {
                if (followTarget == null && maze != null && maze.runner != null)
                    followTarget = maze.runner.transform;
                if (followTarget == null)
                {
                    _following = false;
                    Debug.LogWarning("[maze-cam] nothing to follow — no runner in the scene.", this);
                    return;
                }
                _target = followTarget;
                transform.position = FollowPosition();
            }
            else
            {
                SnapToMaze();
            }
        }

        /// <summary>Jumps to the overhead shot of the current maze (also used
        /// after G rebuilt it, via LateUpdate's centre check).</summary>
        public void SnapToMaze()
        {
            if (maze == null) return;
            transform.position = DesiredOverheadPosition();
            LookAtGround(maze.MazeCentre);
        }

        /// <summary>Looks at a ground point. Straight-down views are degenerate
        /// for LookAt (view direction parallel to its default up), so the roll
        /// is pinned to world +Z: north stays up in frame and the exit is at
        /// the top of the picture in follow mode.</summary>
        private void LookAtGround(Vector3 point)
        {
            Vector3 toPoint = point - transform.position;
            if (toPoint.sqrMagnitude < 0.0001f) return;
            transform.rotation = Quaternion.LookRotation(toPoint.normalized, Vector3.forward);
        }

        private void LateUpdate()
        {
            if (_following)
            {
                if (_target == null) { _following = false; SnapToMaze(); return; }
                Vector3 desired = FollowPosition();
                transform.position = Vector3.SmoothDamp(transform.position, desired,
                                                        ref _velocity, 0.1f);
                LookAtGround(_target.position);
                return;
            }

            // Overhead mode: G rebuilds the maze in place — re-frame it when its
            // centre moves, and follow inspector knob edits (tilt, margin, FOV)
            // live. Comparing against the DESIRED tilted position, so this only
            // fires when something actually changed, not every frame.
            if (maze != null)
            {
                Vector3 desired = DesiredOverheadPosition();
                if ((desired - transform.position).sqrMagnitude > 0.0001f)
                {
                    _velocity = Vector3.zero;
                    SnapToMaze();
                }
            }
        }

        /// <summary>The overhead camera spot: above the maze centre, slid back
        /// along -Z by the tilt, looking at the centre. The result frames the
        /// whole maze viewed at `tiltDegrees` from vertical — enough to see the
        /// walls standing up without losing the grid.</summary>
        private Vector3 DesiredOverheadPosition()
        {
            Vector3 above = OverheadPosition();
            if (tiltDegrees > 0.01f)
            {
                float height = above.y - maze.GroundY;
                above += new Vector3(0f, 0f, -Mathf.Tan(tiltDegrees * Mathf.Deg2Rad) * height);
            }
            return above;
        }

        /// <summary>Height that fits the whole maze (plus margin) in the vertical
        /// FOV, from the camera's own field of view.</summary>
        private float OverheadHeight()
        {
            float fov = _camera != null ? _camera.fieldOfView : 60f;
            float extent = Mathf.Max(maze.MazeWidth, maze.MazeDepth) * 0.5f + margin;
            return extent / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
        }

        private Vector3 OverheadPosition()
        {
            return maze.MazeCentre + new Vector3(0f, OverheadHeight(), 0f);
        }

        private Vector3 FollowPosition()
        {
            Vector3 t = _target.position;
            return new Vector3(t.x, maze.GroundY + followHeight, t.z);
        }
    }
}
