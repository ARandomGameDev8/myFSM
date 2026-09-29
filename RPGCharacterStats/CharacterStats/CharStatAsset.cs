// RPG Character & Stats System — StatValueOverride and the ScriptableObject
// wrappers that live inside Unity projects.
//
// StatValueOverride is section 4.5's per-character value record. CharStatAsset
// wraps a .charstat schema as an asset so builders and definitions can drag it
// around; the canonical text is kept on the asset so the file stays readable
// and diffable, exactly like a hand-written .charstat.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPGCharacterStats
{
    /// <summary>Section 4.5: one per-character value overriding a schema
    /// default. Type travels with the record so the builder can render the
    /// right editor control without consulting the schema.</summary>
    [Serializable]
    public class StatValueOverride
    {
        public string statName;
        public StatType type;

        public float floatValue;
        public int intValue;
        public bool boolValue;

        public static StatValueOverride Float(string name, float value)
        {
            return new StatValueOverride { statName = name, type = StatType.Float, floatValue = value };
        }

        public static StatValueOverride Int(string name, int value)
        {
            return new StatValueOverride { statName = name, type = StatType.Int, intValue = value };
        }

        public static StatValueOverride Bool(string name, bool value)
        {
            return new StatValueOverride { statName = name, type = StatType.Bool, boolValue = value };
        }
    }

#if UNITY_2018_1_OR_NEWER
    /// <summary>A .charstat schema as a project asset. Create via the Character
    /// Stats Builder or Assets ▸ Create ▸ RPG ▸ Character Stat Schema.</summary>
    [CreateAssetMenu(fileName = "RPGStats", menuName = "RPG/Character Stat Schema", order = 0)]
    public class CharStatAsset : ScriptableObject
    {
        [Multiline(12)] public string schemaText;

        public StatSchema Schema
        {
            get { return string.IsNullOrEmpty(schemaText) ? new StatSchema() : CharStatFormat.Parse(schemaText); }
        }
    }

    /// <summary>A .gameplaystat formula set as a project asset. The canonical
    /// text stays parseable by GameplayStatFormat (section 9).</summary>
    [CreateAssetMenu(fileName = "RPGGameplay", menuName = "RPG/Gameplay Stat Formulas", order = 1)]
    public class GameplayStatAsset : ScriptableObject
    {
        [Multiline(16)] public string formulaText;
    }
#endif
}
