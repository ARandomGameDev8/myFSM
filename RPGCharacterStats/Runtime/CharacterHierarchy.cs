// RPG Character & Stats System — the concrete character hierarchy (section 5)
// and the movement layer that goes with each kind.
//
// PlayerCharacter and NPCCharacter carry the behavior split from the design
// doc: input handling on the player, an AIInstance reference on the NPC. The
// movement is done by ONE MonoBehaviour per player — PlayerMovement — so the
// character is self-contained and the movement never disappears.
//
// Previous design: an invisible "Movement" strategy column (PhysicsPlayerMovement,
// CharacterControllerMovement, Kinematic2DMovement, NpcMovement), allocated per
// character. Those were invisible strategy classes next to the character and
// they vanished on Play → Stop because they were never script assets. The
// straightforward replacement is PlayerMovement, a MonoBehaviour + CharacterController
// that reads WASD/Space, applies gravity by hand, and calls CharacterController.Move.
// Nothing else moves the player.

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

        [Header("Player settings")]
        public float moveSpeed = 5f;
        public float jumpHeight = 1.2f;

        /// <summary>Filled each frame by the input adapter (or a test).</summary>
        public PlayerInput input;

        /// <summary>The single Movement script the character carries (added by
        /// the spawn pipeline, section 16). It does WASD + Space + gravity and
        /// CharacterController.Move — nothing else moves the player.</summary>
        public PlayerMovement movement;

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

        [Header("NPC settings")]
        public float moveSpeed = 3.5f;

        /// <summary>The AIInstance component the spawn pipeline attached (or a
        /// test-provided stand-in). The RPG layer only knows the contract:
        /// a component that ticks itself.</summary>
        public Component aiInstance;
    }

    public class EnemyCharacter : NPCCharacter
    {
    }

    public class FriendlyNPC : NPCCharacter
    {
    }
}
