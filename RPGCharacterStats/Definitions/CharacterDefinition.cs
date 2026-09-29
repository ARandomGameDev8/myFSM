// RPG Character & Stats System — definitions (sections 4.5, 5, 14).
//
// A CharacterDefinition is the saved, editable form of one character: identity
// (name/ID/description), the three configuration values (dimension, kind,
// physics mode), the .charstat/.gameplaystat text it uses, and the per-
// character stat values as overrides. The subclass tree mirrors section 5 —
// Player vs NPC, NPC splitting into Enemy and Friendly — with NPC definitions
// referencing an AIInstance subclass by assembly-qualified name.

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace RPGCharacterStats
{
    /// <summary>Section 14: "OrcWarrior_001". The registry hands out serial
    /// numbers per name so every spawned character gets a unique tag.</summary>
    [Serializable]
    public class CharacterTag
    {
        public string name;
        public int serialNumber;

        public CharacterTag() { }

        public CharacterTag(string name, int serialNumber)
        {
            this.name = name;
            this.serialNumber = serialNumber;
        }

        public override string ToString()
        {
            return name + "_" + serialNumber.ToString("000");
        }

        public static CharacterTag Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int underscore = text.LastIndexOf('_');
            if (underscore < 0) return new CharacterTag(text, 0);
            int serial;
            if (!int.TryParse(text.Substring(underscore + 1), out serial))
                return new CharacterTag(text, 0);
            return new CharacterTag(text.Substring(0, underscore), serial);
        }
    }

    /// <summary>Editor reference to a MonoBehaviour subclass (section 5's
    /// "AIInstance subclass" field). Stored as a string so definitions survive
    /// without the script present; the builder resolves it against the class
    /// list and the runtime validates it before attaching.</summary>
    [Serializable]
    public class SerializedType
    {
        public string typeName; // assembly-qualified-lite: "Namespace.Class, Assembly"

        public Type Resolve()
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            Type t = Type.GetType(typeName);
            if (t != null) return t;
            // Fallback: search every loaded assembly by full name (Unity wraps
            // scripts in Assembly-CSharp, so the plain name often resolves).
            string simple = typeName;
            int comma = simple.IndexOf(',');
            if (comma > 0) simple = simple.Substring(0, comma).Trim();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type found = assemblies[i].GetType(simple, false);
                if (found != null) return found;
            }
            return null;
        }
    }

    /// <summary>Section 4.5: the abstract saved character.</summary>
    [Serializable]
    public abstract class CharacterDefinition : ScriptableObject
    {
        [Header("Identity")]
        public string characterID = "";
        public string characterName = "";
        [TextArea(3, 6)] public string description;

        [Header("Configuration (section 2.5)")]
        public CharacterDimension dimension = CharacterDimension.ThreeD;
        public CharacterKind kind = CharacterKind.NPC;
        public PhysicsMode physicsMode = PhysicsMode.PhysicsBased;

        [Header("Stat sources")]
        [Multiline(10)] public string charStatText = "";
        [Multiline(12)] public string gameplayStatText = "";

        public List<StatValueOverride> statValues = new List<StatValueOverride>();

        public abstract CharacterKind Kind { get; }
    }

    /// <summary>A playable character definition.</summary>
    [CreateAssetMenu(fileName = "Player", menuName = "RPG/Player Character Definition", order = 10)]
    [Serializable]
    public class PlayerCharacterDefinition : CharacterDefinition
    {
        [Header("Player options")]
        public string inputAxisPrefix = ""; // optional custom input prefix

        public override CharacterKind Kind { get { return CharacterKind.Player; } }
    }

    /// <summary>Base for AI-driven characters; carries the AIInstance subclass
    /// reference (section 5: "NPCCharacterDefinition aiInstanceType").</summary>
    public abstract class NPCCharacterDefinition : CharacterDefinition
    {
        [Header("NPC AI")]
        public SerializedType aiInstanceType = new SerializedType();

        public override CharacterKind Kind { get { return CharacterKind.NPC; } }
    }

    [CreateAssetMenu(fileName = "Enemy", menuName = "RPG/Enemy Character Definition", order = 11)]
    [Serializable]
    public class EnemyCharacterDefinition : NPCCharacterDefinition
    {
    }

    [CreateAssetMenu(fileName = "FriendlyNPC", menuName = "RPG/Friendly NPC Definition", order = 12)]
    [Serializable]
    public class FriendlyNPCDefinition : NPCCharacterDefinition
    {
    }
}
