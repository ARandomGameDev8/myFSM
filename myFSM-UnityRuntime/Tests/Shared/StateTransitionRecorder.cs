// myFSM test support — records the state changes the runtime reports for one AI.
//
// Every AI publishes each state change through MainServer (DB timetable + the
// ordered/priority broadcast servers), which is why this recorder does not
// guess: it subscribes to the ordered broadcast and stores what the runtime
// itself says happened. A per-frame poll of CurrentStateName sits behind it as
// a safety net, so a change is never lost if this component subscribes late
// (for example when the recorder is added at runtime).
//
// Drop it on the same GameObject as the AI (or assign `ai` from anywhere) and
// it writes <fileName> under Application.persistentDataPath on quit, with one
// row per transition, plus the runtime variables you list in `watchVariables`.
// That file is the evidence for "the state machine followed the variable":
//
//   time,tick,from,to,reason,zone,position
//   1.234,74,AtHome,Above10,traversal,11,(0.0, 10.0, 0.0)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using MyFSM.Core;
using MyFSM.Unity;

namespace MyFSM.Tests
{
    /// <summary>
    /// Logs this AI's state changes: in-memory rows, a CSV on quit, one console
    /// line per change (optional), and the raw broadcast event behind each row.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public class StateTransitionRecorder : MonoBehaviour
    {
        [Serializable]
        public class Entry
        {
            public float time;
            public long tick;
            public string from;
            public string to;
            public string reason;
            public string variables;

            public string ToCsvRow(string[] names)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(time.ToString("F3")).Append(',');
                sb.Append(tick).Append(',');
                sb.Append(from).Append(',');
                sb.Append(to).Append(',');
                sb.Append(reason);
                if (names != null && names.Length > 0)
                {
                    // `variables` holds the values in the same order as `names`.
                    string[] values = variables == null
                        ? new string[0]
                        : variables.Split(new[] { '|' }, StringSplitOptions.None);
                    for (int i = 0; i < names.Length; i++)
                        sb.Append(',').Append(i < values.Length ? values[i] : "?");
                }
                return sb.ToString();
            }
        }

        [Header("What to watch")]
        [Tooltip("The AI whose transitions are recorded. Left empty: found on this GameObject.")]
        public AIInstance ai;

        [Tooltip("Runtime variables to sample at every transition (names as written in the .fsm).")]
        public string[] watchVariables = new string[0];

        [Header("Where the log goes")]
        [Tooltip("Written under Application.persistentDataPath when Play stops.")]
        public string fileName = "state_transitions.csv";

        [Tooltip("Print one line per transition to the console as it happens.")]
        public bool logToConsole = true;

        [Tooltip("Write the CSV when the run ends (Play mode exit / scene unload).")]
        public bool writeCsvOnQuit = true;

        /// <summary>Every transition seen so far, in order.</summary>
        public readonly List<Entry> Entries = new List<Entry>();

        public int Count { get { return Entries.Count; } }

        private readonly List<StateChangeEvent> _events = new List<StateChangeEvent>();
        private Relay _relay;
        private string _lastObserved;
        private bool _wroteCsv;

        /// <summary>The exact broadcast events received, if the runtime published them.</summary>
        public IList<StateChangeEvent> Events { get { return _events; } }

        // The subscriber object handed to the ordered broadcast server. The
        // built-in ActionWithStateSubscriber only forwards the state name; a
        // transition log wants the whole event (from/to/tick/reason).
        private sealed class Relay : StateSubscriber
        {
            private readonly StateTransitionRecorder _owner;

            public Relay(StateTransitionRecorder owner)
                : base(AnyTarget) // every AI; filtered by instance id in Notify
            {
                _owner = owner;
            }

            public override void Notify(StateChangeEvent e)
            {
                _owner.OnBroadcast(e);
            }
        }

        private void Awake()
        {
            if (ai == null) ai = GetComponent<AIInstance>();
            if (ai == null)
            {
                Debug.LogWarning("[recorder] no AIInstance found — recorder disabled: " + name, this);
                enabled = false;
            }
        }

        private void Start()
        {
            // Subscribe to AnyTarget so component order (this recorder vs the
            // AI's own Start) cannot make the subscription miss events.
            MainServer server = MainServer.EnsureExists();
            _relay = new Relay(this);
            server.OrderedBroadcast.Subscribe(_relay);

            _lastObserved = ai.CurrentStateName;
            if (!string.IsNullOrEmpty(_lastObserved))
                Record("", _lastObserved, "start", 0, Time.time);

            Debug.Log("[recorder] watching " + ai.DisplayName + " (instance " + ai.InstanceId
                      + ", state " + _lastObserved + ")", this);
        }

        private void OnBroadcast(StateChangeEvent e)
        {
            if (ai != null && e.InstanceId != ai.InstanceId) return; // other AI, not ours
            if (ai == null) return;
            _events.Add(e);
            _lastObserved = e.ToState;
            Record(e.FromState, e.ToState,
                   string.IsNullOrEmpty(e.Reason) ? "transition" : e.Reason, e.Tick, e.Time);
        }

        private void Update()
        {
            if (ai == null) return;
            // Safety net only: if the broadcast already stamped this change,
            // Record() drops the duplicate (same from -> to as the last row).
            string now = ai.CurrentStateName;
            if (now == _lastObserved) return;
            Record(_lastObserved, now, "poll", -1, Time.time);
            _lastObserved = now;
        }

        private void Record(string from, string to, string reason, long tick, float time)
        {
            if (string.IsNullOrEmpty(to)) return;

            Entry entry = new Entry
            {
                time = time,
                tick = tick,
                from = string.IsNullOrEmpty(from) ? "-" : from,
                to = to,
                reason = reason
            };
            entry.variables = SampleVariables();

            // A poll row and the broadcast row for the same change are the same
            // transition; keep the first one that arrived.
            if (Entries.Count > 0)
            {
                Entry last = Entries[Entries.Count - 1];
                if (last.from == entry.from && last.to == entry.to
                    && Mathf.Abs(last.time - entry.time) < 0.5f)
                    return;
            }

            Entries.Add(entry);
            if (logToConsole)
                Debug.Log("[recorder] " + entry.from + " -> " + entry.to + " (" + entry.reason
                          + ", t=" + entry.time.ToString("F3") + ")"
                          + (entry.variables.Length > 0 ? "  vars: " + entry.variables : ""), this);
        }

        private string SampleVariables()
        {
            if (watchVariables == null || watchVariables.Length == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < watchVariables.Length; i++)
            {
                if (i > 0) sb.Append('|');
                FsmValue value;
                sb.Append(ai.TryGetVariable(watchVariables[i], out value) ? value.ToString() : "?");
            }
            return sb.ToString();
        }

        /// <summary>Path the CSV is written to.</summary>
        public string CsvPath
        {
            get { return Path.Combine(Application.persistentDataPath, fileName); }
        }

        /// <summary>Builds the CSV text: header, one row per transition.</summary>
        public string ToCsv()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("time,tick,from,to,reason");
            if (watchVariables != null)
                for (int i = 0; i < watchVariables.Length; i++)
                    sb.Append(',').Append(watchVariables[i]);
            sb.Append('\n');
            for (int i = 0; i < Entries.Count; i++)
                sb.Append(Entries[i].ToCsvRow(watchVariables)).Append('\n');
            return sb.ToString();
        }

        /// <summary>Writes the CSV once (safe to call repeatedly).</summary>
        public void WriteCsv()
        {
            if (_wroteCsv) return;
            _wroteCsv = true;
            string text = ToCsv();
            File.WriteAllText(CsvPath, text);
            Debug.Log("[recorder] " + Entries.Count + " transitions -> " + CsvPath, this);
        }

        private void OnApplicationQuit()
        {
            if (writeCsvOnQuit) WriteCsv();
        }

        private void OnDestroy()
        {
            if (writeCsvOnQuit) WriteCsv();
            if (_relay != null && MainServer.Instance != null)
                MainServer.Instance.OrderedBroadcast.Unsubscribe(_relay.SubscriberId);
        }
    }
}
