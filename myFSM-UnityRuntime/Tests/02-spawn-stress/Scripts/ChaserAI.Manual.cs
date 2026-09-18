// Test 02 — the HAND-WRITTEN half of the generated ChaserAI class.
//
// ChaserAI.cs comes from the class generator (Tests/Tools/gen_class.py) and is
// rewritten whenever the .fsm changes; bindings live here.
//
// The module declares two Object3D slots (`self`, `player`) — the same type tag, so
// the generator marks both AMBIGUOUS and expects Bind(...) by hand. `self` is
// obvious; `player` is not, because every chaser is created at run time by the test.
//
// SpawnStressTest sets Target on each cube immediately after AddComponent: a
// runtime component's Awake runs inside AddComponent and its Start (where the
// runtime reads the slots) runs later in the same frame, so the value is always
// there before boot. That is per cube — no static field, so two tests (or two
// spawners) in one project cannot fight over a shared "player".

using UnityEngine;
using MyFSM.Unity;

public sealed partial class ChaserAI
{
    [Tooltip("What this chaser walks towards. Set before boot. Empty: the scene " +
             "object named playerName, else the chaser itself.")]
    public Transform Target;

    [Tooltip("Used when Target is not set: the scene object with this name.")]
    public string playerName = "Player";

    /// <summary>
    /// What the `player` slot actually got bound to (the cube itself when there was
    /// nothing to chase). SpawnStressTest logs it once after the first boot, so the
    /// question "what is the crowd chasing?" has an answer in the console.
    /// </summary>
    public Transform BoundTarget { get; private set; }

    partial void OnBindingsManual()
    {
        Bind(Slot_self, gameObject);

        Transform target = Target;
        if (target == null)
        {
            GameObject found = GameObject.Find(playerName);
            if (found != null) target = found.transform;
        }

        if (target != null)
        {
            BoundTarget = target;
            Bind(Slot_player, target.gameObject);
        }
        else
        {
            // Bind the cube to ITSELF rather than leaving the slot empty, and say so
            // as an error. An empty slot makes getPosition(...) refuse, which stops
            // the chaser dead - far better than the alternative that shipped once:
            // reading (0,0,0) and marching every cube to the world origin, i.e. the
            // middle of the plane, which looked exactly like the scene having its own
            // gravity well.
            BoundTarget = transform;
            Bind(Slot_player, gameObject);
            Debug.LogError("[chaser] '" + name + "' has no target: set ChaserAI.Target "
                           + "before the cube boots, or have an object named '"
                           + playerName + "' in the scene. It will stand still until then.",
                           this);
        }
    }
}
