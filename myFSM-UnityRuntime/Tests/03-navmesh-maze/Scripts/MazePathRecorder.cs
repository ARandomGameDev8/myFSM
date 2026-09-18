// Test 03 — checks, afterwards, that the runner really took the shortest path.
//
// Three numbers decide it, and they come from three different places:
//
//   plannedLength  the module's own findPath()+getPathLength() result — what the
//                  engine says the route costs (unavailable: the run is marked
//                  SKIPPED, that is the only skip condition)
//   travelled      this recorder's own accumulation of the runner's movement,
//                  sampled every frame — what actually happened in the scene
//   straightLine   entry to exit in a straight line — a shortest path through a
//                  maze can never be shorter than that
//
// Verdict: arrived inside `arrivalTolerance` of the exit, travelled >= the
// straight line, and travelled within [0.85, 1.25] x plannedLength. A runner
// that wandered (travelled >> planned) or that got teleported along a shortcut
// (travelled << planned) fails those last two.
//
// Files (Application.persistentDataPath):
//   maze_run.csv    one verdict row + the numbers behind it
//   maze_trail.csv  per-frame positions, distance to the exit, and FSM state

using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using MyFSM.Core;
using MyFSM.Unity;

namespace MyFSM.Tests
{
    [DefaultExecutionOrder(50)] // after the AI has ticked this frame
    public class MazePathRecorder : MonoBehaviour
    {
        [Header("What to watch")]
        [Tooltip("The runner. Left empty: found on this GameObject.")]
        public MazeRunnerAI ai;
        [Tooltip("The exit object. Left empty: the maze controller's exit.")]
        public Transform exit;
        public MazeGeneratorController maze;

        [Header("Verdict thresholds")]
        [Tooltip("How close to the exit counts as arrived (metres).")]
        public float arrivalTolerance = 1.0f;
        public float plannedLengthToleranceLow = 0.85f;
        public float plannedLengthToleranceHigh = 1.25f;
        [Tooltip("Seconds without arrival before the run is reported as stuck.")]
        public float giveUpAfterSeconds = 300f;

        [Header("Recording")]
        public bool recordTrail = true;
        [Tooltip("Halve the trail cost by sampling every N frames.")]
        public int trailEveryFrames = 2;
        public string verdictFileName = "maze_run.csv";
        public string trailFileName = "maze_trail.csv";
        [Tooltip("Draw the walked route in the scene.")]
        public bool drawTrail = true;

        // ------------------------------------------------------------------
        // Results
        // ------------------------------------------------------------------

        public float PlannedLength { get; private set; } = -1f;
        public float Travelled { get; private set; }
        public float StraightLine { get; private set; }
        public float ArrivalError { get; private set; } = -1f;
        public float RunSeconds { get; private set; } = -1f;
        public bool Arrived { get; private set; }
        public bool Skipped { get; private set; }
        public string Verdict { get; private set; } = "running";

        private Vector3 _lastPosition;
        private float _startTime;
        private int _frameCounter;
        private bool _finished;
        private bool _wroteFiles;
        private LineRenderer _trail;
        private readonly List<Vector3> _trailPositions = new List<Vector3>();
        private readonly List<float> _trailTimes = new List<float>();
        private readonly List<string> _trailStates = new List<string>();

        private void Awake()
        {
            if (ai == null) ai = GetComponent<MazeRunnerAI>();
            if (maze == null) maze = FindObjectOfType<MazeGeneratorController>();
        }

        private void Start()
        {
            _startTime = Time.time;
            _lastPosition = ai.transform.position;
            if (exit == null && maze != null) exit = maze.Exit;

            if (drawTrail)
            {
                _trail = gameObject.AddComponent<LineRenderer>();
                _trail.widthMultiplier = 0.12f;
                _trail.positionCount = 0;
                _trail.useWorldSpace = true;
                Shader shader = Shader.Find("Sprites/Default");
                if (shader != null) _trail.material = new Material(shader);
                _trail.startColor = Color.yellow;
                _trail.endColor = new Color(1f, 0.5f, 0f);
            }

            if (exit == null)
            {
                Debug.LogWarning("[maze-03] no exit transform — set `exit` or add the maze controller.", this);
            }
            else
            {
                // The straight line is an entry-to-exit fact: measure it now,
                // before the runner has moved, not at the end of the run.
                StraightLine = maze != null
                    ? maze.StraightDistance
                    : HorizontalDistance(ai.transform.position, exit.position);
            }
        }

        private void Update()
        {
            if (_finished || ai == null) return;

            Vector3 position = ai.transform.position;
            Travelled += HorizontalDistance(_lastPosition, position);
            _lastPosition = position;

            ReadModuleNumbers();

            if (exit != null)
            {
                ArrivalError = HorizontalDistance(position, exit.position);
                if (ArrivalError <= arrivalTolerance) Arrived = true;
            }

            FsmValue arrivedValue;
            if (ai.TryGetVariable("arrived", out arrivedValue) && arrivedValue.B)
                Arrived = true;

            _frameCounter++;
            if (recordTrail && trailEveryFrames > 0 && _frameCounter % trailEveryFrames == 0)
                AddTrailPoint(position);

            if (!Arrived && giveUpAfterSeconds > 0f && Time.time - _startTime > giveUpAfterSeconds)
            {
                Verdict = "stuck (no arrival in " + giveUpAfterSeconds.ToString("F0") + " s)";
                Finish();
                return;
            }
            if (Arrived) Finish();
        }

        private void ReadModuleNumbers()
        {
            if (PlannedLength > 0f) return;
            FsmValue value;
            if (ai.TryGetVariable("plannedLength", out value) && value.Kind == FsmValueKind.Float
                && value.F > 0f)
                PlannedLength = value.F;
        }

        private void AddTrailPoint(Vector3 position)
        {
            _trailPositions.Add(position);
            _trailTimes.Add(Time.time);
            _trailStates.Add(ai != null ? ai.CurrentStateName : "-");

            if (_trail != null)
            {
                _trail.positionCount = _trailPositions.Count;
                _trail.SetPosition(_trailPositions.Count - 1, position);
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
            RunSeconds = Time.time - _startTime;

            if (PlannedLength <= 0f)
            {
                // The one legitimate skip: the navigation built-in did not give a
                // path (no NavMesh where the runner stands), so there is nothing
                // to compare the walk against.
                Skipped = true;
                Verdict = "SKIPPED — findPath returned no route (NavMesh missing at the runner's position?)";
            }
            else
            {
                bool arrivedOk = Arrived && ArrivalError >= 0f && ArrivalError <= arrivalTolerance;
                bool notShorter = Travelled + 0.001f >= StraightLine;
                bool followsPlan = Travelled >= PlannedLength * plannedLengthToleranceLow
                                   && Travelled <= PlannedLength * plannedLengthToleranceHigh;
                Verdict = (arrivedOk && notShorter && followsPlan ? "PASS" : "FAIL")
                          + " — arrived " + (arrivedOk ? "yes" : "no")
                          + ", travelled " + Travelled.ToString("F1") + " m vs planned "
                          + PlannedLength.ToString("F1") + " m (straight line "
                          + StraightLine.ToString("F1") + " m)";
                if (!notShorter) Verdict += " [shorter than a straight line: it did not walk]";
                else if (!followsPlan) Verdict += " [walked a different route than the planned one]";
            }

            Debug.Log("[maze-03] " + Verdict + " in " + RunSeconds.ToString("F1") + " s"
                      + " (" + _frameCounter + " frames).", this);
            WriteFiles();
        }

        public string OutputDir { get { return Application.persistentDataPath; } }
        public string VerdictPath { get { return Path.Combine(OutputDir, verdictFileName); } }
        public string TrailPath { get { return Path.Combine(OutputDir, trailFileName); } }

        public void WriteFiles()
        {
            if (_wroteFiles) return;
            _wroteFiles = true;

            StringBuilder row = new StringBuilder();
            row.Append("plannedLength,travelled,straightLine,pathCells,pathLowerBound,arrived,arrivalError,seconds,detourRatio,verdict\n");
            float detour = PlannedLength > 0f ? Travelled / PlannedLength : -1f;
            row.Append(PlannedLength.ToString("F3")).Append(',')
               .Append(Travelled.ToString("F3")).Append(',')
               .Append(StraightLine.ToString("F3")).Append(',')
               .Append(maze != null ? maze.PathCells.ToString() : "-").Append(',')
               .Append(maze != null ? maze.PathLengthLowerBound.ToString("F1") : "-").Append(',')
               .Append(Arrived ? "yes" : "no").Append(',')
               .Append(ArrivalError.ToString("F3")).Append(',')
               .Append(RunSeconds.ToString("F3")).Append(',')
               .Append(detour.ToString("F3")).Append(',')
               .Append('"').Append(Verdict).Append('"').Append('\n');
            File.WriteAllText(VerdictPath, row.ToString());

            if (recordTrail && _trailPositions.Count > 0)
            {
                StringBuilder trail = new StringBuilder();
                trail.Append("index,time,x,y,z,distanceToExit,state\n");
                for (int i = 0; i < _trailPositions.Count; i++)
                {
                    Vector3 p = _trailPositions[i];
                    trail.Append(i).Append(',')
                         .Append(_trailTimes[i].ToString("F3")).Append(',')
                         .Append(p.x.ToString("F3")).Append(',')
                         .Append(p.y.ToString("F3")).Append(',')
                         .Append(p.z.ToString("F3")).Append(',')
                         .Append((exit != null ? HorizontalDistance(p, exit.position) : -1f).ToString("F3"))
                         .Append(',')
                         .Append(_trailStates[i]).Append('\n');
                }
                File.WriteAllText(TrailPath, trail.ToString());
            }

            Debug.Log("[maze-03] wrote " + VerdictPath
                      + (recordTrail ? " and " + TrailPath : ""), this);
        }

        private void OnApplicationQuit() { WriteFiles(); }
        private void OnDestroy() { WriteFiles(); }
    }
}
