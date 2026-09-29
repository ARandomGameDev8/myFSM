// RPG Character & Stats System — the type & scope checker (section 11's
// "Parser → GameplayStat AST → Dependency Graph" middle ground).
//
// For one parsed formula the checker:
//   1. Requires a primary parameter (section 9.1) and records it.
//   2. Classifies every secondary parameter into a SecondaryInput kind:
//      character stat (name+type against the .charstat) → gameplay stat →
//      blackboard variable → constant fallback.
//   3. Walks the body enforcing section 9.2: known variables only, known
//      builtins only, assignable types, and at least one return.
//
// Every failure lands in the report with the line/column of the offending
// token, which is what the builder's live validation displays (section 10.1).

using System;
using System.Collections.Generic;
using System.Globalization;

namespace RPGCharacterStats
{
    public sealed class FormulaError
    {
        public int Line;
        public int Column;
        public string Message;

        public FormulaError(int line, int column, string message)
        {
            Line = line;
            Column = column;
            Message = message;
        }

        public override string ToString()
        {
            return Line + ":" + Column + ": " + Message;
        }
    }

    public sealed class FormulaCheckResult
    {
        public ParsedFormula Formula;
        public FormulaSignature Signature = new FormulaSignature();
        public List<FormulaError> Errors = new List<FormulaError>();

        public bool Ok
        {
            get { return Errors.Count == 0; }
        }
    }

    public static class FormulaChecker
    {
        /// <summary>Section 9.2's builtin set. Arithmetic builtins accept any
        /// numeric argument and produce float; conversion to int happens at
        /// assignment/return. min/max/clamp take 2/2/3 args.</summary>
        private static readonly Dictionary<string, int> Builtins = new Dictionary<string, int>
        {
            { "sqrt", 1 }, { "pow", 2 }, { "abs", 1 }, { "min", 2 }, { "max", 2 },
            { "clamp", 3 }, { "floor", 1 }, { "ceil", 1 }, { "round", 1 },
            { "log", 1 }, { "sin", 1 }, { "cos", 1 },
        };

        public static bool IsBuiltin(string name)
        {
            return Builtins.ContainsKey(name);
        }

        public static FormulaCheckResult Check(ParsedFormula f, StatSchema charStats, Blackboard blackboard)
        {
            return Check(f, charStats, blackboard, null);
        }

        /// <param name="gameplayStatTypes">Name → return type of every other
        /// formula in the document. A body reference to one of these becomes an
        /// implicit GameplayStatInput secondary (appended to the params), which
        /// is what drives the dependency graph's edges.</param>
        public static FormulaCheckResult Check(ParsedFormula f, StatSchema charStats, Blackboard blackboard,
            Dictionary<string, StatType> gameplayStatTypes)
        {
            FormulaCheckResult r = new FormulaCheckResult();
            r.Formula = f;
            FormulaSignature sig = r.Signature;
            sig.name = f.Name;
            sig.returnType = f.ReturnType;

            if (f.Params.Count == 0)
            {
                r.Errors.Add(new FormulaError(f.NameLine, f.NameColumn, "missing primary parameter (section 9.1: the first parameter must be 'primary')"));
                return r;
            }

            // ---- primary: name AND type must match a character stat ----
            FormulaParam primary = f.Primary;
            sig.primaryName = primary.Name;
            sig.primaryType = primary.ParamType;

            StatSchemaEntry stat = charStats != null ? charStats.Find(primary.Name) : null;
            if (stat == null)
            {
                r.Errors.Add(new FormulaError(primary.Line, primary.Column,
                    "primary parameter \"" + primary.Name + "\" is not a character stat"));
            }
            else if (stat.type != primary.ParamType)
            {
                r.Errors.Add(new FormulaError(primary.Line, primary.Column,
                    "primary parameter \"" + primary.Name + "\" is " + TypeWord(primary.ParamType) +
                    " but the character stat is " + TypeWord(stat.type) + " (section 9.1)"));
            }

            // ---- secondaries ----
            for (int i = 1; i < f.Params.Count; i++)
            {
                FormulaParam p = f.Params[i];
                SecondaryInput input = ResolveSecondary(p, charStats, blackboard, r);
                sig.secondaries.Add(input);

                // Reject an explicit parameter that tries to redeclare a
                // character stat with the wrong type — that's 9.1's trap in
                // secondary form.
                StatSchemaEntry asStat = charStats != null ? charStats.Find(p.Name) : null;
                if (asStat != null && asStat.type != p.ParamType)
                {
                    r.Errors.Add(new FormulaError(p.Line, p.Column,
                        "parameter \"" + p.Name + "\" is " + TypeWord(p.ParamType) +
                        " but the character stat is " + TypeWord(asStat.type)));
                }
            }

            // ---- body ----
            CheckerScope scope = new CheckerScope();
            scope.Declare(primary.Name, primary.ParamType, primary.Line, primary.Column, r);
            for (int i = 1; i < f.Params.Count; i++)
            {
                FormulaParam p = f.Params[i];
                scope.Declare(p.Name, p.ParamType, p.Line, p.Column, r);
            }

            // ---- implicit gameplay-stat references ----
            // "return MaxHP / 2;" reads another formula's output. Inject it as
            // an implicit float parameter so the compiled delegate reads it
            // from the ordered args — and so the dependency graph sees the edge.
            // ONLY names the body actually references: injecting every other
            // formula would manufacture a cycle out of nothing.
            if (gameplayStatTypes != null && gameplayStatTypes.Count > 0)
            {
                HashSet<string> referenced = new HashSet<string>();
                CollectReferencedNames(f.Body, referenced);

                foreach (string statName in referenced)
                {
                    StatType refType;
                    if (!gameplayStatTypes.TryGetValue(statName, out refType)) continue;
                    if (statName == f.Name) continue;
                    if (scope.Vars.ContainsKey(statName)) continue;

                    FormulaParam implicitParam = new FormulaParam();
                    implicitParam.Name = statName;
                    implicitParam.ParamType = refType;
                    f.Params.Add(implicitParam);
                    scope.Declare(statName, refType, f.NameLine, f.NameColumn, r);

                    GameplayStatInput input = new GameplayStatInput();
                    input.name = statName;
                    input.type = refType;
                    input.gameplayStatName = statName;
                    sig.secondaries.Add(input);
                }
            }

            bool hasReturn = CheckBlock(r, f.Body, scope, f.ReturnType, charStats);
            if (!hasReturn)
            {
                r.Errors.Add(new FormulaError(f.NameLine, f.NameColumn,
                    "missing return — every formula must return its " + TypeWord(f.ReturnType)));
            }

            return r;
        }

        /// <summary>Every variable name an expression tree reads (not writes).
        /// Function arguments and branch bodies included; nothing else counts.</summary>
        private static void CollectReferencedNames(List<object> body, HashSet<string> into)
        {
            for (int i = 0; i < body.Count; i++)
            {
                VarDeclStatement decl = body[i] as VarDeclStatement;
                if (decl != null)
                {
                    CollectExprNames(decl.Initializer, into);
                    continue;
                }

                AssignStatement assign = body[i] as AssignStatement;
                if (assign != null)
                {
                    CollectExprNames(assign.Value, into);
                    continue;
                }

                IfStatement iff = body[i] as IfStatement;
                if (iff != null)
                {
                    CollectExprNames(iff.Condition, into);
                    CollectReferencedNames(iff.ThenBody, into);
                    CollectReferencedNames(iff.ElseBody, into);
                    continue;
                }

                ReturnStatement ret = body[i] as ReturnStatement;
                if (ret != null) CollectExprNames(ret.Value, into);
            }
        }

        private static void CollectExprNames(FormulaExpr e, HashSet<string> into)
        {
            if (e == null) return;
            switch (e.Kind)
            {
                case ExprKind.Variable:
                    into.Add(e.Name);
                    break;
                case ExprKind.Unary:
                    CollectExprNames(e.Left, into);
                    break;
                case ExprKind.Binary:
                    CollectExprNames(e.Left, into);
                    CollectExprNames(e.Right, into);
                    break;
                case ExprKind.Call:
                    for (int i = 0; i < e.Args.Count; i++) CollectExprNames(e.Args[i], into);
                    break;
            }
        }

        private static SecondaryInput ResolveSecondary(FormulaParam p, StatSchema charStats,
            Blackboard blackboard, FormulaCheckResult r)
        {
            StatSchemaEntry asStat = charStats != null ? charStats.Find(p.Name) : null;
            if (asStat != null && asStat.type == p.ParamType)
            {
                CharacterStatInput input = new CharacterStatInput();
                input.name = p.Name;
                input.type = p.ParamType;
                input.statName = p.Name;
                return input;
            }

            BlackboardVariable asVar = blackboard != null ? blackboard.Find(p.Name) : null;
            if (asVar != null && asVar.type == p.ParamType)
            {
                BlackboardInput input = new BlackboardInput();
                input.name = p.Name;
                input.type = p.ParamType;
                input.blackboardKey = p.Name;
                return input;
            }

            // Constant fallback: the definition (or blackboard.Bind) supplies
            // the value per character; default zero/false until then.
            ConstantInput constant = new ConstantInput();
            constant.name = p.Name;
            constant.type = p.ParamType;
            constant.value = 0f;
            return constant;
        }

        // ---- scope ----

        private sealed class CheckerScope
        {
            public readonly Dictionary<string, StatType> Vars = new Dictionary<string, StatType>();
            public readonly List<string> Order = new List<string>();

            public bool Declare(string name, StatType type, int line, int col, FormulaCheckResult r)
            {
                if (Vars.ContainsKey(name))
                {
                    r.Errors.Add(new FormulaError(line, col, "\"" + name + "\" is already declared in this formula"));
                    return false;
                }
                Vars[name] = type;
                Order.Add(name);
                return true;
            }

            public bool TryGetType(string name, out StatType type)
            {
                return Vars.TryGetValue(name, out type);
            }
        }

        private static bool CheckBlock(FormulaCheckResult r, List<object> body, CheckerScope scope,
            StatType returnType, StatSchema charStats)
        {
            bool hasReturn = false;
            for (int i = 0; i < body.Count; i++)
            {
                object st = body[i];

                VarDeclStatement decl = st as VarDeclStatement;
                if (decl != null)
                {
                    StatType initType = CheckExpr(r, decl.Initializer, scope, charStats);
                    if (initType != StatType.Bool && decl.VarType == StatType.Bool)
                    {
                        r.Errors.Add(new FormulaError(decl.Line, decl.Column,
                            "cannot assign a number to bool \"" + decl.Name + "\""));
                    }
                    else if (initType == StatType.Bool && decl.VarType != StatType.Bool)
                    {
                        r.Errors.Add(new FormulaError(decl.Line, decl.Column,
                            "cannot assign a bool to " + TypeWord(decl.VarType) + " \"" + decl.Name + "\""));
                    }
                    scope.Declare(decl.Name, decl.VarType, decl.Line, decl.Column, r);
                    continue;
                }

                AssignStatement assign = st as AssignStatement;
                if (assign != null)
                {
                    StatType existing;
                    if (!scope.TryGetType(assign.Name, out existing))
                    {
                        r.Errors.Add(new FormulaError(assign.Line, assign.Column,
                            "unknown variable \"" + assign.Name + "\" — declare it first"));
                    }
                    else
                    {
                        StatType valueType = CheckExpr(r, assign.Value, scope, charStats);
                        if (valueType == StatType.Bool && existing != StatType.Bool)
                            r.Errors.Add(new FormulaError(assign.Line, assign.Column,
                                "cannot assign a bool to " + TypeWord(existing) + " \"" + assign.Name + "\""));
                        else if (valueType != StatType.Bool && existing == StatType.Bool)
                            r.Errors.Add(new FormulaError(assign.Line, assign.Column,
                                "cannot assign a number to bool \"" + assign.Name + "\""));
                    }
                    continue;
                }

                IfStatement iff = st as IfStatement;
                if (iff != null)
                {
                    StatType condType = CheckExpr(r, iff.Condition, scope, charStats);
                    if (condType == StatType.Float || condType == StatType.Int)
                    {
                        r.Errors.Add(new FormulaError(iff.Line, iff.Column,
                            "if condition must be bool — this is " + TypeWord(condType)));
                    }
                    hasReturn |= CheckBlock(r, iff.ThenBody, scope, returnType, charStats);
                    hasReturn |= CheckBlock(r, iff.ElseBody, scope, returnType, charStats);
                    continue;
                }

                ReturnStatement ret = st as ReturnStatement;
                if (ret != null)
                {
                    StatType valueType = CheckExpr(r, ret.Value, scope, charStats);
                    if (valueType == StatType.Bool && returnType != StatType.Bool)
                    {
                        r.Errors.Add(new FormulaError(ret.Line, ret.Column,
                            "return type mismatch — formula declares " + TypeWord(returnType) + " but returns bool"));
                    }
                    else if (valueType != StatType.Bool && returnType == StatType.Bool)
                    {
                        r.Errors.Add(new FormulaError(ret.Line, ret.Column,
                            "return type mismatch — formula declares bool but returns " + TypeWord(valueType)));
                    }
                    hasReturn = true;
                    continue;
                }
            }
            return hasReturn;
        }

        private static StatType CheckExpr(FormulaCheckResult r, FormulaExpr e, CheckerScope scope,
            StatSchema charStats)
        {
            if (e == null) return StatType.Float;

            switch (e.Kind)
            {
                case ExprKind.Number:
                    e.ExprType = StatType.Float;
                    return e.ExprType;

                case ExprKind.BoolLiteral:
                    e.ExprType = StatType.Bool;
                    return e.ExprType;

                case ExprKind.Variable:
                    {
                        StatType t;
                        if (!scope.TryGetType(e.Name, out t))
                        {
                            r.Errors.Add(new FormulaError(e.Line, e.Column,
                                "unknown variable \"" + e.Name + "\" — use a parameter or declare a temporary"));
                            t = StatType.Float;
                        }
                        e.ExprType = t;
                        return t;
                    }

                case ExprKind.Unary:
                    {
                        StatType inner = CheckExpr(r, e.Left, scope, charStats);
                        if (e.Op == FormulaTokenType.Not)
                        {
                            if (inner == StatType.Float || inner == StatType.Int)
                                r.Errors.Add(new FormulaError(e.Line, e.Column,
                                    "'!' needs a bool operand — this is " + TypeWord(inner)));
                            e.ExprType = StatType.Bool;
                        }
                        else
                        {
                            if (inner == StatType.Bool)
                                r.Errors.Add(new FormulaError(e.Line, e.Column,
                                    "unary '-' needs a number operand — this is bool"));
                            e.ExprType = inner == StatType.Int ? StatType.Int : StatType.Float;
                        }
                        return e.ExprType;
                    }

                case ExprKind.Binary:
                    {
                        StatType lt = CheckExpr(r, e.Left, scope, charStats);
                        StatType rt = CheckExpr(r, e.Right, scope, charStats);
                        bool arithmetic = e.Op == FormulaTokenType.Plus || e.Op == FormulaTokenType.Minus
                            || e.Op == FormulaTokenType.Star || e.Op == FormulaTokenType.Slash
                            || e.Op == FormulaTokenType.Percent || e.Op == FormulaTokenType.Caret;
                        bool comparison = e.Op == FormulaTokenType.Less || e.Op == FormulaTokenType.LessEquals
                            || e.Op == FormulaTokenType.Greater || e.Op == FormulaTokenType.GreaterEquals;
                        bool equality = e.Op == FormulaTokenType.EqualsEquals || e.Op == FormulaTokenType.NotEquals;
                        bool logic = e.Op == FormulaTokenType.AndAnd || e.Op == FormulaTokenType.OrOr;

                        if (arithmetic)
                        {
                            if (lt == StatType.Bool || rt == StatType.Bool)
                                r.Errors.Add(new FormulaError(e.Line, e.Column,
                                    "arithmetic on bool operands — " + TypeWord(lt) + " " +
                                    FormulaLexer.Symbol(e.Op) + " " + TypeWord(rt)));
                            e.ExprType = lt == StatType.Int && rt == StatType.Int ? StatType.Int : StatType.Float;
                        }
                        else if (comparison)
                        {
                            if (lt == StatType.Bool || rt == StatType.Bool)
                                r.Errors.Add(new FormulaError(e.Line, e.Column,
                                    "ordered comparison on bool operands"));
                            e.ExprType = StatType.Bool;
                        }
                        else if (equality)
                        {
                            e.ExprType = StatType.Bool;
                        }
                        else if (logic)
                        {
                            if (lt != StatType.Bool)
                                r.Errors.Add(new FormulaError(e.Line, e.Column,
                                    "'&&'/'||' need bool operands — the left one is " + TypeWord(lt)));
                            if (rt != StatType.Bool)
                                r.Errors.Add(new FormulaError(e.Line, e.Column,
                                    "'&&'/'||' need bool operands — the right one is " + TypeWord(rt)));
                            e.ExprType = StatType.Bool;
                        }
                        return e.ExprType;
                    }

                case ExprKind.Call:
                    {
                        int arity;
                        if (!Builtins.TryGetValue(e.Function, out arity))
                        {
                            r.Errors.Add(new FormulaError(e.Line, e.Column,
                                "unknown function \"" + e.Function + "\" — builtins: " +
                                string.Join(", ", new List<string>(Builtins.Keys).ToArray())));
                        }
                        else if (e.Args.Count != arity)
                        {
                            r.Errors.Add(new FormulaError(e.Line, e.Column,
                                e.Function + " takes " + arity + " argument" + (arity == 1 ? "" : "s") +
                                " — got " + e.Args.Count));
                        }
                        for (int i = 0; i < e.Args.Count; i++)
                        {
                            StatType at = CheckExpr(r, e.Args[i], scope, charStats);
                            if (at == StatType.Bool)
                                r.Errors.Add(new FormulaError(e.Args[i].Line, e.Args[i].Column,
                                    e.Function + " needs numeric arguments — argument " + (i + 1) + " is bool"));
                        }
                        e.ExprType = StatType.Float;
                        return e.ExprType;
                    }

                default:
                    e.ExprType = StatType.Float;
                    return e.ExprType;
            }
        }

        internal static string TypeWord(StatType t)
        {
            return t == StatType.Int ? "int" : t == StatType.Bool ? "bool" : "float";
        }
    }
}
