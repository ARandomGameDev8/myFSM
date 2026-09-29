// RPG Character & Stats System — the concrete character hierarchy (section 5)
// and the movement layer that goes with each kind.
//
// PlayerCharacter and NPCCharacter (with Enemy/Friendly leaves) carry the
// behavior split from the design doc: input handling on the player, an
// AIInstance reference on the NPC. The movement controllers below implement
// the section 2.5 matrix — physics movement on a body with gravity, a
// CharacterController for a 3D non-physics Player, and a kinematic Rigidbody2D
// controller for the 2D equivalent Unity doesn't ship.

using UnityEngine;

namespace RPGCharacterStats
{
    /// <summary>One axis read per frame, already normalized: the input vector
    /// the player's movement controller consumes. Sandbox tests drive this
    /// directly; in Unity a small input adapter fills it.</summary>
    public struct PlayerInput
    {
        public float x; // -1..1 (A/D, left/right)
        public float y; // -1..1 (W/S, up/forward)
        public bool jumpHeld;
    }

    // ------------------------------------------------------------------
    // Players
    // ------------------------------------------------------------------

    public class PlayerCharacter : Character
    {
        public override CharacterKind Kind { get { return CharacterKind.Player; } }

        [Header("Player movement")]
        public float moveSpeed = 5f;
        public float jumpSpeed = 6f;

        /// <summary>Filled each frame by the input adapter (or a test).</summary>
        public PlayerInput input;

        /// <summary>The movement strategy picked at spawn (section 2.5's
        /// "Movement" column).</summary>
        public ICharacterMovement movement;

        protected virtual void Update()
        {
            if (movement != null) movement.Tick(this, input);
        }

        /// <summary>Level-up hook (section 5's "level up logic"): bump Level,
        /// let the gameplay stats cascade — XP thresholds etc. recalculate on
        /// their own through the section 19 event flow.</summary>
        public void LevelUp(int newLevel)
        {
            if (characterStats == null) return;
            if (characterStats.Find("Level") != null) characterStats.SetInt("Level", newLevel);
            else characterStats.SetFloat("Level", newLevel);
        }
    }

    // ------------------------------------------------------------------
    // NPCs
    // ------------------------------------------------------------------

    public abstract class NPCCharacter : Character
    {
        public override CharacterKind Kind { get { return CharacterKind.NPC; } }

        [Header("NPC movement")]
        public float moveSpeed = 3.5f;

        /// <summary>The AIInstance component the spawn pipeline attached (or a
        /// test-provided stand-in). The RPG layer only knows the contract:
        /// a component that ticks itself.</summary>
        public Component aiInstance;

        /// <summary>The movement strategy driven by the AI (physics velocity
        /// for physics-based NPCs, transform steps otherwise).</summary>
        public ICharacterMovement movement;

        protected virtual void Update()
        {
            if (movement != null) movement.Tick(this, new PlayerInput());
        }
    }

    public class EnemyCharacter : NPCCharacter
    {
    }

    public class FriendlyNPC : NPCCharacter
    {
    }

    // ------------------------------------------------------------------
    // Movement strategies (the "Movement" column of the 2.5 matrix)
    // ------------------------------------------------------------------

    /// <summary>A per-tick movement step for a character. Implementations are
    /// the strategy pattern (section 20.3) applied to locomotion.</summary>
    public interface ICharacterMovement
    {
        void Tick(Character character, PlayerInput input);
    }

    /// <summary>Player + PhysicsBased: write a velocity, let the simulation
    /// move the body. Works for both dimensions.</summary>
    public class PhysicsPlayerMovement : ICharacterMovement
    {
        public void Tick(Character character, PlayerInput input)
        {
            Vector3 wish = new Vector3(input.x, 0f, input.y);
            if (wish.sqrMagnitude > 1f) wish = wish.normalized;

            if (character.dimension == CharacterDimension.TwoD)
            {
                Rigidbody2D body = character.rigidbody2D;
                if (body == null) return;
                Vector2 v = body.velocity;
                v.x = wish.x * character.GetComponent<PlayerCharacter>().moveSpeed;
                if (input.jumpHeld) v.y = character.GetComponent<PlayerCharacter>().jumpSpeed;
                body.velocity = v;
            }
            else
            {
                Rigidbody3DContainer body = new Rigidbody3DContainer(character.rigidbody3D);
                if (!body.Valid) return;
                Vector3 v = body.Velocity;
                v.x = wish.x * character.GetComponent<PlayerCharacter>().moveSpeed;
                v.z = wish.z * character.GetComponent<PlayerCharacter>().moveSpeed;
                if (input.jumpHeld && character.rigidbody3D != null && Mathf.Abs(v.y) < 0.01f)
                    v.y = character.GetComponent<PlayerCharacter>().jumpSpeed;
                body.Velocity = v;
            }
        }
    }

    /// <summary>3D non-physics Player: Unity's CharacterController, gravity
    /// applied by hand (it isn't a physics body) per section 7.3b.</summary>
    public class CharacterControllerMovement : ICharacterMovement
    {
        public float gravity = 12f;

        public void Tick(Character character, PlayerInput input)
        {
            CharacterController cc = character.controller;
            if (cc == null) return;

            Vector3 wish = new Vector3(input.x, 0f, input.y);
            if (wish.sqrMagnitude > 1f) wish = wish.normalized;

            Vector3 motion = wish * character.GetComponent<PlayerCharacter>().moveSpeed * Time.deltaTime;
            motion.y = -gravity * Time.deltaTime; // stick to the ground; a real game adds jump state
            cc.Move(motion);
        }
    }

    /// <summary>2D non-physics Player: kinematic Rigidbody2D (gravity 0),
    /// MovePosition per tick — Unity has no 2D CharacterController.</summary>
    public class Kinematic2DMovement : ICharacterMovement
    {
        public void Tick(Character character, PlayerInput input)
        {
            Rigidbody2D body = character.rigidbody2D;
            if (body == null) return;

            Vector2 wish = new Vector2(input.x, input.y);
            if (wish.sqrMagnitude > 1f) wish = wish.normalized;

            Vector2 target = body.position + wish * character.GetComponent<PlayerCharacter>().moveSpeed * Time.deltaTime;
            body.MovePosition(target);
        }
    }

    /// <summary>NPC movement: a step toward a goal the AI sets. Physics-based
    /// NPCs write velocity (the simulation owns the body); non-physics NPCs
    /// translate or MovePosition directly. The myFSM AIInstance on the same
    /// GameObject uses its own MovementSystem — this one is for games without
    /// it — but both respect the same dimension/physics matrix.</summary>
    public class NpcMovement : ICharacterMovement
    {
        /// <summary>World point the NPC walks toward this tick; AI or test sets it.</summary>
        public Vector3 goal;

        public void Tick(Character character, PlayerInput input)
        {
            Vector3 position = character.transform.position;
            Vector3 toGoal = goal - position;
            toGoal.y = 0f;
            if (toGoal.sqrMagnitude < 1e-6f) return;

            float speed = character.GetComponent<NPCCharacter>().moveSpeed;
            Vector3 step = Vector3.MoveTowards(position, goal, speed * Time.deltaTime);

            if (character.dimension == CharacterDimension.TwoD)
            {
                Rigidbody2D body = character.rigidbody2D;
                if (body == null) return;
                if (character.physicsMode == PhysicsMode.PhysicsBased)
                {
                    Vector2 v = body.velocity;
                    v.x = (step - position).x / Mathf.Max(Time.deltaTime, 1e-5f);
                    body.velocity = v;
                }
                else
                {
                    body.MovePosition(new Vector2(step.x, step.y));
                }
            }
            else
            {
                Rigidbody body = character.rigidbody3D;
                if (body == null) return;
                if (character.physicsMode == PhysicsMode.PhysicsBased)
                {
                    Vector3 v = body.velocity;
                    v.x = (step - position).x / Mathf.Max(Time.deltaTime, 1e-5f);
                    v.z = (step - position).z / Mathf.Max(Time.deltaTime, 1e-5f);
                    body.velocity = v;
                }
                else
                {
                    body.MovePosition(step);
                }
            }
        }
    }

    /// <summary>Tiny wrapper so PhysicsPlayerMovement can handle a null
    /// rigidbody3D cleanly in C# 7.3 without NullConditional suggestions.</summary>
    internal struct Rigidbody3DContainer
    {
        public readonly Rigidbody Body;

        public Rigidbody3DContainer(Rigidbody body) { Body = body; }

        public bool Valid { get { return Body != null; } }

        public Vector3 Velocity
        {
            get { return Body != null ? Body.velocity : Vector3.zero; }
            set { if (Body != null) Body.velocity = value; }
        }
    }
}
