// RPG Character & Stats System — lexer for the .gameplaystat formula language.
//
// Section 9: a tiny C-like expression/statement language. Tokens carry 1-based
// line and column so every downstream error (parser, checker, the builder's
// live parse preview) can point at the exact character — section 11's
// "errors include line and column information".

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RPGCharacterStats
{
    public enum FormulaTokenType
    {
        // Literals & names
        Number,
        Identifier,
        True,
        False,

        // Keywords
        Primary,
        Float,
        Int,
        Bool,
        If,
        Else,
        Return,

        // Punctuation
        LParen,
        RParen,
        LBrace,
        RBrace,
        Comma,
        Semicolon,
        Colon,
        Arrow,          // =>

        // Operators
        Plus,
        Minus,
        Star,
        Slash,
        Percent,
        Caret,          // power, sugar for pow()
        Equals,         // assignment
        EqualsEquals,
        NotEquals,
        Less,
        LessEquals,
        Greater,
        GreaterEquals,
        AndAnd,
        OrOr,
        Not,

        EndOfFile,
    }

    public struct FormulaToken
    {
        public FormulaTokenType Type;
        public string Text;
        public float Number;
        public int Line;    // 1-based
        public int Column;  // 1-based

        public override string ToString()
        {
            return Type == FormulaTokenType.Number
                ? "\"" + Number + "\""
                : "\"" + (Text ?? FormulaLexer.Symbol(Type)) + "\"";
        }
    }

    public sealed class FormulaLexException : Exception
    {
        public int Line;
        public int Column;
        public FormulaLexException(int line, int column, string message)
            : base(line + ":" + column + ": " + message)
        {
            Line = line;
            Column = column;
        }
    }

    public static class FormulaLexer
    {
        public static List<FormulaToken> Lex(string source)
        {
            List<FormulaToken> tokens = new List<FormulaToken>();
            if (source == null) source = "";

            int line = 1, col = 1;
            int i = 0, n = source.Length;

            Func<FormulaToken> start = delegate
            {
                FormulaToken t = default(FormulaToken);
                t.Line = line;
                t.Column = col;
                return t;
            };

            while (i < n)
            {
                char c = source[i];

                // Whitespace
                if (c == ' ' || c == '\t' || c == '\r')
                {
                    i++; col++;
                    continue;
                }
                if (c == '\n')
                {
                    i++; line++; col = 1;
                    continue;
                }

                // Comments: // to end of line (the builder writes these)
                if (c == '/' && i + 1 < n && source[i + 1] == '/')
                {
                    while (i < n && source[i] != '\n') { i++; }
                    continue;
                }

                FormulaToken t = start();

                // Numbers
                if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(source[i + 1])))
                {
                    int begin = i;
                    while (i < n && char.IsDigit(source[i])) { i++; col++; }
                    if (i < n && source[i] == '.')
                    {
                        i++; col++;
                        while (i < n && char.IsDigit(source[i])) { i++; col++; }
                    }
                    if (i < n && (source[i] == 'e' || source[i] == 'E'))
                    {
                        int save = i;
                        i++; col++;
                        if (i < n && (source[i] == '+' || source[i] == '-')) { i++; col++; }
                        if (i < n && char.IsDigit(source[i]))
                        {
                            while (i < n && char.IsDigit(source[i])) { i++; col++; }
                        }
                        else { i = save; } // not an exponent after all
                    }
                    string num = source.Substring(begin, i - begin);
                    float f;
                    if (!float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                        throw new FormulaLexException(t.Line, t.Column, "bad number \"" + num + "\"");
                    t.Type = FormulaTokenType.Number;
                    t.Number = f;
                    t.Text = num;
                    tokens.Add(t);
                    continue;
                }

                // Identifiers & keywords
                if (char.IsLetter(c) || c == '_')
                {
                    int begin = i;
                    while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) { i++; col++; }
                    string word = source.Substring(begin, i - begin);
                    t.Text = word;
                    switch (word)
                    {
                        case "primary": t.Type = FormulaTokenType.Primary; break;
                        case "float": t.Type = FormulaTokenType.Float; break;
                        case "int": t.Type = FormulaTokenType.Int; break;
                        case "bool": t.Type = FormulaTokenType.Bool; break;
                        case "if": t.Type = FormulaTokenType.If; break;
                        case "else": t.Type = FormulaTokenType.Else; break;
                        case "return": t.Type = FormulaTokenType.Return; break;
                        case "true": t.Type = FormulaTokenType.True; break;
                        case "false": t.Type = FormulaTokenType.False; break;
                        default: t.Type = FormulaTokenType.Identifier; break;
                    }
                    tokens.Add(t);
                    continue;
                }

                // Operators & punctuation
                i++; col++;
                switch (c)
                {
                    case '(': t.Type = FormulaTokenType.LParen; break;
                    case ')': t.Type = FormulaTokenType.RParen; break;
                    case '{': t.Type = FormulaTokenType.LBrace; break;
                    case '}': t.Type = FormulaTokenType.RBrace; break;
                    case ',': t.Type = FormulaTokenType.Comma; break;
                    case ';': t.Type = FormulaTokenType.Semicolon; break;
                    case ':': t.Type = FormulaTokenType.Colon; break;
                    case '+': t.Type = FormulaTokenType.Plus; break;
                    case '-': t.Type = FormulaTokenType.Minus; break;
                    case '*': t.Type = FormulaTokenType.Star; break;
                    case '/': t.Type = FormulaTokenType.Slash; break;
                    case '%': t.Type = FormulaTokenType.Percent; break;
                    case '^': t.Type = FormulaTokenType.Caret; break;
                    case '=':
                        if (i < n && source[i] == '=')
                        {
                            i++; col++;
                            t.Type = FormulaTokenType.EqualsEquals;
                        }
                        else if (i < n && source[i] == '>')
                        {
                            i++; col++;
                            t.Type = FormulaTokenType.Arrow;
                        }
                        else t.Type = FormulaTokenType.Equals;
                        break;
                    case '!':
                        if (i < n && source[i] == '=')
                        {
                            i++; col++;
                            t.Type = FormulaTokenType.NotEquals;
                        }
                        else t.Type = FormulaTokenType.Not;
                        break;
                    case '<':
                        if (i < n && source[i] == '=')
                        {
                            i++; col++;
                            t.Type = FormulaTokenType.LessEquals;
                        }
                        else t.Type = FormulaTokenType.Less;
                        break;
                    case '>':
                        if (i < n && source[i] == '=')
                        {
                            i++; col++;
                            t.Type = FormulaTokenType.GreaterEquals;
                        }
                        else t.Type = FormulaTokenType.Greater;
                        break;
                    case '&':
                        if (i < n && source[i] == '&')
                        {
                            i++; col++;
                            t.Type = FormulaTokenType.AndAnd;
                        }
                        else throw new FormulaLexException(t.Line, t.Column, "single '&' — did you mean '&&'?");
                        break;
                    case '|':
                        if (i < n && source[i] == '|')
                        {
                            i++; col++;
                            t.Type = FormulaTokenType.OrOr;
                        }
                        else throw new FormulaLexException(t.Line, t.Column, "single '|' — did you mean '||'?");
                        break;
                    default:
                        throw new FormulaLexException(t.Line, t.Column, "unexpected character '" + c + "'");
                }
                tokens.Add(t);
            }

            FormulaToken eof = start();
            eof.Type = FormulaTokenType.EndOfFile;
            eof.Text = "<eof>";
            tokens.Add(eof);
            return tokens;
        }

        public static string Symbol(FormulaTokenType type)
        {
            switch (type)
            {
                case FormulaTokenType.LParen: return "(";
                case FormulaTokenType.RParen: return ")";
                case FormulaTokenType.LBrace: return "{";
                case FormulaTokenType.RBrace: return "}";
                case FormulaTokenType.Comma: return ",";
                case FormulaTokenType.Semicolon: return ";";
                case FormulaTokenType.Colon: return ":";
                case FormulaTokenType.Arrow: return "=>";
                case FormulaTokenType.Plus: return "+";
                case FormulaTokenType.Minus: return "-";
                case FormulaTokenType.Star: return "*";
                case FormulaTokenType.Slash: return "/";
                case FormulaTokenType.Percent: return "%";
                case FormulaTokenType.Caret: return "^";
                case FormulaTokenType.Equals: return "=";
                case FormulaTokenType.EqualsEquals: return "==";
                case FormulaTokenType.NotEquals: return "!=";
                case FormulaTokenType.Less: return "<";
                case FormulaTokenType.LessEquals: return "<=";
                case FormulaTokenType.Greater: return ">";
                case FormulaTokenType.GreaterEquals: return ">=";
                case FormulaTokenType.AndAnd: return "&&";
                case FormulaTokenType.OrOr: return "||";
                case FormulaTokenType.Not: return "!";
                default: return type.ToString();
            }
        }
    }
}
