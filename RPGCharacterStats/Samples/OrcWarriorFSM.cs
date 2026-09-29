// OrcWarriorFSM — the section 7.6 example's AI script: a partial class that
// inherits from a class named AIInstance (the contract section 7.4 spells out:
// any class of that name, anywhere; the subclass must be partial).
//
// In a myFSM project this IS MyFSM.Unity.AIInstance and the boot logic comes
// from the base; here the base is declared in this sample folder so the
// package is self-contained. Either way the RPG system only requires: the
// base chain ends in a class NAMED "AIInstance" and the subclass is partial.

using UnityEngine;

// The contract base — in the myFSM world this lives in the runtime package.
public class AIInstance : MonoBehaviour
{
    public string CurrentStateName = "idle";

    protected virtual void Update()
    {
        // A real AIInstance ticks its FSM; the sample just idles.
    }
}

/// <summary>The Orc's brain: chases the nearest "Player", attacks in range.
/// Declared partial — section 7.4 requires it and generated FSM classes
/// extend this class with their own partial half.</summary>
public partial class OrcWarriorFSM : AIInstance
{
    public float attackRange = 1.5f;

    partial void ChaseStep();

    protected override void Update()
    {
        ChaseStep();
    }

    private Transform FindNearestPlayer()
    {
        GameObject player = GameObject.Find("Player_001");
        return player != null ? player.transform : null;
    }
}
