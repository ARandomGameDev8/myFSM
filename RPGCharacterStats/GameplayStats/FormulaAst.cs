// RPG Character & Stats System — AST for the .gameplaystat formula language.
//
// Section 11: Lexer → Token Stream → Parser → GameplayStat AST → Dependency
// Graph → Topological Sort → Code Generator. This file is the AST: expressions
// (with a kind tag for the type checker) and the four statement shapes the
// language allows (section 9.2): declarations, assignments, if/else-if/else,
// and return. No loops, by design — that is what keeps evaluation O(1).

using System;
using System.Collections.Generic;

namespace RPGCharacterStats
{
    public enum ExprKind
    {
        Number,
        BoolLiteral,
        Variable,
        Binary,
        Unary,
        Call,
    }

    public class FormulaExpr
    {
        public ExprKind Kind;

        public float Number;
        public bool Bool;
        public string Name;

        public FormulaTokenType Op;
        public FormulaExpr Left;
        public FormulaExpr Right;

        public string Function;
        public List<FormulaExpr> Args;

        /// <summary>Set by the checker: the resolved type of this expression.</summary>
        public StatType ExprType;

        public int Line;
        public int Column;
    }

    public class VarDeclStatement
    {
        public StatType VarType;
        public string Name;
        public FormulaExpr Initializer;
        public int Line;
        public int Column;
    }

    public class AssignStatement
    {
        public string Name;
        public FormulaExpr Value;
        public int Line;
        public int Column;
    }

    public class IfStatement
    {
        public FormulaExpr Condition;
        public List<object> ThenBody = new List<object>();   // statements
        public List<object> ElseBody = new List<object>();   // statements (may be empty)
        public int Line;
        public int Column;
    }

    public class ReturnStatement
    {
        public FormulaExpr Value;
        public int Line;
        public int Column;
    }

    /// <summary>A parameter of the formula: the primary (first) one must match a
    /// character stat by name AND type (section 9.1); the rest are secondary.</summary>
    public class FormulaParam
    {
        public string Name;
        public StatType ParamType;
        public int Line;
        public int Column;
    }

    /// <summary>One fully parsed formula: "[type] [name]: (primary ...) => { ... }".</summary>
    public class ParsedFormula
    {
        public string Name;
        public StatType ReturnType;
        public int NameLine;
        public int NameColumn;

        public List<FormulaParam> Params = new List<FormulaParam>();
        public List<object> Body = new List<object>();       // statements

        public FormulaParam Primary
        {
            get { return Params.Count > 0 ? Params[0] : null; }
        }
    }
}
