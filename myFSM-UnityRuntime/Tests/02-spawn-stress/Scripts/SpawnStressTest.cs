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
// The scene builds itself: a large ground plane, a cylinder player you drive with
// WASD, and a top-down camera that follows the player. The cubes exist only after
// you press Space (or B) — nothing spawns on its own.
//
// Each spawned cube is a dynamic Rigidbody WITH GRAVITY (rotations locked to X/Z
// so it stays upright), dropped from the air above the centre of the plane: it
// falls, lands, and then chases the player in the XZ plane. The runtime moves it
// by setting velocity, so Unity's own solver does the falling, the colliding and
// the pile-ups — that physics load is part of what this test measures.
//
// Controls are read through StressInput, which works with either Unity input
// backend (legacy Input Manager or the Input System package).
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
        public enum SpawnPlacement
        {
            /// <summary>Drop them from the air above the centre of the plane.</summary>
            CenterInAir,
            /// <summary>Put them on the ground around the player.</summary>
            AroundPlayer
        }

        [Header("Scene (built automatically)")]
        [Tooltip("The object the chasers hunt (a PlayerController). Created if empty.")]
        public Transform player;
        public bool createPlayerIfMissing = true;
        [Tooltip("The player is a cylinder: 2 m tall, standing on the ground.")]
        public float playerHeight = 2f;
        public Color playerColour = new Color(0.2f, 0.7f, 1f);
        public bool createGroundIfMissing = true;
        [Tooltip("Side length of the ground plane in metres. Big enough for thousands of " +
                 "cubes: 400 leaves room for 1000+ without stacking at the edge.")]
        public float groundSize = 400f;

        [Header("Camera")]
        [Tooltip("Park the Main Camera above the player, looking down, and make it follow.")]
        public bool setUpTopDownCamera = true;
        [Tooltip("Metres above the player (45 keeps the whole spawn column in frame).")]
        public float cameraHeight = 45f;
        public float cameraBackDistance = 18f;

        [Header("Where spawned cubes appear")]
        [Tooltip("CenterInAir (default): cubes fall from the air above the middle of the " +
                 "plane, with gravity on. AroundPlayer: old behaviour, placed on the ground " +
                 "around the player.")]
        public SpawnPlacement placement = SpawnPlacement.CenterInAir;
        [Tooltip("CentreInAir: metres above the ground they are dropped from.")]
        public float spawnHeight = 15f;
        [Tooltip("CentreInAir: random horizontal offset from the centre, so they do not " +
                 "all start inside each other.")]
        public float spawnJitter = 1.5f;
        [Tooltip("CentreInAir: how far the drop disc widens as more cubes are alive " +
                 "(radius = jitter + spread * sqrt(alive)). Spreads a big crowd out " +
                 "instead of piling it on one spot.")]
        public float spawnSpread = 0.8f;
        [Tooltip("AroundPlayer: radius of the placement disc/ring around the player.")]
        public float spawnRadius = 20f;
        [Tooltip("AroundPlayer: place them on a ring instead of filling the disc.")]
        public bool spawnOnRing = false;
        [Tooltip("AroundPlayer: height of the cube centre when it is standing on the ground.")]
        public float spawnY = 0.55f;
        [Tooltip("AroundPlayer: keep this far away from the player.")]
        public float minimumDistanceFromPlayer = 2f;

        [Header("What to spawn")]
        [Tooltip("Cubes fall and pile up under gravity (Unity's solver is then part of the load " +
                 "being measured). Off: gravity-free cubes that only slide, which is cheaper.")]
        public bool gravityEnabled = true;
        [Tooltip("Optional prefab with Rigidbody + ChaserAI. Empty: built from a primitive cube.")]
        public GameObject cubePrefab;
        public int spawnPerPress = 1;
        public int burstSize = 100;
        [Tooltip("Spawn this many cubes as soon as Play starts. 0 (default) = nothing " +
                 "spawns until you press the spawn key.")]
        public int spawnOnStart = 0;
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
        private bool _warnedNoGravity;
        private Transform _ground;
        private long _lastCalls;
        private bool _wroteFiles;

        // ------------------------------------------------------------------

        private void Awake()
        {
            EnsureGround();
            ChaserAI.Player = ResolvePlayer();
            ConfigurePlayerBounds();
            SetUpCamera();
        }

        /// <summary>
        /// Keeps the player's clamp square inside the plane, so walking cannot take
        /// it off the edge of the ground the cubes are standing on.
        /// </summary>
        private void ConfigurePlayerBounds()
        {
            if (player == null || !createGroundIfMissing) return;
            PlayerController controller = player.GetComponent<PlayerController>();
            if (controller == null) return;
            controller.halfExtent = Mathf.Max(5f, groundSize * 0.5f - 5f);
            controller.height = playerHeight * 0.5f;
        }

        private void SetUpCamera()
        {
            if (!setUpTopDownCamera) return;
            Camera camera = Camera.main;
            if (camera == null)
            {
                Debug.LogWarning("[stress] no Main Camera (nothing tagged MainCamera) — skipping "
                                 + "the follow rig. Add a camera tagged MainCamera, or drag a "
                                 + "TopDownFollowCamera onto one yourself.", this);
                return;
            }
            TopDownFollowCamera rig = camera.GetComponent<TopDownFollowCamera>();
            if (rig == null) rig = camera.gameObject.AddComponent<TopDownFollowCamera>();
            rig.height = cameraHeight;
            rig.backDistance = cameraBackDistance;
            rig.target = player;
            rig.SnapToTarget();
            Debug.Log("[stress] camera rig: '" + camera.name + "' parked " + cameraHeight
                      + " m above and " + cameraBackDistance + " m behind the player, following it.",
                      camera);
        }

        private Transform ResolvePlayer()
        {
            if (player != null) return player;
            GameObject found = GameObject.Find("Player");
            if (found == null && createPlayerIfMissing)
            {
                // A cylinder, standing on the ground: Unity's cylinder primitive is
                // 2 m tall with its origin at the centre, so y = half its height.
                found = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                found.name = "Player";
                found.transform.position = new Vector3(0f, playerHeight * 0.5f, 0f);
                found.transform.localScale = new Vector3(1f, playerHeight * 0.5f, 1f);
                found.AddComponent<PlayerController>();
                Renderer renderer = found.GetComponent<Renderer>();
                if (renderer != null) renderer.material.color = playerColour;
                Debug.Log("[stress] created the player: a " + playerHeight + " m cylinder you "
                          + "drive with WASD.", found);
            }
            return found != null ? found.transform : null;
        }

        private void EnsureGround()
        {
            GameObject existing = GameObject.Find("StressGround");
            if (existing != null)
            {
                _ground = existing.transform;
                return;
            }
            if (!createGroundIfMissing) return;

            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "StressGround";
            ground.transform.position = Vector3.zero;
            // Unity's plane primitive is 10x10 m, so the scale is size/10.
            ground.transform.localScale = new Vector3(groundSize / 10f, 1f, groundSize / 10f);
            _ground = ground.transform;
            Debug.Log("[stress] ground plane " + groundSize + " x " + groundSize + " m created "
                      + "(room for thousands of cubes), centred on " + ground.transform.position
                      + ".", ground);
        }

        private void Start()
        {
            Debug.Log("[stress] ready — nothing has spawned yet: you press the keys. Space = +"
                      + spawnPerPress + " AI, B = +" + burstSize + ", P = pause spawning, "
                      + "L = write the CSVs, Backspace = clear. Chasers hunt '"
                      + (ChaserAI.Player != null ? ChaserAI.Player.name : "NOTHING")
                      + "'. Input backend: " + StressInput.Backend
                      + ". Placement: " + placement
                      + (placement == SpawnPlacement.CenterInAir
                         ? " (from " + spawnHeight + " m up, gravity "
                           + (gravityEnabled ? "on" : "OFF") + ")"
                         : " (on the ground at " + spawnY + " m)")
                      + ", ground plane " + groundSize + " m.", this);

            if (!StressInput.Available)
                Debug.LogError("[stress] no readable keyboard input: " + StressInput.Backend + ". "
                               + StressInput.Fix, this);

            // Opt-in only (spawnOnStart is 0 by default): the test does not put AIs in
            // the scene behind your back, it only answers what you ask it to spawn.
            if (spawnOnStart > 0) Spawn(spawnOnStart);
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
            if (StressInput.GetKeyDown(clearKey)) { ClearAll(); return; }
            if (StressInput.GetKeyDown(pauseKey))
            {
                Paused = !Paused;
                Debug.Log("[stress] spawning " + (Paused ? "paused" : "resumed")
                          + " at " + AliveCount + " AIs", this);
            }
            if (StressInput.GetKeyDown(reportKey))
            {
                WriteFiles();
                Debug.Log("[stress] report written at " + AliveCount + " AIs", this);
            }
            if (Paused) return;

            if (StressInput.GetKeyDown(spawnKey)) Spawn(spawnPerPress);
            if (StressInput.GetKeyDown(burstKey)) Spawn(burstSize);
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

                Vector3 position = NextSpawnPosition();
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
            // Dynamic body WITH gravity, so Unity's solver does the falling, the
            // landing, the colliding and the pile-ups. Only the tumbling is locked
            // (X/Z rotation) so a cube stays upright and can be pushed around.
            body.useGravity = gravityEnabled;
            body.constraints = RigidbodyConstraints.FreezeRotationX
                               | RigidbodyConstraints.FreezeRotationZ;
            if (!gravityEnabled)
            {
                // No gravity: keep the cubes at the height they were dropped at,
                // otherwise they would drift down forever with nothing pulling back.
                body.constraints |= RigidbodyConstraints.FreezePositionY;
            }
            body.interpolation = RigidbodyInterpolation.Interpolate;

            go.AddComponent<ChaserAI>(); // boots in its own Start() this frame
            return go;
        }

        /// <summary>
        /// Where the next cube appears. The default drops it from the air above the
        /// centre of the plane (spawnHeight, widening a little as the crowd grows);
        /// AroundPlayer keeps the old ground-level ring/disc around the player.
        /// </summary>
        public Vector3 NextSpawnPosition()
        {
            if (placement == SpawnPlacement.CenterInAir) return AirSpawnPosition();
            return GroundSpawnPosition();
        }

        /// <summary>
        /// Above the centre of the plane, in the air, so the cube falls and lands.
        /// Successive cubes spread out on a golden-angle spiral whose radius grows
        /// with the live population (jitter + spread * sqrt(alive)): 100 cubes land
        /// in a loose cluster instead of all inside each other, which would make the
        /// solver fire them off in every direction and ruin the measurement.
        /// </summary>
        private Vector3 AirSpawnPosition()
        {
            if (!gravityEnabled && !_warnedNoGravity)
            {
                _warnedNoGravity = true;
                Debug.LogWarning("[stress] gravityEnabled is OFF but cubes are being dropped from "
                                 + spawnHeight.ToString("F0") + " m: with Y frozen they will hang in "
                                 + "the air there. Set placement = AroundPlayer, or turn gravity on.",
                                 this);
            }
            int index = _spawned.Count;
            float radius = spawnJitter + spawnSpread * Mathf.Sqrt(index);
            float angle = index * 2.39996323f; // golden angle: even coverage, no runs
            float height = spawnHeight + Random.Range(-0.5f, 0.5f);

            Vector3 centre = GroundCentre();
            return new Vector3(centre.x + Mathf.Cos(angle) * radius,
                               centre.y + height,
                               centre.z + Mathf.Sin(angle) * radius);
        }

        /// <summary>Centre of the ground plane: the object was cached in EnsureGround.</summary>
        private Vector3 GroundCentre()
        {
            return _ground != null ? _ground.position : Vector3.zero;
        }

        private Vector3 GroundSpawnPosition()
        {
            Vector3 centre = ChaserAI.Player != null ? ChaserAI.Player.position : Vector3.zero;
            centre.y = 0f;
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
            summary.Append("ground plane       : ").Append(groundSize).Append(" x ").Append(groundSize).Append(" m\n");
            summary.Append("cube gravity       : ").Append(gravityEnabled ? "ON (physics solver included in these numbers)" : "off").Append('\n');
            summary.Append("spawn placement    : ").Append(placement).Append(placement == SpawnPlacement.CenterInAir
                              ? " at " + spawnHeight.ToString("F0") + " m" : "").Append('\n');
            summary.Append("input backend      : ").Append(StressInput.Backend).Append('\n');
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
