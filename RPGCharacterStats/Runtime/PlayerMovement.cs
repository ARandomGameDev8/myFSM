// RPG Character & Stats System — the single movement component a spawned
// player carries. Reads WASD/Space and drives WHATEVER body the spawn pipeline
// gave the player: CharacterController.Move for the 3D non-physics case,
// Rigidbody / Rigidbody2D otherwise. One component, zero camera code, and the
// whole movement story is auditable from one place.
//
// It replaces the old "movement strategy" subclasses (PhysicsPlayerMovement /
// CharacterControllerMovement / Kinematic2DMovement / NpcMovement), which were
// invisible strategy classes next to the character. The spawn pipeline now
// attaches exactly one movement component per player — this one.
//
// NOTE: there is deliberately NO [RequireComponent]. A 2D player has no
// CharacterController, and RequireComponent(typeof(CharacterController)) would
// force-add one to every spawned player. The pipeline owns the body; this
// component just finds it at runtime.

using UnityEngine;

namespace RPGCharacterStats
{
    [DisallowMultipleComponent]
    [AddComponentMenu("RPG/Player Movement")]
    public class PlayerMovement : MonoBehaviour
    {
        [Header("Movement")]
        [Tooltip("Horizontal speed in m/s.")]
        public float moveSpeed = 5f;

        [Tooltip("Jump apex height in meters.")]
        public float jumpHeight = 1.2f;

        [Tooltip("Gravity acceleration in m/s² (negative pulls down). Only used by the CharacterController path — physics bodies get gravity from the physics engine.")]
        public float gravity = -20f;

        [Tooltip("Terminal fall speed in m/s so long drops stay sane (CharacterController path).")]
        public float maxFallSpeed = -50f;

        [Header("Input")]
        public KeyCode forwardKey = KeyCode.W;
        public KeyCode backKey = KeyCode.S;
        public KeyCode leftKey = KeyCode.A;
        public KeyCode rightKey = KeyCode.D;
        public KeyCode jumpKey = KeyCode.Space;
        [Tooltip("Arrow keys mirror WASD.")]
        public bool arrowKeysAlsoMove = true;

        private CharacterController _controller;
        private Rigidbody _body3D;
        private Rigidbody2D _body2D;
        private float _verticalVelocity;

        private void Awake()
        {
            ResolveBodies();
        }

        private void ResolveBodies()
        {
            if (_controller == null) _controller = GetComponent<CharacterController>();
            if (_body2D == null) _body2D = GetComponent<Rigidbody2D>();
            if (_body3D == null) _body3D = GetComponent<Rigidbody>();
        }

        private void Update()
        {
            Tick(ReadInput());
        }

        /// <summary>One full simulation step for the given input. Public so
        /// headless tests and scripted sequences can drive the movement
        /// component without touching real keyboard input. Fills the owning
        /// PlayerCharacter's input field so other systems (tests, AI, UI) read
        /// one input. Exactly one of the three drive paths runs per tick.</summary>
        public void Tick(PlayerInput input)
        {
            ResolveBodies();

            PlayerCharacter owner = GetComponentInParent<PlayerCharacter>();
            if (owner != null) owner.input = input;

            if (_controller != null) { TickCharacterController(input); return; }
            if (_body2D != null) { TickRigidbody2D(input); return; }
            if (_body3D != null) { TickRigidbody3D(input); return; }
            // No body on this GameObject — nothing to move.
        }

        // ---- drive path 1: 3D non-physics Player (CharacterController) ----

        private void TickCharacterController(PlayerInput input)
        {
            // Horizontal wish direction, normalized so diagonals aren't faster.
            Vector2 wish = new Vector2(input.x, input.y);
            if (wish.sqrMagnitude > 1f) wish = wish.normalized;
            Vector3 motion = new Vector3(wish.x, 0f, wish.y) * moveSpeed * Time.deltaTime;

            // Vertical: the CharacterController is not a physics body, so
            // gravity is integrated by hand and folded into the same Move call.
            if (_controller.isGrounded)
            {
                if (_verticalVelocity < 0f) _verticalVelocity = -2f;  // keep ground contact
                if (input.jumpHeld) _verticalVelocity = JumpVelocity();
            }
            _verticalVelocity += gravity * Time.deltaTime;
            if (_verticalVelocity < maxFallSpeed) _verticalVelocity = maxFallSpeed;
            motion.y = _verticalVelocity * Time.deltaTime;

            _controller.Move(motion);
        }

        // ---- drive path 2: 2D Player (Rigidbody2D) ----
        // A/D strafe on X, Space jumps. For a PhysicsBased body the physics
        // engine owns vertical gravity; for the NonPhysics kinematic body the
        // pipeline set gravityScale = 0, so velocity writes are pure motion.

        private void TickRigidbody2D(PlayerInput input)
        {
            Vector2 v = _body2D.velocity;
            v.x = input.x * moveSpeed;
            if (input.jumpHeld && v.y <= 0.01f) v.y = JumpVelocity();
            _body2D.velocity = v;
        }

        // ---- drive path 3: 3D physics Player (Rigidbody) ----
        // WASD steers X/Z, Space jumps; the physics engine owns gravity and
        // collisions. (The 3D NON-physics player uses the CharacterController
        // path above, so this path only ever sees a physics body.)

        private void TickRigidbody3D(PlayerInput input)
        {
            Vector2 wish = new Vector2(input.x, input.y);
            if (wish.sqrMagnitude > 1f) wish = wish.normalized;

            Vector3 v = _body3D.velocity;
            v.x = wish.x * moveSpeed;
            v.z = wish.y * moveSpeed;
            if (input.jumpHeld && v.y <= 0.01f) v.y = JumpVelocity();
            _body3D.velocity = v;
        }

        /// <summary>Upward launch speed that reaches <see cref="jumpHeight"/>
        /// meters under gravity magnitude |g|: v = √(2·h·|g|). For physics-body
        /// paths the effective gravity is the project's Physics settings — the
        /// height is then approximate by design.</summary>
        private float JumpVelocity()
        {
            float g = Mathf.Abs(gravity);
            return g > 0f ? Mathf.Sqrt(2f * g * Mathf.Max(jumpHeight, 0f)) : 0f;
        }

        /// <summary>Keyboard read: WASD (+ optional arrows) and the jump key,
        /// clamped to the -1..1 axes PlayerInput defines. Virtual so a game can
        /// subclass for gamepads, netcode, or AI-driven players.</summary>
        protected virtual PlayerInput ReadInput()
        {
            PlayerInput input;
            input.x = 0f;
            input.y = 0f;
            input.jumpHeld = false;

            if (Input.GetKey(leftKey)) input.x -= 1f;
            if (Input.GetKey(rightKey)) input.x += 1f;
            if (Input.GetKey(forwardKey)) input.y += 1f;
            if (Input.GetKey(backKey)) input.y -= 1f;
            if (arrowKeysAlsoMove)
            {
                if (Input.GetKey(KeyCode.LeftArrow)) input.x -= 1f;
                if (Input.GetKey(KeyCode.RightArrow)) input.x += 1f;
                if (Input.GetKey(KeyCode.UpArrow)) input.y += 1f;
                if (Input.GetKey(KeyCode.DownArrow)) input.y -= 1f;
            }
            input.x = Mathf.Clamp(input.x, -1f, 1f);
            input.y = Mathf.Clamp(input.y, -1f, 1f);

            input.jumpHeld = Input.GetKey(jumpKey);   // hold to hop; swap for GetKeyDown to forbid bunny-hopping
            return input;
        }
    }
}
