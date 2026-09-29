// RPG Character & Stats System — the "AIInstance" contract (section 7.4).
//
// NPC definitions reference an AI script by type. The design doc is explicit
// about what counts as a valid one:
//   • it must derive, directly or indirectly, from a class NAMED "AIInstance"
//     (any namespace, any assembly — the name is the contract, not the type);
//   • it must be declared `partial`.
// Reflection cannot see the `partial` modifier (it's a compile-time detail:
// partial classes compile into one normal type), so the source-text check is
// an explicit input. The Character Builder feeds it the script's text from
// the asset database; the runtime server validates derivation only.

using System;
using System.Text.RegularExpressions;

namespace RPGCharacterStats
{
    public static class AIInstanceContract
    {
        public const string BaseName = "AIInstance";

        /// <summary>Walk the base-type chain looking for a class named
        /// "AIInstance" (section 7.4: "directly or indirectly").</summary>
        public static bool DerivesFromAIInstance(Type t)
        {
            if (t == null) return false;
            Type baseType = t.BaseType;
            while (baseType != null)
            {
                if (baseType.Name == BaseName) return true;
                baseType = baseType.BaseType;
            }
            return false;
        }

        /// <summary>True when ANY class declaration in the source carries the
        /// partial modifier. Deliberately loose: nested/partial declarations
        /// across files all count — the strict per-class check is the
        /// builder's job, this is the guard.</summary>
        public static bool LooksPartial(string sourceText)
        {
            if (string.IsNullOrEmpty(sourceText)) return false;
            return Regex.IsMatch(sourceText, @"\bpartial\s+(?:sealed\s+|abstract\s+|static\s+)*class\b",
                RegexOptions.Multiline);
        }

        /// <summary>Full validation. Returns null when the type satisfies the
        /// contract, otherwise the error message the builder blocks with.</summary>
        public static string Validate(Type aiType, string sourceText)
        {
            if (aiType == null) return "no AI script assigned";
            if (aiType.IsAbstract) return aiType.Name + " is abstract — pick a concrete AIInstance subclass";
            if (!DerivesFromAIInstance(aiType))
                return aiType.Name + " does not inherit from a class named \"" + BaseName + "\"";
            if (sourceText != null && !LooksPartial(sourceText))
                return aiType.Name + " must be declared \"partial\" (section 7.4)";
            if (aiType.GetConstructor(Type.EmptyTypes) == null)
                return aiType.Name + " needs a parameterless constructor so the server can attach it";
            return null;
        }
    }
}
