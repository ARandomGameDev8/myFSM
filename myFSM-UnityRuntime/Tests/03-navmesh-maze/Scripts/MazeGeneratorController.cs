// Test 03 — builds a random, collidable maze on a ground plane, puts an entry
// and an exit in it, bakes a NavMesh over the result, and creates the chase
// target the AI runs at.
//
// Maze shape (the three knobs):
//
//   pathsFromStartToFinish  how many independent routes entry -> exit. 1 = a
//                           perfect maze (exactly one simple path, no loops).
//                           N > 1 opens N-1 extra loops by removing walls
//                           ("braiding"), each of which adds at least one more
//                           way through — so a solver has real choices.
//   corridorStraightness    how often the carver keeps going straight instead
//                           of turning: 0 = max windiness (short jittery
//                           corridors, the classic tight hedge maze), 1 = long
//                           straight galleries. This is the average tightness.
//   tightnessVariation      how NON-uniform the tightness is across the maze:
//                           0 = the same straightness everywhere, higher values
//                           blend tight regions into loose ones via a coarse
//                           seeded noise field, so one maze contains both.
//
// The base maze is an iterative depth-first search over the cell grid: every
// cell is visited exactly once and each carved passage joins two cells that
// were not connected before. That is what guarantees "at least one path
// exists" — the carved passages form a spanning tree, so entry and exit are
// always connected. Braiding then only removes walls (adds edges), which can
// never disconnect the maze. Build() proves connectivity at runtime with a
// breadth-first search and logs the corridor length it found, which is the
// independent number the recorder compares the run against.
//
// Geometry is clamped to the ground: every wall starts at the ground plane's
// own height (GroundY) and is built from there, so raising/lowering the plane
// moves the maze with it and nothing floats or sinks. The NavMesh bake
// collects exactly those colliders (plus the ground), so the mesh follows the
// ground plane too.
//
// Target: when `target` is empty a static marker is created standing on the
// exit area at run time and used as the chase target instead. An assigned
// target (e.g. test 04's MazeTargetWalker) is used as-is — it may move.
//
// NavMesh: the runtime bake uses UnityEngine.AI.NavMeshBuilder (CollectSources
// + BuildNavMeshData + NavMesh.AddNavMeshData) straight from the built-in
// UnityEngine.AIModule — the same API the AI Navigation package wraps in its
// NavMeshSurface component, and no extra package needed. Set buildNavMesh =
// false to skip it and rely on a NavMesh baked in the editor instead (press
// Bake in the Navigation window after building the maze in edit mode).
//
//   G  rebuild a new random maze (new seed, new NavMesh) at runtime
//   B  re-bake the NavMesh over the current layout

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using MyFSM.Unity;

namespace MyFSM.Tests
{
    public class MazeGeneratorController : MonoBehaviour
    {
        [Header("Grid")]
        [Tooltip("Cells across (X).")]
        public int cellsX = 9;
        [Tooltip("Cells along (Z).")]
        public int cellsY = 9;
        public float cellSize = 2f;
        public float wallHeight = 2.5f;
        public float wallThickness = 0.35f;
        [Tooltip("0 = pick a random seed each build, otherwise reproduce the same maze.")]
        public int seed = 12345;

        [Header("Maze shape")]
        [Tooltip("Independent routes entry -> exit. 1 = perfect maze (no loops); " +
                 "N opens N-1 extra loops by removing walls (braiding).")]
        [Min(1)] public int pathsFromStartToFinish = 1;
        [Range(0f, 1f)]
        [Tooltip("How often the carver continues straight instead of turning. " +
                 "0 = max windiness (tight maze), 1 = long straight corridors.")]
        public float corridorStraightness = 0.35f;
        [Range(0f, 1f)]
        [Tooltip("How non-uniform the tightness is: 0 = same straightness everywhere, " +
                 "higher blends tight and loose regions (seeded coarse noise).")]
        public float tightnessVariation = 0.5f;

        [Header("Ground")]
        [Tooltip("Ground plane. One is created when this is empty.")]
        public Transform groundPlane;
        public string groundName = "MazeGround";
        public float groundMargin = 6f;
        public float groundThickness = 0.2f;

        [Header("Actors")]
        [Tooltip("The runner, moved to the entry cell so it starts the maze at the entry.")]
        public TestAI runner;
        [Tooltip("The chase target. Empty: a static marker is created on the exit area at run time.")]
        public Transform target;
        [Tooltip("The walking target of test 04, also moved to the entry cell.")]
        public Transform targetWalker;

        [Header("NavMesh")]
        public bool buildNavMesh = true;
        public int agentTypeId = 0;
        public float agentRadius = 0.4f;
        public float agentHeight = 2f;
        public float agentSlope = 45f;
        public float agentClimb = 0.4f;
        [Tooltip("Unity layers the baked NavMesh collects. Default: everything.")]
        public LayerMask collectionMask = ~0;

        [Header("Keys")]
        public KeyCode rebuildKey = KeyCode.G;
        public KeyCode rebakeKey = KeyCode.B;

        // ------------------------------------------------------------------
        // Results
        // ------------------------------------------------------------------

        /// <summary>The cell the runner starts in.</summary>
        public Transform Entry { get; private set; }
        /// <summary>The cell the runner must reach.</summary>
        public Transform Exit { get; private set; }
        /// <summary>World height of the ground surface the maze sits on.</summary>
        public float GroundY { get; private set; }
        /// <summary>Cells on the shortest corridor route entry -> exit (BFS).</summary>
        public int PathCells { get; private set; }
        /// <summary>Shortest-path length of the PERFECT maze before braiding (diagnostics).</summary>
        public int PerfectPathCells { get; private set; }
        /// <summary>Loops actually opened by braiding (= paths-1, clamped to available walls).</summary>
        public int LoopsOpened { get; private set; }
        /// <summary>Lower bound of the shortest route's length in metres: (PathCells-1) * cellSize.</summary>
        public float PathLengthLowerBound { get { return Mathf.Max(0, PathCells - 1) * cellSize; } }
        /// <summary>Straight-line distance entry -> exit (what a maze makes you beat).</summary>
        public float StraightDistance { get; private set; }
        /// <summary>The NavMesh instance created for the current layout.</summary>
        public NavMeshDataInstance NavMeshInstance { get; private set; }
        /// <summary>The baked NavMesh data (null until BakeNavMesh succeeds).
        /// Not named NavMesh: that would shadow UnityEngine.AI.NavMesh here.</summary>
        public NavMeshData BakedNavMesh { get { return _navMeshData; } }
        /// <summary>Used when no runner/target was assigned: the last spawn point.</summary>
        public Vector3 EntryPosition { get; private set; }
        public Vector3 ExitPosition { get; private set; }
        /// <summary>The object the AI should run at: the assigned target, or the
        /// static marker this component created on the exit area.</summary>
        public Transform ActiveTarget
        {
            get { return target != null ? target : DefaultTarget; }
        }
        /// <summary>The static target created when no target was assigned (null until then).</summary>
        public Transform DefaultTarget { get; private set; }

        /// <summary>The maze currently in the scene (the manual bindings read this).</summary>
        public static MazeGeneratorController Current { get; private set; }

        private const byte WallNorth = 1;
        private const byte WallEast = 2;
        private const byte WallSouth = 4;
        private const byte WallWest = 8;

        private readonly List<GameObject> _walls = new List<GameObject>();
        private byte[] _wallsByCell;
        private Transform _container;
        private NavMeshData _navMeshData;
        private bool _navMeshAdded;

        // ------------------------------------------------------------------

        private void Awake()
        {
            Current = this;
            Build(seed);
        }

        private void OnDestroy()
        {
            if (Current == this) Current = null;
            if (_navMeshAdded) NavMeshInstance.Remove();
        }

        private void Update()
        {
            // MazeInput: works with either Unity input backend (see MazeInput.cs).
            if (MazeInput.GetKeyDown(rebuildKey)) Build(0);
            if (MazeInput.GetKeyDown(rebakeKey)) BakeNavMesh();
        }

        /// <summary>Builds a maze (seed 0 = random), its markers, target, and NavMesh.</summary>
        public void Build(int withSeed)
        {
            if (withSeed == 0) withSeed = Random.Range(1, int.MaxValue);
            seed = withSeed;
            Random.InitState(withSeed);

            ClearMaze();
            EnsureGround();

            int count = cellsX * cellsY;
            _wallsByCell = new byte[count];
            for (int i = 0; i < count; i++) _wallsByCell[i] = WallNorth | WallEast | WallSouth | WallWest;

            CarvePerfectMaze();
            OpenEntryAndExit();
            PerfectPathCells = CountPathCells();
            Braid(Mathf.Max(0, pathsFromStartToFinish - 1));
            CreateWallGeometry();
            CreateMarkers();
            CreateTargetIfNeeded();
            PlaceActors();

            PathCells = CountPathCells();
            StraightDistance = Vector3.Distance(EntryPosition, ExitPosition);

            Debug.Log("[maze] seed " + withSeed + ", " + cellsX + "x" + cellsY + " cells of "
                      + cellSize + " m | straightness " + corridorStraightness.ToString("F2")
                      + " ± " + tightnessVariation.ToString("F2")
                      + " | braided loops " + LoopsOpened
                      + " (shortest route " + PerfectPathCells + " -> " + PathCells + " cells)"
                      + ": entry " + EntryPosition + " -> exit " + ExitPosition
                      + " | straight line " + StraightDistance.ToString("F1") + " m"
                      + " | shortest corridor route at least "
                      + PathLengthLowerBound.ToString("F1") + " m"
                      + " | target " + (target != null ? target.name : "static (created)"), this);

            if (buildNavMesh) BakeNavMesh();
        }

        // ------------------------------------------------------------------
        // Maze
        // ------------------------------------------------------------------

        // Coarse seeded noise for the non-uniform tightness: one random value per
        // lattice point every `noiseScale` cells, bilinearly interpolated. All of
        // it comes from UnityEngine.Random, so the seed reproduces the maze.
        private const int NoiseScale = 4; // cells between lattice points

        private float NoiseAt(int x, int y)
        {
            float fx = x / (float)NoiseScale;
            float fy = y / (float)NoiseScale;
            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0;
            float ty = fy - y0;
            // Smoothstep so regions blend rather than crease.
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            float v00 = LatticeValue(x0, y0);
            float v10 = LatticeValue(x0 + 1, y0);
            float v01 = LatticeValue(x0, y0 + 1);
            float v11 = LatticeValue(x0 + 1, y0 + 1);
            return Mathf.Lerp(Mathf.Lerp(v00, v10, tx), Mathf.Lerp(v01, v11, tx), ty);
        }

        // Lattice hash: a tiny deterministic integer scramble (xorshift-ish) so
        // the same seed always produces the same tightness regions.
        private static float LatticeValue(int x, int y)
        {
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            h = h ^ (h >> 16);
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }

        /// <summary>
        /// Local straightness for a cell: the average knob shifted by the noise
        /// field, scaled by the variation. variation 0 gives a uniform maze;
        /// higher values make some regions tight and others loose.
        /// </summary>
        private float StraightnessAt(int x, int y)
        {
            float noise = NoiseAt(x, y); // 0..1
            float local = corridorStraightness + (noise - 0.5f) * 2f * tightnessVariation;
            return Mathf.Clamp01(local);
        }

        private void CarvePerfectMaze()
        {
            int count = cellsX * cellsY;
            bool[] visited = new bool[count];
            int[] stack = new int[count];
            int stackSize = 0;

            int current = 0; // entry corner (0, 0)
            visited[current] = true;
            stack[stackSize++] = current;
            int lastDirection = -1;

            // Iterative DFS: the classic "recursive backtracker". The
            // straightness knob biases the direction order: when the roll
            // passes, the previous direction is tried first, which keeps the
            // corridor going instead of turning — long straight runs on loose
            // mazes, frequent turns on tight ones.
            while (stackSize > 0)
            {
                current = stack[stackSize - 1];
                int cx = current % cellsX;
                int cy = current / cellsX;

                int order = Random.Range(0, 4);
                bool preferLast = lastDirection >= 0 && Random.value < StraightnessAt(cx, cy);
                int next = -1;
                byte sharedWall = 0;
                int chosenDirection = -1;

                for (int attempt = 0; attempt < 4 && next < 0; attempt++)
                {
                    // Order of directions: the biased favourite first, then the
                    // shuffled remainder.
                    int direction;
                    if (preferLast && attempt == 0)
                    {
                        direction = lastDirection;
                    }
                    else
                    {
                        direction = (order + (preferLast ? attempt - 1 : attempt)) % 4;
                        if (direction == lastDirection && preferLast) continue;
                    }

                    int nx = cx;
                    int ny = cy;
                    if (direction == 0) ny += 1;        // north
                    else if (direction == 1) nx += 1;   // east
                    else if (direction == 2) ny -= 1;   // south
                    else nx -= 1;                       // west

                    if (nx < 0 || ny < 0 || nx >= cellsX || ny >= cellsY) continue;
                    int candidate = ny * cellsX + nx;
                    if (visited[candidate]) continue;

                    next = candidate;
                    chosenDirection = direction;
                    sharedWall = direction == 0 ? WallNorth
                               : direction == 1 ? WallEast
                               : direction == 2 ? WallSouth
                               : WallWest;
                }

                if (next < 0)
                {
                    stackSize--; // dead end: back up
                    lastDirection = -1;
                    continue;
                }

                RemoveWall(current, sharedWall);
                visited[next] = true;
                stack[stackSize++] = next;
                lastDirection = chosenDirection;
            }
        }

        private void RemoveWall(int cell, byte wall)
        {
            _wallsByCell[cell] = (byte)(_wallsByCell[cell] & ~wall);
            int x = cell % cellsX;
            int y = cell / cellsX;
            int nx = x;
            int ny = y;
            byte opposite = 0;
            if (wall == WallNorth) { ny += 1; opposite = WallSouth; }
            else if (wall == WallEast) { nx += 1; opposite = WallWest; }
            else if (wall == WallSouth) { ny -= 1; opposite = WallNorth; }
            else { nx -= 1; opposite = WallEast; }
            if (nx < 0 || ny < 0 || nx >= cellsX || ny >= cellsY) return;
            int neighbour = ny * cellsX + nx;
            _wallsByCell[neighbour] = (byte)(_wallsByCell[neighbour] & ~opposite);
        }

        private void OpenEntryAndExit()
        {
            // Punch a door in the outside wall of the entry and exit cells so a
            // camera — and an AI — can tell where the run starts and ends.
            _wallsByCell[0] = (byte)(_wallsByCell[0] & ~WallWest);
            int exitCell = cellsY * cellsX - 1;
            _wallsByCell[exitCell] = (byte)(_wallsByCell[exitCell] & ~WallEast);
        }

        /// <summary>
        /// Braiding: removes up to `loops` interior walls at random. Every removed
        /// wall adds an edge to the spanning tree, which creates a cycle — and a
        /// cycle through the entry/exit region is another way from start to
        /// finish. Removing walls can never disconnect the maze, so the "at least
        /// one path" guarantee survives.
        /// </summary>
        private void Braid(int loops)
        {
            LoopsOpened = 0;
            if (loops <= 0) return;

            List<int> cells = new List<int>();
            List<byte> walls = new List<byte>();
            for (int cell = 0; cell < cellsX * cellsY; cell++)
            {
                int x = cell % cellsX;
                int y = cell / cellsX;
                // Interior walls only (entry/exit doors are already open, and the
                // grid border stays closed except for those doors).
                if ((_wallsByCell[cell] & WallEast) != 0 && x + 1 < cellsX) { cells.Add(cell); walls.Add(WallEast); }
                if ((_wallsByCell[cell] & WallNorth) != 0 && y + 1 < cellsY) { cells.Add(cell); walls.Add(WallNorth); }
            }

            // Fisher-Yates with the seeded generator: reproducible braiding.
            for (int i = cells.Count - 1; i > 0 && LoopsOpened < loops; i--)
            {
                int j = Random.Range(0, i + 1);
                int c = cells[j];
                byte w = walls[j];
                cells[j] = cells[i];
                walls[j] = walls[i];
                RemoveWall(c, w);
                LoopsOpened++;
            }
        }

        /// <summary>BFS over the carved passages: cells on the entry -> exit route.</summary>
        private int CountPathCells()
        {
            int count = cellsX * cellsY;
            int[] distance = new int[count];
            for (int i = 0; i < count; i++) distance[i] = -1;

            int[] queue = new int[count];
            int head = 0;
            int tail = 0;
            distance[0] = 0;
            queue[tail++] = 0;

            int exitCell = count - 1;
            while (head < tail)
            {
                int cell = queue[head++];
                if (cell == exitCell) break;

                int x = cell % cellsX;
                int y = cell / cellsX;
                byte walls = _wallsByCell[cell];

                if ((walls & WallNorth) == 0 && y + 1 < cellsY)
                    Visit(distance, queue, ref tail, cell, cell + cellsX);
                if ((walls & WallEast) == 0 && x + 1 < cellsX)
                    Visit(distance, queue, ref tail, cell, cell + 1);
                if ((walls & WallSouth) == 0 && y - 1 >= 0)
                    Visit(distance, queue, ref tail, cell, cell - cellsX);
                if ((walls & WallWest) == 0 && x - 1 >= 0)
                    Visit(distance, queue, ref tail, cell, cell - 1);
            }

            return distance[exitCell] < 0 ? -1 : distance[exitCell] + 1;
        }

        private static void Visit(int[] distance, int[] queue, ref int tail, int from, int to)
        {
            if (distance[to] >= 0) return;
            distance[to] = distance[from] + 1;
            queue[tail++] = to;
        }

        // ------------------------------------------------------------------
        // Geometry
        // ------------------------------------------------------------------

        private void ClearMaze()
        {
            for (int i = 0; i < _walls.Count; i++)
                if (_walls[i] != null) Destroy(_walls[i]);
            _walls.Clear();

            if (_container != null) Destroy(_container.gameObject);
            if (Entry != null) Destroy(Entry.gameObject);
            if (Exit != null) Destroy(Exit.gameObject);
            Entry = null;
            Exit = null;

            if (_navMeshAdded) { NavMeshInstance.Remove(); _navMeshAdded = false; }
        }

        private void EnsureGround()
        {
            if (groundPlane == null)
            {
                GameObject found = GameObject.Find(groundName);
                if (found == null)
                {
                    found = GameObject.CreatePrimitive(PrimitiveType.Plane);
                    found.name = groundName;
                    found.transform.position = Vector3.zero;
                    // Unity's plane primitive is 10x10 units: scale to the maze
                    // plus a margin, so the walls never hang over the edge.
                    found.transform.localScale = new Vector3(
                        (cellsX * cellSize + groundMargin) / 10f, 1f,
                        (cellsY * cellSize + groundMargin) / 10f);
                }
                groundPlane = found.transform;
            }
            GroundY = groundPlane.position.y; // the plane's surface is its origin
        }

        private void CreateWallGeometry()
        {
            GameObject container = new GameObject("Maze (generated)");
            container.transform.SetParent(transform, false);
            _container = container.transform;

            for (int y = 0; y < cellsY; y++)
            {
                for (int x = 0; x < cellsX; x++)
                {
                    int cell = y * cellsX + x;
                    byte walls = _wallsByCell[cell];
                    Vector3 centre = CellCentre(x, y);

                    if ((walls & WallNorth) != 0)
                        CreateWall(container.transform, centre + new Vector3(0f, 0f, cellSize * 0.5f), true);
                    if ((walls & WallWest) != 0)
                        CreateWall(container.transform, centre + new Vector3(-cellSize * 0.5f, 0f, 0f), false);
                    // Close the remaining two edges of the grid: the south wall
                    // of the near row (y == 0) and the east wall of the far column
                    // (x == cellsX-1). North and west walls are already drawn by
                    // the two tests above, for every row/column.
                    if (y == 0 && (walls & WallSouth) != 0)
                        CreateWall(container.transform, centre + new Vector3(0f, 0f, -cellSize * 0.5f), true);
                    if (x == cellsX - 1 && (walls & WallEast) != 0)
                        CreateWall(container.transform, centre + new Vector3(cellSize * 0.5f, 0f, 0f), false);
                }
            }
        }

        private void CreateWall(Transform parent, Vector3 centre, bool alongX)
        {
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Wall";
            wall.transform.SetParent(parent, false);
            wall.transform.position = new Vector3(centre.x, GroundY + wallHeight * 0.5f, centre.z);
            wall.transform.localScale = alongX
                ? new Vector3(cellSize + wallThickness, wallHeight, wallThickness)
                : new Vector3(wallThickness, wallHeight, cellSize + wallThickness);
            _walls.Add(wall);
        }

        private void CreateMarkers()
        {
            Entry = CreateMarker("Entry", CellCentre(0, 0), new Color(0.2f, 0.9f, 0.3f));
            Exit = CreateMarker("Exit", CellCentre(cellsX - 1, cellsY - 1), new Color(1f, 0.3f, 0.2f));
            EntryPosition = Entry.position;
            ExitPosition = Exit.position;
        }

        private Transform CreateMarker(string markerName, Vector3 centre, Color colour)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = markerName;
            marker.transform.SetParent(transform, false);
            marker.transform.position = new Vector3(centre.x, GroundY + 0.25f, centre.z);
            marker.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);

            // Markers must not be obstacles for the baked NavMesh.
            Collider collider = marker.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            Renderer renderer = marker.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = colour;
            return marker.transform;
        }

        /// <summary>
        /// The chase target. An assigned `target` is used as-is (it may move);
        /// with none, a static marker is created standing on the exit area —
        /// collider-free, so it never blocks the bake or the agent.
        /// </summary>
        private void CreateTargetIfNeeded()
        {
            if (target != null)
            {
                if (DefaultTarget != null) Destroy(DefaultTarget.gameObject); // an assigned target replaces it
                DefaultTarget = null;
                return;
            }
            // No assigned target: keep/repair the static exit marker so a rebuilt
            // maze (new exit position) still has its chase target on top of it.
            if (DefaultTarget != null)
            {
                DefaultTarget.position = ExitPosition + new Vector3(0f, 0.6f, 0f);
                return;
            }

            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "ChaseTarget (static)";
            marker.transform.SetParent(transform, false);
            marker.transform.position = ExitPosition + new Vector3(0f, 0.6f, 0f);
            marker.transform.localScale = new Vector3(0.6f, 0.6f, 0.6f);

            Collider collider = marker.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            Renderer renderer = marker.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = new Color(0.2f, 0.8f, 1f);

            DefaultTarget = marker.transform;
        }

        private void PlaceActors()
        {
            Vector3 spawn = CellCentre(0, 0) + new Vector3(0f, 0.5f, 0f);
            // No binding happens here on purpose: the runner's own Start() reads
            // MazeGeneratorController.Current.ActiveTarget (this Awake runs before
            // it), so this script never touches fields that only exist in a
            // hand-written partial binding file.
            if (runner != null) runner.transform.position = spawn;
            if (targetWalker != null) targetWalker.position = spawn;
        }

        /// <summary>Centre of cell (x, y) on the ground.</summary>
        public Vector3 CellCentre(int x, int y)
        {
            return new Vector3(transform.position.x + x * cellSize,
                               GroundY,
                               transform.position.z + y * cellSize);
        }

        // ------------------------------------------------------------------
        // NavMesh
        // ------------------------------------------------------------------

        /// <summary>
        /// Bakes a NavMesh over the maze at runtime (built-in UnityEngine.AI:
        /// collect the colliders in the maze bounds, build the data, add it).
        /// </summary>
        public void BakeNavMesh()
        {
            if (_navMeshAdded) { NavMeshInstance.Remove(); _navMeshAdded = false; }

            float width = cellsX * cellSize + groundMargin;
            float depth = cellsY * cellSize + groundMargin;
            Vector3 centre = new Vector3(
                transform.position.x + (cellsX - 1) * cellSize * 0.5f,
                GroundY,
                transform.position.z + (cellsY - 1) * cellSize * 0.5f);
            Bounds bounds = new Bounds(centre,
                new Vector3(width, wallHeight * 2f, depth));

            NavMeshBuildSettings settings = NavMesh.GetSettingsByID(agentTypeId);
            settings.agentRadius = agentRadius;
            settings.agentHeight = agentHeight;
            settings.agentSlope = agentSlope;
            settings.agentClimb = agentClimb;

            List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(bounds, collectionMask, NavMeshCollectGeometry.PhysicsColliders,
                                          0, new List<NavMeshBuildMarkup>(), sources);

            // Sources and bounds are already world-space (CollectSources output),
            // so the offset must stay zero: it is a DELTA for incremental
            // rebuilds, not the data's position — a non-zero offset here would
            // shift the mesh twice.
            NavMeshData data = NavMeshBuilder.BuildNavMeshData(
                settings, sources, bounds, Vector3.zero, Quaternion.identity);

            if (data == null)
            {
                Debug.LogWarning("[maze] NavMesh build returned nothing — check agentRadius/height "
                                 + "against the cell size (" + cellSize + " m) and the bake bounds. "
                                 + "Press G to rebuild, or clear buildNavMesh and bake in the "
                                 + "Navigation window instead.", this);
                return;
            }

            _navMeshData = data;
            NavMeshInstance = NavMesh.AddNavMeshData(data);
            _navMeshAdded = true;

            Debug.Log("[maze] NavMesh baked: " + sources.Count + " sources, radius "
                      + agentRadius.ToString("F2") + " m, bounds " + bounds.size
                      + " — test 03/04 can path through it.", this);
        }
    }
}
