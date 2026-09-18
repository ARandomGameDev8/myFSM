// myFSM Unity Runtime — per-AI execution context.
//
// One AiExecution drives one AI instance (one loaded .fsmb module): it owns
// the variable table, the expression evaluator, the three callback servers
// and the state handler. The Unity layer (AIInstance) creates exactly one of
// these, applies binding-slot values, then boots and ticks it.
//
// Lifecycle: Create -> bind runtime slots -> Boot (selects the entry head,
// enters nothing) -> Tick every frame (the first tick enters the head and
// runs state initializers + Start, then Update/Traversals like any tick).

using System;
using System.Collections.Generic;

namespace MyFSM.Core
{
    /// <summary>Fatal, fail-fast runtime contract violation (bad module, bad
    /// boot). Per-tick serving errors never throw: they log and yield default
    /// values so one bad statement cannot kill the player loop.</summary>
    public sealed class FsmRuntimeException : Exception
    {
        public FsmRuntimeException(string message) : base(message) { }
    }

    public interface ITimeProvider
    {
        float Time { get; }
        float DeltaTime { get; }
    }

    public interface IExecutionLog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    public interface IFunctionDispatcher
    {
        FsmValue Dispatch(ushort functionId, FsmValue[] args, AiExecution exec);
    }

    public sealed class StateChangeInfo
    {
        public int FromState;
        public int ToState;
        public string FromName;
        public string ToName;
        public string Reason;
    }

    public sealed class AiExecution
    {
        public readonly FsmbModule Module;
        public readonly string ModuleName;
        public readonly int InstanceId;
        public readonly string InstanceName;
        public readonly VariableTable Vars;
        public readonly ExpressionEvaluator Evaluator;
        public readonly StateHandler States;
        public readonly StartActionServer StartServer;
        public readonly UpdateActionServer UpdateServer;
        public readonly TraversalServer Traversal;
        public readonly IFunctionDispatcher Dispatcher;
        public readonly ITimeProvider Time;
        public readonly IExecutionLog Log;

        /// <summary>
        /// Keys already reported through <see cref="ErrorOnce"/>/<see cref="WarnOnce"/>.
        /// A misconfigured AI hits the same problem on EVERY tick, and there are as
        /// many AIs as the scene holds: reporting each key once per AI keeps the
        /// console readable while still naming every distinct mistake.
        /// </summary>
        private readonly HashSet<string> _onceKeys = new HashSet<string>();

        /// <summary>Logs an error once per key (per AI). Returns true if it logged.</summary>
        public bool ErrorOnce(string key, string message)
        {
            if (!_onceKeys.Add(key)) return false;
            Log.Error(message);
            return true;
        }

        /// <summary>Logs a warning once per key (per AI). Returns true if it logged.</summary>
        public bool WarnOnce(string key, string message)
        {
            if (!_onceKeys.Add(key)) return false;
            Log.Warn(message);
            return true;
        }

        private readonly Dictionary<uint, int> _astByOffset =
            new Dictionary<uint, int>();
        private readonly Dictionary<uint, int> _stateByOffset =
            new Dictionary<uint, int>();

        public long TickCount;
        public long CallCount;

        // wait() / waitUntil() suspension: while set, Update + Traversals are
        // skipped each tick until time elapses or the condition turns true.
        public bool IsSuspended;
        public float ResumeAtTime;
        public bool HasWaitCondition;
        public int WaitCondAst = -1;

        // Transition requested while serving Traversals (first if wins).
        public bool HasTransitionRequest;
        public int RequestedState = -1;
        public string TransitionReason;

        // Stashed by the evaluator when it serves a waitUntil call: the AST
        // index of the boolean condition, re-evaluated every tick.
        public int PendingWaitCondAst = -1;

        private AiExecution(FsmbModule module, string moduleName, int instanceId,
                            string instanceName, IFunctionDispatcher dispatcher,
                            ITimeProvider time, IExecutionLog log)
        {
            Module = module;
            ModuleName = moduleName;
            InstanceId = instanceId;
            InstanceName = instanceName;
            Dispatcher = dispatcher;
            Time = time;
            Log = log;

            for (int i = 0; i < module.AstEntryOffsets.Count; i++)
                _astByOffset[module.AstEntryOffsets[i]] = i;
            for (int i = 0; i < module.StateEntryOffsets.Count; i++)
                _stateByOffset[module.StateEntryOffsets[i]] = i;

            string tableError;
            Vars = new VariableTable(module, out tableError);
            if (tableError != null)
                throw new FsmRuntimeException(tableError);

            Evaluator = new ExpressionEvaluator(this);
            StartServer = new StartActionServer(this);
            UpdateServer = new UpdateActionServer(this);
            Traversal = new TraversalServer(this);
            States = new StateHandler(this);
        }

        public static AiExecution Create(FsmbModule module, string moduleName,
                                         int instanceId, string instanceName,
                                         IFunctionDispatcher dispatcher,
                                         ITimeProvider time, IExecutionLog log,
                                         out string error)
        {
            error = null;
            AiExecution exec;
            try
            {
                exec = new AiExecution(module, moduleName, instanceId, instanceName,
                                       dispatcher, time, log);
            }
            catch (FsmRuntimeException ex)
            {
                error = ex.Message;
                return null;
            }

            int entries = 0;
            for (int i = 0; i < module.States.Count; i++)
            {
                int root;
                if (exec.AstIndexFromAddress(module.States[i].RootAddr, out root) &&
                    module.Ast[root].Data.Length == 1 && module.Ast[root].Data[0] == 1)
                {
                    entries++;
                }
            }
            if (entries != 1)
            {
                error = "module must declare exactly one entry state, found " + entries;
                return null;
            }
            if (!exec.States.ValidateTraversalRules(out error))
            {
                return null;
            }
            return exec;
        }

        public bool AstIndexFromAddress(uint addr, out int index)
        {
            index = -1;
            if (FsmbFormat.AddressSection(addr) != FsmbFormat.SecAst) return false;
            return _astByOffset.TryGetValue(FsmbFormat.AddressOffset(addr), out index);
        }

        public bool StateIndexFromAddress(uint addr, out int index)
        {
            index = -1;
            if (FsmbFormat.AddressSection(addr) != FsmbFormat.SecState) return false;
            return _stateByOffset.TryGetValue(FsmbFormat.AddressOffset(addr), out index);
        }

        public void RequestTransition(int stateIndex, string reason)
        {
            // First request wins within a tick (first true if in Traversals).
            if (HasTransitionRequest) return;
            HasTransitionRequest = true;
            RequestedState = stateIndex;
            TransitionReason = reason;
        }

        public void ClearTransitionRequest()
        {
            HasTransitionRequest = false;
            RequestedState = -1;
            TransitionReason = null;
        }

        public void SuspendUntil(float time)
        {
            IsSuspended = true;
            HasWaitCondition = false;
            WaitCondAst = -1;
            ResumeAtTime = time;
        }

        public void SuspendOnCondition(int condAst)
        {
            IsSuspended = true;
            HasWaitCondition = true;
            WaitCondAst = condAst;
        }

        public void ClearSuspension()
        {
            IsSuspended = false;
            HasWaitCondition = false;
            WaitCondAst = -1;
        }

        public bool Boot(out string error)
        {
            return States.Boot(out error);
        }

        public bool Tick(out StateChangeInfo change)
        {
            return States.Tick(out change);
        }
    }
}
