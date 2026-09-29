// RPG Character & Stats System — StatField: the one value holder everywhere.
//
// Section 4.3 of the design document: a single named slot carrying a Float,
// Int, or Bool value. Stat schemas (Stats), character overrides
// (StatValueOverride), the blackboard, and gameplay-stat outputs all reuse
// this class so the recalc pipeline can hand the SAME object to every
// consumer. Deliberately a class, not a struct: the FSM blackboard wants a
// stable reference it can read through OnStatChanged listeners, and the
// GameplayStatClient writes its result straight into the field it owns.

using System;

namespace RPGCharacterStats
{
    [Serializable]
    public class StatField
    {
        public string name;
        public StatType type;

        // All three live side by side (like the design doc shows); the
        // accessors below pick the right one for the field's type.
        public float floatValue;
        public int intValue;
        public bool boolValue;

        public StatField() { }

        public StatField(string name, StatType type)
        {
            this.name = name;
            this.type = type;
        }

        /// <summary>Raw float storage, regardless of the declared type — the
        /// formula compiler evaluates Int math in float space and converts
        /// only at assignment time (mirrors the myFSM DSL's FsmValue model).</summary>
        public float AsFloat
        {
            get { return floatValue; }
            set { floatValue = value; }
        }

        public int AsInt
        {
            get { return type == StatType.Int ? intValue : (int)floatValue; }
            set { intValue = value; }
        }

        public bool AsBool
        {
            get { return boolValue; }
            set { boolValue = value; }
        }

        /// <summary>Read the field as the formula runtime sees it: floats and
        /// ints collapse to float; bools become 0/1. Bools keep their own
        /// accessor because a Bool stat is never numerically assigned.</summary>
        public float NumericValue
        {
            get
            {
                switch (type)
                {
                    case StatType.Int: return intValue;
                    case StatType.Bool: return boolValue ? 1f : 0f;
                    default: return floatValue;
                }
            }
        }

        public bool BoolValue
        {
            get { return type == StatType.Bool ? boolValue : NumericValue != 0f; }
        }

        /// <summary>Write a value with automatic conversion toward the field's
        /// declared type (float→int truncates, anything→bool is != 0).</summary>
        public void SetFromNumber(float value)
        {
            switch (type)
            {
                case StatType.Int: intValue = (int)value; break;
                case StatType.Bool: boolValue = value != 0f; break;
                default: floatValue = value; break;
            }
        }

        public void CopyFrom(StatField other)
        {
            if (other == null) return;
            type = other.type;
            floatValue = other.floatValue;
            intValue = other.intValue;
            boolValue = other.boolValue;
        }

        public override string ToString()
        {
            switch (type)
            {
                case StatType.Int: return name + " = " + intValue;
                case StatType.Bool: return name + " = " + (boolValue ? "true" : "false");
                default: return name + " = " + floatValue;
            }
        }
    }
}
