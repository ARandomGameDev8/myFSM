// RPG Character & Stats System — the user-facing player controller.
//
// A proper WASD/arrows + Space CharacterController mover: gravity applied by
// hand (the CharacterController is not a physics body — section 7.3b), a jump
// tuned by height, and NOT A SINGLE LINE of camera code. Aim the camera,
// parent it, or skip the camera entirely — this component only ever moves its
// own capsule; whatever camera the game has (or doesn't) is someone else's job.
//
// The spawn pipeline attaches it automatically to a 3D non-physics Player
// (the section 2.5 CharacterController case) INSTEAD of the internal
// CharacterControllerMovement strategy — exactly one thing may call
// CharacterController.Move or the motion doubles. You can also add it by hand
// to any GameObject that already has a CharacterController; nothing else is
// required (the RPG stats/AI layers are entirely optional here).

using UnityEngine;

namespace RPGCharacterStats
{
    [RequireComponent(typeof(CharacterController))]
    [DisallowMultipleComponent]
    [AddComponentMenu("RPG/Player Controller")]
    public class PlayerController : MonoBehaviour
    {
        [Header("Movement")]
        [Tooltip("Horizontal speed in m/s.")]
        public float moveSpeed = 5f;

        [Tooltip("Jump apex height in meters.")]
        public float jumpHeight = 1.2f;

        [Tooltip("Gravity acceleration in m/s² (negative pulls down).")]
        public float gravity = -20f;

        [Tooltip("Terminal fall speed in m/s so long drops stay sane.")]
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
        private PlayerCharacter _player;      // optional sibling on spawned players
        private float _verticalVelocity;

        protected virtual void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _player = GetComponent<PlayerCharacter>();
        }

        private void Update()
        {
            Tick(ReadInput());
        }

        /// <summary>One full simulation step for the given input. Public so
        /// headless tests and scripted sequences can drive the controller
        /// without going through real keyboard input.</summary>
        public void Tick(PlayerInput input)
        {
            if (_controller == null) _controller = GetComponent<CharacterController>();
            if (_controller == null) return;   // someone removed the CC — nothing to move

            // Horizontal wish direction, normalized so diagonals aren't faster.
            Vector2 wish = new Vector2(input.x, input.y);
            if (wish.sqrMagnitude > 1f) wish = wish.normalized;
            Vector3 motion = new Vector3(wish.x, 0f, wish.y) * moveSpeed * Time.deltaTime;

            // Vertical: the CharacterController is not a physics body, so
            // gravity is integrated by hand (section 7.3b) and folded into the
            // same Move call.
            if (_controller.isGrounded)
            {
                if (_verticalVelocity < 0f) _verticalVelocity = -2f;  // keep ground contact
                if (input.jumpHeld) _verticalVelocity = JumpVelocity();
            }
            _verticalVelocity += gravity * Time.deltaTime;
            if (_verticalVelocity < maxFallSpeed) _verticalVelocity = maxFallSpeed;
            motion.y = _verticalVelocity * Time.deltaTime;

            _controller.Move(motion);

            // The design doc's input adapter: the character's PlayerInput field
            // is filled from here each frame so other systems read one input.
            if (_player == null) _player = GetComponent<PlayerCharacter>();
            if (_player != null) _player.input = input;
        }

        /// <summary>Upward launch speed that reaches <see cref="jumpHeight"/>
        /// meters under <see cref="gravity"/>: v = √(2·h·|g|).</summary>
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
