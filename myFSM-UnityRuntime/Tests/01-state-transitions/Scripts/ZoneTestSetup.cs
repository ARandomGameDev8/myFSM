// Test 01 — one component that finishes the scene: it adds the controller and
// the state-transition recorder, and tells the recorder to log `zone` with every
// transition, so the CSV reads like the acceptance table:
//
//   zone 0..10 -> AtHome | 11..20 -> Above10 | 21.. -> Above40
//
// Put this on the object that carries ZoneBridgeAI (the RequireComponent adds
// the AI for you when you create it from an empty GameObject), press Play, then
// step the zone with W/S or the arrow keys and read the console.

using UnityEngine;

namespace MyFSM.Tests
{
    [RequireComponent(typeof(ZoneBridgeAI))]
    public class ZoneTestSetup : MonoBehaviour
    {
        [Header("Wired automatically")]
        public ZoneBridgeAI ai;
        public ZoneController controller;
        public StateTransitionRecorder recorder;

        [Header("What the transition log should carry")]
        public string[] watchVariables = { "zone" };

        private void Awake()
        {
            if (ai == null) ai = GetComponent<ZoneBridgeAI>();

            if (GetComponent<ZoneController>() == null)
                gameObject.AddComponent<ZoneController>();
            controller = GetComponent<ZoneController>();
            // The marker is created by ZoneController during its own Awake, which
            // has already run if it was added above; the AI's bindings (Start)
            // look it up by name, so ordering is safe either way.
            if (controller.ai == null) controller.ai = ai;

            if (GetComponent<StateTransitionRecorder>() == null)
                gameObject.AddComponent<StateTransitionRecorder>();
            recorder = GetComponent<StateTransitionRecorder>();
            recorder.ai = ai;
            recorder.watchVariables = watchVariables;
            recorder.fileName = "state_transitions.csv";
        }
    }
}
