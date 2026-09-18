// Test 02 — camera rig: parked above the player, looking down, following it.
//
// The stress test spreads chasers over a large plane, so a camera at eye height
// shows a wall of cubes and no context. This rig sits `height` metres above the
// player, `backDistance` metres behind it, and looks down at it: you can see the
// crowd, the spawn column in the air, and where you are walking.
//
// It writes the transform in LateUpdate (after the player has moved this frame,
// so the camera never lags a frame behind its target). Smoothing is exponential,
// which keeps the follow speed identical at any frame rate.

using UnityEngine;

namespace MyFSM.Tests
{
    [DefaultExecutionOrder(200)] // after player/AI movement, before rendering
    public class TopDownFollowCamera : MonoBehaviour
    {
        [Header("What to follow")]
        [Tooltip("The player. Empty: looks for a PlayerController in the scene.")]
        public Transform target;

        [Header("Where to sit")]
        [Tooltip("Metres above the target (45 keeps the whole spawn column in frame).")]
        public float height = 45f;
        [Tooltip("Metres behind the target along -Z, which tilts the view so the " +
                 "player is not dead centre in the picture.")]
        public float backDistance = 18f;
        [Tooltip("Look at the target plus this offset (1.5 keeps the player low in frame).")]
        public Vector3 lookOffset = new Vector3(0f, 1.5f, 0f);
        [Tooltip("Seconds of follow lag. 0 snaps hard every frame; 0.12 is smooth.")]
        public float smoothing = 0.12f;
        [Tooltip("Keep the camera's world XZ position fixed (only y follows). Useful " +
                 "if you want a static overhead shot of a growing crowd.")]
        public bool stayAboveTarget = true;

        private Vector3 _velocity;

        private void Start()
        {
            if (target == null)
            {
                PlayerController player = FindObjectOfType<PlayerController>();
                if (player != null) target = player.transform;
            }
            if (target == null)
            {
                Debug.LogWarning("[camera] no target (no PlayerController in the scene) — " +
                                 "the rig stays where it is.", this);
                enabled = false;
                return;
            }
            SnapToTarget();
        }

        private void LateUpdate()
        {
            if (target == null) return;

            Vector3 desired = DesiredPosition();
            transform.position = smoothing <= 0f
                ? desired
                : Vector3.SmoothDamp(transform.position, desired, ref _velocity, smoothing);
            transform.LookAt(target.position + lookOffset);
        }

        /// <summary>Jump straight to the framed position (no fly-in at start).</summary>
        public void SnapToTarget()
        {
            if (target == null) return;
            transform.position = DesiredPosition();
            transform.LookAt(target.position + lookOffset);
            _velocity = Vector3.zero;
        }

        private Vector3 DesiredPosition()
        {
            Vector3 origin = stayAboveTarget
                ? new Vector3(target.position.x, 0f, target.position.z)
                : target.position;
            return origin + new Vector3(0f, height, -backDistance);
        }
    }
}
