// Test 03 — the HAND-WRITTEN half of the generated TestAI class.
//
// TestAI.cs is generated (Tests/Tools/gen_class.py) from the compiled chase
// module; bindings live here. The three slots:
//
//   agent  NavMeshAgent, unique tag -> auto-bound to this GameObject's
//          NavMeshAgent component. No decision needed.
//   dest   Object3D, unique tag -> auto-bind resolves it to THIS GameObject,
//          which would make the runner chase itself (zero distance, instant
//          "arrival"). Overridden here with the maze's ActiveTarget: the
//          assigned target (it may move, e.g. test 04's walker), or the
//          static marker the maze created on the exit area.
//   speed  float value slot -> seeded from MazeTestSetup.runnerSpeed so the
//          inspector knob on the setup component controls the chase.
//
// Bindings run during boot, after auto-bind (later bindings win) and before
// the entry state's Start{} — so the goal below is posted with the final
// values on the very first tick.

using UnityEngine;
using MyFSM.Core;
using MyFSM.Unity;
using MyFSM.Tests;

public sealed partial class TestAI
{
    partial void OnBindingsManual()
    {
        MazeGeneratorController maze = MazeGeneratorController.Current;
        if (maze == null)
        {
            Debug.LogWarning("[testai] no MazeGeneratorController in the scene — bind `dest` " +
                             "by hand or add the maze setup. Left alone, the runner chases " +
                             "itself: the auto-bind of the module's only Object3D slot points " +
                             "at this GameObject.", this);
        }
        else if (maze.ActiveTarget != null)
        {
            Bind(Slot_dest, maze.ActiveTarget.gameObject);
        }
        else
        {
            Debug.LogWarning("[testai] the maze has no ActiveTarget yet — the runner has " +
                             "nothing to chase.", this);
        }

        MazeTestSetup setup = MazeTestSetup.Current;
        SetBoundValue(Slot_speed, FsmValue.MakeFloat(setup != null ? setup.runnerSpeed : 3.5f));
    }
}
