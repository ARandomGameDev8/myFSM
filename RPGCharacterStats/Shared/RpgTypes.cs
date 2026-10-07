// RPG Character & Stats System — the vocabulary every component speaks.
//
// The three per-character configuration values from section 2.5 of the design
// document (dimension, kind, physics mode) plus the stat value type shared by
// schemas, overrides, and the formula language. Engine-neutral on purpose:
// nothing here may reference UnityEngine.

namespace RPGCharacterStats
{
    /// <summary>Which plane a character lives on. Drives the physics, collision,
    /// and movement layer only (section 2.5) — stats, AI, and UI are agnostic.</summary>
    public enum CharacterDimension
    {
        TwoD = 0,
        ThreeD = 1,
    }

    /// <summary>Player or NPC. Decides input handling vs. an AIInstance subclass
    /// and whether a CharacterController may be used (3D non-physics Player).</summary>
    public enum CharacterKind
    {
        Player = 0,
        NPC = 1,
    }

    /// <summary>PhysicsBased turns gravity on; NonPhysics turns it off and (in
    /// 3D, for a Player) makes the rigidbody kinematic so CharacterController
    /// and Rigidbody never fight (section 2.5 implementation notes).</summary>
    public enum PhysicsMode
    {
        PhysicsBased = 0,
        NonPhysics = 1,
    }

    /// <summary>The render-only 3D body chosen in the Character Builder.
    /// It does not replace or change the character's physics collider.</summary>
    public enum CharacterVisual3D
    {
        Cube = 0,
        Capsule = 1,
        Model = 2,
    }

    /// <summary>Camera behavior for 3D player characters. DoNotAlter is the
    /// compatibility default and leaves all scene cameras untouched.</summary>
    public enum CharacterCameraMode
    {
        DoNotAlter = 0,
        FirstPerson = 1,
        ThirdPerson = 2,
    }

    /// <summary>The value kind of a stat field. The formula language, the
    /// schema, the overrides, and the blackboard all speak this one enum.</summary>
    public enum StatType
    {
        Float = 0,
        Int = 1,
        Bool = 2,
    }
}
