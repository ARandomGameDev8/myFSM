// RPG Character & Stats System — CharacterStats: one character's concrete
// stat values, shaped by a StatSchema.
//
// Section 4.2: the abstract Stats surface with Recalculate/GetFloat/GetInt/
// GetBool. Here Recalculate() re-syncs the field list to the schema (adding
// missing fields at their defaults) and clamps every value into its declared
// min/max. Setters clamp too, then ping the GameplayStatsServer (if one is
// attached) so derived stats recalculate — the event flow of section 19.
//
// The schema reference is by TEXT, not by asset: definitions store the
// .charstat string, so this container works headless in the Sandbox and in
// Unity alike.

using System;
using System.Collections.Generic;

namespace RPGCharacterStats
{
    [Serializable]
    public class CharacterStats : Stats
    {
        /// <summary>The .charstat text this container is shaped by.</summary>
        public string schemaText;

        /// <summary>Fired after a Set* actually changed a value. GameplayStats
        /// wires its server to this; stat bars and the FSM blackboard listen
        /// one step further out (sections 19/20.2).</summary>
        public event Action<StatField> OnStatChanged;

        private StatSchema _schema;
        private bool _schemaDirty = true;

        public StatSchema Schema
        {
            get
            {
                if (_schemaDirty || _schema == null)
                {
                    _schema = string.IsNullOrEmpty(schemaText)
                        ? new StatSchema()
                        : CharStatFormat.Parse(schemaText);
                    _schemaDirty = false;
                }
                return _schema;
            }
        }

        public void SetSchemaText(string text)
        {
            schemaText = text;
            _schemaDirty = true;
        }

        public override void Recalculate()
        {
            StatSchema schema = Schema;
            List<StatField> fresh = new List<StatField>(schema.entries.Count);

            for (int i = 0; i < schema.entries.Count; i++)
            {
                StatSchemaEntry e = schema.entries[i];
                StatField existing = Find(e.name);
                if (existing == null)
                {
                    existing = new StatField(e.name, e.type);
                    ApplyDefault(existing, e);
                }
                Clamp(existing, e);
                fresh.Add(existing);
            }

            fields = fresh;
        }

        private static void ApplyDefault(StatField f, StatSchemaEntry e)
        {
            if (!e.hasDefault) return;
            switch (e.type)
            {
                case StatType.Int: f.intValue = e.defaultInt; break;
                case StatType.Bool: f.boolValue = e.defaultBool; break;
                default: f.floatValue = e.defaultFloat; break;
            }
        }

        private static void Clamp(StatField f, StatSchemaEntry e)
        {
            if (e.type == StatType.Bool) return;
            float v = e.type == StatType.Int ? f.intValue : f.floatValue;
            float clamped = e.Clamp(v);
            if (e.type == StatType.Int) f.intValue = (int)clamped;
            else f.floatValue = clamped;
        }

        // ---- typed setters: clamp, write, notify (section 12/19) ----

        public void SetFloat(string name, float value)
        {
            StatField f = Find(name);
            StatSchemaEntry e = Schema.Find(name);
            if (f == null || e == null)
            {
                throw new KeyNotFoundException("no character stat named \"" + name + "\"");
            }
            float clamped = e.Clamp(value);
            if (f.floatValue == clamped) return;
            f.floatValue = clamped;
            Notify(f);
        }

        public void SetInt(string name, int value)
        {
            StatField f = Find(name);
            StatSchemaEntry e = Schema.Find(name);
            if (f == null || e == null)
                throw new KeyNotFoundException("no character stat named \"" + name + "\"");
            int clamped = (int)e.Clamp(value);
            if (f.intValue == clamped) return;
            f.intValue = clamped;
            Notify(f);
        }

        public void SetBool(string name, bool value)
        {
            StatField f = Find(name);
            if (f == null || Schema.Find(name) == null)
                throw new KeyNotFoundException("no character stat named \"" + name + "\"");
            if (f.boolValue == value) return;
            f.boolValue = value;
            Notify(f);
        }

        private void Notify(StatField f)
        {
            Action<StatField> handler = OnStatChanged;
            if (handler != null) handler(f);
        }

        // ---- Stats surface ----

        public override float GetFloat(string name)
        {
            StatField f = Find(name);
            if (f == null) throw new KeyNotFoundException("no character stat named \"" + name + "\"");
            return f.NumericValue;
        }

        public override int GetInt(string name)
        {
            StatField f = Find(name);
            if (f == null) throw new KeyNotFoundException("no character stat named \"" + name + "\"");
            return f.AsInt;
        }

        public override bool GetBool(string name)
        {
            StatField f = Find(name);
            if (f == null) throw new KeyNotFoundException("no character stat named \"" + name + "\"");
            return f.BoolValue;
        }

        /// <summary>Apply the definition's override list (section 4.5) on top
        /// of the schema defaults; values still respect min/max. Used by the
        /// spawn pipeline and by the builder's preview.</summary>
        public void ApplyOverrides(List<StatValueOverride> overrides)
        {
            if (overrides == null) return;
            for (int i = 0; i < overrides.Count; i++)
            {
                StatValueOverride o = overrides[i];
                if (o == null || string.IsNullOrEmpty(o.statName)) continue;
                StatSchemaEntry e = Schema.Find(o.statName);
                if (e == null) continue; // stale override for a removed stat: ignore

                switch (e.type)
                {
                    case StatType.Int:
                        SetInt(o.statName, (int)e.Clamp(o.intValue));
                        break;
                    case StatType.Bool:
                        SetBool(o.statName, o.boolValue);
                        break;
                    default:
                        SetFloat(o.statName, e.Clamp(o.floatValue));
                        break;
                }
            }
        }

        public override string ToString()
        {
            return "CharacterStats(" + fields.Count + " stats)";
        }
    }
}
