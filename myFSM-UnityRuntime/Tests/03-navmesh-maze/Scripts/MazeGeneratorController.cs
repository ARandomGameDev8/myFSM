// Test 03 — builds a random, collidable maze on a ground plane, puts an entry
// and an exit in it, and bakes a NavMesh over the result.
//
// The maze is a perfect maze from an iterative depth-first search over the cell
// grid: every cell is visited exactly once and each carved passage joins two
// cells that were not connected before. That is what guarantees "at least one
// path exists" — the carved passages form a spanning tree, so entry and exit
// are always connected. Build() also proves it at runtime with a breadth-first
// search over the same grid and logs the corridor length it found, which is the
// independent number the recorder compares the engine's path against.
//
// Geometry is clamped to the ground: every wall starts at the ground plane's own
// height (GroundY) and is built from there, so raising/lowering the plane moves
// the maze with it and nothing floats or sinks.
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

        [Header("Ground")]
        [Tooltip("Ground plane. One is created when this is empty.")]
        public Transform groundPlane;
        public string groundName = "MazeGround";
        public float groundMargin = 6f;
        public float groundThickness = 0.2f;

        [Header("Actors")]
        [Tooltip("The runner, moved to the entry cell so it starts the maze at the entry.")]
        public MazeRunnerAI runner;
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
        /// <summary>Lower bound of that route's length in metres: (PathCells-1) * cellSize.</summary>
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

        /// <summary>Builds a maze (seed 0 = random), its markers, and its NavMesh.</summary>
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
            CreateWallGeometry();
            CreateMarkers();
            PlaceActors();

            PathCells = CountPathCells();
            StraightDistance = Vector3.Distance(EntryPosition, ExitPosition);

            Debug.Log("[maze] seed " + withSeed + ", " + cellsX + "x" + cellsY + " cells of "
                      + cellSize + " m: entry " + EntryPosition + " -> exit " + ExitPosition
                      + " | straight line " + StraightDistance.ToString("F1") + " m"
                      + " | shortest corridor route " + PathCells + " cells, at least "
                      + PathLengthLowerBound.ToString("F1") + " m.", this);

            if (buildNavMesh) BakeNavMesh();
        }

        // ------------------------------------------------------------------
        // Maze
        // ------------------------------------------------------------------

        private void CarvePerfectMaze()
        {
            int count = cellsX * cellsY;
            bool[] visited = new bool[count];
            int[] stack = new int[count];
            int stackSize = 0;

            int current = 0; // entry corner (0, 0)
            visited[current] = true;
            stack[stackSize++] = current;

            // Iterative DFS: the classic "recursive backtracker".
            while (stackSize > 0)
            {
                current = stack[stackSize - 1];
                int order = Random.Range(0, 4);
                int next = -1;
                byte sharedWall = 0;

                for (int attempt = 0; attempt < 4 && next < 0; attempt++)
                {
                    int direction = (order + attempt) % 4;
                    int nx = current % cellsX;
                    int ny = current / cellsX;
                    if (direction == 0) ny += 1;        // north
                    else if (direction == 1) nx += 1;   // east
                    else if (direction == 2) ny -= 1;   // south
                    else nx -= 1;                       // west

                    if (nx < 0 || ny < 0 || nx >= cellsX || ny >= cellsY) continue;
                    int candidate = ny * cellsX + nx;
                    if (visited[candidate]) continue;

                    next = candidate;
                    sharedWall = direction == 0 ? WallNorth
                               : direction == 1 ? WallEast
                               : direction == 2 ? WallSouth
                               : WallWest;
                }

                if (next < 0)
                {
                    stackSize--; // dead end: back up
                    continue;
                }

                RemoveWall(current, sharedWall);
                visited[next] = true;
                stack[stackSize++] = next;
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

        private void PlaceActors()
        {
            Vector3 entry = CellCentre(0, 0);
            Vector3 target = entry + new Vector3(0f, 0.5f, 0f);
            // No binding happens here on purpose: the runner's own Start() reads
            // MazeGeneratorController.Current.Exit (this Awake runs before it), so
            // this script never touches fields that only exist in a hand-written
            // partial binding file.
            if (runner != null) runner.transform.position = target;
            if (targetWalker != null) targetWalker.position = target;
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

            NavMeshData data = NavMeshBuilder.BuildNavMeshData(
                settings, sources, bounds,
                new Vector3(transform.position.x, GroundY, transform.position.z),
                Quaternion.identity);

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
