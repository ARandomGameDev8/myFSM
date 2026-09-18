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
// WHY bindings are code here and not inspector fields: the module declares two
// Object3D slots (`self` and `marker`). Auto-binding maps a slot to this
// GameObject by its type tag, so with two slots sharing a tag it cannot know
// which is which — the generator prints "AMBIGUOUS (2x) — Bind(Slot_x, ...)"
// and leaves the decision to you.
//
// THIS FILE NEEDS NOTHING FILLED IN. Both of its fields are optional overrides:
// with the defaults, `self` is this GameObject and the marker is found (or
// created) by name — see the comments on the fields below.

using UnityEngine;
// MyFSM.Core is where FsmValue lives: the SetBoundValue() calls below name it,
// and a using is per file - importing MyFSM.Unity does not bring MyFSM.Core in.
using MyFSM.Core;
using MyFSM.Unity;
using MyFSM.Tests; // ZoneMarker (the find-or-create helper for slot 1)

public sealed partial class ZoneBridgeAI
{
    [Header("ZoneBridge bindings (manual) - both fields are OPTIONAL")]
    [Tooltip("Slot 1, the module's 'home pin'. LEAVE EMPTY and the test finds or creates " +
             "an object named 'ZoneMarker': the marker is scenery that shows where home is " +
             "while this object rises. Drag any GameObject here only if you want a specific " +
             "one to be the pin.")]
    public Transform marker;

    [Tooltip("Name used to find (or create) the marker when the field above is empty.")]
    public string markerName = "ZoneMarker";

    partial void OnBindingsManual()
    {
        // ---- is anything driving this AI? -------------------------------
        // The module never reads the keyboard: it only reacts to its `zone`
        // variable, which ZoneController writes. An AI component on its own is
        // therefore a machine nobody drives - it boots, sits in AtHome and looks
        // broken while being perfectly alive. Say so, loudly, once.
        if (FindObjectOfType<ZoneController>() == null && FindObjectOfType<ZoneTestSetup>() == null)
        {
            Debug.LogWarning("[zone] this AI has NO driver: 'zone' will stay 0, so the FSM stays in "
                             + "AtHome and nothing will appear to happen. Add ZoneTestSetup to any "
                             + "GameObject (it builds the visible cube, the keyboard controller and "
                             + "the recorder), or drive zone yourself with "
                             + "SetBoundValue(Slot_zone, FsmValue.MakeInt(...)).", this);
        }

        // ---- handle slots -------------------------------------------------
        // Slot order/type comes from the .fsm: slot 0 Object3D self,
        // slot 1 Object3D marker.

        // `self` is the object the module moves around; that is this GameObject.
        Bind(Slot_self, gameObject);

        // The marker is NOT the fallback for a missing marker: every state in
        // zonebridge.fsm runs `setPosition(marker, home)` right after moving
        // `self`, so binding slot 1 to this same GameObject would teleport the
        // lifted object straight back down to home and the state would look
        // broken. Find-or-create a real, separate pin instead (any GameObject
        // would do; ZoneMarker makes a small green sphere if the scene has none).
        Transform markerTarget = marker;
        if (markerTarget == null)
            markerTarget = ZoneMarker.Ensure(markerName, transform.position, this);
        Bind(Slot_marker, markerTarget.gameObject);

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
