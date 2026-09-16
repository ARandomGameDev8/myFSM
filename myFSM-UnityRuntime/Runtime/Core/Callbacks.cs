// myFSM Unity Runtime — statement clients + callbacks + block serving.
//
// Every DSL statement is a *client* of one of the three callback servers.
// Each client owns a callback (an AiCallback subclass) that runs when the
// client's turn comes up, in source order:
//
// - a non-conditional statement's callback always runs on its turn;
// - a conditional (if / else-if / else chain) calls its children's callbacks
//   in order when its condition holds. Chains are C-like: the first true
//   branch is served and the rest of the chain is skipped. (Statements after
//   the chain are still served; only the losing branches are skipped.)
//
// Temp declarations open a lifetime in the innermost scope frame; every
// container body (Actions, Start, Update, if/else-if/else) is served with a
// fresh frame, so a temp dies exactly when its parent block closes.

using System;
using System.Collections.Generic;

namespace MyFSM.Core
{
    /// <summary>The callback every statement client serves through.</summary>
    public abstract class AiCallback
    {
        public abstract void Invoke(CallbackContext ctx);
    }

    public sealed class CallbackContext
    {
        public readonly AiExecution Exec;
        public readonly int TokenIndex;
        /// <summary>Set by goto: stop serving the rest of this block.</summary>
        public bool StopServing;

        public CallbackContext(AiExecution exec, int tokenIndex)
        {
            Exec = exec;
            TokenIndex = tokenIndex;
        }
    }

    /// <summary>A statement (client) plus its callback.</summary>
    public sealed class StatementClient
    {
        public readonly int TokenIndex;
        public readonly AiCallback Callback;

        public StatementClient(int tokenIndex, AiCallback callback)
        {
            TokenIndex = tokenIndex;
            Callback = callback;
        }

        /// <returns>True when the rest of the block must be skipped.</returns>
        public bool Serve(AiExecution exec)
        {
            CallbackContext ctx = new CallbackContext(exec, TokenIndex);
            Callback.Invoke(ctx);
            return ctx.StopServing;
        }
    }

    // ------------------------------------------------------------------
    // Leaf callbacks
    // ------------------------------------------------------------------

    internal sealed class TempDeclCallback : AiCallback
    {
        public override void Invoke(CallbackContext ctx)
        {
            AiExecution exec = ctx.Exec;
            FsmAstToken t = exec.Module.Ast[ctx.TokenIndex];
            uint addr = AstCodec.ReadU32LE(t.Data, 1);
            VarRef vr;
            if (!exec.Vars.TryResolveAddress(addr, out vr) ||
                vr.Section != FsmbFormat.SecTemp)
            {
                exec.Log.Error("TEMP_VAR_DECL variable address does not resolve");
                return;
            }
            if (exec.Vars.GetTempTag(vr.Index) != t.Data[0])
            {
                exec.Log.Error("TEMP_VAR_DECL tag does not match its temp entry");
                return;
            }
            string err;
            if (!exec.Vars.DeclareTemp(vr.Index, out err))
                exec.Log.Error(err);
            // The initializer (if any) is the sibling ASSIGN token, served as
            // its own client right after this one.
        }
    }

    internal sealed class AssignCallback : AiCallback
    {
        public override void Invoke(CallbackContext ctx)
        {
            AiExecution exec = ctx.Exec;
            FsmAstToken t = exec.Module.Ast[ctx.TokenIndex];
            uint targetAddr = AstCodec.ReadU32LE(t.Data, 0);
            uint valueAddr = AstCodec.ReadU32LE(t.Data, 4);
            int valueIdx;
            if (!exec.AstIndexFromAddress(valueAddr, out valueIdx))
            {
                exec.Log.Error("ASSIGN value address does not resolve");
                return;
            }
            FsmValue value = exec.Evaluator.Eval(valueIdx);
            VarRef vr;
            if (!exec.Vars.TryResolveAddress(targetAddr, out vr))
            {
                exec.Log.Error("ASSIGN target address does not resolve");
                return;
            }
            switch (vr.Section)
            {
                case FsmbFormat.SecGlobal:
                    exec.Log.Error("cannot assign to const '" +
                                   exec.Vars.GetConstName(vr.Index) + "'");
                    return;
                case FsmbFormat.SecRuntime:
                    {
                        byte tag = exec.Vars.GetRuntimeTag(vr.Index);
                        if (!ValueMatchesTag(value, tag))
                        {
                            exec.Log.Error("cannot assign " + value.Kind + " to '" +
                                           exec.Vars.GetRuntimeName(vr.Index) + "' (" +
                                           DslTypes.NameOf(tag) + ")");
                            return;
                        }
                        exec.Vars.SetRuntime(vr.Index, value);
                        return;
                    }
                case FsmbFormat.SecTemp:
                    if (!exec.Vars.IsTempAlive(vr.Index))
                    {
                        exec.Log.Error("temp '" + exec.Vars.GetTempName(vr.Index) +
                                       "' is out of scope");
                        return;
                    }
                    {
                        byte tag = exec.Vars.GetTempTag(vr.Index);
                        if (!ValueMatchesTag(value, tag))
                        {
                            exec.Log.Error("cannot assign " + value.Kind + " to temp '" +
                                           exec.Vars.GetTempName(vr.Index) + "' (" +
                                           DslTypes.NameOf(tag) + ")");
                            return;
                        }
                        exec.Vars.SetTemp(vr.Index, value);
                        return;
                    }
                default:
                    exec.Log.Error("ASSIGN target address does not resolve");
                    return;
            }
        }

        internal static bool ValueMatchesTag(FsmValue v, byte tag)
        {
            switch (tag)
            {
                case FsmbType.Int: return v.Kind == FsmValueKind.Int;
                case FsmbType.Float: return v.Kind == FsmValueKind.Float;
                case FsmbType.Double: return v.Kind == FsmValueKind.Double;
                case FsmbType.Bool: return v.Kind == FsmValueKind.Bool;
                case FsmbType.String: return v.Kind == FsmValueKind.String;
                case FsmbType.Vector2: return v.Kind == FsmValueKind.Vec2;
                case FsmbType.Vector3: return v.Kind == FsmValueKind.Vec3;
                case FsmbType.Quaternion: return v.Kind == FsmValueKind.Quat;
                default:
                    return v.Kind == FsmValueKind.Handle && v.TypeTag == tag;
            }
        }
    }

    internal sealed class CallCallback : AiCallback
    {
        public override void Invoke(CallbackContext ctx)
        {
            // Statement-level call: evaluate, discard the result.
            ctx.Exec.Evaluator.Eval(ctx.TokenIndex);
        }
    }

    internal sealed class GotoCallback : AiCallback
    {
        public override void Invoke(CallbackContext ctx)
        {
            AiExecution exec = ctx.Exec;
            FsmAstToken t = exec.Module.Ast[ctx.TokenIndex];
            uint targetAddr = AstCodec.ReadU32LE(t.Data, 0);
            int state;
            if (!exec.StateIndexFromAddress(targetAddr, out state))
            {
                exec.Log.Error("GOTO target does not resolve to a state entry");
                return;
            }
            exec.RequestTransition(state, "goto " + exec.Module.States[state].Name);
            ctx.StopServing = true;
        }
    }

    /// <summary>Serves a START / UPDATE phase container's children in order,
    /// inside the phase's own scope frame.</summary>
    internal sealed class PhaseCallback : AiCallback
    {
        public override void Invoke(CallbackContext ctx)
        {
            AiExecution exec = ctx.Exec;
            if (BlockRunner.ServeBlock(exec, ctx.TokenIndex, true, false))
                ctx.StopServing = true;
        }
    }

    /// <summary>
    /// One if / else-if / else chain (C-like: first true branch wins, the rest
    /// of the chain is skipped). Each branch body gets its own scope frame.
    /// </summary>
    internal sealed class IfChainCallback : AiCallback
    {
        private readonly List<int> _branches;
        private readonly bool _isTraversal;

        public IfChainCallback(List<int> branches, bool isTraversal)
        {
            _branches = branches;
            _isTraversal = isTraversal;
        }

        public override void Invoke(CallbackContext ctx)
        {
            AiExecution exec = ctx.Exec;
            for (int b = 0; b < _branches.Count; b++)
            {
                FsmAstToken br = exec.Module.Ast[_branches[b]];
                bool take;
                if (br.Type == FsmbAst.Else)
                {
                    take = true;
                }
                else
                {
                    uint condAddr = AstCodec.ReadU32LE(br.Data, 0);
                    int condIdx;
                    if (!exec.AstIndexFromAddress(condAddr, out condIdx))
                    {
                        exec.Log.Error("condition address does not resolve");
                        return;
                    }
                    take = exec.Evaluator.EvalCondition(condIdx);
                }
                if (!take) continue;
                exec.Vars.PushFrame();
                try
                {
                    List<int> kids = BlockRunner.ResolveChildren(exec, _branches[b]);
                    // Temps are allowed in Actions branches only; the loader
                    // already rejected any temp under Traversals.
                    if (BlockRunner.ServeStatements(exec, kids, !_isTraversal, _isTraversal, 0))
                        ctx.StopServing = true;
                }
                finally
                {
                    exec.Vars.PopFrame();
                }
                return; // first true branch wins
            }
        }
    }

    // ------------------------------------------------------------------
    // Factory + block serving
    // ------------------------------------------------------------------

    internal static class ClientFactory
    {
        public static StatementClient Create(AiExecution exec, int tokenIndex,
                                             bool allowTempDecls)
        {
            byte type = exec.Module.Ast[tokenIndex].Type;
            switch (type)
            {
                case FsmbAst.TempVarDecl:
                    if (!allowTempDecls)
                    {
                        exec.Log.Error("temp declarations are not allowed here");
                        return null;
                    }
                    return new StatementClient(tokenIndex, new TempDeclCallback());
                case FsmbAst.Assign:
                    return new StatementClient(tokenIndex, new AssignCallback());
                case FsmbAst.FunctionCall:
                    return new StatementClient(tokenIndex, new CallCallback());
                case FsmbAst.Goto:
                    return new StatementClient(tokenIndex, new GotoCallback());
                case FsmbAst.Start:
                case FsmbAst.Update:
                    return new StatementClient(tokenIndex, new PhaseCallback());
                default:
                    exec.Log.Error("token " + FsmbAst.TokenName(type) +
                                   " is not a statement");
                    return null;
            }
        }
    }

    public static class BlockRunner
    {
        public static List<int> ResolveChildren(AiExecution exec, int containerToken)
        {
            List<int> kids = new List<int>();
            FsmAstToken cont = exec.Module.Ast[containerToken];
            for (int i = 0; i < cont.Children.Length; i++)
            {
                int idx;
                if (exec.AstIndexFromAddress(cont.Children[i], out idx))
                    kids.Add(idx);
                else
                    exec.Log.Error("ast entry has a bad child address");
            }
            return kids;
        }

        /// <summary>
        /// Serves a container's children in order inside a fresh scope frame.
        /// </summary>
        /// <returns>True when the caller must stop serving (goto fired).</returns>
        public static bool ServeBlock(AiExecution exec, int containerToken,
                                      bool allowTempDecls, bool isTraversal)
        {
            exec.Vars.PushFrame();
            try
            {
                return ServeStatements(exec, ResolveChildren(exec, containerToken),
                                       allowTempDecls, isTraversal, 0);
            }
            finally
            {
                exec.Vars.PopFrame();
            }
        }

        /// <summary>Serves statement tokens in order, grouping if-chains.</summary>
        /// <returns>True when the caller must stop serving (goto fired).</returns>
        public static bool ServeStatements(AiExecution exec, List<int> stmtTokens,
                                           bool allowTempDecls, bool isTraversal,
                                           byte skipContainer)
        {
            for (int i = 0; i < stmtTokens.Count; i++)
            {
                byte type = exec.Module.Ast[stmtTokens[i]].Type;
                if (type == skipContainer) continue;
                if (type == FsmbAst.If)
                {
                    List<int> chain = new List<int>();
                    chain.Add(stmtTokens[i]);
                    int j = i + 1;
                    while (j < stmtTokens.Count)
                    {
                        byte nt = exec.Module.Ast[stmtTokens[j]].Type;
                        if (nt == FsmbAst.ElseIf || nt == FsmbAst.Else)
                        {
                            chain.Add(stmtTokens[j]);
                            j++;
                        }
                        else break;
                    }
                    i = j - 1;
                    IfChainCallback cb = new IfChainCallback(chain, isTraversal);
                    CallbackContext ctx = new CallbackContext(exec, chain[0]);
                    cb.Invoke(ctx);
                    if (ctx.StopServing) return true;
                    continue;
                }
                if (type == FsmbAst.ElseIf || type == FsmbAst.Else)
                {
                    exec.Log.Error("else-if/else without a leading if");
                    continue;
                }
                StatementClient c = ClientFactory.Create(exec, stmtTokens[i], allowTempDecls);
                if (c == null) continue;
                if (c.Serve(exec)) return true;
            }
            return false;
        }
    }
}
