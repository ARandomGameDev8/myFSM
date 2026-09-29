// RPG Character & Stats System — the code generator (section 11's last
// pipeline stages: "Code Generator → Compiled C# Delegates").
//
// A checked ParsedFormula compiles ONCE into a delegate tree evaluated with a
// tiny Frame (parameters + locals + result cell). There are no loops in the
// language, so evaluation is straight-line: O(1) work per gameplay-stat
// client, per recalculation (section 2.4). Bool values ride the float pipe as
// 1/0 — the checker has already guaranteed bools never meet arithmetic, so
// the conversion is lossless for every program that passes Check().

using System;
using System.Collections.Generic;

namespace RPGCharacterStats
{
    /// <summary>Per-evaluation state: parameter values in, locals scratch,
    /// result out. A frame is a small allocation per recalculation — formulas
    /// run on stat changes, not every render frame.</summary>
    public sealed class FormulaFrame
    {
        public float[] Params;
        public float[] Locals;
        public float Result;
        public bool Returned;
    }

    /// <summary>One compiled formula: call Evaluate(orderedParamValues).</summary>
    public sealed class CompiledFormula
    {
        public string Name;
        public StatType ReturnType;

        private readonly Func<FormulaFrame, float> _body;
        private readonly int _localCount;

        internal CompiledFormula(ParsedFormula source, Func<FormulaFrame, float> body, int localCount)
        {
            Name = source.Name;
            ReturnType = source.ReturnType;
            _body = body;
            _localCount = localCount;
        }

        public float Evaluate(float[] parameters)
        {
            FormulaFrame f = new FormulaFrame();
            f.Params = parameters;
            f.Locals = _localCount > 0 ? new float[_localCount] : null;
            _body(f);
            return f.Result; // checker guarantees a return ran; zero is the safe fallback
        }
    }

    public static class FormulaCompiler
    {
        public static CompiledFormula Compile(ParsedFormula f)
        {
            CompilerScope scope = new CompilerScope();
            for (int i = 0; i < f.Params.Count; i++) scope.Declare(f.Params[i].Name, i);

            // Pre-pass: give every declared temporary a flat slot (the checker
            // forbids redeclaring a name, so one slot per name suffices).
            int localCount = 0;
            AllocateLocals(f.Body, scope, ref localCount);

            // The compiled body runs statements in order, stopping after the
            // first return; the expression form is "float Evaluate(frame)".
            Func<FormulaFrame, float> compiled = delegate (FormulaFrame frame)
            {
                RunBlock(f.Body, scope, frame);
                return frame.Result;
            };
            return new CompiledFormula(f, compiled, localCount);
        }

        private static void AllocateLocals(List<object> body, CompilerScope scope, ref int count)
        {
            for (int i = 0; i < body.Count; i++)
            {
                VarDeclStatement decl = body[i] as VarDeclStatement;
                if (decl != null)
                {
                    if (!scope.HasSlot(decl.Name)) scope.DeclareLocal(decl.Name, count++);
                    continue;
                }
                IfStatement iff = body[i] as IfStatement;
                if (iff != null)
                {
                    AllocateLocals(iff.ThenBody, scope, ref count);
                    AllocateLocals(iff.ElseBody, scope, ref count);
                }
            }
        }

        // ---- statement execution (compiled shapes, no reflection) ----

        private static void RunBlock(List<object> body, CompilerScope scope, FormulaFrame frame)
        {
            for (int i = 0; i < body.Count; i++)
            {
                if (frame.Returned) return;
                RunStatement(body[i], scope, frame);
            }
        }

        private static void RunStatement(object st, CompilerScope scope, FormulaFrame frame)
        {
            VarDeclStatement decl = st as VarDeclStatement;
            if (decl != null)
            {
                Func<FormulaFrame, float> init = CompileExpr(decl.Initializer, scope);
                int slot = scope.SlotOf(decl.Name);
                frame.Locals[slot] = init(frame);
                return;
            }

            AssignStatement assign = st as AssignStatement;
            if (assign != null)
            {
                Func<FormulaFrame, float> value = CompileExpr(assign.Value, scope);
                int slot = scope.SlotOf(assign.Name);
                frame.Locals[slot] = value(frame);
                return;
            }

            IfStatement iff = st as IfStatement;
            if (iff != null)
            {
                Func<FormulaFrame, float> cond = CompileExpr(iff.Condition, scope);
                if (cond(frame) != 0f) RunBlock(iff.ThenBody, scope, frame);
                else RunBlock(iff.ElseBody, scope, frame);
                return;
            }

            ReturnStatement ret = st as ReturnStatement;
            if (ret != null)
            {
                Func<FormulaFrame, float> value = CompileExpr(ret.Value, scope);
                frame.Result = value(frame);
                frame.Returned = true;
            }
        }

        // ---- expression compilation ----

        private static Func<FormulaFrame, float> CompileExpr(FormulaExpr e, CompilerScope scope)
        {
            switch (e.Kind)
            {
                case ExprKind.Number:
                    float constant = e.Number;
                    return delegate { return constant; };

                case ExprKind.BoolLiteral:
                    float b = e.Bool ? 1f : 0f;
                    return delegate { return b; };

                case ExprKind.Variable:
                    return CompileVariable(e, scope);

                case ExprKind.Unary:
                    return CompileUnary(e, scope);

                case ExprKind.Binary:
                    return CompileBinary(e, scope);

                case ExprKind.Call:
                    return CompileCall(e, scope);

                default:
                    return delegate { return 0f; };
            }
        }

        private static Func<FormulaFrame, float> CompileVariable(FormulaExpr e, CompilerScope scope)
        {
            // Parameters live in frame.Params (primary first, secondaries in
            // declaration order); locals in frame.Locals. Slot numbers were
            // assigned by the checker's scope order.
            CompilerSlot slot = scope.Resolve(e.Name);
            if (slot.IsParam)
            {
                int index = slot.Index;
                return delegate (FormulaFrame f) { return f.Params[index]; };
            }
            int local = slot.Index;
            return delegate (FormulaFrame f) { return f.Locals[local]; };
        }

        private static Func<FormulaFrame, float> CompileUnary(FormulaExpr e, CompilerScope scope)
        {
            Func<FormulaFrame, float> inner = CompileExpr(e.Left, scope);
            if (e.Op == FormulaTokenType.Not)
                return delegate (FormulaFrame f) { return inner(f) == 0f ? 1f : 0f; };
            return delegate (FormulaFrame f) { return -inner(f); };
        }

        private static Func<FormulaFrame, float> CompileBinary(FormulaExpr e, CompilerScope scope)
        {
            Func<FormulaFrame, float> left = CompileExpr(e.Left, scope);
            Func<FormulaFrame, float> right = CompileExpr(e.Right, scope);

            switch (e.Op)
            {
                case FormulaTokenType.Plus: return delegate (FormulaFrame f) { return left(f) + right(f); };
                case FormulaTokenType.Minus: return delegate (FormulaFrame f) { return left(f) - right(f); };
                case FormulaTokenType.Star: return delegate (FormulaFrame f) { return left(f) * right(f); };
                case FormulaTokenType.Slash:
                    return delegate (FormulaFrame f) { return left(f) / right(f); }; // div-by-zero → Infinity, like C
                case FormulaTokenType.Percent: return delegate (FormulaFrame f) { return left(f) % right(f); };
                case FormulaTokenType.Caret:
                    return delegate (FormulaFrame f) { return (float)Math.Pow(left(f), right(f)); };
                case FormulaTokenType.Less: return delegate (FormulaFrame f) { return left(f) < right(f) ? 1f : 0f; };
                case FormulaTokenType.LessEquals: return delegate (FormulaFrame f) { return left(f) <= right(f) ? 1f : 0f; };
                case FormulaTokenType.Greater: return delegate (FormulaFrame f) { return left(f) > right(f) ? 1f : 0f; };
                case FormulaTokenType.GreaterEquals: return delegate (FormulaFrame f) { return left(f) >= right(f) ? 1f : 0f; };
                case FormulaTokenType.EqualsEquals: return delegate (FormulaFrame f) { return left(f) == right(f) ? 1f : 0f; };
                case FormulaTokenType.NotEquals: return delegate (FormulaFrame f) { return left(f) != right(f) ? 1f : 0f; };
                case FormulaTokenType.AndAnd:
                    // Short-circuit, exactly like C.
                    return delegate (FormulaFrame f) { return left(f) != 0f && right(f) != 0f ? 1f : 0f; };
                case FormulaTokenType.OrOr:
                    return delegate (FormulaFrame f) { return left(f) != 0f || right(f) != 0f ? 1f : 0f; };
                default:
                    return delegate { return 0f; };
            }
        }

        private static Func<FormulaFrame, float> CompileCall(FormulaExpr e, CompilerScope scope)
        {
            List<Func<FormulaFrame, float>> args = new List<Func<FormulaFrame, float>>(e.Args.Count);
            for (int i = 0; i < e.Args.Count; i++) args.Add(CompileExpr(e.Args[i], scope));

            switch (e.Function)
            {
                case "sqrt":
                    return delegate (FormulaFrame f) { return (float)Math.Sqrt(args[0](f)); };
                case "pow":
                    return delegate (FormulaFrame f) { return (float)Math.Pow(args[0](f), args[1](f)); };
                case "abs":
                    return delegate (FormulaFrame f) { return Math.Abs(args[0](f)); };
                case "min":
                    return delegate (FormulaFrame f) { return Math.Min(args[0](f), args[1](f)); };
                case "max":
                    return delegate (FormulaFrame f) { return Math.Max(args[0](f), args[1](f)); };
                case "clamp":
                    return delegate (FormulaFrame f)
                    {
                        float v = args[0](f), lo = args[1](f), hi = args[2](f);
                        return v < lo ? lo : (v > hi ? hi : v);
                    };
                case "floor":
                    return delegate (FormulaFrame f) { return (float)Math.Floor(args[0](f)); };
                case "ceil":
                    return delegate (FormulaFrame f) { return (float)Math.Ceiling(args[0](f)); };
                case "round":
                    return delegate (FormulaFrame f) { return (float)Math.Round(args[0](f)); };
                case "log":
                    return delegate (FormulaFrame f) { return (float)Math.Log(args[0](f)); };
                case "sin":
                    return delegate (FormulaFrame f) { return (float)Math.Sin(args[0](f)); };
                case "cos":
                    return delegate (FormulaFrame f) { return (float)Math.Cos(args[0](f)); };
                default:
                    return delegate { return 0f; }; // unreachable: the checker rejects unknown functions
            }
        }

        // ---- compile-time scope: names → param/local slots ----

        private struct CompilerSlot
        {
            public bool IsParam;
            public int Index;
        }

        private sealed class CompilerScope
        {
            private readonly Dictionary<string, CompilerSlot> _slots =
                new Dictionary<string, CompilerSlot>();
            public int ParamCount;

            public void Declare(string name, int index)
            {
                CompilerSlot slot;
                slot.IsParam = true;
                slot.Index = index;
                _slots[name] = slot;
                ParamCount = index + 1;
            }

            public void DeclareLocal(string name, int index)
            {
                CompilerSlot slot;
                slot.IsParam = false;
                slot.Index = index;
                _slots[name] = slot;
            }

            public bool HasSlot(string name)
            {
                return _slots.ContainsKey(name);
            }

            public CompilerSlot Resolve(string name)
            {
                CompilerSlot slot;
                if (_slots.TryGetValue(name, out slot)) return slot;
                CompilerSlot bad;
                bad.IsParam = false;
                bad.Index = -1;
                return bad; // unreachable: checker rejects unknown variables
            }

            public int SlotOf(string name)
            {
                return Resolve(name).Index;
            }
        }
    }
}
