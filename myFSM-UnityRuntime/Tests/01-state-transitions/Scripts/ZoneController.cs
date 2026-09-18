// Test 01 — drives the `zone` runtime variable and checks the state machine
// actually followed it.
//
// The FSM (Fsm/zonebridge.fsm) never reads the keyboard: it only reacts to its
// `zone` variable, which this controller writes through
// AIInstance.SetBoundValue(slot, FsmValue.MakeInt(zone)). That is the point of
// the test — per-state Traversal evaluation must move the head to the state the
// variable asks for:
//
//   zone  0..10  -> AtHome   (the position it started at)
//   zone 11..20  -> Above10  (10 m above it)
//   zone 21..    -> Above40  (40 m above it)
//
// ("0-10 / 11-20 / >=20" overlaps at 20 as written; the module lets the lower
// band own the boundary, so 20 still means 10 m. Change one comparison in the
// .fsm if you want 20 to mean 40 m.)
//
// Controls
//   W / Up-Arrow     +1     (tapping steps once, holding repeats slowly)
//   S / Down-Arrow   -1     (zone never goes below 0)
//   R                reset to 0
//
// While the variable changes, the controller also verifies the OUTCOME a few
// ticks later: the AI's state must be the expected state, and the objects must
// be where that state puts them (self at home + lift, marker back at home). The
// rows go to zone_checks.csv next to the transition log.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using MyFSM.Core;
using MyFSM.Unity;

namespace MyFSM.Tests
{
    [DefaultExecutionOrder(-100)] // run before the AI ticks, so `zone` is fresh
    public class ZoneController : MonoBehaviour
    {
        [Header("The AI this controller drives")]
        [Tooltip("Left empty: found on this GameObject.")]
        public ZoneBridgeAI ai;

        [Tooltip("OPTIONAL. Leave empty: the marker is read back from the module's own slot 1 " +
                 "(the object the FSM actually teleports), which is what this controller verifies.")]
        public Transform marker;

        [Header("Input")]
        public KeyCode increaseKey = KeyCode.UpArrow;
        public KeyCode increaseKeyAlt = KeyCode.W;
        public KeyCode decreaseKey = KeyCode.DownArrow;
        public KeyCode decreaseKeyAlt = KeyCode.S;
        public KeyCode resetKey = KeyCode.R;
        public int minZone = 0;
        public int maxZone = 1000;

        [Header("Hold behaviour (deliberately slower than a tap)")]
        [Tooltip("Seconds a key must be held before it starts repeating.")]
        public float repeatDelay = 0.40f;
        [Tooltip("Seconds between repeats once the delay has passed.")]
        public float repeatInterval = 0.15f;

        [Header("Expected geometry (matches the .fsm constants)")]
        public float lift10 = 10f;
        public float lift40 = 40f;
        public float positionTolerance = 0.05f;

        [Header("Verification")]
        [Tooltip("Frames to wait after a variable change before checking the outcome.")]
        public int settleFrames = 3;
        [Tooltip("Also re-check every N frames while nothing changes. 0 disables.")]
        public int recheckEvery = 60;
        public string fileName = "zone_checks.csv";

        /// <summary>The variable the FSM reads. 0..10 home, 11..20 +10 m, 21+ +40 m.</summary>
        public int zone;

        /// <summary>Verification rows: one per check.</summary>
        public readonly List<string> Checks = new List<string>();

        public int Failures { get; private set; }

        private Vector3 _home;
        private bool _homeKnown;
        private int _settle;
        private int _frameAccumulator;
        private int _lastDirection;
        private bool _unresolvedMarkerWarned;
        private float _untilRepeat;
        private bool _wroteCsv;

        // ------------------------------------------------------------------
        // Public API — usable from the inspector or from your own test driver
        // ------------------------------------------------------------------

        public void SetZone(int value)
        {
            value = Mathf.Max(minZone, Mathf.Min(maxZone, value));
            if (value == zone) return;
            zone = value;
            PushZone();
        }

        public void ResetZone() { SetZone(0); }

        /// <summary>The state the module must be in for this zone value.</summary>
        public static string ExpectedState(int value)
        {
            if (value <= 10) return ZoneBridgeAI.State_AtHome;
            if (value <= 20) return ZoneBridgeAI.State_Above10;
            return ZoneBridgeAI.State_Above40;
        }

        /// <summary>Metres above home that state puts the object at.</summary>
        public float ExpectedLift(int value)
        {
            if (value <= 10) return 0f;
            if (value <= 20) return lift10;
            return lift40;
        }

        // ------------------------------------------------------------------

        private void Awake()
        {
            if (ai == null) ai = GetComponent<ZoneBridgeAI>();
            if (ai == null)
            {
                Debug.LogWarning("[zone] no ZoneBridgeAI found — controller disabled: " + name, this);
                enabled = false;
                return;
            }
        }

        // No marker creation here: the BINDING (ZoneBridgeAI.Manual.cs) must bind
        // something to slot 1 anyway and creates the pin when the scene has none.
        // This controller only reads that result back (see MarkerObject), so
        // there is nothing to assign in the inspector.

        /// <summary>
        /// The object the module itself holds in slot 1 — the marker it teleports.
        /// Resolved from the runtime slot rather than from a second inspector
        /// field, so the verification below checks the object the FSM really
        /// moved, not whatever we think it moved. `marker` overrides it when set.
        /// </summary>
        public Transform MarkerObject
        {
            get
            {
                if (marker != null) return marker;
                if (ai == null) return null;
                FsmValue value;
                if (!ai.TryGetVariable("marker", out value) || value.Kind != FsmValueKind.Handle)
                    return null;
                UnityEngine.Object bound = ai.Handles.Resolve(value.HandleId);
                if (bound is GameObject) return ((GameObject)bound).transform;
                if (bound is Component) return ((Component)bound).transform;
                return null;
            }
        }

        private void Start()
        {
            // Both fields here are optional overrides; the marker is read from
            // the module's own slot 1 (see MarkerObject). This controller never
            // touches fields that only exist in the hand-written partial file,
            // so dropping ZoneBridgeAI.Manual.cs still compiles.
            _home = ReadHome();
            _homeKnown = true;
            PushZone(); // make the FSM and this controller agree from frame one
            Debug.Log("[zone] home = " + _home + " — W/Up +1, S/Down -1, R resets."
                      + " Expecting " + ExpectedState(zone) + " at zone " + zone
                      + ". The marker object (slot 1) pins the home position;"
                      + " nothing needs assigning.", this);
        }

        private Vector3 ReadHome()
        {
            // Prefer the value the module itself holds: reading it back proves
            // the Vector3 value binding worked, not just our own copy.
            FsmValue value;
            if (ai != null && ai.TryGetVariable("home", out value) && value.Kind == FsmValueKind.Vec3)
                return new Vector3(value.X, value.Y, value.Z);
            return ai != null ? ai.transform.position : transform.position;
        }

        private void PushZone()
        {
            if (ai != null)
                ai.SetBoundValue(ZoneBridgeAI.Slot_zone, FsmValue.MakeInt(zone));
            _settle = settleFrames; // verify once the transitions have had a few ticks
            Debug.Log("[zone] zone = " + zone + " -> expecting "
                      + ExpectedState(zone) + " (" + ExpectedLift(zone) + " m up)", this);
        }

        private void Update()
        {
            if (ai == null) return;
            HandleInput();

            if (_settle > 0)
            {
                _settle--;
                if (_settle == 0) Verify("after change");
            }
            else if (recheckEvery > 0)
            {
                _frameAccumulator++;
                if (_frameAccumulator >= recheckEvery)
                {
                    _frameAccumulator = 0;
                    Verify("recheck");
                }
            }
        }

        private int Direction()
        {
            bool up = Input.GetKey(increaseKey) || Input.GetKey(increaseKeyAlt);
            bool down = Input.GetKey(decreaseKey) || Input.GetKey(decreaseKeyAlt);
            if (up && !down) return 1;
            if (down && !up) return -1;
            return 0;
        }

        private void HandleInput()
        {
            if (Input.GetKeyDown(resetKey)) { ResetZone(); return; }

            int direction = Direction();
            if (direction == 0)
            {
                _lastDirection = 0;
                _untilRepeat = repeatDelay;
                return;
            }

            if (direction != _lastDirection)
            {
                // Fresh press: one step immediately, repeats only after a hold.
                _lastDirection = direction;
                _untilRepeat = repeatDelay;
                Step(direction);
                return;
            }

            _untilRepeat -= Time.deltaTime;
            if (_untilRepeat > 0f) return;
            _untilRepeat = repeatInterval; // held: slow, steady repeat
            Step(direction);
        }

        private void Step(int direction)
        {
            SetZone(zone + direction);
        }

        // ------------------------------------------------------------------
        // Verification
        // ------------------------------------------------------------------

        private void Verify(string when)
        {
            string expectedState = ExpectedState(zone);
            string actualState = ai.CurrentStateName;
            bool stateOk = actualState == expectedState;

            float lift = ExpectedLift(zone);
            Vector3 expectedPosition = _homeKnown ? _home + new Vector3(0f, lift, 0f) : ai.transform.position;
            float selfError = Vector3.Distance(ai.transform.position, expectedPosition);
            bool selfOk = selfError <= positionTolerance;

            bool markerOk = true;
            float markerError = 0f;
            Transform pin = MarkerObject;
            if (pin != null && _homeKnown)
            {
                markerError = Vector3.Distance(pin.position, _home);
                markerOk = markerError <= positionTolerance;
            }
            else if (pin == null && _unresolvedMarkerWarned == false)
            {
                _unresolvedMarkerWarned = true;
                Debug.LogWarning("[zone] slot 1 (marker) is not bound yet — cannot verify the "
                                 + "home pin. It is bound by ZoneBridgeAI.Manual.cs.", this);
            }

            bool ok = stateOk && selfOk && markerOk;
            if (!ok) Failures++;

            string row = string.Format(
                "{0:F3},{1},{2},{3},{4},{5:F3},{6:F3},{7},{8},{9}",
                Time.time, zone, expectedState, actualState, stateOk ? "ok" : "FAIL",
                expectedPosition.y, ai.transform.position.y, selfOk ? "ok" : "FAIL",
                markerOk ? "ok" : "FAIL", when);
            Checks.Add(row);

            if (ok)
            {
                Debug.Log("[zone] OK  zone=" + zone + " state=" + actualState
                          + " y=" + ai.transform.position.y.ToString("F2") + " (" + when + ")", this);
            }
            else
            {
                Debug.LogWarning("[zone] FAIL zone=" + zone + " expected state " + expectedState
                                 + " but found " + actualState
                                 + " | expected y " + expectedPosition.y.ToString("F3")
                                 + " but found " + ai.transform.position.y.ToString("F3")
                                 + (pin != null
                                     ? " | marker off home by " + markerError.ToString("F3") + " m"
                                     : "")
                                 + " (" + when + ")", this);
            }
        }

        public string CsvPath
        {
            get { return Path.Combine(Application.persistentDataPath, fileName); }
        }

        public string ToCsv()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("time,zone,expectedState,actualState,stateOk,expectedY,actualY,positionOk,markerOk,when\n");
            for (int i = 0; i < Checks.Count; i++) sb.Append(Checks[i]).Append('\n');
            return sb.ToString();
        }

        public void WriteCsv()
        {
            if (_wroteCsv) return;
            _wroteCsv = true;
            File.WriteAllText(CsvPath, ToCsv());
            Debug.Log("[zone] " + Checks.Count + " checks, " + Failures + " failures -> " + CsvPath, this);
        }

        private void OnApplicationQuit() { WriteCsv(); }
        private void OnDestroy() { WriteCsv(); }
    }
}
