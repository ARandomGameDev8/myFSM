// Test 02 — spawn stress: many chasing AIs, one WASD player, real collisions.
//
// WHAT IT BUILDS (put this component on any GameObject in an otherwise empty scene)
//
//   StressGround   a plane at the origin, `groundSize` metres square
//   Player         a 2 m cylinder with a CharacterController — ordinary WASD
//                  movement (keys -> Move -> gravity), and a body the crowd can
//                  block but never push
//   the camera     moved above and behind the player, following it
//
// WHAT IT SPAWNS (only when you ask; nothing spawns on its own)
//
//   A cube: dynamic Rigidbody (gravity on, X/Z rotation frozen) dropped in the
//   air, plus one chaser AI. The AI walks towards the player with moveTowards,
//   which the runtime turns into the plain follower script, verbatim, once per
//   physics step from the AI's FixedUpdate:
//     rb.MovePosition(rb.position + (target - rb.position).normalized * speed
//                     * Time.fixedDeltaTime)
//   Nothing else is written to the body, so the physics step resolves every
//   contact: cubes push each other, are stopped by each other, and pile up.
//
// KEYS
//
//   Space  +1 cube          B  +100 cubes        P  pause spawning
//   L      write the CSVs   Backspace  clear     R  player back to the origin
//
// OUTPUT (Application.persistentDataPath)
//
//   spawn_stress_spawns.csv    one row per spawn: instantiate / step / settle ms
//   spawn_stress_frames.csv    sampled frames: ms, fps, alive, FSM calls, the
//                              mean/nearest crowd distance to the player, and the
//                              player's own position + key state (-1 = no crowd)
//   spawn_stress_summary.txt   the same numbers at the end
//
// The player is watched, not trusted: its scripts are listed at startup, and if it
// moves sideways while no movement key is held that is a console ERROR with the
// distance - the runtime never quietly lets the player be dragged anywhere.
//
// Nothing here hardens or converts anything at runtime: the player is created the
// way it should be (controller + WASD), each cube is created the way it should be
// (body + target bound on the spot), and what the console says is what happened.

using System.Collections.Generic;
using System.IO;
using System.Text;
using MyFSM.Unity;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEngine;

namespace MyFSM.Tests
{
    public class SpawnStressTest : MonoBehaviour
    {
        public enum SpawnPlacement
        {
            /// <summary>Drop points spread over the whole plane (default: no pile at
            /// the middle, so the crowd visibly comes from everywhere).</summary>
            SpreadOverPlane,
            /// <summary>Above the centre of the plane, widening as the crowd grows.</summary>
            CentreInAir,
            /// <summary>On the ground in a ring/disc around the player.</summary>
            AroundPlayer,
        }

        [Header("Scene (built automatically)")]
        [Tooltip("The WASD object the crowd chases. Empty: an object named 'Player', "
                 + "or the object carrying a PlayerController, or a new cylinder.")]
        public Transform player;
        public bool createPlayerIfMissing = true;
        [Tooltip("Height of the player cylinder in metres (its centre sits at half of it).")]
        public float playerHeight = 2f;
        [Tooltip("Metres per second for the WASD player.")]
        public float playerSpeed = 8f;
        public Color playerColour = new Color(0.2f, 0.7f, 1f);

        public bool createGroundIfMissing = true;
        [Tooltip("Side length of the ground plane. 400 leaves room for 1000+ cubes.")]
        public float groundSize = 400f;
        public Color groundColour = new Color(0.25f, 0.28f, 0.3f);

        [Header("Where spawned cubes appear")]
        public SpawnPlacement placement = SpawnPlacement.SpreadOverPlane;
        [Tooltip("Metres above the ground the cubes are dropped from.")]
        public float spawnHeight = 15f;
        [Tooltip("SpreadOverPlane: the fraction of the plane's half-size the drop points use.")]
        [Range(0.05f, 1f)] public float spreadFraction = 0.45f;
        [Tooltip("CentreInAir: radius of the first cubes.")]
        public float centreSpread = 1.5f;
        [Tooltip("CentreInAir: extra metres of radius per sqrt(cube).")]
        public float centreSpreadGrowth = 0.8f;
        [Tooltip("AroundPlayer: radius of the ring/disc around the player.")]
        public float playerRingRadius = 20f;
        [Tooltip("Keep a new cube at least this far from the player (0 = no rule).")]
        public float minimumDistanceFromPlayer = 2f;
        [Tooltip("Gravity on the cubes. Off: they hang where they were dropped.")]
        public bool cubeGravity = true;

        [Header("What to spawn")]
        [Tooltip("Optional prefab with a Rigidbody + ChaserAI. Empty: a primitive cube.")]
        public GameObject cubePrefab;
        [Tooltip("Cubes to spawn by itself when play starts. 0 = nothing self-drives.")]
        public int spawnOnStart = 0;
        public int batchSmall = 1;
        public int batchLarge = 100;

        [Header("Keys")]
        public KeyCode keySpawnSmall = KeyCode.Space;
        public KeyCode keySpawnLarge = KeyCode.B;
        public KeyCode keyPause = KeyCode.P;
        public KeyCode keyWriteFiles = KeyCode.L;
        public KeyCode keyClear = KeyCode.Backspace;
        public KeyCode keyResetPlayer = KeyCode.R;

        [Header("Recording")]
        public bool record = true;
        public string spawnFileName = "spawn_stress_spawns.csv";
        public string frameFileName = "spawn_stress_frames.csv";
        public string summaryFileName = "spawn_stress_summary.txt";
        [Tooltip("Sample a CSV frame every N frames.")]
        public int sampleEveryFrames = 10;

        [Header("Slowdown detection")]
        [Tooltip("Pause spawning once the frame budget has been broken for a while.")]
        public bool stopWhenSlow = true;
        [Tooltip("Smoothed frame time (ms) above which the machine counts as saturated.")]
        public float slowdownFrameMs = 33.3f;
        [Tooltip("Seconds the budget must stay broken before spawning pauses.")]
        public float slowdownHoldSeconds = 1.5f;
        [Tooltip("Never pause before this many cubes exist.")]
        public int minimumCountForStop = 100;
        [Tooltip("Print one line about the crowd (count, mean/nearest distance to the "
                 + "player) every N seconds. 0 = never.")]
        public float logCrowdEverySeconds = 5f;

        [Header("Player watch")]
        [Tooltip("Metres the player may travel sideways with NO key held (measured from "
                 + "where the keys left it, accumulated) before it is logged as an error. "
                 + "0 = off.")]
        public float driftWarnDistance = 0.05f;

        public class SpawnRecord
        {
            public int index;
            public int frame;
            public float instantiateMs;
            public float spawnFrameMs;
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
            /// <summary>Mean distance (m) from the live cubes to the player; -1 = no crowd.</summary>
            public float crowdMeanToPlayer = -1f;
            /// <summary>Distance (m) from the player to the closest cube; -1 = no crowd.</summary>
            public float crowdNearestToPlayer = -1f;
            /// <summary>Where the player was when this frame was sampled.</summary>
            public Vector3 playerPosition;
            /// <summary>True when a movement key was held at sample time.</summary>
            public bool playerMoving;
        }

        public readonly List<SpawnRecord> Spawns = new List<SpawnRecord>();
        public readonly List<FrameSample> Frames = new List<FrameSample>();

        /// <summary>Cubes alive right now.</summary>
        public int AliveCount { get { return _spawned.Count; } }
        /// <summary>Total cubes spawned this run.</summary>
        public int SpawnedTotal { get; private set; }
        /// <summary>Spawning paused (by the P key or by the slowdown detector).</summary>
        public bool Paused { get; private set; }
        /// <summary>Alive count at the moment spawning was declared too slow (-1 = never).</summary>
        public int PracticalMaximum { get; private set; } = -1;

        private readonly List<GameObject> _spawned = new List<GameObject>();
        private readonly Stopwatch _watch = new Stopwatch();
        private Transform _ground;
        private float _smoothedMs;
        private float _slowSeconds;
        private int _frameCounter;
        private int _sampleCounter;
        private long _lastCalls;
        private int _lastRegistered;
        private bool _wroteFiles;
        private PlayerController _wasd;
        private float _nextCrowdLog;
        private Vector3 _lastPlayerPosition;
        private Vector3 _driftAnchor;
        private float _driftSinceAnchor;
        private bool _havePlayerPosition;
        private bool _wasdMovingLastFrame;
        private float _driftTotal;
        private int _driftEvents;
        private float _nextDriftLog;
        private bool _loggedPlayer;
        private int _verifyTries;
        private bool _verifiedTarget;

        // ------------------------------------------------------------------
        // Setup: ground, player, camera. All of it runs once, in Awake, and none
        // of it ever touches the player again.
        // ------------------------------------------------------------------

        private void Awake()
        {
            EnsureGround();
            player = ResolvePlayer();
            SetUpCamera();
            LogSetup();

            for (int i = 0; i < spawnOnStart; i++) Spawn(1);
        }

        private void EnsureGround()
        {
            GameObject existing = GameObject.Find("StressGround");
            if (existing != null)
            {
                _ground = existing.transform;
                return;
            }
            if (!createGroundIfMissing)
            {
                Debug.LogWarning("[stress] no ground: create a plane (or set "
                                 + "createGroundIfMissing) before the cubes start falling.", this);
                return;
            }

            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "StressGround";
            // Unity's plane is 10 m across before scaling.
            ground.transform.localScale = new Vector3(groundSize / 10f, 1f, groundSize / 10f);
            ground.transform.position = Vector3.zero;
            Renderer renderer = ground.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = groundColour;
            _ground = ground.transform;
        }

        /// <summary>
        /// The WASD object: the one you assigned, else an object named "Player", else
        /// the object carrying a PlayerController, else a new cylinder. When the test
        /// builds it, it gets a CharacterController: normal WASD movement, gravity,
        /// and the crowd cannot push it (a controller is not moved by rigidbodies).
        /// </summary>
        private Transform ResolvePlayer()
        {
            if (player != null) return ConfigureKnownPlayer(player);

            GameObject named = GameObject.Find("Player");
            PlayerController wasd = FindObjectOfType<PlayerController>();

            // The object you drive is the object with the WASD controller on it. If a
            // different object happens to be called "Player", say so loudly: chasing
            // the one at the origin while you drive the other one is exactly what
            // "everything is being pulled to the middle" looks like.
            if (wasd != null)
            {
                if (named != null && named.transform != wasd.transform)
                {
                    Debug.LogWarning("[stress] two player candidates: '" + named.name
                                     + "' (named 'Player', NOT driven) and '" + wasd.name
                                     + "' (has a PlayerController). Using the one you drive: '"
                                     + wasd.name + "'. Rename or delete the other one.", wasd);
                }
                else
                {
                    Debug.Log("[stress] using the existing WASD object '" + wasd.name
                              + "' as the player.", wasd);
                }
                return ConfigureKnownPlayer(wasd.transform);
            }

            if (named != null)
            {
                Debug.LogWarning("[stress] using the object named 'Player' ('" + named.name
                                 + "') — it has no PlayerController, so nothing in this test "
                                 + "will move it.", named);
                return ConfigureKnownPlayer(named.transform);
            }

            if (!createPlayerIfMissing)
            {
                Debug.LogError("[stress] no player: assign one, name it 'Player', or set "
                               + "createPlayerIfMissing.", this);
                return null;
            }
            return ConfigureKnownPlayer(CreatePlayer().transform);
        }

        private Transform ConfigureKnownPlayer(Transform who)
        {
            PlayerController controller = who.GetComponent<PlayerController>();
            if (controller == null) return who;
            if (controller.speed <= 0f) controller.speed = playerSpeed;
            if (controller.halfExtent <= 0f) controller.halfExtent = groundSize * 0.48f;
            return who;
        }

        private GameObject CreatePlayer()
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "Player";
            // Unity's cylinder is 2 m tall with its origin at the centre.
            go.transform.position = new Vector3(0f, playerHeight * 0.5f, 0f);
            go.transform.localScale = new Vector3(1f, playerHeight * 0.5f, 1f);

            // The primitive's capsule collider would be a second, static collider on
            // the same object. The CharacterController IS the collider here.
            Collider primitiveCollider = go.GetComponent<Collider>();
            if (primitiveCollider != null) Destroy(primitiveCollider);

            CharacterController controller = go.AddComponent<CharacterController>();
            controller.height = playerHeight;
            controller.radius = 0.5f;
            controller.center = Vector3.zero;

            PlayerController wasd = go.AddComponent<PlayerController>();
            wasd.speed = playerSpeed;
            wasd.halfExtent = groundSize * 0.48f;

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = playerColour;

            Debug.Log("[stress] created the player: a " + playerHeight
                      + " m cylinder with a CharacterController — drive it with WASD.", go);
            return go;
        }

        private void SetUpCamera()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                Debug.LogWarning("[stress] no Camera.main in the scene: add a camera "
                                 + "(the view is not part of the measurement).", this);
                return;
            }
            TopDownFollowCamera follow = camera.GetComponent<TopDownFollowCamera>();
            if (follow == null) follow = camera.gameObject.AddComponent<TopDownFollowCamera>();
            follow.target = player;
            follow.SnapToTarget();   // guards a null target itself
            Debug.Log("[stress] camera: TopDownFollowCamera on '" + camera.gameObject.name
                      + "', " + follow.height.ToString("F0") + " m up / "
                      + follow.backDistance.ToString("F0") + " m back, follow smoothing "
                      + follow.smoothing.ToString("F2") + " s"
                      + (follow.smoothing > 0f
                         ? " — while you walk the camera trails behind you, and when you "
                           + "release the keys it glides forward to catch up, so the "
                           + "world appears to slide and the player re-centres in the "
                           + "frame for a moment. That is the camera, not movement: set "
                           + "smoothing = 0 for a rigid camera and it disappears."
                         : " (rigid: the camera cannot lag the player at all)."),
                      follow);
        }

        private void LogSetup()
        {
            if (player != null && !_loggedPlayer)
            {
                _loggedPlayer = true;
                _wasd = player.GetComponent<PlayerController>();
                Debug.Log("[stress] player '" + player.name + "': "
                          + DescribeBody(player) + ", driven by WASD only"
                          + (StressInput.Available
                             ? " (input: " + StressInput.Backend + ")."
                             : " — BUT NO INPUT BACKEND IS AVAILABLE: " + StressInput.Fix),
                          player);
            }
            if (player != null && _wasd != null)
            {
                Debug.Log("[stress] WASD controller is on '" + _wasd.gameObject.name
                          + "'; the crowd's target is '" + player.name
                          + "' — the object itself, not a child of it: "
                          + (_wasd.gameObject == player.gameObject
                             ? "same object, so the crowd is moved by what you move."
                             : "A DIFFERENT OBJECT. Point this component's Player field at "
                               + "the object you drive."), player);
            }
            if (player != null)
            {
                int foreign;
                string loadout = PlayerLoadout(out foreign);
                if (foreign > 0)
                    Debug.LogError("[stress] the player '" + player.name + "' carries "
                                   + foreign + " script(s) that are NOT PlayerController: "
                                   + loadout + ". Anything here that writes a position can "
                                   + "move the player - remove it, or point it elsewhere.",
                                   player);
                else
                    Debug.Log("[stress] scripts on the player '" + player.name + "': "
                              + loadout + " - the complete list of things that can move it.",
                              player);

                Rigidbody body = player.GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic)
                    Debug.LogError("[stress] the player carries a DYNAMIC Rigidbody: physics "
                                   + "can push this object, so the crowd CAN shove it. "
                                   + "Make it kinematic or remove it - a CharacterController "
                                   + "is the collider this test needs.", player);
                else if (body != null)
                    Debug.Log("[stress] the player also carries a kinematic Rigidbody "
                              + "(nothing can push a kinematic body).", player);
                else if (player.GetComponent<CharacterController>() == null)
                    Debug.LogWarning("[stress] the player has no CharacterController and no "
                                     + "Rigidbody: it is moved by writing its transform, and "
                                     + "cubes will walk through it.", player);
            }
            Debug.Log("[stress] ground " + groundSize + " m, cubes "
                      + (cubeGravity ? "with gravity" : "without gravity")
                      + ", spawn placement " + placement
                      + ", spawnOnStart " + spawnOnStart
                      + " (Space +" + batchSmall + ", B +" + batchLarge + ").", this);
        }

        private string DescribeBody(Transform who)
        {
            CharacterController controller = who.GetComponent<CharacterController>();
            if (controller != null)
                return "CharacterController (the crowd blocks it, never pushes it)";
            Rigidbody body = who.GetComponent<Rigidbody>();
            if (body != null)
                return body.isKinematic
                    ? "kinematic Rigidbody (nothing can push it)"
                    : "DYNAMIC Rigidbody — the crowd can push this player around";
            return "transform only (the crowd is blocked by its collider)";
        }

        // ------------------------------------------------------------------
        // Spawning
        // ------------------------------------------------------------------

        /// <summary>Spawns `count` cubes now, each at its own drop point.</summary>
        public void Spawn(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Vector3 position = NextSpawnPosition();
                _watch.Reset();
                _watch.Start();
                GameObject cube = CreateChaser(position);
                float instantiateMs = (float)_watch.Elapsed.TotalMilliseconds;   // TimeSpan totals are double
                _watch.Stop();

                SpawnedTotal++;
                Spawns.Add(new SpawnRecord
                {
                    index = SpawnedTotal,
                    frame = Time.frameCount,
                    instantiateMs = instantiateMs,
                    spawnFrameMs = Time.unscaledDeltaTime * 1000f,
                    aliveAfter = _spawned.Count
                });
            }
        }

        private GameObject CreateChaser(Vector3 position)
        {
            GameObject cube;
            if (cubePrefab != null)
            {
                cube = Instantiate(cubePrefab, position, Quaternion.identity);
            }
            else
            {
                cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.position = position;

                Rigidbody body = cube.AddComponent<Rigidbody>();
                body.useGravity = cubeGravity;
                // Tumbling locked so a cube stays upright and can be pushed around.
                body.constraints = RigidbodyConstraints.FreezeRotationX
                                   | RigidbodyConstraints.FreezeRotationZ;
                if (!cubeGravity)
                {
                    // Nothing would bring it back down, so hold the drop height.
                    body.constraints |= RigidbodyConstraints.FreezePositionY;
                }
            }

            ChaserAI ai = cube.GetComponent<ChaserAI>();
            if (ai == null) ai = cube.AddComponent<ChaserAI>();
            // Bind the target on the spot: the component's Awake has run by now and
            // its Start (where the runtime reads this) runs later this frame, so no
            // global state is involved and nothing to guess about.
            ai.Target = player;

            _spawned.Add(cube);
            return cube;
        }

        /// <summary>Where the next cube appears, per `placement`.</summary>
        public Vector3 NextSpawnPosition()
        {
            if (placement == SpawnPlacement.AroundPlayer) return GroundSpawnPosition();

            Vector3 centre = GroundCentre();
            float radius = placement == SpawnPlacement.CentreInAir
                ? centreSpread + centreSpreadGrowth * Mathf.Sqrt(_spawned.Count)
                : groundSize * 0.5f * spreadFraction;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                Vector2 point = Random.insideUnitCircle * radius;
                Vector3 candidate = centre + new Vector3(point.x, 0f, point.y);
                candidate.y = spawnHeight;
                if (Vector3.Distance(candidate, GroundCentre()) > groundSize * 0.5f)
                    continue;
                if (player != null && minimumDistanceFromPlayer > 0f &&
                    Vector3.Distance(candidate, player.position) < minimumDistanceFromPlayer)
                    continue;
                return candidate;
            }
            Vector3 fallback = centre;
            fallback.y = spawnHeight;
            return fallback;
        }

        private Vector3 GroundSpawnPosition()
        {
            Vector3 around = player != null ? player.position : GroundCentre();
            around.y = 0f;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                float angle = Random.value * Mathf.PI * 2f;
                float radius = playerRingRadius * Mathf.Sqrt(Random.value);
                Vector3 candidate = around
                                    + new Vector3(Mathf.Cos(angle) * radius, 0f,
                                                  Mathf.Sin(angle) * radius);
                candidate.y = spawnHeight;
                if (minimumDistanceFromPlayer <= 0f) return candidate;
                if (Vector3.Distance(candidate, around) >= minimumDistanceFromPlayer)
                    return candidate;
            }
            Vector3 fallback = around;
            fallback.y = spawnHeight;
            return fallback;
        }

        private Vector3 GroundCentre()
        {
            return _ground != null ? _ground.position : Vector3.zero;
        }

        // ------------------------------------------------------------------
        // Frame loop
        // ------------------------------------------------------------------

        private void Update()
        {
            float deltaMs = Time.unscaledDeltaTime * 1000f;
            _smoothedMs = _smoothedMs <= 0f
                ? deltaMs
                : _smoothedMs + (deltaMs - _smoothedMs) * 0.1f;

            VerifyFirstChaser();
            HandleInput();
            WatchPlayerDrift();
            DetectSlowdown();
            LogCrowd();

            _frameCounter++;
            if (sampleEveryFrames > 0 && _frameCounter >= sampleEveryFrames)
            {
                _frameCounter = 0;
                SampleFrame(deltaMs);
            }
        }

        private void HandleInput()
        {
            if (!StressInput.Available) return;

            if (StressInput.GetKeyDown(keyClear)) { ClearAll(); return; }
            if (StressInput.GetKeyDown(keyPause))
            {
                Paused = !Paused;
                Debug.Log("[stress] spawning " + (Paused ? "paused" : "resumed")
                          + " at " + AliveCount + " cubes", this);
            }
            if (StressInput.GetKeyDown(keyWriteFiles))
            {
                WriteFiles();
                Debug.Log("[stress] report written at " + AliveCount + " cubes", this);
            }
            if (StressInput.GetKeyDown(keyResetPlayer)) ResetPlayer();
            if (Paused) return;

            if (StressInput.GetKeyDown(keySpawnSmall)) Spawn(batchSmall);
            if (StressInput.GetKeyDown(keySpawnLarge)) Spawn(batchLarge);
        }

        /// <summary>Puts the player back at the origin (the R key). Only the key
        /// handler calls this — nothing moves the player on its own.</summary>
        private void ResetPlayer()
        {
            if (player == null) return;
            CharacterController controller = player.GetComponent<CharacterController>();
            if (controller != null) controller.enabled = false;
            player.position = new Vector3(0f, playerHeight * 0.5f, 0f);
            if (controller != null) controller.enabled = true;
            Rigidbody body = player.GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic) body.velocity = Vector3.zero;
            SnapPlayerWatch();
            Debug.Log("[stress] player reset to the origin.", player);
        }

        /// <summary>
        /// Once, after the first cube has booted: say which object its `player` slot
        /// ended up bound to. That single line answers "why is the crowd going the
        /// wrong way" without guessing.
        /// </summary>
        private void VerifyFirstChaser()
        {
            if (_verifiedTarget || _spawned.Count == 0) return;
            ChaserAI ai = _spawned[0] != null ? _spawned[0].GetComponent<ChaserAI>() : null;
            if (ai == null) { _verifiedTarget = true; return; }
            if (!ai.Booted)
            {
                if (++_verifyTries > 600) _verifiedTarget = true;
                return;
            }
            _verifiedTarget = true;

            if (ai.BoundTarget == null)
            {
                Debug.LogError("[stress] cube #1 booted without a target. It will stand "
                               + "still; see the [chaser] error above.", _spawned[0]);
                return;
            }
            if (player != null && ai.BoundTarget != player)
            {
                Debug.LogError("[stress] cube #1 chases '" + ai.BoundTarget.name
                               + "', not the player '" + player.name + "'. Bind the right "
                               + "object (ai.Target) before the cube boots.", _spawned[0]);
                return;
            }
            float distance = player != null
                ? Vector3.Distance(_spawned[0].transform.position, player.position)
                : 0f;
            Debug.Log("[stress] cube #1 chases '" + ai.BoundTarget.name + "' ("
                      + distance.ToString("F1") + " m away), slot 'player' bound and booted "
                      + "as instance " + ai.InstanceId + ".", _spawned[0]);
        }

        /// <summary>
        /// One line every few seconds saying where the crowd is relative to the
        /// player. Small and stable = the chase works; growing while you walk = the
        /// crowd is not following, and the [chaser]/[stress] lines above say why.
        /// </summary>
        private void LogCrowd()
        {
            if (logCrowdEverySeconds <= 0f || player == null || _spawned.Count == 0) return;
            if (Time.unscaledTime < _nextCrowdLog) return;
            _nextCrowdLog = Time.unscaledTime + logCrowdEverySeconds;

            Vector3 playerPosition = player.position;
            float sum = 0f;
            float nearest = float.MaxValue;
            int counted = 0;
            for (int i = 0; i < _spawned.Count; i++)
            {
                GameObject cube = _spawned[i];
                if (cube == null) continue;
                float distance = Vector3.Distance(cube.transform.position, playerPosition);
                sum += distance;
                if (distance < nearest) nearest = distance;
                counted++;
            }
            if (counted == 0) return;

            Debug.Log("[stress] crowd: " + counted + " cubes, mean "
                      + (sum / counted).ToString("F1") + " m from the player, nearest "
                      + nearest.ToString("F1") + " m"
                      + (_wasd != null && _wasd.Moving ? " (you are walking)" : ""), this);
        }

        /// <summary>
        /// The player moves only while a movement key is held, so if it moves
        /// sideways with NO key held, that is said out loud as an error with the
        /// distance - never left for the player to notice by eye. The distance is
        /// measured from where the keys left the player and ACCUMULATES, so a slow
        /// pull is as visible as a fast one (a per-frame test would miss a slow one).
        /// Vertical motion is ignored (gravity moves the player down legitimately), and
        /// the frame a key is released is skipped so a last step is not misread.
        /// </summary>
        private void WatchPlayerDrift()
        {
            if (player == null) return;
            Vector3 now = player.position;
            if (!_havePlayerPosition)
            {
                _havePlayerPosition = true;
                _lastPlayerPosition = now;
                _driftAnchor = now;
                return;
            }
            Vector3 moved = now - _lastPlayerPosition;
            _lastPlayerPosition = now;

            bool walking = _wasd != null && _wasd.Moving;
            bool wasWalking = _wasdMovingLastFrame;
            _wasdMovingLastFrame = walking;
            if (walking || wasWalking)   // the keys are (or were, this frame) moving him
            {
                _driftAnchor = now;
                _driftSinceAnchor = 0f;
                return;
            }
            if (driftWarnDistance <= 0f) return;

            // The distance from the place the keys left the player, not one frame's
            // twitch: a pull of a few millimetres per frame is still a pull, and it
            // adds up here instead of slipping under a per-frame threshold.
            float twitch = new Vector2(moved.x, moved.z).magnitude;
            if (twitch > 0.0005f)
                _driftSinceAnchor = new Vector2(now.x - _driftAnchor.x,
                                                now.z - _driftAnchor.z).magnitude;
            if (_driftSinceAnchor <= driftWarnDistance) return;

            float sideways = _driftSinceAnchor;
            _driftAnchor = now;          // the next report covers the next chunk
            _driftSinceAnchor = 0f;
            _driftEvents++;
            _driftTotal += sideways;
            if (Time.unscaledTime < _nextDriftLog) return;
            _nextDriftLog = Time.unscaledTime + 1f;
            Debug.LogError("[stress] THE PLAYER MOVED " + sideways.ToString("F3")
                           + " m from where the keys left it, with NO movement key held ("
                           + _driftEvents + " time(s) so far, "
                           + _driftTotal.ToString("F2") + " m total). "
                           + (_wasd != null
                              ? "PlayerController moves this object only while a key is "
                                + "held, so something else is driving it."
                              : "This object has no PlayerController at all.")
                           + " The complete list of scripts on the player is in the setup "
                           + "lines above.", player);
        }

        /// <summary>
        /// Re-baseline the drift watch after the test itself teleports the player
        /// (the R key), so a deliberate reset is not reported as drift.
        /// </summary>
        private void SnapPlayerWatch()
        {
            if (player == null) return;
            _lastPlayerPosition = player.position;
            _driftAnchor = _lastPlayerPosition;
            _driftSinceAnchor = 0f;
            _havePlayerPosition = true;
        }

        /// <summary>
        /// Every script on the player object, by name; `foreign` counts the ones that
        /// are not PlayerController. A second movement script on the player is the
        /// classic way for "the player gets dragged somewhere" to happen while the AI
        /// side is perfectly fine, so this list is printed at startup and anything
        /// extra is an error.
        /// </summary>
        private string PlayerLoadout(out int foreign)
        {
            foreign = 0;
            StringBuilder text = new StringBuilder();
            MonoBehaviour[] scripts = player.GetComponents<MonoBehaviour>();
            for (int i = 0; i < scripts.Length; i++)
            {
                MonoBehaviour script = scripts[i];
                if (script == null) continue;
                if (text.Length > 0) text.Append(", ");
                if (script is PlayerController)
                {
                    text.Append(script.GetType().Name);
                }
                else
                {
                    text.Append(script.GetType().Name).Append(" (NOT PlayerController)");
                    foreign++;
                }
            }
            return text.Length == 0 ? "none at all - nothing here reads WASD" : text.ToString();
        }

        private void DetectSlowdown()
        {
            if (!stopWhenSlow || Paused || AliveCount < minimumCountForStop) return;
            if (_smoothedMs <= slowdownFrameMs) { _slowSeconds = 0f; return; }
            _slowSeconds += Time.unscaledDeltaTime;
            if (_slowSeconds >= slowdownHoldSeconds)
            {
                Paused = true;
                PracticalMaximum = AliveCount;
                Debug.LogWarning("[stress] paused spawning: smoothed frame time stayed above "
                                 + slowdownFrameMs.ToString("F1") + " ms for "
                                 + slowdownHoldSeconds.ToString("F1") + " s at "
                                 + AliveCount + " cubes. The practical maximum is about "
                                 + AliveCount + ".", this);
            }
        }

        private void SampleFrame(float deltaMs)
        {
            FrameSample sample = new FrameSample
            {
                frame = Time.frameCount,
                time = Time.unscaledTime,
                deltaMs = deltaMs,
                smoothedMs = _smoothedMs,
                alive = AliveCount,
                playerPosition = player != null ? player.position : Vector3.zero,
                playerMoving = _wasd != null && _wasd.Moving
            };
            // Counting every AI's calls is O(N): refresh it every 5th sample and
            // report the last known number in between (never a fake zero).
            _sampleCounter++;
            if (_sampleCounter >= 5)
            {
                _sampleCounter = 0;
                _lastCalls = TotalCalls();
                _lastRegistered = RegisteredCount();
            }
            sample.totalCalls = _lastCalls;
            sample.registered = _lastRegistered;

            if (player != null && _spawned.Count > 0)
            {
                Vector3 playerPosition = player.position;
                float sum = 0f;
                float nearest = float.MaxValue;
                int counted = 0;
                for (int i = 0; i < _spawned.Count; i++)
                {
                    GameObject cube = _spawned[i];
                    if (cube == null) continue;
                    float distance = Vector3.Distance(cube.transform.position, playerPosition);
                    sum += distance;
                    if (distance < nearest) nearest = distance;
                    counted++;
                }
                if (counted > 0)
                {
                    sample.crowdMeanToPlayer = sum / counted;
                    sample.crowdNearestToPlayer = nearest;
                }
            }
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

        /// <summary>Removes every spawned cube (the Backspace key).</summary>
        public void ClearAll()
        {
            int removed = _spawned.Count;
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null) Destroy(_spawned[i]);
            }
            _spawned.Clear();
            Debug.Log("[stress] cleared " + removed + " cubes.", this);
        }

        // ------------------------------------------------------------------
        // Files
        // ------------------------------------------------------------------

        private string SpawnCsvPath { get { return Path.Combine(Application.persistentDataPath, spawnFileName); } }
        private string FrameCsvPath { get { return Path.Combine(Application.persistentDataPath, frameFileName); } }
        private string SummaryPath { get { return Path.Combine(Application.persistentDataPath, summaryFileName); } }

        /// <summary>Writes the three files once. Safe to call from anywhere.</summary>
        public void WriteFiles()
        {
            if (!record || _wroteFiles) return;
            _wroteFiles = true;

            StringBuilder spawns = new StringBuilder();
            spawns.Append("index,frame,instantiateMs,spawnFrameMs,aliveAfter,instanceId\n");
            for (int i = 0; i < Spawns.Count; i++)
            {
                SpawnRecord recordRow = Spawns[i];
                int id = recordRow.instanceId;
                if (id == 0 && recordRow.index - 1 < _spawned.Count &&
                    _spawned[recordRow.index - 1] != null)
                {
                    ChaserAI ai = _spawned[recordRow.index - 1].GetComponent<ChaserAI>();
                    if (ai != null) { id = ai.InstanceId; recordRow.instanceId = id; }
                }
                spawns.Append(recordRow.index).Append(',')
                      .Append(recordRow.frame).Append(',')
                      .Append(recordRow.instantiateMs.ToString("F4")).Append(',')
                      .Append(recordRow.spawnFrameMs.ToString("F2")).Append(',')
                      .Append(recordRow.aliveAfter).Append(',')
                      .Append(id).Append('\n');
            }
            File.WriteAllText(SpawnCsvPath, spawns.ToString());

            StringBuilder frames = new StringBuilder();
            frames.Append("frame,time,deltaMs,smoothedMs,fps,alive,registered,totalCalls,"
                          + "crowdMeanToPlayer,crowdNearestToPlayer,"
                          + "playerX,playerY,playerZ,playerMoving\n");
            for (int i = 0; i < Frames.Count; i++)
            {
                FrameSample sample = Frames[i];
                frames.Append(sample.frame).Append(',')
                      .Append(sample.time.ToString("F3")).Append(',')
                      .Append(sample.deltaMs.ToString("F2")).Append(',')
                      .Append(sample.smoothedMs.ToString("F2")).Append(',')
                      .Append((1000f / Mathf.Max(0.0001f, sample.smoothedMs)).ToString("F1"))
                      .Append(',')
                      .Append(sample.alive).Append(',')
                      .Append(sample.registered).Append(',')
                      .Append(sample.totalCalls).Append(',')
                      .Append(sample.crowdMeanToPlayer.ToString("F2")).Append(',')
                      .Append(sample.crowdNearestToPlayer.ToString("F2")).Append(',')
                      .Append(sample.playerPosition.x.ToString("F2")).Append(',')
                      .Append(sample.playerPosition.y.ToString("F2")).Append(',')
                      .Append(sample.playerPosition.z.ToString("F2")).Append(',')
                      .Append(sample.playerMoving ? 1 : 0).Append('\n');
            }
            File.WriteAllText(FrameCsvPath, frames.ToString());

            StringBuilder summary = new StringBuilder();
            summary.Append("myFSM spawn stress test\n");
            summary.Append("player            : ")
                   .Append(player != null ? player.name + " (" + DescribeBody(player) + ")" : "NONE")
                   .Append('\n');
            summary.Append("player drift      : ")
                   .Append(_driftEvents == 0
                           ? "none - the player only moved while a movement key was held\n"
                           : _driftEvents + " move(s) with NO key held, "
                             + _driftTotal.ToString("F2") + " m total (with no "
                             + "PlayerController-drivable explanation; see the console "
                             + "errors)\n");
            summary.Append("spawned total     : ").Append(SpawnedTotal).Append('\n');
            summary.Append("alive at stop     : ").Append(AliveCount).Append('\n');
            summary.Append("practical maximum : ")
                   .Append(PracticalMaximum < 0 ? "not reached" : PracticalMaximum.ToString())
                   .Append('\n');
            summary.Append("ground plane      : ").Append(groundSize).Append(" x ")
                   .Append(groundSize).Append(" m\n");
            summary.Append("cube gravity      : ")
                   .Append(cubeGravity ? "ON (the physics step is part of these numbers)" : "off")
                   .Append('\n');
            summary.Append("spawn placement   : ").Append(placement)
                   .Append(placement == SpawnPlacement.SpreadOverPlane
                           ? " (drop points spread over the plane)"
                           : placement == SpawnPlacement.CentreInAir
                             ? " (above the centre, at " + spawnHeight.ToString("F0") + " m)"
                             : " (ring around the player)")
                   .Append('\n');
            summary.Append("input backend     : ").Append(StressInput.Backend).Append('\n');
            summary.Append("final smoothed ms : ").Append(_smoothedMs.ToString("F2")).Append('\n');
            summary.Append("final fps         : ")
                   .Append((1000f / Mathf.Max(0.0001f, _smoothedMs)).ToString("F1")).Append('\n');
            summary.Append("crowd to player   : ");
            if (Frames.Count > 0)
            {
                FrameSample last = Frames[Frames.Count - 1];
                summary.Append("mean ").Append(last.crowdMeanToPlayer.ToString("F2"))
                       .Append(" m, nearest ").Append(last.crowdNearestToPlayer.ToString("F2"))
                       .Append(" m (a small mean means the crowd is on the player; a mean "
                               + "that grows as the player walks means the crowd is not "
                               + "following)\n");
            }
            else
            {
                summary.Append("no frames sampled\n");
            }
            summary.Append("registered AIs    : ")
                   .Append(_lastRegistered > 0 ? _lastRegistered : RegisteredCount()).Append('\n');
            summary.Append("total FSM calls   : ")
                   .Append(_lastCalls > 0 ? _lastCalls : TotalCalls()).Append('\n');
            File.WriteAllText(SummaryPath, summary.ToString());

            Debug.Log("[stress] wrote " + SpawnCsvPath + ", " + FrameCsvPath + " and "
                      + SummaryPath, this);
        }

        private void OnApplicationQuit() { WriteFiles(); }
        private void OnDestroy() { WriteFiles(); }
    }
}
