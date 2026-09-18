// Test 03 — the HAND-WRITTEN half of the generated MazeRunnerAI class.
//
// MazeRunnerAI.cs is generated (Tests/Tools/gen_class.py) from the compiled
// maze runner module; this file supplies the bindings, because the module has
// two Object3D slots (`self`, `exit`) and two Object3D slots cannot be told
// apart by type: the generator marks them AMBIGUOUS and expects Bind(...) here.
//
// `exit` is the object MazeGeneratorController creates for the far cell, so the
// generator hands it over through a static field before/while it builds the
// scene — the same trick as test 02, and the reason a runtime-created object
// cannot simply be dragged into the inspector.

using UnityEngine;
using MyFSM.Unity;
using MyFSM.Tests; // MazeGeneratorController (the maze the runner is dropped into)

public sealed partial class MazeRunnerAI
{
    [Header("MazeRunner bindings (manual)")]
    [Tooltip("The exit the runner must reach. MazeGeneratorController fills this in.")]
    public Transform exit;

    partial void OnBindingsManual()
    {
        Bind(Slot_self, gameObject);

        Transform target = exit;
        if (target == null && MazeGeneratorController.Current != null)
            target = MazeGeneratorController.Current.Exit;

        if (target != null)
        {
            Bind(Slot_exit, target.gameObject);
        }
        else
        {
            Debug.LogWarning("[mazerunner] no exit bound — build the maze first "
                             + "(MazeGeneratorController), or drag the exit onto this component. "
                             + "The module's findPath() call needs the slot.", this);
        }

        // Value slots start at zero; the module writes `plannedLength` itself on
        // Run.Start and flips `arrived` on arrival. Seeding them keeps the
        // recorder's first read honest instead of "?".
        SetBoundValue(Slot_plannedLength, FsmValue.MakeFloat(0f));
        SetBoundValue(Slot_arrived, FsmValue.MakeBool(false));
    }
}
