// RPG Character & Stats System — recursive-descent parser for the formula
// language (section 9). Two entry points:
//
//   ParseDocument  — a whole .gameplaystat file: "[type] [name]: (primary ...)
//                    => { ... }" blocks back to back.
//   ParseRow       — one builder row: just "(primary ...) => { ... }"; the
//                    [type] [name]: label travels as arguments (section 10.1).
//                    If the row text carries its own label anyway, the label in
//                    the text wins (section 10.3: the text is source of truth).
//
// Precedence, low to high: || && == != < <= > >= + - * / % ^ (right-assoc)
// with unary -/! above that. Calls are Identifier "(" args ")".

using System;
using System.Collections.Generic;

namespace RPGCharacterStats
{
    public sealed class FormulaParseException : Exception
    {
        public int Line;
        public int Column;
        public FormulaParseException(int line, int column, string message)
            : base(line + ":" + column + ": " + message)
        {
            Line = line;
            Column = column;
        }
    }

    public sealed class FormulaParser
    {
        private readonly List<FormulaToken> _tokens;
        private int _pos;

        private FormulaParser(List<FormulaToken> tokens)
        {
            _tokens = tokens;
        }

        // ---- public entry points ----

        public static List<ParsedFormula> ParseDocument(string source)
        {
            FormulaParser p = new FormulaParser(FormulaLexer.Lex(source));
            List<ParsedFormula> formulas = new List<ParsedFormula>();
            while (!p.At(FormulaTokenType.EndOfFile))
            {
                formulas.Add(p.ParseOneFormula());
            }
            if (formulas.Count == 0)
                throw new FormulaParseException(1, 1, "no formulas found");
            return formulas;
        }

        public static ParsedFormula ParseRow(StatType labelType, string labelName, string rowText)
        {
            FormulaParser p = new FormulaParser(FormulaLexer.Lex(rowText));

            // Tolerate (and honour) a full "[type] [name]:" prefix in the text.
            if (p.At(FormulaTokenType.Float) || p.At(FormulaTokenType.Int) || p.At(FormulaTokenType.Bool))
            {
                FormulaToken typeTok = p.Next();
                labelType = typeTok.Type == FormulaTokenType.Float ? StatType.Float
                    : typeTok.Type == FormulaTokenType.Int ? StatType.Int : StatType.Bool;

                FormulaToken nameTok = p.Expect(FormulaTokenType.Identifier, "a formula name");
                labelName = nameTok.Text;
                p.Expect(FormulaTokenType.Colon, "':' after the formula name");
            }

            ParsedFormula f = p.ParseFormulaTail(labelType, labelName, 1, 1);
            p.Expect(FormulaTokenType.EndOfFile, "end of formula");
            return f;
        }

        // ---- formulas ----

        private ParsedFormula ParseOneFormula()
        {
            FormulaToken typeTok = Next(); // consume the result type
            StatType type;
            if (typeTok.Type == FormulaTokenType.Float) type = StatType.Float;
            else if (typeTok.Type == FormulaTokenType.Int) type = StatType.Int;
            else if (typeTok.Type == FormulaTokenType.Bool) type = StatType.Bool;
            else throw Err(typeTok, "expected the result type (float, int or bool)");

            FormulaToken nameTok = Expect(FormulaTokenType.Identifier, "a formula name");
            Expect(FormulaTokenType.Colon, "':' after the formula name");
            return ParseFormulaTail(type, nameTok.Text, nameTok.Line, nameTok.Column);
        }

        /// <summary>Parse "(primary ...) => { body }" — everything after the label.</summary>
        private ParsedFormula ParseFormulaTail(StatType returnType, string name, int line, int col)
        {
            ParsedFormula f = new ParsedFormula();
            f.Name = name;
            f.ReturnType = returnType;
            f.NameLine = line;
            f.NameColumn = col;

            FormulaToken paren = Expect(FormulaTokenType.LParen, "'(' to start the parameter list");

            bool first = true;
            if (!At(FormulaTokenType.RParen))
            {
                while (true)
                {
                    if (first)
                    {
                        // Section 9.1: the first parameter is ALWAYS the
                        // primary input and carries the 'primary' keyword.
                        Expect(FormulaTokenType.Primary, "'primary' for the first parameter");
                        f.Params.Add(ParseParam());
                        first = false;
                    }
                    else
                    {
                        if (At(FormulaTokenType.Primary))
                            throw Err(Peek(), "only the FIRST parameter is 'primary' (section 9.1)");
                        f.Params.Add(ParseParam()); // secondaries: just "type name"
                    }

                    if (At(FormulaTokenType.Comma)) { Next(); continue; }
                    break;
                }
            }
            Expect(FormulaTokenType.RParen, "')' to close the parameter list");
            Expect(FormulaTokenType.Arrow, "'=>' between the parameters and the body");
            Expect(FormulaTokenType.LBrace, "'{' to open the formula body");

            f.Body = ParseBlock();
            return f;
        }

        private FormulaParam ParseParam()
        {
            FormulaToken typeTok = Next();
            StatType type;
            if (typeTok.Type == FormulaTokenType.Float) type = StatType.Float;
            else if (typeTok.Type == FormulaTokenType.Int) type = StatType.Int;
            else if (typeTok.Type == FormulaTokenType.Bool) type = StatType.Bool;
            else throw Err(typeTok, "expected a parameter type (float, int or bool) after 'primary'");

            FormulaToken nameTok = Expect(FormulaTokenType.Identifier, "a parameter name");
            FormulaParam p = new FormulaParam();
            p.Name = nameTok.Text;
            p.ParamType = type;
            p.Line = nameTok.Line;
            p.Column = nameTok.Column;
            return p;
        }

        // ---- statements ----

        private List<object> ParseBlock()
        {
            List<object> body = new List<object>();
            while (!At(FormulaTokenType.RBrace))
            {
                if (At(FormulaTokenType.EndOfFile))
                    throw Err(_tokens[_pos], "'}' to close the formula body");
                object st = ParseStatement();
                if (st != null) body.Add(st);
            }
            Next(); // consume }
            return body;
        }

        private object ParseStatement()
        {
            FormulaToken t = Peek();

            if (t.Type == FormulaTokenType.Float || t.Type == FormulaTokenType.Int || t.Type == FormulaTokenType.Bool)
                return ParseVarDecl();

            if (t.Type == FormulaTokenType.If)
                return ParseIf();

            if (t.Type == FormulaTokenType.Return)
                return ParseReturn();

            if (t.Type == FormulaTokenType.Identifier)
            {
                if (_tokens[_pos + 1].Type == FormulaTokenType.Equals)
                    return ParseAssign();
                throw Err(_tokens[_pos + 1],
                    "unexpected \"" + FormulaLexer.Symbol(_tokens[_pos + 1].Type) +
                    "\" — statements are declarations, assignments, if, or return");
            }

            throw Err(t, "expected a statement (declaration, assignment, if, or return)");
        }

        private VarDeclStatement ParseVarDecl()
        {
            FormulaToken typeTok = Next();
            StatType type = typeTok.Type == FormulaTokenType.Float ? StatType.Float
                : typeTok.Type == FormulaTokenType.Int ? StatType.Int : StatType.Bool;

            FormulaToken nameTok = Expect(FormulaTokenType.Identifier, "a variable name");
            FormulaToken eq = Expect(FormulaTokenType.Equals, "'=' — temporary variables must be initialized");
            VarDeclStatement st = new VarDeclStatement();
            st.VarType = type;
            st.Name = nameTok.Text;
            st.Line = nameTok.Line;
            st.Column = nameTok.Column;
            st.Initializer = ParseExpression();
            Expect(FormulaTokenType.Semicolon, "';' after the declaration");
            return st;
        }

        private AssignStatement ParseAssign()
        {
            FormulaToken nameTok = Expect(FormulaTokenType.Identifier, "a variable name");
            Expect(FormulaTokenType.Equals, "'=' in the assignment");
            AssignStatement st = new AssignStatement();
            st.Name = nameTok.Text;
            st.Line = nameTok.Line;
            st.Column = nameTok.Column;
            st.Value = ParseExpression();
            Expect(FormulaTokenType.Semicolon, "';' after the assignment");
            return st;
        }

        private IfStatement ParseIf()
        {
            FormulaToken ifTok = Expect(FormulaTokenType.If, "'if'");
            IfStatement st = new IfStatement();
            st.Line = ifTok.Line;
            st.Column = ifTok.Column;

            Expect(FormulaTokenType.LParen, "'(' after 'if'");
            st.Condition = ParseExpression();
            Expect(FormulaTokenType.RParen, "')' after the if condition");
            Expect(FormulaTokenType.LBrace, "'{' — if bodies use braces");
            st.ThenBody = ParseBlock();

            if (At(FormulaTokenType.Else))
            {
                Next();
                if (At(FormulaTokenType.If))
                {
                    // else if (...) {...} nests as the sole member of ElseBody.
                    IfStatement nested = ParseIf();
                    st.ElseBody.Add(nested);
                }
                else
                {
                    Expect(FormulaTokenType.LBrace, "'{' — else bodies use braces");
                    st.ElseBody = ParseBlock();
                }
            }
            return st;
        }

        private ReturnStatement ParseReturn()
        {
            FormulaToken retTok = Expect(FormulaTokenType.Return, "'return'");
            ReturnStatement st = new ReturnStatement();
            st.Line = retTok.Line;
            st.Column = retTok.Column;
            st.Value = ParseExpression();
            Expect(FormulaTokenType.Semicolon, "';' after the return value");
            return st;
        }

        // ---- expressions (precedence climbing) ----

        internal FormulaExpr ParseExpression()
        {
            return ParseOr();
        }

        private FormulaExpr ParseOr()
        {
            FormulaExpr left = ParseAnd();
            while (At(FormulaTokenType.OrOr))
            {
                FormulaToken op = Next();
                FormulaExpr e = Bin(op, left, ParseAnd());
                left = e;
            }
            return left;
        }

        private FormulaExpr ParseAnd()
        {
            FormulaExpr left = ParseEquality();
            while (At(FormulaTokenType.AndAnd))
            {
                FormulaToken op = Next();
                left = Bin(op, left, ParseEquality());
            }
            return left;
        }

        private FormulaExpr ParseEquality()
        {
            FormulaExpr left = ParseRelational();
            while (At(FormulaTokenType.EqualsEquals) || At(FormulaTokenType.NotEquals))
            {
                FormulaToken op = Next();
                left = Bin(op, left, ParseRelational());
            }
            return left;
        }

        private FormulaExpr ParseRelational()
        {
            FormulaExpr left = ParseAdditive();
            while (At(FormulaTokenType.Less) || At(FormulaTokenType.LessEquals)
                || At(FormulaTokenType.Greater) || At(FormulaTokenType.GreaterEquals))
            {
                FormulaToken op = Next();
                left = Bin(op, left, ParseAdditive());
            }
            return left;
        }

        private FormulaExpr ParseAdditive()
        {
            FormulaExpr left = ParseMultiplicative();
            while (At(FormulaTokenType.Plus) || At(FormulaTokenType.Minus))
            {
                FormulaToken op = Next();
                left = Bin(op, left, ParseMultiplicative());
            }
            return left;
        }

        private FormulaExpr ParseMultiplicative()
        {
            FormulaExpr left = ParsePower();
            while (At(FormulaTokenType.Star) || At(FormulaTokenType.Slash) || At(FormulaTokenType.Percent))
            {
                FormulaToken op = Next();
                left = Bin(op, left, ParsePower());
            }
            return left;
        }

        private FormulaExpr ParsePower()
        {
            FormulaExpr left = ParseUnary();
            if (At(FormulaTokenType.Caret))
            {
                FormulaToken op = Next();
                // Right-associative: 2^3^2 == 2^(3^2).
                return Bin(op, left, ParsePower());
            }
            return left;
        }

        private FormulaExpr ParseUnary()
        {
            if (At(FormulaTokenType.Minus) || At(FormulaTokenType.Not))
            {
                FormulaToken op = Next();
                FormulaExpr e = new FormulaExpr();
                e.Kind = ExprKind.Unary;
                e.Op = op.Type;
                e.Left = ParseUnary();
                e.Line = op.Line;
                e.Column = op.Column;
                return e;
            }
            return ParsePrimary();
        }

        private FormulaExpr ParsePrimary()
        {
            FormulaToken t = Peek();

            switch (t.Type)
            {
                case FormulaTokenType.Number:
                    Next();
                    {
                        FormulaExpr e = new FormulaExpr();
                        e.Kind = ExprKind.Number;
                        e.Number = t.Number;
                        e.ExprType = StatType.Float;
                        e.Line = t.Line;
                        e.Column = t.Column;
                        return e;
                    }

                case FormulaTokenType.True:
                case FormulaTokenType.False:
                    Next();
                    {
                        FormulaExpr e = new FormulaExpr();
                        e.Kind = ExprKind.BoolLiteral;
                        e.Bool = t.Type == FormulaTokenType.True;
                        e.ExprType = StatType.Bool;
                        e.Line = t.Line;
                        e.Column = t.Column;
                        return e;
                    }

                case FormulaTokenType.Identifier:
                    Next();
                    if (At(FormulaTokenType.LParen))
                    {
                        Next(); // (
                        FormulaExpr call = new FormulaExpr();
                        call.Kind = ExprKind.Call;
                        call.Function = t.Text;
                        call.Args = new List<FormulaExpr>();
                        call.Line = t.Line;
                        call.Column = t.Column;
                        if (!At(FormulaTokenType.RParen))
                        {
                            while (true)
                            {
                                call.Args.Add(ParseExpression());
                                if (At(FormulaTokenType.Comma)) { Next(); continue; }
                                break;
                            }
                        }
                        Expect(FormulaTokenType.RParen, "')' to close the call arguments");
                        return call;
                    }
                    {
                        FormulaExpr e = new FormulaExpr();
                        e.Kind = ExprKind.Variable;
                        e.Name = t.Text;
                        e.Line = t.Line;
                        e.Column = t.Column;
                        return e;
                    }

                case FormulaTokenType.LParen:
                    Next();
                    {
                        FormulaExpr inner = ParseExpression();
                        Expect(FormulaTokenType.RParen, "')' to close the parenthesised expression");
                        return inner;
                    }

                default:
                    throw Err(t, "expected an expression — got " + FormulaLexer.Symbol(t.Type));
            }
        }

        private static FormulaExpr Bin(FormulaToken op, FormulaExpr left, FormulaExpr right)
        {
            FormulaExpr e = new FormulaExpr();
            e.Kind = ExprKind.Binary;
            e.Op = op.Type;
            e.Left = left;
            e.Right = right;
            e.Line = op.Line;
            e.Column = op.Column;
            return e;
        }

        // ---- token helpers ----

        private FormulaToken Peek()
        {
            return _tokens[_pos];
        }

        private FormulaToken Next()
        {
            FormulaToken t = _tokens[_pos];
            if (_pos < _tokens.Count - 1) _pos++;
            return t;
        }

        private bool At(FormulaTokenType type)
        {
            return _tokens[_pos].Type == type;
        }

        private FormulaToken Expect(FormulaTokenType type, string what)
        {
            FormulaToken t = _tokens[_pos];
            if (t.Type != type)
                throw Err(t, "expected " + what + " — got \"" +
                    (t.Type == FormulaTokenType.Number ? t.Text : FormulaLexer.Symbol(t.Type)) + "\"");
            return Next();
        }

        private static FormulaParseException Err(FormulaToken t, string message)
        {
            return new FormulaParseException(t.Line, t.Column, message);
        }
    }
}
