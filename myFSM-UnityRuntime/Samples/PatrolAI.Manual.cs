// Hand-written companion to the generated PatrolAI. This file is NEVER
// rewritten by the burst compiler: durable manual bindings live here, in the
// partial OnBindingsManual() hook the generated file declares.
using UnityEngine;

public sealed partial class PatrolAI
{
    partial void OnBindingsManual()
    {
        // waypointA/waypointB share Object3D, so the base leaves them alone
        // (ambiguous) - bind them by hand:
        Bind(Slot_waypointA, GameObject.Find("WaypointA"));
        Bind(Slot_waypointB, GameObject.Find("WaypointB"));
        // agent (unique NavMeshAgent) was auto-bound to this GameObject's own
        // agent component - override here only if some other agent should
        // drive this AI:
        // Bind(Slot_agent, GameObject.Find("PatrolBot").GetComponent<UnityEngine.AI.NavMeshAgent>());
    }
}
