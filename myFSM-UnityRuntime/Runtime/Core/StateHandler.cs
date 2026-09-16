// myFSM Unity Runtime — state handler: loads state structure, runs entry/
// exit, and drives the per-tick Update + Traversals flow.
//
// Tick order for the head state:
//   1. honor wait()/waitUntil() suspension (skip the tick while suspended);
//   2. honor an externally commanded transition (query server), if any;
//   3. Update round, then Traversals round (either block may be absent —
//      the compiler only requires at least one of Actions/Traversals);
//   4. perform the requested transition, if any.
//
// Every entry (initial boot or a transition) runs the state initializers +
// Start immediately; Update/Traversals begin on the following tick.
// `goto CurrentState` (self-loop) means *stay*: no exit/enter, no Start
// re-run, no broadcast — otherwise trailing default gotos would reset the
// state's temps every tick and time-based transitions could never fire.

using System;
using System.Collections.Generic;

namespace MyFSM.Core
{
    public sealed class StateHandler
    {
        private readonly AiExecution _exec;
        private bool _resolved;
        private int[] _stateRoot;
        private int[] _actionsToken;
        private int[] _traversalsToken;
        private List<int>[] _stateInits;
        private int _entryState = -1;
        private bool _hasExternalRequest;
        private int _externalTarget = -1;
        private string _externalReason;

        public int CurrentState = -1;
        public bool Booted;

        public StateHandler(AiExecution exec)
        {
            _exec = exec;
        }

        public int StateCount { get { return _exec.Module.States.Count; } }
        public int EntryState { get { return _entryState; } }

        public string CurrentStateName
        {
            get
            {
                if (CurrentState < 0 || CurrentState >= _exec.Module.States.Count)
                    return "<none>";
                return _exec.Module.States[CurrentState].Name;
            }
        }

        public string StateName(int state)
        {
            return _exec.Module.States[state].Name;
        }

        private bool EnsureResolved(out string error)
        {
            error = null;
            if (_resolved) return true;
            FsmbModule m = _exec.Module;
            int n = m.States.Count;
            _stateRoot = new int[n];
            _actionsToken = new int[n];
            _traversalsToken = new int[n];
            _stateInits = new List<int>[n];
            for (int s = 0; s < n; s++)
            {
                int root;
                if (!_exec.AstIndexFromAddress(m.States[s].RootAddr, out root))
                {
                    error = "state " + m.States[s].Name + " AST root does not resolve";
                    return false;
                }
                _stateRoot[s] = root;
                _actionsToken[s] = -1;
                _traversalsToken[s] = -1;
                _stateInits[s] = new List<int>();
                List<int> kids = BlockRunner.ResolveChildren(_exec, root);
                for (int i = 0; i < kids.Count; i++)
                {
                    byte t = m.Ast[kids[i]].Type;
                    if (t == FsmbAst.Actions) _actionsToken[s] = kids[i];
                    else if (t == FsmbAst.Traversals) _traversalsToken[s] = kids[i];
                    else if (t == FsmbAst.TempVarDecl || t == FsmbAst.Assign)
                        _stateInits[s].Add(kids[i]);
                    else
                    {
                        error = "state '" + m.States[s].Name + "' has an unexpected " +
                                FsmbAst.TokenName(t) + " child";
                        return false;
                    }
                }
                // Either block may be absent: the compiler only rejects a
                // state with NEITHER Actions nor Traversals. -1 means absent
                // and its rounds are skipped (see Tick / EnterState).
            }
            _resolved = true;
            return true;
        }

        /// <summary>
        /// Enforces the Traversals shape rules in the runtime (the compiler
        /// guarantees them; hand-built modules are rejected here with a clear
        /// message instead of misbehaving): only if/goto; each if holds
        /// exactly one goto; only plain ifs (no else-if/else); no temps; at
        /// least one goto per Traversals block. States without a Traversals
        /// block are skipped (they never transition on their own).
        /// </summary>
        public bool ValidateTraversalRules(out string error)
        {
            if (!EnsureResolved(out error)) return false;
            FsmbModule m = _exec.Module;
            for (int s = 0; s < m.States.Count; s++)
            {
                if (_traversalsToken[s] < 0) continue;
                string sname = m.States[s].Name;
                List<int> kids = BlockRunner.ResolveChildren(_exec, _traversalsToken[s]);
                int gotos = 0;
                for (int i = 0; i < kids.Count; i++)
                {
                    byte t = m.Ast[kids[i]].Type;
                    if (t == FsmbAst.Goto)
                    {
                        gotos++;
                        continue;
                    }
                    if (t == FsmbAst.If)
                    {
                        List<int> body = BlockRunner.ResolveChildren(_exec, kids[i]);
                        if (body.Count != 1 || m.Ast[body[0]].Type != FsmbAst.Goto)
                        {
                            error = "state '" + sname +
                                    "': each if inside Traversals must contain only one goto";
                            return false;
                        }
                        gotos++;
                        continue;
                    }
                    if (t == FsmbAst.ElseIf || t == FsmbAst.Else)
                    {
                        error = "state '" + sname +
                                "': Traversals allows only if (no else-if/else)";
                        return false;
                    }
                    if (t == FsmbAst.TempVarDecl)
                    {
                        error = "state '" + sname +
                                "': temp declarations are not allowed in Traversals";
                        return false;
                    }
                    error = "state '" + sname + "': only if and goto are allowed in " +
                            "Traversals (found " + FsmbAst.TokenName(t) + ")";
                    return false;
                }
                if (gotos < 1)
                {
                    error = "state '" + sname +
                            "': Traversals must contain at least one goto";
                    return false;
                }
            }
            error = null;
            return true;
        }

        public bool Boot(out string error)
        {
            error = null;
            if (!EnsureResolved(out error)) return false;
            FsmbModule m = _exec.Module;
            _entryState = -1;
            for (int s = 0; s < m.States.Count; s++)
            {
                byte[] data = m.Ast[_stateRoot[s]].Data;
                if (data.Length == 1 && data[0] == 1) _entryState = s;
            }
            if (_entryState < 0)
            {
                error = "module has no entry state";
                return false;
            }
            CurrentState = _entryState;
            EnterState(CurrentState);
            Booted = true;
            return true;
        }

        /// <summary>
        /// Commands a transition from outside the AI (query server). Honored
        /// at the next tick boundary, before that tick's Update round.
        /// </summary>
        public void RequestExternalTransition(int to, string reason)
        {
            _hasExternalRequest = true;
            _externalTarget = to;
            _externalReason = reason;
        }

        public bool Tick(out StateChangeInfo change)
        {
            change = null;
            if (!Booted)
            {
                _exec.Log.Error("AI ticked before boot");
                return false;
            }
            _exec.TickCount++;

            // 1. wait()/waitUntil() suspension.
            if (_exec.IsSuspended)
            {
                if (_exec.HasWaitCondition)
                {
                    if (!_exec.Evaluator.EvalCondition(_exec.WaitCondAst)) return true;
                    _exec.ClearSuspension();
                }
                else
                {
                    if (_exec.Time.Time < _exec.ResumeAtTime) return true;
                    _exec.ClearSuspension();
                }
            }

            // 2. Externally commanded transition first.
            if (_hasExternalRequest)
            {
                _hasExternalRequest = false;
                if (_externalTarget < 0 || _externalTarget >= StateCount)
                {
                    _exec.Log.Error("external transition target out of range");
                }
                else if (_externalTarget != CurrentState)
                {
                    int from = CurrentState;
                    ExitState(from);
                    CurrentState = _externalTarget;
                    EnterState(CurrentState);
                    change = MakeChange(from, CurrentState,
                                        _externalReason ?? "external command");
                    return true;
                }
            }

            // 3. Update round, then Traversals round (absent blocks skip).
            _exec.ClearTransitionRequest();
            int actions = _actionsToken[CurrentState];
            int trav = _traversalsToken[CurrentState];
            if (actions >= 0) _exec.UpdateServer.ServeTickRound(actions);
            if (trav >= 0) _exec.Traversal.ServeRound(trav);

            // 4. Perform the requested transition, if any.
            if (_exec.HasTransitionRequest)
            {
                int to = _exec.RequestedState;
                string reason = _exec.TransitionReason;
                _exec.ClearTransitionRequest();
                if (to < 0 || to >= StateCount)
                {
                    _exec.Log.Error("transition target out of range");
                    return true;
                }
                if (to == CurrentState) return true; // stay: no exit/enter
                int from = CurrentState;
                ExitState(from);
                CurrentState = to;
                EnterState(CurrentState);
                change = MakeChange(from, to, reason ?? "goto");
            }
            return true;
        }

        private StateChangeInfo MakeChange(int from, int to, string reason)
        {
            return new StateChangeInfo
            {
                FromState = from,
                ToState = to,
                FromName = StateName(from),
                ToName = StateName(to),
                Reason = reason
            };
        }

        private void EnterState(int s)
        {
            // State-body frame: created on entry, destroyed on exit.
            _exec.Vars.PushFrame();
            BlockRunner.ServeStatements(_exec, _stateInits[s], true, false, 0);
            if (_actionsToken[s] >= 0)
                _exec.StartServer.ServeEntryRound(_actionsToken[s]);
        }

        private void ExitState(int s)
        {
            _exec.Vars.PopFrame();
        }
    }
}
