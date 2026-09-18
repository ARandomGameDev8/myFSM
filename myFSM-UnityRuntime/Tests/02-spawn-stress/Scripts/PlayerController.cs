// Test 02 — the WASD object every spawned chaser hunts.
//
// Ordinary character movement, the way Unity documents it:
//
//   * keys are read every frame in Update, through StressInput so either input
//     backend works (UnityEngine.Input throws on a project set to the Input
//     System package - the failure looks exactly like a broken test),
//   * movement is applied in FixedUpdate, where physics runs,
//   * the step is the same formula the agents use: position += direction * speed * time,
//   * the vertical axis belongs to gravity, never to WASD.
//
// Whichever component the object carries decides how that is written down:
//
//   CharacterController -> Move(direction * speed * dt) plus a gravity accumulator.
//                          This is what SpawnStressTest puts on the player:
//                          rigidbodies cannot push a controller, so the crowd can
//                          block the player but never drag it, and walking into the
//                          crowd pushes cubes - what a solid object moving through
//                          them does.
//   Rigidbody, kinematic -> MovePosition(position + direction * speed * dt)
//   Rigidbody, dynamic   -> the plane is commanded and the body's own gravity keeps
//                          the fall (setting the velocity is how a dynamic body is
//                          moved in Unity; a position command would fight gravity)
//   no body at all       -> transform.position += direction * speed * dt
//
// Nothing here knows about the crowd, and nothing else in the test moves this
// object: the player goes where the keys say and nowhere else.

using UnityEngine;

namespace MyFSM.Tests
{
    public class PlayerController : MonoBehaviour
    {
        [Header("Movement")]
        [Tooltip("Metres per second.")]
        public float speed = 8f;
        [Tooltip("Move relative to the camera's facing instead of world axes.")]
        public bool cameraRelative = true;
        [Tooltip("Downward acceleration for a CharacterController, m/s^2.")]
        public float fallGravity = -9.81f;

        [Header("Boundary (optional)")]
        [Tooltip("Keep the player inside a square of this half-size, centred on the "
                 + "origin. 0 = no boundary. SpawnStressTest sets it from the ground "
                 + "plane so the crowd always has ground under the player.")]
        public float halfExtent = 0f;

        /// <summary>
        /// True while a movement key is held. The test logs it, so a player that
        /// moves while this is false is reported instead of guessed about.
        /// </summary>
        public bool Moving { get; private set; }

        private CharacterController _controller;
        private Rigidbody _body;
        private Vector3 _direction;    // world space, y = 0, zero when no key is held
        private float _fallSpeed;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _body = GetComponent<Rigidbody>();
        }

        private void Update()
        {
            _direction = ReadKeys();
            Moving = _direction.sqrMagnitude > 0f;
        }

        private void FixedUpdate()
        {
            Vector3 step = _direction * (speed * Time.fixedDeltaTime);

            if (_controller != null && _controller.enabled)
            {
                // Move() is the controller's own capsule sweep: walls, steps, slopes.
                // Gravity is the one thing it does not do for you.
                if (_controller.isGrounded && _fallSpeed < 0f) _fallSpeed = -1f;
                _fallSpeed += fallGravity * Time.fixedDeltaTime;
                step.y = _fallSpeed * Time.fixedDeltaTime;
                _controller.Move(step);
                Clamp();
                return;
            }

            if (_body != null && _body.isKinematic)
            {
                _body.MovePosition(Clamped(_body.position + step));
                return;
            }

            if (_body != null)
            {
                // Dynamic body: command the plane, leave velocity.y to gravity.
                Vector3 velocity = _body.velocity;
                _body.velocity = new Vector3(step.x / Time.fixedDeltaTime, velocity.y,
                                             step.z / Time.fixedDeltaTime);
                return;
            }

            transform.position = Clamped(transform.position + step);
        }

        /// <summary>Reads WASD (and the arrow keys), normalised, in world space.</summary>
        private Vector3 ReadKeys()
        {
            float x = 0f;
            float z = 0f;
            if (StressInput.GetKeyEither(KeyCode.A, KeyCode.LeftArrow)) x -= 1f;
            if (StressInput.GetKeyEither(KeyCode.D, KeyCode.RightArrow)) x += 1f;
            if (StressInput.GetKeyEither(KeyCode.S, KeyCode.DownArrow)) z -= 1f;
            if (StressInput.GetKeyEither(KeyCode.W, KeyCode.UpArrow)) z += 1f;

            Vector3 direction = new Vector3(x, 0f, z);
            if (direction.sqrMagnitude > 1f) direction.Normalize();

            if (cameraRelative && direction.sqrMagnitude > 0f)
            {
                Camera cam = Camera.main;
                if (cam != null)
                {
                    Vector3 forward = cam.transform.forward;
                    forward.y = 0f;
                    if (forward.sqrMagnitude > 1e-6f)
                    {
                        forward.Normalize();
                        Vector3 right = new Vector3(forward.z, 0f, -forward.x);
                        direction = right * direction.x + forward * direction.z;
                        if (direction.sqrMagnitude > 1f) direction.Normalize();
                    }
                }
            }
            return direction;
        }

        private Vector3 Clamped(Vector3 position)
        {
            if (halfExtent <= 0f) return position;
            position.x = Mathf.Clamp(position.x, -halfExtent, halfExtent);
            position.z = Mathf.Clamp(position.z, -halfExtent, halfExtent);
            return position;
        }

        /// <summary>Applies the boundary after a controller move (which cannot take a
        /// clamped target, it moves by a delta).</summary>
        private void Clamp()
        {
            if (halfExtent <= 0f || _controller == null) return;
            Vector3 position = transform.position;
            float x = Mathf.Clamp(position.x, -halfExtent, halfExtent);
            float z = Mathf.Clamp(position.z, -halfExtent, halfExtent);
            if (x != position.x || z != position.z)
            {
                _controller.enabled = false;
                transform.position = new Vector3(x, position.y, z);
                _controller.enabled = true;
            }
        }
    }
}
