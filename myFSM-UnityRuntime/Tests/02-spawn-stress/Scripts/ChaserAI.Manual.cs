// Test 02 — the HAND-WRITTEN half of the generated ChaserAI class.
//
// ChaserAI.cs comes from the class generator (Tests/Tools/gen_class.py) and is
// thrown away and rewritten whenever the .fsm changes; bindings live here.
//
// The module declares two Object3D slots (`self`, `player`) — same type tag, so
// the generator marks both AMBIGUOUS and expects Bind(...) by hand. `self` is
// obvious; `player` is not, because each chaser is created at RUNTIME by
// SpawnStressTest and cannot have a scene reference dragged onto it. The test
// therefore publishes the player through the static field below before it
// instantiates the cubes (a fallback GameObject.Find keeps it working when the
// cube is dropped into a scene by hand).

using UnityEngine;
using MyFSM.Unity;

public sealed partial class ChaserAI
{
    [Tooltip("Used when no player has been published: the scene object with this name.")]
    public string playerName = "Player";

    /// <summary>
    /// The object every chaser hunts. SpawnStressTest sets this once before it
    /// instantiates cubes; the value survives across scene loads only if the
    /// test sets it again (it is plain static state, intentionally simple).
    /// </summary>
    public static Transform Player;

    partial void OnBindingsManual()
    {
        Bind(Slot_self, gameObject);

        Transform target = Player;
        if (target == null)
        {
            GameObject found = GameObject.Find(playerName);
            if (found != null) target = found.transform;
        }

        if (target != null)
        {
            Bind(Slot_player, target.gameObject);
        }
        else
        {
            // Bind the cube to ITSELF instead of leaving the slot unbound, and
            // say so as an error. An unresolved handle makes getPosition(...)
            // return (0,0,0), so a chaser with no player would walk to the WORLD
            // ORIGIN — the centre of the plane — which reads as the scene having
            // its own gravity well. Chasing yourself keeps it standing still.
            Bind(Slot_player, gameObject);
            Debug.LogError("[chaser] no player target for " + name
                           + " — set ChaserAI.Player or create an object named '"
                           + playerName + "' before spawning. Until then this cube "
                           + "stands still (without a target it would otherwise walk to "
                           + "the world origin).", this);
        }
    }
}
