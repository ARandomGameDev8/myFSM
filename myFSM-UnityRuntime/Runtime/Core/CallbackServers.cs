// myFSM Unity Runtime — the three logical callback servers.
//
// - StartActionServer: serves each Actions client once, in order, on state
//   entry only (temp initializers at their source positions + the Start{}
//   phase). Equivalent to Unity's Start().
// - UpdateActionServer: serves each Actions client once, in order, every tick
//   (temp initializers re-run + the Update{} phase). Equivalent to Update().
// - TraversalServer: serves each Traversals client once, in order, every tick
//   (after Update). The first conditional whose condition holds fires its
//   goto and becomes the new head of state.
//
// The Actions-body temps are served inside a per-round scope frame, so they
// are visible to both phases' statements positionally yet die at the round's
// end, exactly C-like.

using System;
using System.Collections.Generic;

namespace MyFSM.Core
{
    public sealed class StartActionServer
    {
        private readonly AiExecution _exec;

        public StartActionServer(AiExecution exec)
        {
            _exec = exec;
        }

        /// <summary>Runs once per state entry.</summary>
        public void ServeEntryRound(int actionsToken)
        {
            _exec.Vars.PushFrame();
            try
            {
                List<int> kids = BlockRunner.ResolveChildren(_exec, actionsToken);
                BlockRunner.ServeStatements(_exec, kids, true, false, FsmbAst.Update);
            }
            finally
            {
                _exec.Vars.PopFrame();
            }
        }
    }

    public sealed class UpdateActionServer
    {
        private readonly AiExecution _exec;

        public UpdateActionServer(AiExecution exec)
        {
            _exec = exec;
        }

        /// <summary>Runs every tick while the state is the head.</summary>
        public void ServeTickRound(int actionsToken)
        {
            _exec.Vars.PushFrame();
            try
            {
                List<int> kids = BlockRunner.ResolveChildren(_exec, actionsToken);
                BlockRunner.ServeStatements(_exec, kids, true, false, FsmbAst.Start);
            }
            finally
            {
                _exec.Vars.PopFrame();
            }
        }
    }

    public sealed class TraversalServer
    {
        private readonly AiExecution _exec;

        public TraversalServer(AiExecution exec)
        {
            _exec = exec;
        }

        /// <summary>
        /// Runs every tick after Update. Only plain ifs (single-goto bodies)
        /// and gotos may appear here — everything else is rejected at load.
        /// The first goto that fires wins.
        /// </summary>
        public void ServeRound(int traversalsToken)
        {
            // No scope frame: temps are forbidden in Traversals (load error).
            List<int> kids = BlockRunner.ResolveChildren(_exec, traversalsToken);
            BlockRunner.ServeStatements(_exec, kids, false, true, 0);
        }
    }
}
