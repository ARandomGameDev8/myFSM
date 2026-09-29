// RPG Character & Stats System — Item and Stats: the two abstract bases the
// design document fixes in section 4. Item is inventory vocabulary; Stats is
// the abstract surface every stat container exposes (schema-derived or
// gameplay-derived). Kept here because GameplayStats extends Stats directly.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPGCharacterStats
{
    /// <summary>Section 4.1: the abstract inventory base. The RPG framework
    /// never instantiates it directly — concrete games subclass it with their
    /// own item kinds.</summary>
    [Serializable]
    public abstract class Item
    {
        public string id;
        public string name;
        [TextArea(3, 6)] public string description;
        public Sprite icon;
        public bool isStackable;
        public float weight;
    }

    /// <summary>Section 4.2: a named collection of StatFields plus the abstract
    /// read/refresh surface every stats container provides. Recalculate() is
    /// what makes CharacterStats (apply defaults + overrides) and GameplayStats
    /// (run compiled formulas in topological order) different.</summary>
    [Serializable]
    public abstract class Stats
    {
        public List<StatField> fields = new List<StatField>();

        public abstract void Recalculate();

        public abstract float GetFloat(string name);
        public abstract int GetInt(string name);
        public abstract bool GetBool(string name);

        /// <summary>Case-sensitive exact lookup; null when absent. Shared by
        /// all containers so callers need one TryGet idiom.</summary>
        public StatField Find(string name)
        {
            if (fields == null || name == null) return null;
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i] != null && fields[i].name == name) return fields[i];
            }
            return null;
        }
    }
}
