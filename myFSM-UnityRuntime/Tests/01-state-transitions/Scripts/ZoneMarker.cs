// Test 01 — the marker object, in one place.
//
// WHAT THE MARKER IS: the module's slot 1 (`var Object3D marker`). Every state
// in zonebridge.fsm teleports it to `home`, so it is a visual pin that shows
// where "home" is while slot 0 (`self`) rises to +10 m or +40 m. It is scenery
// with a purpose: it makes it obvious which state the machine is in, and it
// gives the test something to verify (the marker must be back at home in every
// state, since the FSM says so).
//
// Because it is scenery, it needs no particular object: any GameObject works,
// and leaving the fields empty is the intended path. This helper is the single
// implementation of "find the marker, or make one", used by both the binding
// (which must bind SOMETHING to slot 1) and the controller (which checks where
// it ended up). It is idempotent: called twice, the second call finds the
// object the first one created.

using UnityEngine;

namespace MyFSM.Tests
{
    public static class ZoneMarker
    {
        /// <summary>
        /// Returns the scene object called <paramref name="name"/>, creating it
        /// (a small green sphere with no collider) at <paramref name="position"/>
        /// when the scene has none.
        /// </summary>
        public static Transform Ensure(string name, Vector3 position, UnityEngine.Object context = null)
        {
            GameObject found = GameObject.Find(name);
            if (found != null) return found.transform;

            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = name;
            marker.transform.position = position;
            marker.transform.localScale = new Vector3(0.4f, 0.4f, 0.4f);

            // Physics neutrality: a visual aid must not push the AI around or
            // take part in a NavMesh bake.
            Collider collider = marker.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);
            Renderer renderer = marker.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = new Color(0.3f, 0.9f, 0.4f);

            Debug.Log("[zone] created the marker object '" + name + "' at " + position
                      + " (slot 1 of the module: the home pin). It is scenery - nothing "
                      + "needs to be dragged into the inspector for it.", context);
            return marker.transform;
        }
    }
}
