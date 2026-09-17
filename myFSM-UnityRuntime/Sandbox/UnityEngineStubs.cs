// UnityEngine stubs for the myFSM sandbox harness ONLY.
// Lets the Runtime compile and run headless under `dotnet run` with no Unity
// installed. NEVER copy this file into a Unity project — it would collide
// with the real engine. Behavior is intentionally minimal: identity math,
// null/empty scene queries, time driven by hand.
using System;

namespace UnityEngine
{
    public class Object
    {
        public string name { get; set; }
        public static void Destroy(Object o) { }
        public static void DontDestroyOnLoad(Object o) { }
        public static T[] FindObjectsOfType<T>() where T : Object
        {
            return new T[0];
        }
    }

    public class Component : Object
    {
        public GameObject gameObject { get; set; }
        public Transform transform { get; set; }
        public T GetComponent<T>()
        {
            if (gameObject == null) return default(T);
            return gameObject.GetComponent<T>();
        }

        /// <summary>
        /// Self first, then up the parents, like Unity. Movement uses this so a
        /// body or collider on a parent still drives the agent.
        /// </summary>
        public T GetComponentInParent<T>()
        {
            Transform p = transform;
            while (p != null)
            {
                if (p.gameObject != null)
                {
                    T found = p.gameObject.GetComponent<T>();
                    if (found != null) return found;
                }
                p = p.parent;
            }
            return default(T);
        }
    }

    public class Behaviour : Component
    {
        public bool enabled = true;
    }

    public class MonoBehaviour : Behaviour
    {
    }

    public sealed class SerializeField : Attribute
    {
    }

    public sealed class Tooltip : Attribute
    {
        public Tooltip(string text) { }
    }

    /// <summary>
    /// Application.dataPath is the project's "&lt;project&gt;/Assets" folder in
    /// the editor. The harness points it at a temp project before running the
    /// burst compiler's path-mapping checks.
    /// </summary>
    public static class Application
    {
        public static string dataPath = "";
    }

    public sealed class GameObject : Object
    {
        public string tag;
        public int layer;
        public bool activeSelf = true;
        public Transform transform;

        public GameObject(string name)
        {
            this.name = name;
            transform = new Transform();
            transform.gameObject = this;
            transform.name = name;
            // Unity's Transform.transform is itself; GetComponentInParent walks
            // up from here, so the stub must not answer null for it.
            transform.transform = transform;
        }

        public void SetActive(bool value) { activeSelf = value; }

        // Components live on the GameObject, like the real thing: a component
        // added here is found by GetComponent (movement reads colliders and
        // rigidbodies off the agent, so the harness needs this to be real).
        private readonly System.Collections.Generic.List<Component> _components =
            new System.Collections.Generic.List<Component>();

        public T GetComponent<T>()
        {
            for (int i = 0; i < _components.Count; i++)
            {
                if (_components[i] is T) return (T)(object)_components[i];
            }
            if (typeof(T) == typeof(Transform) && transform != null)
                return (T)(object)transform;
            return default(T);
        }

        public T[] GetComponents<T>()
        {
            System.Collections.Generic.List<T> found =
                new System.Collections.Generic.List<T>();
            for (int i = 0; i < _components.Count; i++)
            {
                if (_components[i] is T) found.Add((T)(object)_components[i]);
            }
            return found.ToArray();
        }

        public T AddComponent<T>() where T : Component, new()
        {
            T c = new T();
            c.gameObject = this;
            c.transform = transform;
            _components.Add(c);
            // Mimic Unity: Awake runs synchronously at AddComponent time.
            System.Reflection.MethodInfo awake = typeof(T).GetMethod("Awake",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            if (awake != null) awake.Invoke(c, null);
            return c;
        }
    }

    public sealed class Transform : Component
    {
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 localScale = new Vector3(1f, 1f, 1f);
        public Vector3 eulerAngles { get; set; }
        public Vector3 forward
        {
            get { return new Vector3(0f, 0f, 1f); }
        }
        public Vector3 right
        {
            get { return new Vector3(1f, 0f, 0f); }
        }
        public Transform parent { get; set; }
    }

    public struct Vector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static Vector3 zero
        {
            get { return new Vector3(0f, 0f, 0f); }
        }
        public static Vector3 one
        {
            get { return new Vector3(1f, 1f, 1f); }
        }
        public static Vector3 up
        {
            get { return new Vector3(0f, 1f, 0f); }
        }
        public static Vector3 right
        {
            get { return new Vector3(1f, 0f, 0f); }
        }
        public static Vector3 forward
        {
            get { return new Vector3(0f, 0f, 1f); }
        }
        public static Vector3 down
        {
            get { return new Vector3(0f, -1f, 0f); }
        }

        public static implicit operator Vector2(Vector3 v)
        {
            return new Vector2(v.x, v.y);
        }
        public static implicit operator Vector3(Vector2 v)
        {
            return new Vector3(v.x, v.y, 0f);
        }

        public float magnitude
        {
            get { return (float)Math.Sqrt(x * x + y * y + z * z); }
        }
        public float sqrMagnitude
        {
            get { return x * x + y * y + z * z; }
        }
        public Vector3 normalized
        {
            get
            {
                float m = magnitude;
                return m > 1e-6f ? this / m : zero;
            }
        }

        public static Vector3 operator +(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        }
        public static Vector3 operator -(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        }
        public static Vector3 operator *(Vector3 a, float s)
        {
            return new Vector3(a.x * s, a.y * s, a.z * s);
        }
        public static Vector3 operator *(float s, Vector3 a)
        {
            return a * s;
        }
        public static Vector3 operator /(Vector3 a, float s)
        {
            return new Vector3(a.x / s, a.y / s, a.z / s);
        }

        public static float Dot(Vector3 a, Vector3 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }
        public static Vector3 Cross(Vector3 a, Vector3 b)
        {
            return new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z,
                a.x * b.y - a.y * b.x);
        }
        public static float Distance(Vector3 a, Vector3 b)
        {
            return (a - b).magnitude;
        }
        public static float Angle(Vector3 a, Vector3 b)
        {
            float d = Dot(a.normalized, b.normalized);
            d = d < -1f ? -1f : (d > 1f ? 1f : d);
            return (float)Math.Acos(d) * Mathf.Rad2Deg;
        }

        /// <summary>The part of <paramref name="v"/> that lies in the plane.</summary>
        public static Vector3 ProjectOnPlane(Vector3 v, Vector3 normal)
        {
            float m = normal.sqrMagnitude;
            if (m <= 1e-12f) return v;
            return v - normal * (Dot(v, normal) / m);
        }
    }

    public struct Vector2
    {
        public float x;
        public float y;

        public Vector2(float x, float y) { this.x = x; this.y = y; }

        public static Vector2 zero
        {
            get { return new Vector2(0f, 0f); }
        }
        public static Vector2 one
        {
            get { return new Vector2(1f, 1f); }
        }
        public static Vector2 up
        {
            get { return new Vector2(0f, 1f); }
        }
        public static Vector2 right
        {
            get { return new Vector2(1f, 0f); }
        }
        public static Vector2 down
        {
            get { return new Vector2(0f, -1f); }
        }

        public float magnitude
        {
            get { return (float)Math.Sqrt(x * x + y * y); }
        }
        public float sqrMagnitude
        {
            get { return x * x + y * y; }
        }
        public Vector2 normalized
        {
            get
            {
                float m = magnitude;
                return m > 1e-6f ? this / m : zero;
            }
        }

        public static Vector2 operator +(Vector2 a, Vector2 b)
        {
            return new Vector2(a.x + b.x, a.y + b.y);
        }
        public static Vector2 operator -(Vector2 a, Vector2 b)
        {
            return new Vector2(a.x - b.x, a.y - b.y);
        }
        public static Vector2 operator *(Vector2 a, float s)
        {
            return new Vector2(a.x * s, a.y * s);
        }
        public static Vector2 operator *(float s, Vector2 a)
        {
            return a * s;
        }
        public static Vector2 operator /(Vector2 a, float s)
        {
            return new Vector2(a.x / s, a.y / s);
        }

        public static float Distance(Vector2 a, Vector2 b)
        {
            return (a - b).magnitude;
        }
        public static float Angle(Vector2 a, Vector2 b)
        {
            float ma = a.magnitude;
            float mb = b.magnitude;
            if (ma < 1e-6f || mb < 1e-6f) return 0f;
            float d = (a.x * b.x + a.y * b.y) / (ma * mb);
            d = d < -1f ? -1f : (d > 1f ? 1f : d);
            return (float)Math.Acos(d) * Mathf.Rad2Deg;
        }
    }

    public struct Quaternion
    {
        public float x;
        public float y;
        public float z;
        public float w;

        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x; this.y = y; this.z = z; this.w = w;
        }

        public static Quaternion identity
        {
            get { return new Quaternion(0f, 0f, 0f, 1f); }
        }

        /// <summary>Z-only, which is the axis the DSL's 2D rotation uses.</summary>
        public static Quaternion Euler(float x, float y, float z)
        {
            float half = z * 0.5f * Mathf.Deg2Rad;
            return new Quaternion(0f, 0f, (float)Math.Sin(half), (float)Math.Cos(half));
        }
        public Vector3 eulerAngles
        {
            get
            {
                float z = 2f * (float)Math.Atan2(z, w) * Mathf.Rad2Deg;
                return new Vector3(0f, 0f, z);
            }
        }
        public static Quaternion LookRotation(Vector3 forward)
        {
            return identity;
        }
        public static Quaternion RotateTowards(Quaternion from, Quaternion to, float step)
        {
            return to;
        }
        public static float Angle(Quaternion a, Quaternion b)
        {
            return 0f;
        }
    }

    public static class Mathf
    {
        public const float Rad2Deg = 57.29578f;
        public const float Deg2Rad = 0.0174532924f;

        public static float Sin(float f) { return (float)Math.Sin(f); }
        public static float Cos(float f) { return (float)Math.Cos(f); }
        public static float Tan(float f) { return (float)Math.Tan(f); }
        public static float Asin(float f) { return (float)Math.Asin(f); }
        public static float Acos(float f) { return (float)Math.Acos(f); }
        public static float Atan(float f) { return (float)Math.Atan(f); }
        public static float Atan2(float y, float x) { return (float)Math.Atan2(y, x); }
        public static float Sqrt(float f) { return (float)Math.Sqrt(f); }
        public static float Pow(float b, float e) { return (float)Math.Pow(b, e); }
        public static float Abs(float f) { return Math.Abs(f); }
        public static float Sign(float f) { return f < 0f ? -1f : (f > 0f ? 1f : 0f); }
        public static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
        public static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }
        public static float Min(float a, float b) { return a < b ? a : b; }
        public static float Max(float a, float b) { return a > b ? a : b; }
        public static float Floor(float f) { return (float)Math.Floor(f); }
        public static float Ceil(float f) { return (float)Math.Ceiling(f); }
        public static float Round(float f) { return (float)Math.Round(f); }

        public static float DeltaAngle(float a, float b)
        {
            float d = (b - a) % 360f;
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }
        public static float MoveTowardsAngle(float a, float b, float step)
        {
            float d = DeltaAngle(a, b);
            if (Math.Abs(d) <= step) return b;
            return a + (d > 0f ? step : -step);
        }
    }

    public static class Random
    {
        private static System.Random _r = new System.Random(1234);

        public static float value
        {
            get { return (float)_r.NextDouble(); }
        }
        public static float Range(float lo, float hi)
        {
            return lo + (float)_r.NextDouble() * (hi - lo);
        }
        public static Vector3 insideUnitSphere
        {
            get
            {
                return new Vector3(Range(-1f, 1f), Range(-1f, 1f), Range(-1f, 1f));
            }
        }
        public static Vector2 insideUnitCircle
        {
            get { return new Vector2(Range(-1f, 1f), Range(-1f, 1f)); }
        }
    }

    public struct Color
    {
        public float r;
        public float g;
        public float b;
        public float a;

        public Color(float r, float g, float b, float a)
        {
            this.r = r; this.g = g; this.b = b; this.a = a;
        }
    }

    public struct Rect
    {
        public Vector2 size;

        public Rect(float w, float h) { size = new Vector2(w, h); }
    }

    public struct Bounds
    {
        public Vector3 center;
        public Vector3 size;

        public Vector3 extents
        {
            get { return size / 2f; }
        }
    }

    public class Camera : Behaviour
    {
        public int pixelWidth = 1920;
        public int pixelHeight = 1080;
        public float nearClipPlane = 0.3f;

        public Vector3 WorldToScreenPoint(Vector3 p) { return p; }
        public Vector3 ScreenToWorldPoint(Vector3 p) { return p; }
        public Vector3 WorldToViewportPoint(Vector3 p) { return p; }
    }

    public class Sprite : Object
    {
        public Rect rect = new Rect(64f, 64f);
        public float pixelsPerUnit = 100f;
        public Bounds bounds;
    }

    public class SpriteRenderer : Behaviour
    {
        public Color color = new Color(1f, 1f, 1f, 1f);
        public Sprite sprite;
        public Bounds bounds;
    }

    public class Animator : Behaviour
    {
        public float speed = 1f;
        public int layerCount = 0;

        public void Play(int hash, int layer, float time) { }
        public void Rebind() { }
        public AnimatorStateInfo GetCurrentAnimatorStateInfo(int layer)
        {
            return new AnimatorStateInfo();
        }
    }

    public struct AnimatorStateInfo
    {
        public float length;
        public float normalizedTime;
        public int fullPathHash;
    }

    public class Rigidbody : Component
    {
        public Vector3 velocity;
        public float mass = 1f;
        public bool isKinematic = false;

        public void AddForce(Vector3 f) { }
        public void AddForce(Vector3 f, ForceMode mode) { }
        public void MovePosition(Vector3 p)
        {
            if (transform != null) transform.position = p;
        }
        public void MoveRotation(Quaternion q)
        {
            if (transform != null) transform.rotation = q;
        }
    }

    public class Rigidbody2D : Component
    {
        public Vector2 velocity;
        public float mass = 1f;
        public bool isKinematic = false;

        public void AddForce(Vector2 f) { }
        public void AddForce(Vector2 f, ForceMode2D mode) { }
        public void MovePosition(Vector2 p)
        {
            if (transform != null)
            {
                transform.position = new Vector3(p.x, p.y, transform.position.z);
            }
        }
        public void MoveRotation(float degrees)
        {
            if (transform != null) transform.rotation = Quaternion.Euler(0f, 0f, degrees);
        }
    }

    public enum ForceMode
    {
        Force,
        Acceleration,
        Impulse,
        VelocityChange,
    }

    public enum ForceMode2D
    {
        Force,
        Impulse,
    }

    public class Collider : Component
    {
        public Bounds bounds;
        public bool enabled = true;
        public bool isTrigger = false;

        public Vector3 ClosestPoint(Vector3 p) { return p; }
    }

    public class Collider2D : Component
    {
        public Bounds bounds;
        public bool enabled = true;
        public bool isTrigger = false;

        public Vector2 ClosestPoint(Vector2 p) { return p; }
    }

    public enum CollisionFlags
    {
        None = 0,
        Sides = 1,
        Above = 2,
        Below = 4,
    }

    /// <summary>
    /// Unity's CharacterController is a Collider, which is why movement tests
    /// for it BEFORE the plain-collider case. Here Move() just translates.
    /// </summary>
    public class CharacterController : Collider
    {
        public float radius = 0.5f;
        public float height = 2f;
        public Vector3 center;
        public float slopeLimit = 45f;
        public float stepOffset = 0.3f;
        public float skinWidth = 0.08f;
        public bool isGrounded = false;

        public CollisionFlags Move(Vector3 motion)
        {
            if (transform != null) transform.position = transform.position + motion;
            return CollisionFlags.None;
        }
    }

    public struct RaycastHit
    {
        public Collider collider;
        public Transform transform;
        public Vector3 point;
        public Vector3 normal;
        public float distance;
    }

    public struct RaycastHit2D
    {
        public Collider2D collider;
        public Transform transform;
        public Vector2 point;
        public Vector2 normal;
        public float distance;
    }

    public static class Physics
    {
        public static bool Raycast(Vector3 o, Vector3 d, float dist)
        {
            return false;
        }
        public static bool Raycast(Vector3 o, Vector3 d, out RaycastHit hit, float dist)
        {
            hit = new RaycastHit();
            return false;
        }
        public static Collider[] OverlapSphere(Vector3 p, float r)
        {
            return new Collider[0];
        }
        /// <summary>
        /// No collision world in the harness: always a miss, exactly like an
        /// empty scene. Tests that need a wall inject an IMotionProbe instead.
        /// </summary>
        public static bool SphereCast(Vector3 o, float radius, Vector3 d, out RaycastHit hit,
                                      float dist, int mask, QueryTriggerInteraction q)
        {
            hit = new RaycastHit();
            return false;
        }
    }

    public enum QueryTriggerInteraction
    {
        UseGlobal = 0,
        Ignore = 1,
        Collide = 2,
    }

    public static class Physics2D
    {
        public static RaycastHit2D Raycast(Vector2 o, Vector2 d, float dist)
        {
            return new RaycastHit2D();
        }
        public static RaycastHit2D Raycast(Vector2 o, Vector2 d, float dist, int mask)
        {
            return new RaycastHit2D();
        }
        public static RaycastHit2D CircleCast(Vector2 o, float r, Vector2 d, float dist)
        {
            return new RaycastHit2D();
        }
        public static Collider2D[] OverlapCircleAll(Vector2 p, float r)
        {
            return new Collider2D[0];
        }
    }

    public class TextAsset : Object
    {
        public byte[] bytes;
    }

    public static class Resources
    {
        public static T Load<T>(string path) where T : Object
        {
            return null;
        }
    }

    public static class Debug
    {
        public static void Log(object m) { Console.WriteLine("[log] " + m); }
        public static void LogWarning(object m) { Console.WriteLine("[warn] " + m); }
        public static void LogError(object m) { Console.WriteLine("[error] " + m); }
    }

    public static class Time
    {
        public static float time;
        public static float deltaTime;
    }
}

namespace UnityEngine.AI
{
    public enum NavMeshPathStatus
    {
        Invalid,
        Partial,
        Complete,
    }

    public class NavMeshPath
    {
        public Vector3[] corners = new Vector3[0];
        public NavMeshPathStatus status = NavMeshPathStatus.Invalid;
    }

    public static class NavMesh
    {
        public const int AllAreas = -1;

        public static bool CalculatePath(Vector3 from, Vector3 to, int mask, NavMeshPath path)
        {
            return false;
        }
    }

    public class NavMeshAgent : Behaviour
    {
        public float speed = 3.5f;
        public float stoppingDistance = 0f;
        public float remainingDistance = float.MaxValue;
        public bool pathPending = false;
        public NavMeshPathStatus pathStatus = NavMeshPathStatus.Complete;
        public bool isOnNavMesh = false;
        public bool isStopped = false;

        public bool SetDestination(Vector3 target) { return true; }
        public void ResetPath() { }
    }
}
