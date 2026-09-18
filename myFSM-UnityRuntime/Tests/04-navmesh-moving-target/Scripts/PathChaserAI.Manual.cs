// Test 04 — the HAND-WRITTEN half of the generated PathChaserAI class.
//
// PathChaserAI.cs is generated (Tests/Tools/gen_class.py) from the compiled
// moving-target module; bindings live here. The module has two Object3D slots
// (`self`, `target`) — same tag, so the generator marks both AMBIGUOUS and this
// file decides which object is which.
//
// The target is the walking object of the maze (MazeTargetWalker). Because the
// walker is a scene object it can also be dragged onto the `target` field; the
// FindObjectOfType fallback keeps a scene that was set up in a hurry working.

// MyFSM.Core is where FsmValue lives: the SetBoundValue() calls below name it,
// and a using is per file - importing MyFSM.Unity does not bring MyFSM.Core in.
using UnityEngine;
using MyFSM.Core;
using MyFSM.Unity;
using MyFSM.Tests;

public sealed partial class PathChaserAI
{
    [Header("PathChaser bindings (manual)")]
    [Tooltip("The object to catch. Empty: the scene's MazeTargetWalker.")]
    public Transform target;

    partial void OnBindingsManual()
    {
        Bind(Slot_self, gameObject);

        Transform chased = target;
        if (chased == null)
        {
            MazeTargetWalker walker = FindObjectOfType<MazeTargetWalker>();
            if (walker != null) chased = walker.transform;
        }

        if (chased != null)
        {
            Bind(Slot_target, chased.gameObject);
        }
        else
        {
            Debug.LogWarning("[pathchaser] no target bound — add a MazeTargetWalker to the "
                             + "scene or drag one onto this component. The module re-plans "
                             + "against this slot every tick.", this);
        }

        // Both value slots are written by the module itself; seeding them keeps
        // the recorder's first read meaningful instead of "?".
        SetBoundValue(Slot_plannedLength, FsmValue.MakeFloat(0f));
        SetBoundValue(Slot_caught, FsmValue.MakeBool(false));
    }
}
