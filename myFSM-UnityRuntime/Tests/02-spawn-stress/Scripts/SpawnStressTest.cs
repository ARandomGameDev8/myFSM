// Test 02 — how many AIs can the runtime tick before the frame budget breaks?
//
// Everything is recorded, nothing is assumed:
//   * one row per SPAWN  (instantiate cost, the frame it landed in, the cost of
//     the frame after it — when the new AI's Start/boot has actually run), and
//   * one row every N FRAMES for the whole run (frame time, fps, how many AIs
//     are alive, how many the runtime has registered, total FSM calls served).
//
// With autoStopOnSlowdown on, the test also reports the practical maximum: the
// alive count at the moment the smoothed frame time has stayed above the budget
// for a full second. Press Space to add AIs one at a time, B for a burst, P to
// pause spawning (so you can watch a fixed count), L to dump the CSVs early.
//
// The spawned cube is a dynamic Rigidbody (gravity off, XZ only), so the runtime
// moves it by setting velocity and Unity's own solver does the colliding — the
// same path a real game AI takes. The chasers hunt the PlayerController object,
// which you drive with WASD.
//
// Files (Application.persistentDataPath):
//   spawn_stress_spawns.csv, spawn_stress_frames.csv, spawn_stress_summary.txt

using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
// Alias, not `using System.Diagnostics`: that would make Debug ambiguous with
// UnityEngine.Debug and break every log line in this file (CS0104).
using Stopwatch = System.Diagnostics.Stopwatch;
using MyFSM.Core;
using MyFSM.Unity;

namespace MyFSM.Tests
{
    [DefaultExecutionOrder(100)] // after the AIs have ticked in this frame
    public class SpawnStressTest : MonoBehaviour
    {
        [Header("Scene")]
        [Tooltip("The object the chasers hunt (a PlayerController). Created if empty.")]
        public Transform player;
        public bool createPlayerIfMissing = true;
        public bool createGroundIfMissing = true;
        public float groundSize = 100f;

        [Header("Spawn area")]
        public float spawnRadius = 20f;
        public bool spawnOnRing = false;      // false = uniform disc
        public float spawnY = 0.55f;          // cube half-height + a little
        public float minimumDistanceFromPlayer = 2f;

        [Header("What to spawn")]
        [Tooltip("Optional prefab with Rigidbody + ChaserAI. Empty: built from a primitive cube.")]
        public GameObject cubePrefab;
        public int spawnPerPress = 1;
        public int burstSize = 100;
        public int maxAlive = 5000;

        [Header("Keys")]
        public KeyCode spawnKey = KeyCode.Space;
        public KeyCode burstKey = KeyCode.B;
        public KeyCode clearKey = KeyCode.Backspace;
        public KeyCode pauseKey = KeyCode.P;
        public KeyCode reportKey = KeyCode.L;

        [Header("Recording")]
        public int sampleEveryFrames = 10;
        public string frameFileName = "spawn_stress_frames.csv";
        public string spawnFileName = "spawn_stress_spawns.csv";
        public string summaryFileName = "spawn_stress_summary.txt";

        [Header("Slowdown detection (the practical maximum)")]
        public bool autoStopOnSlowdown = true;
        [Tooltip("Frame time considered 'too slow' (33.3 ms = 30 fps).")]
        public float slowdownFrameMs = 33.3f;
        [Tooltip("How long the smoothed frame time must stay above the budget.")]
        public float sustainedSeconds = 1f;
        [Tooltip("Do not declare a maximum below this many AIs (avoids warm-up noise).")]
        public int minimumCountForStop = 25;
        public bool stopSpawningWhenSlow = true;

        // ------------------------------------------------------------------
        // Results
        // ------------------------------------------------------------------

        public class SpawnRecord
        {
            public int index;
            public int frame;
            public double instantiateMs;
            public float spawnFrameMs;
            public float settleFrameMs;
            public int aliveAfter;
            public int instanceId;
        }

        public class FrameSample
        {
            public int frame;
            public float time;
            public float deltaMs;
            public float smoothedMs;
            public int alive;
            public int registered;
            public long totalCalls;
        }

        public readonly List<SpawnRecord> Spawns = new List<SpawnRecord>();
        public readonly List<FrameSample> Frames = new List<FrameSample>();

        /// <summary>Total AIs ever spawned in this run.</summary>
        public int SpawnedTotal { get; private set; }
        /// <summary>Alive AIs (resources this test is holding).</summary>
        public int AliveCount { get { return _spawned.Count; } }
        /// <summary>Alive count when the frame budget was declared broken (-1 = not reached).</summary>
        public int PracticalMaximum { get; private set; } = -1;
        /// <summary>Spawning paused (by the P key or by the slowdown detector).</summary>
        public bool Paused { get; private set; }

        private readonly List<GameObject> _spawned = new List<GameObject>();
        private readonly List<SpawnRecord> _settling = new List<SpawnRecord>();
        private readonly Stopwatch _watch = new Stopwatch();
        private float _smoothedMs;
        private int _slowFrames;
        private int _frameCounter;
        private int _sampleCounter;
        private int _lastRegistered;
        private long _lastCalls;
        private bool _wroteFiles;

        // ------------------------------------------------------------------

        private void Awake()
        {
            ChaserAI.Player = ResolvePlayer();
            EnsureGround();
        }

        private Transform ResolvePlayer()
        {
            if (player != null) return player;
            GameObject found = GameObject.Find("Player");
            if (found == null && createPlayerIfMissing)
            {
                found = GameObject.CreatePrimitive(PrimitiveType.Cube);
                found.name = "Player";
                found.transform.position = new Vector3(0f, 0.5f, 0f);
                found.transform.localScale = new Vector3(1f, 1f, 1f);
                found.AddComponent<PlayerController>();
                Renderer renderer = found.GetComponent<Renderer>();
                if (renderer != null) renderer.material.color = new Color(0.2f, 0.6f, 1f);
            }
            return found != null ? found.transform : null;
        }

        private void EnsureGround()
        {
            if (!createGroundIfMissing) return;
            if (GameObject.Find("StressGround") != null) return;
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "StressGround";
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(groundSize / 10f, 1f, groundSize / 10f);
        }

        private void Start()
        {
            Debug.Log("[stress] Space = +" + spawnPerPress + " AI, B = +" + burstSize
                      + ", P = pause spawning, L = write CSVs, Backspace = clear."
                      + " Chasers hunt '" + (ChaserAI.Player != null ? ChaserAI.Player.name : "NOTHING")
                      + "'.", this);
        }

        private void Update()
        {
            float deltaMs = Time.unscaledDeltaTime * 1000f;
            _smoothedMs = _smoothedMs <= 0f
                ? deltaMs
                : _smoothedMs + (deltaMs - _smoothedMs) * 0.1f;

            // Cost of the frame AFTER a spawn: this is the first frame in which
            // the new AI has booted and is being ticked.
            if (_settling.Count > 0)
            {
                for (int i = 0; i < _settling.Count; i++)
                    _settling[i].settleFrameMs = deltaMs;
                _settling.Clear();
            }

            HandleInput();
            DetectSlowdown();

            _frameCounter++;
            if (sampleEveryFrames > 0 && _frameCounter >= sampleEveryFrames)
            {
                _frameCounter = 0;
                SampleFrame(deltaMs);
            }
        }

        private void HandleInput()
        {
            if (Input.GetKeyDown(clearKey)) { ClearAll(); return; }
            if (Input.GetKeyDown(pauseKey))
            {
                Paused = !Paused;
                Debug.Log("[stress] spawning " + (Paused ? "paused" : "resumed")
                          + " at " + AliveCount + " AIs", this);
            }
            if (Input.GetKeyDown(reportKey))
            {
                WriteFiles();
                Debug.Log("[stress] report written at " + AliveCount + " AIs", this);
            }
            if (Paused) return;

            if (Input.GetKeyDown(spawnKey)) Spawn(spawnPerPress);
            if (Input.GetKeyDown(burstKey)) Spawn(burstSize);
        }

        private void DetectSlowdown()
        {
            if (!autoStopOnSlowdown) return;

            if (AliveCount >= minimumCountForStop && _smoothedMs > slowdownFrameMs)
                _slowFrames++;
            else
                _slowFrames = 0;

            int needed = Mathf.Max(1, Mathf.RoundToInt(sustainedSeconds / Mathf.Max(0.0001f, Time.unscaledDeltaTime)));
            if (_slowFrames < needed) return;

            // Sustained slowdown with a real population: that is the practical max.
            PracticalMaximum = AliveCount;
            if (stopSpawningWhenSlow) Paused = true;
            _slowFrames = 0;

            Debug.LogWarning("[stress] PRACTICAL MAXIMUM reached: " + AliveCount
                             + " AIs — smoothed frame time " + _smoothedMs.ToString("F1")
                             + " ms (> " + slowdownFrameMs.ToString("F1") + " ms) sustained for "
                             + sustainedSeconds.ToString("F1") + " s. Spawning stopped.", this);
            WriteFiles();
        }

        // ------------------------------------------------------------------
        // Spawning
        // ------------------------------------------------------------------

        public void Spawn(int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (_spawned.Count >= maxAlive)
                {
                    Debug.LogWarning("[stress] maxAlive (" + maxAlive + ") reached — not spawning more.", this);
                    return;
                }

                Vector3 position = RandomSpawnPosition();
                _watch.Reset();
                _watch.Start();
                GameObject go = CreateChaser(position);
                _watch.Stop();

                SpawnedTotal++;
                go.name = "Chaser #" + SpawnedTotal;
                _spawned.Add(go);

                SpawnRecord record = new SpawnRecord
                {
                    index = SpawnedTotal,
                    frame = Time.frameCount,
                    instantiateMs = _watch.Elapsed.TotalMilliseconds,
                    spawnFrameMs = Time.unscaledDeltaTime * 1000f,
                    settleFrameMs = -1f,
                    aliveAfter = _spawned.Count
                };
                Spawns.Add(record);
                _settling.Add(record); // filled in on the next Update
            }
        }

        private GameObject CreateChaser(Vector3 position)
        {
            if (cubePrefab != null)
                return Instantiate(cubePrefab, position, Quaternion.identity);

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.position = position;

            Rigidbody body = go.AddComponent<Rigidbody>();
            // Dynamic body so Unity's solver does the colliding, but gravity off
            // and no tumbling: the chasers chase in the XZ plane.
            body.useGravity = false;
            body.constraints = RigidbodyConstraints.FreezePositionY
                               | RigidbodyConstraints.FreezeRotationX
                               | RigidbodyConstraints.FreezeRotationZ;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            go.AddComponent<ChaserAI>(); // boots in its own Start() this frame
            return go;
        }

        private Vector3 RandomSpawnPosition()
        {
            Vector3 centre = ChaserAI.Player != null ? ChaserAI.Player.position : Vector3.zero;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                float angle = Random.value * Mathf.PI * 2f;
                float radius = spawnOnRing
                    ? spawnRadius
                    : spawnRadius * Mathf.Sqrt(Random.value);
                Vector3 candidate = centre + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                candidate.y = spawnY;
                if (minimumDistanceFromPlayer <= 0f) return candidate;
                if (Vector3.Distance(candidate, centre) >= minimumDistanceFromPlayer) return candidate;
            }
            Vector3 fallback = centre + new Vector3(spawnRadius, 0f, 0f);
            fallback.y = spawnY;
            return fallback;
        }

        public void ClearAll()
        {
            for (int i = 0; i < _spawned.Count; i++)
                if (_spawned[i] != null) Destroy(_spawned[i]);
            _spawned.Clear();
            _settling.Clear();
            Debug.Log("[stress] cleared — " + SpawnedTotal + " AIs were spawned in total this run.", this);
        }

        // ------------------------------------------------------------------
        // Recording
        // ------------------------------------------------------------------

        private void SampleFrame(float deltaMs)
        {
            FrameSample sample = new FrameSample
            {
                frame = Time.frameCount,
                time = Time.unscaledTime,
                deltaMs = deltaMs,
                smoothedMs = _smoothedMs,
                alive = AliveCount
            };
            // Summing every AI's call counter (and snapshotting the registry) is
            // O(N): refresh those two on every 5th sample, report the last known
            // values in between so the CSV never shows a fake zero.
            _sampleCounter++;
            if (_sampleCounter >= 5)
            {
                _sampleCounter = 0;
                _lastCalls = TotalCalls();
                _lastRegistered = RegisteredCount();
            }
            sample.totalCalls = _lastCalls;
            sample.registered = _lastRegistered;
            Frames.Add(sample);
        }

        private long TotalCalls()
        {
            long total = 0;
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] == null) continue;
                ChaserAI ai = _spawned[i].GetComponent<ChaserAI>();
                if (ai != null && ai.Execution != null) total += ai.Execution.CallCount;
            }
            return total;
        }

        private int RegisteredCount()
        {
            MainServer server = MainServer.Instance;
            return server != null ? server.Db.SnapshotInstances().Count : 0;
        }

        public string OutputDir { get { return Application.persistentDataPath; } }
        public string SpawnCsvPath { get { return Path.Combine(OutputDir, spawnFileName); } }
        public string FrameCsvPath { get { return Path.Combine(OutputDir, frameFileName); } }
        public string SummaryPath { get { return Path.Combine(OutputDir, summaryFileName); } }

        public void WriteFiles()
        {
            if (_wroteFiles) return;
            _wroteFiles = true;

            StringBuilder spawns = new StringBuilder();
            spawns.Append("index,frame,instantiateMs,spawnFrameMs,settleFrameMs,aliveAfter,instanceId\n");
            for (int i = 0; i < Spawns.Count; i++)
            {
                SpawnRecord r = Spawns[i];
                int id = r.instanceId;
                if (id == 0 && r.index - 1 < _spawned.Count && _spawned[r.index - 1] != null)
                {
                    ChaserAI ai = _spawned[r.index - 1].GetComponent<ChaserAI>();
                    if (ai != null) { id = ai.InstanceId; r.instanceId = id; }
                }
                spawns.Append(r.index).Append(',')
                      .Append(r.frame).Append(',')
                      .Append(r.instantiateMs.ToString("F4")).Append(',')
                      .Append(r.spawnFrameMs.ToString("F2")).Append(',')
                      .Append(r.settleFrameMs.ToString("F2")).Append(',')
                      .Append(r.aliveAfter).Append(',')
                      .Append(id).Append('\n');
            }
            File.WriteAllText(SpawnCsvPath, spawns.ToString());

            StringBuilder frames = new StringBuilder();
            frames.Append("frame,time,deltaMs,smoothedMs,fps,alive,registered,totalCalls\n");
            for (int i = 0; i < Frames.Count; i++)
            {
                FrameSample s = Frames[i];
                frames.Append(s.frame).Append(',')
                      .Append(s.time.ToString("F3")).Append(',')
                      .Append(s.deltaMs.ToString("F2")).Append(',')
                      .Append(s.smoothedMs.ToString("F2")).Append(',')
                      .Append((1000f / Mathf.Max(0.0001f, s.smoothedMs)).ToString("F1")).Append(',')
                      .Append(s.alive).Append(',')
                      .Append(s.registered).Append(',')
                      .Append(s.totalCalls).Append('\n');
            }
            File.WriteAllText(FrameCsvPath, frames.ToString());

            StringBuilder summary = new StringBuilder();
            summary.Append("myFSM spawn stress test\n");
            summary.Append("spawned total      : ").Append(SpawnedTotal).Append('\n');
            summary.Append("alive at stop      : ").Append(AliveCount).Append('\n');
            summary.Append("practical maximum  : ").Append(PracticalMaximum < 0 ? "not reached" : PracticalMaximum.ToString()).Append('\n');
            summary.Append("slowdown budget    : ").Append(slowdownFrameMs.ToString("F1")).Append(" ms smoothed\n");
            summary.Append("final smoothed ms  : ").Append(_smoothedMs.ToString("F2")).Append('\n');
            summary.Append("final fps          : ").Append((1000f / Mathf.Max(0.0001f, _smoothedMs)).ToString("F1")).Append('\n');
            summary.Append("registered AIs     : ").Append(_lastRegistered > 0 ? _lastRegistered : RegisteredCount()).Append('\n');
            summary.Append("total FSM calls    : ").Append(_lastCalls > 0 ? _lastCalls : TotalCalls()).Append('\n');
            File.WriteAllText(SummaryPath, summary.ToString());

            Debug.Log("[stress] wrote " + SpawnCsvPath + ", " + FrameCsvPath + " and " + SummaryPath, this);
        }

        private void OnApplicationQuit() { WriteFiles(); }
        private void OnDestroy() { WriteFiles(); }
    }
}
