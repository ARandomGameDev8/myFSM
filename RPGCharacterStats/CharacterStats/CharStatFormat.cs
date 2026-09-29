// RPG Character & Stats System — the .charstat format.
//
// Sections 6.1/6.3/23: a .charstat file is the REUSABLE answer to "what stats
// does this character type have" — names, types, optional min/max, defaults.
// One individual character never owns this file; many definitions reference
// the same one. The text format is line-based and designer-editable:
//
//     float Strength:   (min: 0, max: 999, default: 10)
//     int   Level:      (min: 1, max: 100, default: 1)
//     bool  IsUndead:   (default: false)
//
// min/max are optional (bool fields have neither); default is optional too and
// falls back to zero/false. The Character Stats Builder edits the schema as
// data and uses Write() to save; Load parses the same text designers can hand-
// edit.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RPGCharacterStats
{
    /// <summary>One row of a stat schema: name, type, optional min/max, default.</summary>
    [Serializable]
    public class StatSchemaEntry
    {
        public string name;
        public StatType type;

        public bool hasMin;
        public float minValue;

        public bool hasMax;
        public float maxValue;

        public bool hasDefault;
        public float defaultFloat;
        public int defaultInt;
        public bool defaultBool;

        public float Clamp(float value)
        {
            float lo = hasMin ? minValue : float.NegativeInfinity;
            float hi = hasMax ? maxValue : float.PositiveInfinity;
            return value < lo ? lo : (value > hi ? hi : value);
        }
    }

    /// <summary>The parsed .charstat document — the "vocabulary" (section 24).</summary>
    [Serializable]
    public class StatSchema
    {
        public List<StatSchemaEntry> entries = new List<StatSchemaEntry>();

        public StatSchemaEntry Find(string name)
        {
            if (name == null) return null;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] != null && entries[i].name == name) return entries[i];
            }
            return null;
        }
    }

    /// <summary>Parse + write for the .charstat text format. Kept static and
    /// string-only so the Sandbox can verify it headless.</summary>
    public static class CharStatFormat
    {
        /// <summary>A parse failure with a 1-based line number, reported inline
        /// by the builder (section 6.2: validation runs live).</summary>
        public sealed class ParseException : Exception
        {
            public int Line;
            public ParseException(int line, string message)
                : base("line " + line + ": " + message)
            {
                Line = line;
            }
        }

        public static StatSchema Parse(string text)
        {
            StatSchema schema = new StatSchema();
            if (text == null) return schema;

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#"))
                    continue;

                int colon = line.IndexOf(':');
                if (colon < 0)
                    throw new ParseException(i + 1, "expected 'type Name:' — got \"" + line + "\"");

                string head = line.Substring(0, colon).Trim();
                string tail = line.Substring(colon + 1).Trim();

                // Head is "type Name" — type first, exactly like the doc's examples.
                int space = head.IndexOfAny(new[] { ' ', '\t' });
                if (space < 0)
                    throw new ParseException(i + 1, "expected 'type Name' before the colon — got \"" + head + "\"");

                StatType type;
                string typeWord = head.Substring(0, space).Trim().ToLowerInvariant();
                if (typeWord == "float") type = StatType.Float;
                else if (typeWord == "int") type = StatType.Int;
                else if (typeWord == "bool") type = StatType.Bool;
                else throw new ParseException(i + 1, "unknown stat type \"" + typeWord + "\" (float, int, bool)");

                string name = head.Substring(space + 1).Trim();
                if (!IsValidName(name))
                    throw new ParseException(i + 1, "invalid stat name \"" + name + "\"");

                if (schema.Find(name) != null)
                    throw new ParseException(i + 1, "duplicate stat name \"" + name + "\"");

                StatSchemaEntry e = new StatSchemaEntry();
                e.name = name;
                e.type = type;
                ParseOptions(tail, e, i + 1);
                schema.entries.Add(e);
            }
            return schema;
        }

        private static void ParseOptions(string tail, StatSchemaEntry e, int line)
        {
            if (tail.Length == 0) return;
            tail = tail.Trim();
            if (tail.StartsWith("(")) tail = tail.Substring(1);
            if (tail.EndsWith(")")) tail = tail.Substring(0, tail.Length - 1);

            string[] parts = tail.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0) continue;

                int colon = part.IndexOf(':');
                if (colon < 0)
                    throw new ParseException(line, "expected 'key: value' in options — got \"" + part + "\"");

                string key = part.Substring(0, colon).Trim().ToLowerInvariant();
                string value = part.Substring(colon + 1).Trim();

                switch (key)
                {
                    case "min":
                        e.hasMin = true;
                        e.minValue = ParseNumber(value, line);
                        break;
                    case "max":
                        e.hasMax = true;
                        e.maxValue = ParseNumber(value, line);
                        break;
                    case "default":
                        e.hasDefault = true;
                        if (e.type == StatType.Bool)
                        {
                            string low = value.ToLowerInvariant();
                            if (low == "true") e.defaultBool = true;
                            else if (low == "false") e.defaultBool = false;
                            else throw new ParseException(line, "bool default must be true or false — got \"" + value + "\"");
                        }
                        else
                        {
                            float num = ParseNumber(value, line);
                            e.defaultFloat = num;
                            e.defaultInt = (int)num;
                        }
                        break;
                    default:
                        throw new ParseException(line, "unknown option \"" + key + "\" (min, max, default)");
                }
            }

            if (e.hasMin && e.hasMax && e.minValue > e.maxValue)
                throw new ParseException(line, e.name + ": min " + e.minValue + " > max " + e.maxValue);
        }

        private static float ParseNumber(string value, int line)
        {
            float f;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                throw new ParseException(line, "\"" + value + "\" is not a number");
            return f;
        }

        public static string Write(StatSchema schema)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < schema.entries.Count; i++)
            {
                StatSchemaEntry e = schema.entries[i];
                if (e == null || string.IsNullOrEmpty(e.name)) continue;

                sb.Append(e.type == StatType.Float ? "float " : e.type == StatType.Int ? "int " : "bool ");
                sb.Append(e.name).Append(':');

                bool first = true;
                Action append = delegate
                {
                    if (first) { sb.Append(" ("); first = false; }
                    else sb.Append(", ");
                };

                if (e.type != StatType.Bool)
                {
                    if (e.hasMin) { append(); sb.Append("min: ").Append(Num(e.minValue)); }
                    if (e.hasMax) { append(); sb.Append("max: ").Append(Num(e.maxValue)); }
                }
                if (e.hasDefault)
                {
                    append();
                    if (e.type == StatType.Bool) sb.Append("default: ").Append(e.defaultBool ? "true" : "false");
                    else if (e.type == StatType.Int) sb.Append("default: ").Append(e.defaultInt);
                    else sb.Append("default: ").Append(Num(e.defaultFloat));
                }
                if (!first) sb.Append(')');
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string Num(float f)
        {
            // Whole numbers print without a decimal point, matching the doc.
            return f == Math.Floor(f) && Math.Abs(f) < 1e9f
                ? ((int)f).ToString(CultureInfo.InvariantCulture)
                : f.ToString("R", CultureInfo.InvariantCulture);
        }

        public static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!(char.IsLetter(name[0]) || name[0] == '_')) return false;
            for (int i = 1; i < name.Length; i++)
            {
                if (!(char.IsLetterOrDigit(name[i]) || name[i] == '_')) return false;
            }
            return true;
        }
    }
}
