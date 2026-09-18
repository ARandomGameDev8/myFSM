// Test 01 — drives the `zone` runtime variable and checks the state machine
// actually followed it.
//
// NOTHING MOVES ON ITS OWN. The only things that change `zone` are:
//   * your keys (W/Up, S/Down, R),
//   * this component's `zone` field — edit it in the Inspector while playing and
//     the new value is pushed to the FSM,
//   * your own code calling SetZone().
// There is no demo, no timer, no automatic stepping.
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
//   C                re-frame the Main Camera on the 0-45 m test column
//
// Keyboard reading goes through ZoneInput, which works with either Unity input
// backend (legacy Input Manager or the Input System package) and reports which
// one it used at startup — "the keys do nothing" is usually that setting.
//
// Every accepted change is logged as
//   [zone] zone = 15  (from: W pressed)  ->  FSM reads 15, expecting Above10
// so "the variable did not change" can be answered from the console alone: if
// that line appears, the variable changed and the machine is at fault; if it
// never appears, the key press is not reaching the game.
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
        [Tooltip("Left empty: found on this GameObject, then anywhere in the scene.")]
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
        public KeyCode frameCameraKey = KeyCode.C;
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

        [Header("Boot")]
        [Tooltip("Seconds to wait for the AI to boot before reporting that it never did.")]
        public float bootTimeout = 3f;

        /// <summary>
        /// The variable the FSM reads. 0..10 home, 11..20 +10 m, 21+ +40 m.
        /// Edit it in the Inspector while playing: the new value is pushed to the
        /// FSM on the next frame and logged like any other change.
        /// </summary>
        public int zone;

        /// <summary>Verification rows: one per check.</summary>
        public readonly List<string> Checks = new List<string>();

        public int Failures { get; private set; }

        private Vector3 _home;
        private bool _homeKnown;
        private int _settle;
        private int _frameAccumulator;
        private int _lastDirection;
        private float _untilRepeat;
        private bool _wroteCsv;

        private int _pushedZone = int.MinValue; // what the FSM currently holds
        private string _pendingReason;          // why `zone` last changed (null = inspector)
        private bool _bootChecked;
        private bool _bootErrorLogged;
        private float _bootWait;
        private bool _unresolvedMarkerWarned;

        // ------------------------------------------------------------------
        // Public API — usable from the inspector or from your own test driver
        // ------------------------------------------------------------------

        /// <summary>Sets the variable. The FSM sees it on the next frame.</summary>
        public void SetZone(int value, string reason = null)
        {
            value = Mathf.Max(minZone, Mathf.Min(maxZone, value));
            if (value == zone) return;
            zone = value;
            _pendingReason = string.IsNullOrEmpty(reason) ? "code" : reason;
        }

        public void ResetZone() { SetZone(0, "reset key"); }

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

        // ------------------------------------------------------------------

        private void Awake()
        {
            if (ai == null) ai = GetComponent<ZoneBridgeAI>();
            if (ai == null) ai = FindObjectOfType<ZoneBridgeAI>(); // setup may live elsewhere
            if (ai == null)
            {
                Debug.LogWarning("[zone] no ZoneBridgeAI in the scene — controller disabled: " + name
                                 + ". Add ZoneTestSetup (which creates the AI + cube) or put this "
                                 + "component on the same object as a ZoneBridgeAI.", this);
                enabled = false;
            }
        }

        private void Start()
        {
            Debug.Log("[zone] input backend: " + ZoneInput.Backend + ". Keys: W/Up +1, S/Down -1,"
                      + " R reset, C camera. Unity only delivers input to a focused window — click"
                      + " inside the Game view first (and note that WASD with the mouse over the"
                      + " SCENE view flies the scene camera instead).", this);

            if (!ZoneInput.Available)
                Debug.LogError("[zone] no readable keyboard input: " + ZoneInput.Backend + ". "
                               + ZoneInput.Fix, this);
        }

        private void Update()
        {
            if (ai == null) return;

            BootCheck();
            if (!_bootChecked) return; // the FSM cannot be driven until it has booted

            if (ZoneInput.GetKeyDown(frameCameraKey)) ZoneTestSetup.FrameCamera(_home);

            _pendingReason = null;   // null here = the value was changed outside a key press
            HandleInput();
            if (zone != _pushedZone)
                PushZone(_pendingReason ?? "inspector / code");

            VerifyTick();
        }

        /// <summary>
        /// Waits for the AI to boot, then reads `home` from the module and pushes
        /// the first value. Pushing before boot is pointless (there is no variable
        /// table yet) and the runtime logs an error for it, so the first push is
        /// deferred until the FSM is really alive — and if it never boots, that is
        /// reported instead of leaving a dead scene.
        /// </summary>
        private void BootCheck()
        {
            if (_bootChecked) return;
            if (!ai.Booted)
            {
                _bootWait += Time.unscaledDeltaTime;
                if (!_bootErrorLogged && _bootWait > bootTimeout)
                {
                    _bootErrorLogged = true;
                    Debug.LogError("[zone] the AI never booted after " + bootTimeout.ToString("F1")
                                   + " s" + (string.IsNullOrEmpty(ai.BootError)
                                             ? " (no boot error reported)"
                                             : ": " + ai.BootError)
                                   + " — so `zone` cannot be driven and nothing will move. Look for "
                                   + "the boot failure above this line.", this);
                }
                return;
            }
            _bootChecked = true;
            _homeKnown = true;
            _home = ReadHome();
            _pushedZone = int.MinValue; // force the push below

            Debug.Log("[zone] driving '" + ai.DisplayName + "' (module " + ai.ModuleName + ", state "
                      + ai.CurrentStateName + ", home " + _home + "). zone " + zone + " -> "
                      + ExpectedState(zone) + ".", this);
            PushZone("start");
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

        /// <summary>
        /// Writes `zone` into the FSM's runtime variable and reads it straight back:
        /// a value that changed in this component but not in the FSM is the whole
        /// question when someone reports "the variable isn't changing", and one
        /// read-back answers it.
        /// </summary>
        private void PushZone(string reason)
        {
            if (ai == null || !ai.Booted) return;

            if (!ai.SetBoundValue(ZoneBridgeAI.Slot_zone, FsmValue.MakeInt(zone)))
            {
                Debug.LogError("[zone] SetBoundValue(Slot_zone = " + ZoneBridgeAI.Slot_zone
                               + ", " + zone + ") failed — the FSM was NOT updated. The error"
                               + " above from the runtime says why (slot type, missing binding,"
                               + " or the AI was not booted).", this);
                return;
            }

            _pushedZone = zone;
            _settle = settleFrames; // verify the outcome once the transitions have had a few ticks

            FsmValue readback;
            bool readOk = ai.TryGetVariable("zone", out readback);
            Debug.Log("[zone] zone = " + zone + "  (from: " + reason + ")  ->  FSM reads "
                      + (readOk ? readback.ToString() : "<unreadable>")
                      + ", expecting " + ExpectedState(zone) + " (" + ExpectedLift(zone) + " m up)",
                      this);
        }

        private void HandleInput()
        {
            if (ZoneInput.GetKeyDown(resetKey)) { ResetZone(); return; }

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

        private int Direction()
        {
            bool up = ZoneInput.GetKey(increaseKey) || ZoneInput.GetKey(increaseKeyAlt);
            bool down = ZoneInput.GetKey(decreaseKey) || ZoneInput.GetKey(decreaseKeyAlt);
            if (up && !down) return 1;
            if (down && !up) return -1;
            return 0;
        }

        private void Step(int direction)
        {
            SetZone(zone + direction, direction > 0 ? "increase key (+1)" : "decrease key (-1)");
        }

        // ------------------------------------------------------------------
        // Verification
        // ------------------------------------------------------------------

        private void VerifyTick()
        {
            if (_settle > 0)
            {
                _settle--;
                if (_settle == 0) Verify("after change");
                return;
            }
            if (recheckEvery <= 0) return;
            _frameAccumulator++;
            if (_frameAccumulator >= recheckEvery)
            {
                _frameAccumulator = 0;
                Verify("recheck");
            }
        }

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
            else if (pin == null && !_unresolvedMarkerWarned)
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

        // ------------------------------------------------------------------

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
