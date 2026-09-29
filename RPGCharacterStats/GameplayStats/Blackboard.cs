// RPG Character & Stats System — the Blackboard (section 13).
//
// Runtime variables a formula can consume as secondary inputs: temporary
// modifiers (a buff multiplier), external system bindings (the in-game hour),
// anything that shouldn't live in CharacterStats. Variables are typed like
// stat fields, and an externally-bound variable pulls its value from a member
// of a Unity object on UpdateExternalBindings() — section 21's "adapter-like"
// behavior, kept behind one method so nothing else touches reflection.

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace RPGCharacterStats
{
    [Serializable]
    public class BlackboardVariable
    {
        public string name;
        public StatType type;

        public float runtimeFloat;
        public int runtimeInt;
        public bool runtimeBool;

        public bool isExternallyBound;
        public UnityEngine.Object boundObject;
        public string boundMemberPath;

        public float Numeric
        {
            get
            {
                switch (type)
                {
                    case StatType.Int: return runtimeInt;
                    case StatType.Bool: return runtimeBool ? 1f : 0f;
                    default: return runtimeFloat;
                }
            }
            set { SetFromNumber(value); }
        }

        public void SetFromNumber(float value)
        {
            switch (type)
            {
                case StatType.Int: runtimeInt = (int)value; break;
                case StatType.Bool: runtimeBool = value != 0f; break;
                default: runtimeFloat = value; break;
            }
        }
    }

    [Serializable]
    public class Blackboard
    {
        public List<BlackboardVariable> variables = new List<BlackboardVariable>();

        public BlackboardVariable Find(string name)
        {
            if (name == null) return null;
            for (int i = 0; i < variables.Count; i++)
            {
                if (variables[i] != null && variables[i].name == name) return variables[i];
            }
            return null;
        }

        public BlackboardVariable Declare(string name, StatType type)
        {
            BlackboardVariable existing = Find(name);
            if (existing != null) return existing;
            BlackboardVariable v = new BlackboardVariable { name = name, type = type };
            variables.Add(v);
            return v;
        }

        // ---- section 13 operations ----

        public float Get(string name)
        {
            BlackboardVariable v = Find(name);
            if (v == null) throw new KeyNotFoundException("no blackboard variable named \"" + name + "\"");
            return v.Numeric;
        }

        public void Set(string name, float value)
        {
            BlackboardVariable v = Find(name);
            if (v == null) throw new KeyNotFoundException("no blackboard variable named \"" + name + "\"");
            v.Numeric = value;
        }

        /// <summary>Bind a variable to a member of a Unity object ("HP" on a
        /// component, "hour" on a clock). The member is read lazily by
        /// UpdateExternalBindings, so no per-frame wiring is needed.</summary>
        public void Bind(string name, UnityEngine.Object target, string memberPath)
        {
            BlackboardVariable v = Find(name);
            if (v == null) throw new KeyNotFoundException("no blackboard variable named \"" + name + "\"");
            v.isExternallyBound = target != null && !string.IsNullOrEmpty(memberPath);
            v.boundObject = target;
            v.boundMemberPath = memberPath;
        }

        /// <summary>Pull every externally-bound variable from its target.
        /// Field or property, walked along a dotted path ("stats.MaxHP").
        /// Unresolvable bindings keep their last value and warn once.</summary>
        public void UpdateExternalBindings()
        {
            for (int i = 0; i < variables.Count; i++)
            {
                BlackboardVariable v = variables[i];
                if (v == null || !v.isExternallyBound || v.boundObject == null) continue;

                object current = v.boundObject;
                MemberInfo member = null;
                string[] parts = (v.boundMemberPath ?? "").Split('.');
                for (int p = 0; p < parts.Length && current != null; p++)
                {
                    member = FindMember(current.GetType(), parts[p]);
                    if (member == null) break;
                    current = GetValue(member, current);
                }

                if (member == null || current == null)
                {
                    Debug.LogWarning("[RPGStats] blackboard binding \"" + v.name +
                        "\" could not read \"" + v.boundMemberPath + "\" — keeping its last value");
                    continue;
                }

                if (current is bool) v.Numeric = (bool)current ? 1f : 0f;
                else if (current is int) v.Numeric = (int)current;
                else if (current is float) v.Numeric = (float)current;
                else if (current is double) v.Numeric = (float)(double)current;
                else if (current is long) v.Numeric = (long)current;
            }
        }

        private static MemberInfo FindMember(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo prop = type.GetProperty(name, flags);
            if (prop != null) return prop;
            return type.GetField(name, flags);
        }

        private static object GetValue(MemberInfo member, object target)
        {
            PropertyInfo prop = member as PropertyInfo;
            if (prop != null) return prop.GetValue(target, null);
            FieldInfo field = member as FieldInfo;
            return field != null ? field.GetValue(target) : null;
        }
    }
}
