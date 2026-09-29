// OrcWarriorFSM — the section 7.6 example's AI script: a partial class that
// inherits from a class named AIInstance (the contract section 7.4 spells out:
// any class of that name, anywhere; the subclass must be partial).
//
// The stub base below exists ONLY so this package compiles and runs headless
// (Sandbox~) without the myFSM runtime. It lives inside namespace
// RPGCharacterStats on purpose: a global-namespace "AIInstance" would silently
// shadow MyFSM.Unity.AIInstance in any project that also contains myFSM
// (name lookup walks outward from the using-less global scope first), which
// broke every "class PatrolAI : AIInstance" override with CS0115.
//
// In a real myFSM project you do NOT need this stub: delete this file's base
// class or let OrcWarriorFSM extend MyFSM.Unity.AIInstance directly — the RPG
// contract only requires the base CHAIN to end in a class NAMED "AIInstance"
// (any namespace) and the subclass to be partial.

using UnityEngine;

namespace RPGCharacterStats
{
    // The contract base — in the myFSM world this is MyFSM.Unity.AIInstance.
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
}
