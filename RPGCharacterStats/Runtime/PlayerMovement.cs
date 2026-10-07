// RPG Character & Stats System — the single movement component a spawned
// player carries. Reads WASD/Space and drives WHATEVER body the spawn pipeline
// gave the player: CharacterController.Move for the 3D non-physics case,
// Rigidbody / Rigidbody2D otherwise. The default camera mode retains the old
// camera-independent movement; optional 3D player modes add first/third-person
// mouse-look and camera-relative movement without touching the default path.
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

        [Header("Camera (optional; DoNotAlter keeps the legacy movement path)")]
        public CharacterCameraMode cameraMode = CharacterCameraMode.DoNotAlter;
        public float mouseSensitivity = 2f;
        public float firstPersonEyeHeight = 1.65f;
        public float thirdPersonDistance = 5f;
        public float thirdPersonLookHeight = 1.4f;

        private static PlayerMovement _cameraOwner;
        private CharacterController _controller;
        private Rigidbody _body3D;
        private Rigidbody2D _body2D;
        private Camera _viewCamera;
        private float _verticalVelocity;
        private float _yaw;
        private float _pitch;
        private bool _createdViewCamera;
        private bool _cameraStateCaptured;
        private Quaternion _pendingBodyRotation;
        private bool _hasPendingBodyRotation;
        private Vector3 _originalCameraPosition;
        private Quaternion _originalCameraRotation;
        private float _originalCameraFieldOfView;
        private bool _cursorStateCaptured;
        private CursorLockMode _originalCursorLockState;
        private bool _originalCursorVisible;

        public Camera ViewCamera { get { return _viewCamera; } }

        private void Awake()
        {
            ResolveBodies();
            if (cameraMode != CharacterCameraMode.DoNotAlter) ActivateCameraRig();
        }

        private void Start()
        {
            if (cameraMode != CharacterCameraMode.DoNotAlter) ActivateCameraRig();
        }

        private void OnDisable()
        {
            ReleaseCameraRig();
        }

        private void OnDestroy()
        {
            ReleaseCameraRig();
        }

        private void ResolveBodies()
        {
            if (_controller == null) _controller = GetComponent<CharacterController>();
            if (_body2D == null) _body2D = GetComponent<Rigidbody2D>();
            if (_body3D == null) _body3D = GetComponent<Rigidbody>();
        }

        private void Update()
        {
            if (cameraMode == CharacterCameraMode.DoNotAlter)
            {
                if (_cameraOwner == this) ReleaseCameraRig();
            }
            else
            {
                // Only acquire an unowned camera during Update. If another
                // player owns it, do not ping-pong the scene camera each frame.
                if (_cameraOwner == null) ActivateCameraRig();
                UpdateCameraLook();
            }
            Tick(ReadInput());
        }

        private void LateUpdate()
        {
            ApplyCameraPose();
        }

        private void FixedUpdate()
        {
            if (!_hasPendingBodyRotation || _body3D == null || _body3D.isKinematic) return;
            _body3D.MoveRotation(_pendingBodyRotation);
            _hasPendingBodyRotation = false;
        }

        /// <summary>Called by the spawn factory after creating the movement
        /// component. DoNotAlter intentionally performs no camera lookup or
        /// scene mutation and preserves the original world-axis movement.</summary>
        public void ConfigureCameraMode(CharacterCameraMode mode)
        {
            CharacterCameraMode previousMode = cameraMode;
            cameraMode = mode;
            if (mode == CharacterCameraMode.DoNotAlter)
            {
                if (_cameraOwner == this) ReleaseCameraRig();
                return;
            }
            if (!Application.isPlaying) return;
            if (_cameraOwner == this && _viewCamera != null)
            {
                if (previousMode != mode)
                    _pitch = mode == CharacterCameraMode.ThirdPerson ? 15f : 0f;
                ApplyCameraPose();
                return;
            }
            ActivateCameraRig();
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
            Vector3 motion = Get3DWish(input) * moveSpeed * Time.deltaTime;

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
            Vector3 wish = Get3DWish(input);

            Vector3 v = _body3D.velocity;
            v.x = wish.x * moveSpeed;
            v.z = wish.z * moveSpeed;
            if (input.jumpHeld && v.y <= 0.01f) v.y = JumpVelocity();
            _body3D.velocity = v;
        }

        private Vector3 Get3DWish(PlayerInput input)
        {
            Vector2 wish = new Vector2(input.x, input.y);
            if (wish.sqrMagnitude > 1f) wish = wish.normalized;
            Vector3 localWish = new Vector3(wish.x, 0f, wish.y);
            if (cameraMode == CharacterCameraMode.DoNotAlter) return localWish;
            return Quaternion.Euler(0f, _yaw, 0f) * localWish;
        }

        private void ActivateCameraRig()
        {
            if (!Application.isPlaying || cameraMode == CharacterCameraMode.DoNotAlter) return;
            if (_cameraOwner == this && _viewCamera != null) return;

            if (_cameraOwner != null && _cameraOwner != this)
                _cameraOwner.ReleaseCameraRig();

            _cameraOwner = this;
            _yaw = transform.eulerAngles.y;
            _pitch = cameraMode == CharacterCameraMode.ThirdPerson ? 15f : 0f;

            _viewCamera = Camera.main;
            _createdViewCamera = _viewCamera == null;
            if (_viewCamera == null)
            {
                GameObject cameraObject = new GameObject("RPG Player Camera");
                cameraObject.tag = "MainCamera";
                _viewCamera = cameraObject.AddComponent<Camera>();
            }
            else
            {
                _originalCameraPosition = _viewCamera.transform.position;
                _originalCameraRotation = _viewCamera.transform.rotation;
                _originalCameraFieldOfView = _viewCamera.fieldOfView;
                _cameraStateCaptured = true;
            }

            _originalCursorLockState = Cursor.lockState;
            _originalCursorVisible = Cursor.visible;
            _cursorStateCaptured = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            ApplyCameraPose();
        }

        private void UpdateCameraLook()
        {
            if (_cameraOwner != this || _viewCamera == null) return;
            _yaw += Input.GetAxis("Mouse X") * mouseSensitivity;
            _pitch -= Input.GetAxis("Mouse Y") * mouseSensitivity;
            _pitch = Mathf.Clamp(_pitch, -80f, 80f);

            // The player body follows the view heading; the camera's pitch is
            // kept separate so the character never tips forward/backward. A
            // dynamic Rigidbody turns in FixedUpdate through MoveRotation.
            Quaternion playerRotation = Quaternion.Euler(0f, _yaw, 0f);
            if (_body3D != null && !_body3D.isKinematic)
            {
                _pendingBodyRotation = playerRotation;
                _hasPendingBodyRotation = true;
            }
            else
            {
                transform.rotation = playerRotation;
            }
        }

        private void ApplyCameraPose()
        {
            if (_cameraOwner != this || _viewCamera == null) return;
            Quaternion lookRotation = Quaternion.Euler(_pitch, _yaw, 0f);
            if (cameraMode == CharacterCameraMode.FirstPerson)
            {
                _viewCamera.transform.position = transform.position + Vector3.up * firstPersonEyeHeight;
            }
            else if (cameraMode == CharacterCameraMode.ThirdPerson)
            {
                Vector3 focus = transform.position + Vector3.up * thirdPersonLookHeight;
                Vector3 forward = lookRotation * Vector3.forward;
                _viewCamera.transform.position = focus - forward * thirdPersonDistance;
            }
            _viewCamera.transform.rotation = lookRotation;
        }

        private void ReleaseCameraRig()
        {
            if (_cameraOwner != this) return;

            if (_viewCamera != null)
            {
                if (_createdViewCamera)
                {
                    // Keep Camera.main from returning a camera scheduled for
                    // destruction if a different player takes over this frame.
                    _viewCamera.gameObject.tag = "Untagged";
                    Destroy(_viewCamera.gameObject);
                }
                else if (_cameraStateCaptured)
                {
                    _viewCamera.transform.position = _originalCameraPosition;
                    _viewCamera.transform.rotation = _originalCameraRotation;
                    _viewCamera.fieldOfView = _originalCameraFieldOfView;
                }
            }

            if (_cursorStateCaptured)
            {
                Cursor.lockState = _originalCursorLockState;
                Cursor.visible = _originalCursorVisible;
            }

            _viewCamera = null;
            _cameraOwner = null;
            _createdViewCamera = false;
            _cameraStateCaptured = false;
            _cursorStateCaptured = false;
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
