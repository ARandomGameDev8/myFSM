// Test 03 — checks, afterwards, that the runner really chased the target
// through the maze and arrived at it.
//
// The AI is the moveTowards(NavMeshAgent, Object3D, speed) brain: it posts a
// persistent chase goal and never reports a planned route, so the verdict is
// built from what this recorder measures itself plus two facts from the maze
// controller:
//
//   travelled    this recorder's accumulation of the runner's movement, sampled
//                every frame — what actually happened in the scene
//   straightLine entry -> exit in a straight line, measured by the maze
//                controller. A shortest path through a maze can never be
//                shorter than this, so a runner that "arrived" without walking
//                (teleport, dropped into the exit) fails this bound
//   pathLowerBound (PathCells - 1) * cellSize — the BFS route through the maze
//                layout, converted to metres. It is a lower bound, not the
//                plan: wall thickness, the agent's radius (corner cutting) and
//                diagonal cutting all shave metres off, and with a MOVING
//                target the runner legitimately shortcuts toward wherever the
//                target is now. So it is only reported, not enforced — the
//                strict bounds are straightLine (must beat) and arrival.
//
// Verdict: arrived inside `arrivalTolerance` of the target's live position,
// travelled >= straightLine. The target may be moving, so arrival is measured
// against where the target is at that moment, not a fixed point.
//
// Files (Application.persistentDataPath):
//   maze_run.csv    one verdict row + the numbers behind it
//   maze_trail.csv  per-frame positions, distance to the target, and FSM state

using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using MyFSM.Unity;

namespace MyFSM.Tests
{
    [DefaultExecutionOrder(50)] // after the AI has ticked this frame
    public class MazePathRecorder : MonoBehaviour
    {
        [Header("What to watch")]
        [Tooltip("The runner. Left empty: found on this GameObject.")]
        public TestAI ai;
        [Tooltip("The chase target. Left empty: the maze controller's ActiveTarget " +
                 "(the assigned target, or the static exit marker it created).")]
        public Transform target;
        public MazeGeneratorController maze;

        [Header("Verdict thresholds")]
        [Tooltip("How close to the target counts as arrived (metres).")]
        public float arrivalTolerance = 1.0f;
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
            if (ai == null) ai = GetComponent<TestAI>();
            if (maze == null) maze = FindObjectOfType<MazeGeneratorController>();
        }

        private void Start()
        {
            _startTime = Time.time;
            _lastPosition = ai.transform.position;
            if (target == null && maze != null) target = maze.ActiveTarget;

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

            if (target == null || maze == null)
            {
                Skipped = true;
                Verdict = "SKIPPED — no target to watch (assign a target or add the maze controller)";
                Debug.LogWarning("[maze-03] " + Verdict, this);
            }
            else
            {
                // The straight line is an entry-to-exit fact: measure it now,
                // before the runner has moved, not at the end of the run.
                StraightLine = maze.StraightDistance;
            }
        }

        private void Update()
        {
            if (_finished || ai == null || target == null) return;

            Vector3 position = ai.transform.position;
            Travelled += HorizontalDistance(_lastPosition, position);
            _lastPosition = position;

            // Against the target's LIVE position: it may be moving (test 04's
            // walker), so "arrived" means caught up with it, not reached a point.
            ArrivalError = HorizontalDistance(position, target.position);
            if (ArrivalError <= arrivalTolerance) Arrived = true;

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

            bool arrivedOk = Arrived && ArrivalError >= 0f && ArrivalError <= arrivalTolerance;
            // The one non-negotiable fact: you cannot arrive at a maze exit by
            // walking less than the straight-line distance to it.
            bool notShorter = Travelled + 0.001f >= StraightLine;

            if (arrivedOk && notShorter)
            {
                Verdict = "PASS — arrived, travelled " + Travelled.ToString("F1")
                          + " m (straight line " + StraightLine.ToString("F1") + " m)";
            }
            else
            {
                Verdict = "FAIL — arrived " + (arrivedOk ? "yes" : "no")
                          + ", travelled " + Travelled.ToString("F1")
                          + " m vs straight line " + StraightLine.ToString("F1") + " m";
                if (!notShorter) Verdict += " [shorter than a straight line: it did not walk]";
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
            row.Append("travelled,straightLine,pathCells,pathLowerBound,loopsOpened,")
               .Append("arrived,arrivalError,seconds,verdict\n");
            row.Append(Travelled.ToString("F3")).Append(',')
               .Append(StraightLine.ToString("F3")).Append(',')
               .Append(maze != null ? maze.PathCells.ToString() : "-").Append(',')
               .Append(maze != null ? maze.PathLengthLowerBound.ToString("F1") : "-").Append(',')
               .Append(maze != null ? maze.LoopsOpened.ToString() : "-").Append(',')
               .Append(Arrived ? "yes" : "no").Append(',')
               .Append(ArrivalError.ToString("F3")).Append(',')
               .Append(RunSeconds.ToString("F3")).Append(',')
               .Append('"').Append(Verdict).Append('"').Append('\n');
            File.WriteAllText(VerdictPath, row.ToString());

            if (recordTrail && _trailPositions.Count > 0)
            {
                StringBuilder trail = new StringBuilder();
                trail.Append("index,time,x,y,z,distanceToTarget,state\n");
                for (int i = 0; i < _trailPositions.Count; i++)
                {
                    Vector3 p = _trailPositions[i];
                    trail.Append(i).Append(',')
                         .Append(_trailTimes[i].ToString("F3")).Append(',')
                         .Append(p.x.ToString("F3")).Append(',')
                         .Append(p.y.ToString("F3")).Append(',')
                         .Append(p.z.ToString("F3")).Append(',')
                         .Append((target != null ? HorizontalDistance(p, target.position) : -1f).ToString("F3"))
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
