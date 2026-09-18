// Test 02 — the WASD object every spawned chaser hunts.
//
// Deliberately not an FSM: the point of this test is how many AIs the runtime
// can tick, so the target has to be free — it just moves. The chasers aim at this
// transform's position, which means walking away makes them spread out and re-path
// every tick (that is the load being measured).
//
// The player must never be moved by anything but these keys. SpawnStressTest puts
// a KINEMATIC Rigidbody on it for exactly that reason (Unity: "collisions won't
// affect the rigidbody itself" — the crowd can block the player, never push it),
// so the body is moved with Rigidbody.MovePosition, the call Unity documents for
// kinematic bodies. Rigidbodies are simulated in the physics step, so that move
// happens in FixedUpdate with Time.fixedDeltaTime; without a body the transform is
// written straight in Update, as before.

using UnityEngine;

namespace MyFSM.Tests
{
    public class PlayerController : MonoBehaviour
    {
        [Header("Movement")]
        public float speed = 8f;
        [Tooltip("Height of the object's CENTRE above the ground. The test's player is a " +
                 "2 m cylinder, so its centre is 1 m up (set automatically by SpawnStressTest).")]
        public float height = 1f;
        [Tooltip("Camera-relative movement instead of world axes.")]
        public bool cameraRelative = true;

        [Header("Bounds (walking off the plane stops the stress test being fair)")]
        public bool clampToSquare = true;
        [Tooltip("Half the side of the walkable square. SpawnStressTest sets this from the " +
                 "ground plane's size, so the player cannot walk off the cubes' ground.")]
        public float halfExtent = 195f;

        /// <summary>
        /// True while a movement key is driving the player this frame. The stress
        /// test reads it to tell "the keys moved me" apart from "something else is
        /// moving me" — which is how a player that drifts on its own is spotted.
        /// </summary>
        public bool Moving { get; private set; }

        private Rigidbody _body;
        private Vector3 _direction;   // world space, y = 0, zero when no key is held
        private bool _warnedDynamic;

        /// <summary>
        /// Resolved every frame rather than cached in Awake: SpawnStressTest adds the
        /// kinematic body in ITS Awake, and the order of the two is not defined.
        /// </summary>
        private Rigidbody Body
        {
            get
            {
                if (_body == null) _body = GetComponent<Rigidbody>();
                return _body;
            }
        }

        private void Update()
        {
            Moving = false;
            float x = 0f;
            float z = 0f;
            // StressInput: works with either Unity input backend (see StressInput.cs).
            if (StressInput.GetKey(KeyCode.A) || StressInput.GetKey(KeyCode.LeftArrow)) x -= 1f;
            if (StressInput.GetKey(KeyCode.D) || StressInput.GetKey(KeyCode.RightArrow)) x += 1f;
            if (StressInput.GetKey(KeyCode.S) || StressInput.GetKey(KeyCode.DownArrow)) z -= 1f;
            if (StressInput.GetKey(KeyCode.W) || StressInput.GetKey(KeyCode.UpArrow)) z += 1f;

            Vector3 direction = new Vector3(x, 0f, z);
            if (direction.sqrMagnitude < 0.0001f)
            {
                _direction = Vector3.zero;
                return;
            }
            direction.Normalize();
            Moving = true;

            if (cameraRelative && Camera.main != null)
            {
                Vector3 forward = Camera.main.transform.forward;
                Vector3 right = Camera.main.transform.right;
                forward.y = 0f;
                right.y = 0f;
                forward.Normalize();
                right.Normalize();
                direction = forward * direction.z + right * direction.x;
            }

            _direction = direction;

            Rigidbody body = Body;
            if (body != null && body.isKinematic)
            {
                // Moved in FixedUpdate with MovePosition: a rigidbody is simulated in
                // the physics step, so that is where its movement belongs.
                return;
            }
            if (body != null && !_warnedDynamic)
            {
                _warnedDynamic = true;
                Debug.LogWarning("[player] a DYNAMIC Rigidbody is on this object: the " +
                                 "WASD keys fight the physics solver, and a crowd can " +
                                 "shove it around. SpawnStressTest makes the player's " +
                                 "body kinematic; do the same (or remove the body) if " +
                                 "you set this up by hand.", this);
            }
            transform.position = NextPosition(transform.position, Time.deltaTime);
        }

        private void FixedUpdate()
        {
            Rigidbody body = Body;
            if (body == null || !body.isKinematic) return;
            if (_direction.sqrMagnitude < 0.0001f) return;
            body.MovePosition(NextPosition(body.position, Time.fixedDeltaTime));
        }

        /// <summary>One step of movement from `from`, pinned to `height` and clamped.</summary>
        private Vector3 NextPosition(Vector3 from, float deltaTime)
        {
            Vector3 next = from + _direction * speed * deltaTime;
            next.y = height;
            if (clampToSquare)
            {
                next.x = Mathf.Clamp(next.x, -halfExtent, halfExtent);
                next.z = Mathf.Clamp(next.z, -halfExtent, halfExtent);
            }
            return next;
        }
    }
}
