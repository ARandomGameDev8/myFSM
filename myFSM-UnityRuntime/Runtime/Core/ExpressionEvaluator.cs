// myFSM Unity Runtime — AST expression evaluator.
//
// Evaluates LITERAL / VAR_REF / BINARY_OP / UNARY_OP / FUNCTION_CALL tokens
// with exactly the DSL's operator semantics (DESIGN.md section 3.5):
// no implicit conversions at the call boundary, int/int -> float,
// // floors toward -infinity, same-type vectors component-wise,
// string + concatenates, ==/!= need the exact same type.

using System;

namespace MyFSM.Core
{
    public sealed class ExpressionEvaluator
    {
        private readonly AiExecution _exec;

        public ExpressionEvaluator(AiExecution exec)
        {
            _exec = exec;
        }

        public FsmValue Eval(int astIndex)
        {
            FsmbModule m = _exec.Module;
            if (astIndex < 0 || astIndex >= m.Ast.Count)
            {
                _exec.Log.Error("expression address does not resolve");
                return FsmValue.Void;
            }
            FsmAstToken t = m.Ast[astIndex];
            switch (t.Type)
            {
                case FsmbAst.Literal:
                    {
                        string err;
                        FsmValue v = FsmValue.DecodeLiteral(t.Data, out err);
                        if (err != null) _exec.Log.Error(err);
                        return v;
                    }
                case FsmbAst.VarRef:
                    return EvalVarRef(t);
                case FsmbAst.BinaryOp:
                    return EvalBinary(t);
                case FsmbAst.UnaryOp:
                    return EvalUnary(t);
                case FsmbAst.FunctionCall:
                    return EvalCall(t);
                default:
                    _exec.Log.Error("token " + FsmbAst.TokenName(t.Type) +
                                    " is not an expression");
                    return FsmValue.Void;
            }
        }

        public bool EvalCondition(int astIndex)
        {
            FsmValue v = Eval(astIndex);
            if (v.Kind != FsmValueKind.Bool)
            {
                _exec.Log.Error("condition is not bool");
                return false;
            }
            return v.B;
        }

        private int AstIndexOf(uint addr, string what)
        {
            int idx;
            if (!_exec.AstIndexFromAddress(addr, out idx))
            {
                _exec.Log.Error(what + " address does not resolve");
                return -1;
            }
            return idx;
        }

        private FsmValue EvalVarRef(FsmAstToken t)
        {
            uint addr = AstCodec.ReadU32LE(t.Data, 0);
            VarRef vr;
            if (!_exec.Vars.TryResolveAddress(addr, out vr))
            {
                _exec.Log.Error("VAR_REF address does not resolve");
                return FsmValue.Void;
            }
            switch (vr.Section)
            {
                case FsmbFormat.SecGlobal:
                    return _exec.Vars.GetConst(vr.Index);
                case FsmbFormat.SecRuntime:
                    return _exec.Vars.GetRuntime(vr.Index);
                case FsmbFormat.SecTemp:
                    if (!_exec.Vars.IsTempAlive(vr.Index))
                    {
                        _exec.Log.Error("temp '" + _exec.Vars.GetTempName(vr.Index) +
                                        "' is out of scope");
                        return FsmValue.DefaultForTag(_exec.Vars.GetTempTag(vr.Index));
                    }
                    return _exec.Vars.GetTemp(vr.Index);
                default:
                    _exec.Log.Error("VAR_REF address does not resolve");
                    return FsmValue.Void;
            }
        }

        private FsmValue EvalBinary(FsmAstToken t)
        {
            byte op = t.Data[0];
            int li = AstIndexOf(AstCodec.ReadU32LE(t.Data, 1), "BINARY_OP left");
            int ri = AstIndexOf(AstCodec.ReadU32LE(t.Data, 5), "BINARY_OP right");
            if (li < 0 || ri < 0) return FsmValue.Void;
            FsmValue a = Eval(li);
            FsmValue b = Eval(ri);
            return ApplyBinary(op, a, b);
        }

        private FsmValue EvalUnary(FsmAstToken t)
        {
            byte op = t.Data[0];
            int oi = AstIndexOf(AstCodec.ReadU32LE(t.Data, 1), "UNARY_OP operand");
            if (oi < 0) return FsmValue.Void;
            FsmValue a = Eval(oi);
            switch (op)
            {
                case FsmbOp.Not:
                    return FsmValue.MakeBool(AsBool(a));
                case FsmbOp.Minus:
                    return Negate(a);
                default:
                    _exec.Log.Error("UNARY_OP has an unknown operator id");
                    return FsmValue.Void;
            }
        }

        private FsmValue EvalCall(FsmAstToken t)
        {
            ushort id = AstCodec.ReadU16LE(t.Data, 0);
            byte argc = t.Data[2];
            FunctionOverload sig = FunctionCatalog.FindById(id);
            if (sig == null)
            {
                _exec.Log.Error("FUNCTION_CALL references unknown function id " + id);
                return FsmValue.Void;
            }
            FsmValue[] args = new FsmValue[argc];
            for (int k = 0; k < argc; k++)
            {
                int ai = AstIndexOf(AstCodec.ReadU32LE(t.Data, 3 + 4 * k),
                                    "FUNCTION_CALL argument");
                if (ai < 0) return FsmValue.Void;
                args[k] = Eval(ai);
                // waitUntil needs its condition re-evaluated every tick, so the
                // raw condition token travels with the call (see AIInstance).
                if (id == FunctionCatalog.WaitUntilId && k == 0)
                    _exec.PendingWaitCondAst = ai;
            }
            if (sig.Arity != argc)
            {
                _exec.Log.Error("'" + sig.Name + "' called with " + argc +
                                " args, expects " + sig.Arity);
                return FsmValue.Void;
            }
            _exec.CallCount++;
            return _exec.Dispatcher.Dispatch(id, args, _exec);
        }

        private bool AsBool(FsmValue v)
        {
            if (v.Kind != FsmValueKind.Bool)
            {
                _exec.Log.Error("expected bool, found " + v.Kind);
                return false;
            }
            return v.B;
        }

        private bool ValuesEqual(FsmValue a, FsmValue b)
        {
            if (a.Kind != b.Kind)
            {
                _exec.Log.Error("'==' needs the exact same type");
                return false;
            }
            switch (a.Kind)
            {
                case FsmValueKind.Int: return a.I == b.I;
                case FsmValueKind.Float: return a.F == b.F;
                case FsmValueKind.Double: return a.D == b.D;
                case FsmValueKind.Bool: return a.B == b.B;
                case FsmValueKind.String:
                    return (a.S ?? string.Empty) == (b.S ?? string.Empty);
                case FsmValueKind.Vec2: return a.X == b.X && a.Y == b.Y;
                case FsmValueKind.Vec3:
                    return a.X == b.X && a.Y == b.Y && a.Z == b.Z;
                case FsmValueKind.Quat:
                    return a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.W == b.W;
                default:
                    _exec.Log.Error("'==' is not defined for " + a.Kind);
                    return false;
            }
        }

        private FsmValue ApplyBinary(byte op, FsmValue a, FsmValue b)
        {
            switch (op)
            {
                case FsmbOp.LogicalAnd:
                    return FsmValue.MakeBool(AsBool(a) && AsBool(b));
                case FsmbOp.LogicalOr:
                    return FsmValue.MakeBool(AsBool(a) || AsBool(b));
                case FsmbOp.EqEq:
                    return FsmValue.MakeBool(ValuesEqual(a, b));
                case FsmbOp.NotEq:
                    return FsmValue.MakeBool(!ValuesEqual(a, b));
                case FsmbOp.Lt:
                case FsmbOp.Gt:
                case FsmbOp.Le:
                case FsmbOp.Ge:
                    return ApplyComparison(op, a, b);
                case FsmbOp.Plus:
                    return ApplyAdd(a, b);
                case FsmbOp.Minus:
                    return ApplySub(a, b);
                case FsmbOp.Star:
                    return ApplyMul(a, b);
                case FsmbOp.Slash:
                    return ApplyDiv(a, b);
                case FsmbOp.FloorDiv:
                    return ApplyFloorDiv(a, b);
                case FsmbOp.Pow:
                    return ApplyPow(a, b);
                default:
                    _exec.Log.Error("BINARY_OP has an unknown operator id");
                    return FsmValue.Void;
            }
        }

        private FsmValue ApplyComparison(byte op, FsmValue a, FsmValue b)
        {
            int ra = FsmValue.RankOf(a.Kind);
            int rb = FsmValue.RankOf(b.Kind);
            if (ra < 0 || rb < 0)
            {
                _exec.Log.Error("comparison needs numeric scalars");
                return FsmValue.MakeBool(false);
            }
            double x = a.ToDouble();
            double y = b.ToDouble();
            switch (op)
            {
                case FsmbOp.Lt: return FsmValue.MakeBool(x < y);
                case FsmbOp.Gt: return FsmValue.MakeBool(x > y);
                case FsmbOp.Le: return FsmValue.MakeBool(x <= y);
                default: return FsmValue.MakeBool(x >= y);
            }
        }

        private bool IsVec(FsmValueKind k)
        {
            return k == FsmValueKind.Vec2 || k == FsmValueKind.Vec3 || k == FsmValueKind.Quat;
        }

        private FsmValue VecCombine(FsmValue a, FsmValue b, bool add)
        {
            float s = add ? 1f : -1f;
            switch (a.Kind)
            {
                case FsmValueKind.Vec2:
                    return FsmValue.MakeVec2(a.X + s * b.X, a.Y + s * b.Y);
                case FsmValueKind.Vec3:
                    return FsmValue.MakeVec3(a.X + s * b.X, a.Y + s * b.Y, a.Z + s * b.Z);
                default:
                    return FsmValue.MakeQuat(a.X + s * b.X, a.Y + s * b.Y,
                                             a.Z + s * b.Z, a.W + s * b.W);
            }
        }

        private FsmValue ApplyAdd(FsmValue a, FsmValue b)
        {
            if (a.Kind == FsmValueKind.String && b.Kind == FsmValueKind.String)
                return FsmValue.MakeString((a.S ?? string.Empty) + (b.S ?? string.Empty));
            if (IsVec(a.Kind) && a.Kind == b.Kind)
                return VecCombine(a, b, true);
            int ra = FsmValue.RankOf(a.Kind);
            int rb = FsmValue.RankOf(b.Kind);
            if (ra >= 0 && rb >= 0)
            {
                int rank = ra > rb ? ra : rb;
                return FsmValue.FromDoubleAtRank(a.ToDouble() + b.ToDouble(), rank);
            }
            _exec.Log.Error("'+' is not defined for " + a.Kind + " and " + b.Kind);
            return FsmValue.Void;
        }

        private FsmValue ApplySub(FsmValue a, FsmValue b)
        {
            if (IsVec(a.Kind) && a.Kind == b.Kind)
                return VecCombine(a, b, false);
            int ra = FsmValue.RankOf(a.Kind);
            int rb = FsmValue.RankOf(b.Kind);
            if (ra >= 0 && rb >= 0)
            {
                int rank = ra > rb ? ra : rb;
                return FsmValue.FromDoubleAtRank(a.ToDouble() - b.ToDouble(), rank);
            }
            _exec.Log.Error("'-' is not defined for " + a.Kind + " and " + b.Kind);
            return FsmValue.Void;
        }

        private FsmValue ApplyMul(FsmValue a, FsmValue b)
        {
            // Scalar x vector (both orders) is the only mixed case.
            if (IsVec(a.Kind) && FsmValue.RankOf(b.Kind) >= 0)
                return VecScale(a, b.ToFloat());
            if (IsVec(b.Kind) && FsmValue.RankOf(a.Kind) >= 0)
                return VecScale(b, a.ToFloat());
            int ra = FsmValue.RankOf(a.Kind);
            int rb = FsmValue.RankOf(b.Kind);
            if (ra >= 0 && rb >= 0)
            {
                int rank = ra > rb ? ra : rb;
                return FsmValue.FromDoubleAtRank(a.ToDouble() * b.ToDouble(), rank);
            }
            _exec.Log.Error("'*' is not defined for " + a.Kind + " and " + b.Kind +
                            " (use dot() for vectors)");
            return FsmValue.Void;
        }

        private FsmValue VecScale(FsmValue v, float s)
        {
            switch (v.Kind)
            {
                case FsmValueKind.Vec2:
                    return FsmValue.MakeVec2(v.X * s, v.Y * s);
                case FsmValueKind.Vec3:
                    return FsmValue.MakeVec3(v.X * s, v.Y * s, v.Z * s);
                default:
                    return FsmValue.MakeQuat(v.X * s, v.Y * s, v.Z * s, v.W * s);
            }
        }

        private FsmValue ApplyDiv(FsmValue a, FsmValue b)
        {
            int ra = FsmValue.RankOf(a.Kind);
            int rb = FsmValue.RankOf(b.Kind);
            if (ra < 0 || rb < 0)
            {
                _exec.Log.Error("'/' is not defined for " + a.Kind + " and " + b.Kind);
                return FsmValue.Void;
            }
            // int / int -> float (true division); otherwise promoted scalar.
            if (ra == 0 && rb == 0)
                return FsmValue.MakeFloat((float)a.I / (float)b.I);
            int rank = ra > rb ? ra : rb;
            if (rank < 1) rank = 1;
            return FsmValue.FromDoubleAtRank(a.ToDouble() / b.ToDouble(), rank);
        }

        private FsmValue ApplyFloorDiv(FsmValue a, FsmValue b)
        {
            if (a.Kind != FsmValueKind.Int || b.Kind != FsmValueKind.Int)
            {
                _exec.Log.Error("'//' needs int operands");
                return FsmValue.MakeInt(0);
            }
            if (b.I == 0)
            {
                _exec.Log.Error("integer '//' by zero");
                return FsmValue.MakeInt(0);
            }
            // Floor toward -infinity (C# '/' truncates toward zero).
            int q = a.I / b.I;
            int r = a.I % b.I;
            if (r != 0 && ((r < 0) != (b.I < 0))) q--;
            return FsmValue.MakeInt(q);
        }

        private FsmValue ApplyPow(FsmValue a, FsmValue b)
        {
            int ra = FsmValue.RankOf(a.Kind);
            int rb = FsmValue.RankOf(b.Kind);
            if (ra < 0 || rb < 0)
            {
                _exec.Log.Error("'**' needs scalar numerics");
                return FsmValue.Void;
            }
            double d = Math.Pow(a.ToDouble(), b.ToDouble());
            int rank = ra > rb ? ra : rb; // same-type stays, mixed promotes
            return FsmValue.FromDoubleAtRank(d, rank);
        }

        private FsmValue Negate(FsmValue a)
        {
            switch (a.Kind)
            {
                case FsmValueKind.Int: return FsmValue.MakeInt(-a.I);
                case FsmValueKind.Float: return FsmValue.MakeFloat(-a.F);
                case FsmValueKind.Double: return FsmValue.MakeDouble(-a.D);
                case FsmValueKind.Vec2: return FsmValue.MakeVec2(-a.X, -a.Y);
                case FsmValueKind.Vec3: return FsmValue.MakeVec3(-a.X, -a.Y, -a.Z);
                case FsmValueKind.Quat:
                    return FsmValue.MakeQuat(-a.X, -a.Y, -a.Z, -a.W);
                default:
                    _exec.Log.Error("unary '-' is not defined for " + a.Kind);
                    return FsmValue.Void;
            }
        }
    }
}
