// RPG Character & Stats System — the .gameplaystat document format (section 9).
//
// The file is the formulas' own text, back to back:
//
//     float MaxHP: (primary float Vitality, float K) => {
//         float base = Vitality * 10;
//         return clamp(base + K, 0, 9999);
//     }
//
// The Gameplay Stats Builder edits rows ("[type] [name]:" label + body text)
// and calls Write to produce exactly this. Parse hands the text to the
// formula parser and returns the ParsedFormula list — one source of truth,
// no re-typed duplicate model (section 10.3).

using System;
using System.Collections.Generic;
using System.Text;

namespace RPGCharacterStats
{
    public static class GameplayStatFormat
    {
        /// <summary>Parse a whole .gameplaystat document into ASTs.</summary>
        public static List<ParsedFormula> Parse(string text)
        {
            return FormulaParser.ParseDocument(text);
        }

        /// <summary>Parse one builder row. The label ("float MaxHP") supplies
        /// the result type and name unless the row text repeats them.</summary>
        public static ParsedFormula ParseRow(StatType labelType, string labelName, string rowText)
        {
            return FormulaParser.ParseRow(labelType, labelName, rowText);
        }

        /// <summary>Serialize formulas back to the canonical text: each
        /// formula keeps its original body text when it round-trips, so a
        /// load/save cycle stays diffable.</summary>
        public static string Write(List<GameplayStatFormula> formulas)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < formulas.Count; i++)
            {
                GameplayStatFormula f = formulas[i];
                if (f == null || string.IsNullOrEmpty(f.name)) continue;
                if (string.IsNullOrEmpty(f.formulaExpression)) continue; // empty rows are skipped (10.1)

                if (sb.Length > 0) sb.Append('\n');
                sb.Append(FormulaChecker.TypeWord(f.type)).Append(' ').Append(f.name).Append(": ");
                sb.Append(f.formulaExpression.Trim());
                if (!f.formulaExpression.TrimEnd().EndsWith("}")) sb.Append(';');
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Split saved text back into editable (label, body) rows for
        /// the builder — no parse errors thrown, best effort, so the editor can
        /// show a broken file instead of refusing to open it.</summary>
        public static List<GameplayStatFormula> ReadLoose(string text)
        {
            List<GameplayStatFormula> rows = new List<GameplayStatFormula>();
            if (string.IsNullOrEmpty(text)) return rows;

            int i = 0, n = text.Length;
            while (i < n)
            {
                // Find the next "[type] [name]:" label.
                while (i < n && char.IsWhiteSpace(text[i])) i++;
                if (i >= n) break;

                int labelStart = i;
                StatType type = StatType.Float;
                string name = null;
                bool ok = TryReadLabel(text, ref i, out type, out name);
                if (!ok)
                {
                    i = labelStart + 1; // not a label after all; skip a char
                    continue;
                }

                // Body runs until the next label at brace depth 0 (or EOF).
                int bodyStart = i;
                int depth = 0;
                while (i < n)
                {
                    char c = text[i];
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            i++;
                            break;
                        }
                    }
                    else if (depth == 0 && c != ' ' && c != '\t' && c != '\r' && c != '\n' && c != '(')
                    {
                        // A non-brace label could start here; peek for "float|int|bool ".
                        StatType probeType;
                        string probeName;
                        int probe = i;
                        if (depth == 0 && TryReadLabel(text, ref probe, out probeType, out probeName))
                        {
                            break; // next formula begins; body ends here
                        }
                    }
                    i++;
                }

                string body = text.Substring(bodyStart, i - bodyStart).Trim();
                GameplayStatFormula f = new GameplayStatFormula();
                f.name = name;
                f.type = type;
                f.formulaExpression = body;
                f.primaryInput = ExtractPrimary(body);
                rows.Add(f);
            }
            return rows;
        }

        private static bool TryReadLabel(string text, ref int i, out StatType type, out string name)
        {
            type = StatType.Float;
            name = null;
            int save = i;

            string word = ReadWord(text, ref i);
            if (word == "float") type = StatType.Float;
            else if (word == "int") type = StatType.Int;
            else if (word == "bool") type = StatType.Bool;
            else { i = save; return false; }

            if (i >= text.Length || !char.IsWhiteSpace(text[i])) { i = save; return false; }
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

            name = ReadWord(text, ref i);
            if (string.IsNullOrEmpty(name) || !CharStatFormat.IsValidName(name)) { i = save; return false; }

            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || text[i] != ':') { i = save; return false; }
            i++;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            return true;
        }

        private static string ReadWord(string text, ref int i)
        {
            int start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            return text.Substring(start, i - start);
        }

        /// <summary>Pull "(primary float Vitality" out of body text for the
        /// loose model; full signatures come from the parser, not this.</summary>
        private static string ExtractPrimary(string body)
        {
            int p = body.IndexOf('(');
            if (p < 0) return "";
            int primary = body.IndexOf("primary", p);
            if (primary < 0) return "";
            int end = primary;
            while (end < body.Length && body[end] != ',' && body[end] != ')') end++;
            return body.Substring(primary, end - primary).Trim();
        }
    }
}
