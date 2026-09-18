// Test 04 — records the chase and decides whether it was a real one.
//
// A "moving target" test is only meaningful if it does not quietly become test
// 03 (a static target), so the verdict needs four facts, all measured here:
//
//   caught        the module's `caught` flag flipped (it fired isInRange inside
//                 catchRadius) — the chaser got there
//   separation    at the catch, the chaser really is within catchRadius +
//                 tolerance of the target
//   targetMoved   the walker travelled at least minTargetTravel metres while the
//                 chase ran — the target was not standing still
//   reroutes      the module's plannedLength changed every tick it re-planned;
//                 a constant value would mean the route never adapted. The
//                 recorder counts the changes, so "it re-plans" is a measurement
//                 rather than a claim.
//
// FILES (Application.persistentDataPath):
//   pursuit.csv          verdict row + the numbers behind it
//   pursuit_samples.csv  per-sample separation, both positions, live route length
//                        and the chaser's state

using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using MyFSM.Core;
using MyFSM.Unity;

namespace MyFSM.Tests
{
    [DefaultExecutionOrder(50)] // after the AI has ticked this frame
    public class MovingTargetRecorder : MonoBehaviour
    {
        [Header("What to watch")]
        [Tooltip("The chaser. Left empty: found on this GameObject.")]
        public PathChaserAI chaser;
        [Tooltip("The thing being chased. Left empty: the scene's MazeTargetWalker.")]
        public MazeTargetWalker target;

        [Header("Verdict thresholds")]
        [Tooltip("Must match catchRadius in Fsm/pathchaser.fsm (2.0 m).")]
        public float catchRadius = 2.0f;
        [Tooltip("Extra slack on the catch distance for the tick the flag fires.")]
        public float catchTolerance = 0.5f;
        [Tooltip("How far the target must have travelled for this to be a chase.")]
        public float minTargetTravel = 5f;
        [Tooltip("Seconds without a catch before the run is reported as not caught.")]
        public float giveUpAfterSeconds = 180f;

        [Header("Recording")]
        public int sampleEveryFrames = 2;
        public bool drawTrail = true;
        public string verdictFileName = "pursuit.csv";
        public string samplesFileName = "pursuit_samples.csv";

        // ------------------------------------------------------------------
        // Results
        // ------------------------------------------------------------------

        public bool Caught { get; private set; }
        public float CaughtAfterSeconds { get; private set; } = -1f;
        public float SeparationAtCatch { get; private set; } = -1f;
        public float MinSeparation { get; private set; } = float.MaxValue;
        public float TargetMoved { get { return target != null ? target.TotalMoved : 0f; } }
        public int Reroutes { get; private set; }
        public string Verdict { get; private set; } = "running";

        private class PursuitSample
        {
            public float time;
            public Vector3 chaser;
            public Vector3 hunted;
            public float separation;
            public float plannedLength;
            public string state;
        }

        private readonly List<PursuitSample> _samples = new List<PursuitSample>();
        private float _startTime;
        private float _lastPlanned = -1f;
        private int _frameCounter;
        private bool _finished;
        private bool _wroteFiles;
        private LineRenderer _trail;
        private Transform _hunted;

        private void Awake()
        {
            if (chaser == null) chaser = GetComponent<PathChaserAI>();
            if (target == null) target = FindObjectOfType<MazeTargetWalker>();
            _hunted = target != null ? target.transform : null;
        }

        private void Start()
        {
            _startTime = Time.time;
            if (drawTrail) SetUpTrail();

            if (_hunted == null)
            {
                Debug.LogWarning("[pursuit] no target — add a MazeTargetWalker or assign one.", this);
                enabled = false;
            }
        }

        private void SetUpTrail()
        {
            _trail = gameObject.AddComponent<LineRenderer>();
            _trail.widthMultiplier = 0.12f;
            _trail.positionCount = 0;
            _trail.useWorldSpace = true;
            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null) _trail.material = new Material(shader);
            _trail.startColor = Color.cyan;
            _trail.endColor = Color.blue;
        }

        private void Update()
        {
            if (_finished || chaser == null || _hunted == null) return;

            float separation = HorizontalDistance(chaser.transform.position, _hunted.position);
            if (separation < MinSeparation) MinSeparation = separation;

            float planned = ReadPlannedLength();
            if (planned > 0f)
            {
                if (_lastPlanned > 0f && Mathf.Abs(planned - _lastPlanned) > 0.01f) Reroutes++;
                _lastPlanned = planned;
            }

            FsmValue caughtValue;
            bool caughtFlag = chaser.TryGetVariable("caught", out caughtValue) && caughtValue.B;
            if (caughtFlag && !Caught)
            {
                Caught = true;
                CaughtAfterSeconds = Time.time - _startTime;
                SeparationAtCatch = separation;
            }

            _frameCounter++;
            if (sampleEveryFrames > 0 && _frameCounter % sampleEveryFrames == 0)
                Sample(separation, planned);

            if (!Caught && giveUpAfterSeconds > 0f && Time.time - _startTime > giveUpAfterSeconds)
            {
                Finish();
                return;
            }
            if (Caught) Finish();
        }

        private float ReadPlannedLength()
        {
            FsmValue value;
            if (chaser.TryGetVariable("plannedLength", out value) && value.Kind == FsmValueKind.Float)
                return value.F;
            return -1f;
        }

        private void Sample(float separation, float planned)
        {
            _samples.Add(new PursuitSample
            {
                time = Time.time - _startTime,
                chaser = chaser.transform.position,
                hunted = _hunted.position,
                separation = separation,
                plannedLength = planned,
                state = chaser.CurrentStateName
            });

            if (_trail != null)
            {
                _trail.positionCount = _samples.Count;
                _trail.SetPosition(_samples.Count - 1, chaser.transform.position);
            }
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // ------------------------------------------------------------------
        // Verdict
        // ------------------------------------------------------------------

        private void Finish()
        {
            if (_finished) return;
            _finished = true;

            bool caughtOk = Caught && SeparationAtCatch >= 0f
                            && SeparationAtCatch <= catchRadius + catchTolerance;
            bool movedOk = TargetMoved >= minTargetTravel;
            bool reroutedOk = Reroutes > 0;
            bool stateOk = chaser.CurrentStateName == PathChaserAI.State_Caught;
            bool pass = caughtOk && movedOk && reroutedOk && stateOk;

            Verdict = (pass ? "PASS" : "FAIL")
                      + " — caught " + (caughtOk ? "yes" : "no")
                      + " after " + (CaughtAfterSeconds < 0f ? "never" : CaughtAfterSeconds.ToString("F1") + " s")
                      + ", separation " + (SeparationAtCatch < 0f ? "n/a" : SeparationAtCatch.ToString("F2") + " m")
                      + ", target travelled " + TargetMoved.ToString("F1") + " m"
                      + ", route updates seen " + Reroutes
                      + ", final state " + chaser.CurrentStateName;
            if (!stateOk) Verdict += " [the module never reached Caught]";
            if (!movedOk) Verdict += " [the target barely moved: this was not a moving-target test]";
            if (!reroutedOk) Verdict += " [the route never changed: re-planning did not happen]";

            Debug.Log("[pursuit] " + Verdict, this);
            WriteFiles();
        }

        public string OutputDir { get { return Application.persistentDataPath; } }
        public string VerdictPath { get { return Path.Combine(OutputDir, verdictFileName); } }
        public string SamplesPath { get { return Path.Combine(OutputDir, samplesFileName); } }

        public void WriteFiles()
        {
            if (_wroteFiles) return;
            _wroteFiles = true;

            StringBuilder verdict = new StringBuilder();
            verdict.Append("caught,caughtAfterSeconds,separationAtCatch,minSeparation,"
                           + "catchRadius,targetTravelled,reroutes,finalState,samples,verdict\n");
            verdict.Append(Caught ? "yes" : "no").Append(',')
                   .Append(CaughtAfterSeconds.ToString("F3")).Append(',')
                   .Append(SeparationAtCatch.ToString("F3")).Append(',')
                   .Append(MinSeparation == float.MaxValue ? "-" : MinSeparation.ToString("F3")).Append(',')
                   .Append(catchRadius.ToString("F2")).Append(',')
                   .Append(TargetMoved.ToString("F3")).Append(',')
                   .Append(Reroutes).Append(',')
                   .Append(chaser != null ? chaser.CurrentStateName : "-").Append(',')
                   .Append(_samples.Count).Append(',')
                   .Append('"').Append(Verdict).Append('"').Append('\n');
            File.WriteAllText(VerdictPath, verdict.ToString());

            StringBuilder samples = new StringBuilder();
            samples.Append("index,time,chaserX,chaserZ,targetX,targetZ,separation,plannedLength,state\n");
            for (int i = 0; i < _samples.Count; i++)
            {
                PursuitSample s = _samples[i];
                samples.Append(i).Append(',')
                       .Append(s.time.ToString("F3")).Append(',')
                       .Append(s.chaser.x.ToString("F3")).Append(',')
                       .Append(s.chaser.z.ToString("F3")).Append(',')
                       .Append(s.hunted.x.ToString("F3")).Append(',')
                       .Append(s.hunted.z.ToString("F3")).Append(',')
                       .Append(s.separation.ToString("F3")).Append(',')
                       .Append(s.plannedLength.ToString("F3")).Append(',')
                       .Append(s.state).Append('\n');
            }
            File.WriteAllText(SamplesPath, samples.ToString());

            Debug.Log("[pursuit] wrote " + VerdictPath + " and " + SamplesPath, this);
        }

        private void OnApplicationQuit() { WriteFiles(); }
        private void OnDestroy() { WriteFiles(); }
    }
}
