// Test 01 — builds the whole scene, including the visible cube.
//
// YOU NEED TO FILL IN NOTHING. Drop this on an empty GameObject, press Play.
//
// What it does, in order:
//   1. finds the AI: on this object, else anywhere in the scene (so an AI you
//      added by hand is USED, not duplicated); if the scene has none at all, the
//      AI is added to THIS object - no second cube springs into existence;
//   2. makes sure the AI's object is visible: an empty GameObject has no mesh,
//      and a test you cannot see moving is not a test — so a cube child is added
//      when the object has no renderer of its own;
//   3. adds the keyboard controller (ZoneController) and the transition recorder
//      TO THE AI'S OWN GameObject, whichever object that turned out to be;
//   4. builds a height ruler (a thin 45 m pole with ticks at 10 m and 40 m) so
//      the +10 m / +40 m teleports can actually be read off the screen;
//   5. frames the Main Camera on that column (disable with frameCameraOnStart).
//
// It drives nothing: the cube only moves when you press a key (W/Up, S/Down,
// R) or edit `zone` on the controller. There is no demo cycle.
//
// Why the ruler and the camera: the machine moves the object 10 m and 40 m up.
// With a default camera at eye height the object simply leaves the frame, which
// looks exactly like "it broke". The ruler shows the height, the framing shows
// the height, and the CSV records it.

using UnityEngine;

namespace MyFSM.Tests
{
    [DefaultExecutionOrder(-200)] // before ZoneController and before the AI ticks
    public class ZoneTestSetup : MonoBehaviour
    {
        [Header("Wired automatically - leave empty")]
        [Tooltip("The AI to drive. Empty: found on this object, then in the scene, " +
                 "then added to THIS object.")]
        public ZoneBridgeAI ai;
        public ZoneController controller;
        public StateTransitionRecorder recorder;

        [Header("Scene building")]
        [Tooltip("Add a ZoneBridgeAI to this object when the scene has no AI at all.")]
        public bool createAiIfMissing = true;
        [Tooltip("Colour of the cube that rises.")]
        public Color cubeColour = new Color(0.22f, 0.6f, 1f);
        [Tooltip("Where the visible cube's centre sits above the ground: 0.5 rests a " +
                 "1x1x1 cube on a ground plane, and makes that height the AI's home.")]
        public float cubeHeight = 0.5f;
        [Tooltip("Build the 45 m height ruler (pole + a tick at 10 m and 40 m).")]
        public bool createHeightRuler = true;
        [Tooltip("How far to the side of the cube the ruler stands.")]
        public float rulerOffset = 0.9f;

        [Header("Camera")]
        [Tooltip("Move the Main Camera to frame the 0-45 m column at start " +
                 "(so the cube cannot leave the frame without you noticing).")]
        public bool frameCameraOnStart = true;

        [Header("Recording")]
        [Tooltip("Runtime variables recorded with every state change.")]
        public string[] watchVariables = { "zone" };

        private void Awake()
        {
            if (ai == null) ai = GetComponent<ZoneBridgeAI>();
            if (ai == null)
            {
                // One you placed yourself. Several AIs: say which one this test drives
                // instead of silently picking one.
                ZoneBridgeAI[] found = FindObjectsOfType<ZoneBridgeAI>();
                if (found.Length > 1)
                    Debug.LogWarning("[zone] " + found.Length + " ZoneBridgeAI components in the "
                                     + "scene — driving '" + found[0].name + "' (assign the `ai` "
                                     + "field to pick a different one).", this);
                if (found.Length > 0) ai = found[0];
            }
            if (ai == null && createAiIfMissing)
            {
                // Make THIS object the AI, rather than spawning a second cube: the
                // object you attached the setup to is the object that moves.
                ai = gameObject.AddComponent<ZoneBridgeAI>();
                Debug.Log("[zone] the scene had no AI, so ZoneBridgeAI was added to '" + name
                          + "'.", this);
            }

            if (ai == null)
            {
                Debug.LogWarning("[zone] no ZoneBridgeAI anywhere in the scene, and " +
                                 "createAiIfMissing is off — nothing to drive. Add this " +
                                 "component to an empty GameObject and press Play.", this);
                enabled = false;
                return;
            }

            GameObject host = ai.gameObject;
            bool addedVisual = EnsureVisible(host);
            if (createHeightRuler) CreateHeightRuler(host.transform.position);

            // Controller + recorder belong on the AI's own object, wherever it is.
            controller = host.GetComponent<ZoneController>();
            if (controller == null) controller = host.AddComponent<ZoneController>();
            controller.ai = ai;

            recorder = host.GetComponent<StateTransitionRecorder>();
            if (recorder == null) recorder = host.AddComponent<StateTransitionRecorder>();
            recorder.ai = ai;
            recorder.watchVariables = watchVariables;
            recorder.fileName = "state_transitions.csv";

            if (frameCameraOnStart) FrameCamera(host.transform.position);

            Debug.Log("[zone] ready. AI on '" + host.name + "'"
                      + (addedVisual ? " (a visible cube was added: the object itself had no mesh)"
                                     : "")
                      + ", marker pin = the '" + ZoneMarker.DefaultName + "' object, recorder watching "
                      + string.Join(", ", watchVariables) + "."
                      + " The cube teleports 0 m / +10 m / +40 m, so the ruler beside it reads the state."
                      + " Drive it with W/Up and S/Down (nothing moves until you do).",
                      this);
        }

        /// <summary>
        /// Adds a visible cube child when the AI's own object has no renderer.
        /// The AI stays where it is (on its own object): the cube is its body, and
        /// moves with it. Returns true when a cube was added.
        /// </summary>
        private bool EnsureVisible(GameObject host)
        {
            if (host.GetComponent<Renderer>() != null) return false;

            // A 1x1x1 cube centred on the AI's origin, so the origin is the cube's
            // centre: `home` is then half a metre above the ground, which is also
            // where the marker pin sits (it would be half-buried at y = 0).
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "ZoneCube (visual)";
            visual.transform.SetParent(host.transform, false);
            visual.transform.localScale = Vector3.one;

            if (host.transform.position.y < cubeHeight)
            {
                // Sitting at ground level: lift the origin so the cube rests ON the
                // ground instead of being half buried in it.
                Vector3 lifted = host.transform.position;
                lifted.y = cubeHeight;
                host.transform.position = lifted;
            }

            // Scenery, not an obstacle: keep it out of the physics scene.
            Collider collider = visual.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            Renderer renderer = visual.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = cubeColour;

            Debug.Log("[zone] '" + host.name + "' is an empty GameObject (no mesh), so a visible "
                      + "cube was added as a child (the AI stays on '" + host.name
                      + "' and the cube follows it — nothing to re-assign). Origin at "
                      + host.transform.position + ", which is now home.", host);
            return true;
        }

        /// <summary>
        /// A thin 45 m pole with ticks at 10 m and 40 m, standing beside the cube:
        /// the teleports are 10 m and 40 m, and a number that big is invisible
        /// without a scale next to it. Separate objects with no colliders, so they
        /// never move with the AI and never block it.
        /// </summary>
        private void CreateHeightRuler(Vector3 origin)
        {
            GameObject ruler = new GameObject("ZoneRuler (0-45 m)");
            ruler.transform.position = origin;

            Color poleColour = new Color(0.35f, 0.35f, 0.4f);
            AddBar(ruler.transform, "Pole (45 m)", new Vector3(rulerOffset, 22.5f, 0f),
                   new Vector3(0.06f, 45f, 0.06f), poleColour);
            AddBar(ruler.transform, "Tick 10 m", new Vector3(rulerOffset, 10f, 0f),
                   new Vector3(0.5f, 0.08f, 0.5f), Color.yellow);
            AddBar(ruler.transform, "Tick 40 m", new Vector3(rulerOffset, 40f, 0f),
                   new Vector3(0.5f, 0.08f, 0.5f), new Color(1f, 0.5f, 0.1f));

            Debug.Log("[zone] height ruler built: 45 m pole with ticks at 10 m (yellow) and "
                      + "40 m (orange), " + rulerOffset + " m to the side of the cube.", ruler);
        }

        private static void AddBar(Transform parent, string barName, Vector3 localPosition,
                                   Vector3 localScale, Color colour)
        {
            GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = barName;
            bar.transform.SetParent(parent, false);
            bar.transform.localPosition = localPosition;
            bar.transform.localScale = localScale;
            Collider collider = bar.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);
            Renderer renderer = bar.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = colour;
        }

        /// <summary>
        /// Points the Main Camera at the column of air above <paramref name="home"/>
        /// (where the cube starts and returns to), from a three-quarter view that
        /// fits the whole 0-45 m span. Public and idempotent: ZoneController's C key
        /// calls the same thing, so it works wherever the cube happens to live.
        /// </summary>
        public static bool FrameCamera(Vector3 home)
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                Debug.LogWarning("[zone] no Main Camera (nothing tagged MainCamera) — tilt your "
                                 + "view manually to see 0-45 m. The test itself is unaffected.");
                return false;
            }
            Vector3 pivot = home + new Vector3(0f, 20f, 0f); // middle of the 0-45 m column
            camera.transform.position = pivot + new Vector3(-32f, 8f, -32f);
            camera.transform.LookAt(pivot);
            return true;
        }
    }
}
