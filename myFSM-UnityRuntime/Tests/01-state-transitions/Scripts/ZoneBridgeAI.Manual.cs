// Test 01 — the HAND-WRITTEN half of the generated ZoneBridgeAI class.
//
// ZoneBridgeAI.cs is produced by the class generator (see Tests/Tools/gen_class.py)
// and is regenerated whenever the .fsm changes. Binding code must not live there,
// because a recompile would throw it away. The generator emits
//
//     partial void OnBindingsManual();
//
// and this file implements it — the same pattern the Unity runtime documents in
// Docs/AIInstance.md ("Bindings must be applied before boot").
//
// Why manual bindings are REQUIRED here: the module declares two Object3D slots
// (`self` and `marker`). Auto-binding maps a slot to this GameObject by its type
// tag, so with two slots sharing a tag it cannot know which is which — the
// generator prints "AMBIGUOUS (2x) — Bind(Slot_x, ...)" and leaves the decision
// to you. The value slots (Vector3 `home`, int `zone`) are also set here so the
// module starts with the numbers the test expects.

// MyFSM.Core is where FsmValue lives: the SetBoundValue() calls below name it,
// and a using is per file - importing MyFSM.Unity does not bring MyFSM.Core in.
using UnityEngine;
using MyFSM.Core;
using MyFSM.Unity;

public sealed partial class ZoneBridgeAI
{
    [Header("ZoneBridge bindings (manual)")]
    [Tooltip("Object the module teleports around. Empty: the scene object named 'ZoneMarker'.")]
    public Transform marker;
    [Tooltip("Name used to find or create the marker when none is assigned.")]
    public string markerName = "ZoneMarker";

    partial void OnBindingsManual()
    {
        // ---- handle slots -------------------------------------------------
        // Slot order/type comes from the .fsm: slot 0 Object3D self,
        // slot 1 Object3D marker.
        Bind(Slot_self, gameObject);

        Transform markerTarget = marker;
        if (markerTarget == null)
        {
            GameObject found = GameObject.Find(markerName);
            if (found != null) markerTarget = found.transform;
        }
        if (markerTarget == null)
            Debug.LogWarning("[zone] no marker object — binding Slot_marker to this GameObject. "
                             + "ZoneController creates one named '" + markerName + "' for you.",
                             this);
        Bind(Slot_marker, markerTarget != null ? markerTarget.gameObject : gameObject);

        // ---- value slots --------------------------------------------------
        // `home` is the position this object was placed at; the module computes
        // its lift targets from it (home + lift10 / home + lift40).
        Vector3 start = transform.position;
        SetBoundValue(Slot_home, FsmValue.MakeVec3(start.x, start.y, start.z));

        // `zone` starts at 0 (home). ZoneController overwrites it at runtime
        // through SetBoundValue, once per key press or hold repeat.
        SetBoundValue(Slot_zone, FsmValue.MakeInt(0));
    }
}
