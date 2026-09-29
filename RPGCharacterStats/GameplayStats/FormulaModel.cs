// RPG Character & Stats System — the data model between parser and runtime.
//
// FormulaSignature is what the checker distills from the formula text (section
// 10.3: the text is the source of truth). The SecondaryInput subclasses are
// section 8.2's five input kinds — the checker classifies each secondary
// parameter into one of them, and the serialized GameplayStatFormula (section
// 8.1) keeps the result next to the original text.

using System;
using System.Collections.Generic;

namespace RPGCharacterStats
{
    /// <summary>Where a secondary parameter's value comes from at runtime.
    /// Resolution order (section 9.1's spirit — the most direct read wins):
    /// character stat → gameplay stat → blackboard → constant.</summary>
    public enum SecondaryInputKind
    {
        CharacterStat,
        GameplayStat,
        Blackboard,
        Constant,
    }

    /// <summary>Section 8.2 base: one named, typed input of a formula.</summary>
    [Serializable]
    public abstract class SecondaryInput
    {
        public string name;
        public StatType type;

        /// <summary>How the runtime resolves this input; Concrete subclasses
        /// carry the extra fields the design doc gives each kind.</summary>
        public abstract SecondaryInputKind Kind { get; }
    }

    /// <summary>A fixed value supplied per formula (defaults to this until a
    /// definition or the blackboard overrides it).</summary>
    [Serializable]
    public class ConstantInput : SecondaryInput
    {
        public float value;

        public override SecondaryInputKind Kind { get { return SecondaryInputKind.Constant; } }
    }

    /// <summary>Reads a stat straight from the character's CharacterStats.</summary>
    [Serializable]
    public class CharacterStatInput : SecondaryInput
    {
        public string statName;

        public override SecondaryInputKind Kind { get { return SecondaryInputKind.CharacterStat; } }
    }

    /// <summary>Reads the OUTPUT of another gameplay stat — the only way
    /// formulas depend on each other (drives the dependency graph).</summary>
    [Serializable]
    public class GameplayStatInput : SecondaryInput
    {
        public string gameplayStatName;

        public override SecondaryInputKind Kind { get { return SecondaryInputKind.GameplayStat; } }
    }

    /// <summary>Reads a runtime blackboard variable (section 13) — the home of
    /// temporary modifiers like a buff multiplier.</summary>
    [Serializable]
    public class BlackboardInput : SecondaryInput
    {
        public string blackboardKey;

        public override SecondaryInputKind Kind { get { return SecondaryInputKind.Blackboard; } }
    }

    /// <summary>Reads a member of an external Unity object (section 8.2's
    /// "adapter-like" bindings, e.g. a day/night cycle's current hour). Bound
    /// through Blackboard.Bind at runtime.</summary>
    [Serializable]
    public class ExternalBindInput : SecondaryInput
    {
        public UnityEngine.Object targetObject;
        public string memberPath;

        public override SecondaryInputKind Kind { get { return SecondaryInputKind.Blackboard; } }
    }

    /// <summary>The distilled signature of one formula (section 10.3).</summary>
    [Serializable]
    public class FormulaSignature
    {
        public string name;
        public StatType returnType;

        public string primaryName;
        public StatType primaryType;

        public List<SecondaryInput> secondaries = new List<SecondaryInput>();
    }

    /// <summary>Section 8.1: one serialized gameplay stat — the model the
    /// .gameplaystat format round-trips and the builder edits.</summary>
    [Serializable]
    public class GameplayStatFormula
    {
        public string name;
        public StatType type;
        public string primaryInput;          // "float Vitality"
        public List<SecondaryInput> secondaryInputs = new List<SecondaryInput>();
        public string formulaExpression;     // "(primary ...) => { ... }" text

        public FormulaSignature ToSignature()
        {
            FormulaSignature s = new FormulaSignature();
            s.name = name;
            s.returnType = type;
            s.primaryName = null;
            s.primaryType = StatType.Float;
            // primaryInput is "type name" — split it back apart.
            if (!string.IsNullOrEmpty(primaryInput))
            {
                string trimmed = primaryInput.Trim();
                int space = trimmed.LastIndexOf(' ');
                if (space > 0)
                {
                    string typeWord = trimmed.Substring(0, space).Trim().ToLowerInvariant();
                    s.primaryType = typeWord == "int" ? StatType.Int
                        : typeWord == "bool" ? StatType.Bool : StatType.Float;
                    s.primaryName = trimmed.Substring(space + 1).Trim();
                }
                else
                {
                    s.primaryName = trimmed;
                }
            }
            if (secondaryInputs != null) s.secondaries = secondaryInputs;
            return s;
        }
    }
}
