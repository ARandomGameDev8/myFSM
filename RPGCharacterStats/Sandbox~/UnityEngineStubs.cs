// RPG Character & Stats System — Unity stubs for the headless sandbox ONLY.
//
// Lets the whole RPGCharacterStats runtime compile and run under `dotnet run`
// with no Unity installed (same trick as myFSM-UnityRuntime/Sandbox, trimmed
// to what THIS package touches). NEVER copy into a Unity project.
//
// Fidelity notes: GameObject really stores components so GetComponent/
// AddComponent behave; Awake runs at AddComponent time (both the generic and
// the Type-based overload the spawn pipeline uses); physics bodies just write
// through to the transform.

using System;
using System.Collections.Generic;

namespace UnityEngine
{
    // ---- attributes ----

    public class SerializeField : Attribute { }
    public class TooltipAttribute : Attribute
    {
        public TooltipAttribute(string tooltip) { }
    }
    public class HeaderAttribute : Attribute
    {
        public HeaderAttribute(string header) { }
    }
    public class MultilineAttribute : Attribute
    {
        public MultilineAttribute(int lines) { }
    }
    public class TextAreaAttribute : Attribute
    {
        public TextAreaAttribute(int minLines, int maxLines) { }
    }
    public class CreateAssetMenuAttribute : Attribute
    {
        public string fileName;
        public string menuName;
        public int order;
    }
    public class ExecuteAlwaysAttribute : Attribute { }
    public class RangeAttribute : Attribute
    {
        public RangeAttribute(float min, float max) { }
    }

    // ---- object model ----

    public class Object
    {
        public string name;
        public bool destroyed;

        public static void Destroy(Object o)
        {
            if (o != null) o.destroyed = true;
        }

        public static void Destroy(GameObject go)
        {
            if (go != null) go.destroyed = true;
        }

        public static void DontDestroyOnLoad(Object o) { }

        public static T[] FindObjectsOfType<T>() where T : Object
        {
            return new T[0];
        }

        public static implicit operator bool(Object o)
        {
            return o != null && !o.destroyed;
        }
    }

    /// <summary>Sandbox stand-in for Unity's Resources: tests seed this
    /// store the way Assets/Resources/CharacterDB is populated in a real
    /// project, so the DB cache-miss paths are exercised headless.</summary>
    public static class Resources
    {
        public static readonly Dictionary<string, Object> store =
            new Dictionary<string, Object>();

        public static T Load<T>(string path) where T : Object
        {
            Object found;
            return store.TryGetValue(path, out found) ? found as T : null;
        }

        public static T[] LoadAll<T>(string path) where T : Object
        {
            List<T> hits = new List<T>();
            foreach (KeyValuePair<string, Object> kv in store)
            {
                if (!kv.Key.StartsWith(path) || kv.Key.Length <= path.Length) continue;
                if (kv.Key[path.Length] != '/') continue;
                T asT = kv.Value as T;
                if (asT != null) hits.Add(asT);
            }
            return hits.ToArray();
        }
    }

    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject, new()
        {
            return new T();
        }
    }

    public class Component : Object
    {
        public GameObject gameObject { get; set; }
        public Transform transform { get; set; }

        public T GetComponent<T>()
        {
            return gameObject != null ? gameObject.GetComponent<T>() : default(T);
        }

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

    public class MonoBehaviour : Behaviour { }

    public sealed class GameObject : Object
    {
        public string tag;
        public int layer;
        public bool activeSelf = true;
        public Transform transform;

        private readonly List<Component> _components = new List<Component>();

        /// <summary>Every live GameObject, so GameObject.Find works like the
        /// real engine (the sample AI and tests use it).</summary>
        private static readonly List<GameObject> _all = new List<GameObject>();

        public GameObject(string name)
        {
            this.name = name;
            transform = new Transform();
            transform.gameObject = this;
            transform.name = name;
            transform.transform = transform;
            _all.Add(this);
        }

        public static GameObject Find(string name)
        {
            for (int i = 0; i < _all.Count; i++)
            {
                if (_all[i].name == name && !_all[i].destroyed) return _all[i];
            }
            return null;
        }

        public void SetActive(bool value) { activeSelf = value; }

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
            List<T> found = new List<T>();
            for (int i = 0; i < _components.Count; i++)
            {
                if (_components[i] is T) found.Add((T)(object)_components[i]);
            }
            return found.ToArray();
        }

        public T AddComponent<T>() where T : Component, new()
        {
            T c = new T();
            Attach(c);
            InvokeAwake(c);
            return c;
        }

        /// <summary>The non-generic overload the spawn pipeline uses for
        /// AIInstance subclasses resolved from a definition.</summary>
        public Component AddComponent(Type type)
        {
            Component c = (Component)Activator.CreateInstance(type);
            Attach(c);
            InvokeAwake(c);
            return c;
        }

        public int ComponentCount { get { return _components.Count; } }

        private void Attach(Component c)
        {
            c.gameObject = this;
            c.transform = transform;
            _components.Add(c);
        }

        private static void InvokeAwake(Component c)
        {
            // Unity runs Awake synchronously at AddComponent time; the
            // singleton server relies on it.
            System.Reflection.MethodInfo awake = c.GetType().GetMethod("Awake",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (awake != null) awake.Invoke(c, null);
        }
    }

    public sealed class Transform : Component
    {
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 localScale = new Vector3(1f, 1f, 1f);

        public Vector3 eulerAngles
        {
            get { return new Vector3(); }
            set { }
        }

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

    // ---- math ----

    public struct Vector3
    {
        public float x, y, z;

        public Vector3(float x, float y, float z)
        {
            this.x = x; this.y = y; this.z = z;
        }

        public static Vector3 zero { get { return new Vector3(0f, 0f, 0f); } }
        public static Vector3 one { get { return new Vector3(1f, 1f, 1f); } }
        public static Vector3 up { get { return new Vector3(0f, 1f, 0f); } }
        public static Vector3 down { get { return new Vector3(0f, -1f, 0f); } }
        public static Vector3 right { get { return new Vector3(1f, 0f, 0f); } }
        public static Vector3 left { get { return new Vector3(-1f, 0f, 0f); } }
        public static Vector3 forward { get { return new Vector3(0f, 0f, 1f); } }
        public static Vector3 back { get { return new Vector3(0f, 0f, -1f); } }

        public float magnitude { get { return (float)Math.Sqrt(x * x + y * y + z * z); } }
        public float sqrMagnitude { get { return x * x + y * y + z * z; } }

        public Vector3 normalized
        {
            get
            {
                float m = magnitude;
                return m > 0f ? new Vector3(x / m, y / m, z / m) : zero;
            }
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static Vector3 operator -(Vector3 a) { return new Vector3(-a.x, -a.y, -a.z); }
        public static Vector3 operator *(Vector3 a, float d) { return new Vector3(a.x * d, a.y * d, a.z * d); }
        public static Vector3 operator *(float d, Vector3 a) { return a * d; }
        public static Vector3 operator /(Vector3 a, float d) { return new Vector3(a.x / d, a.y / d, a.z / d); }

        public static float Distance(Vector3 a, Vector3 b) { return (a - b).magnitude; }
        public static float Dot(Vector3 a, Vector3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }

        public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDelta)
        {
            Vector3 d = target - current;
            float m = d.magnitude;
            if (m <= maxDelta || m == 0f) return target;
            return current + d / m * maxDelta;
        }

        public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
        }

        public override string ToString() { return "(" + x + ", " + y + ", " + z + ")"; }
    }

    public struct Vector2
    {
        public float x, y;

        public Vector2(float x, float y)
        {
            this.x = x; this.y = y;
        }

        public static Vector2 zero { get { return new Vector2(0f, 0f); } }
        public static Vector2 one { get { return new Vector2(1f, 1f); } }
        public static Vector2 up { get { return new Vector2(0f, 1f); } }
        public static Vector2 down { get { return new Vector2(0f, -1f); } }
        public static Vector2 right { get { return new Vector2(1f, 0f); } }
        public static Vector2 left { get { return new Vector2(-1f, 0f); } }

        public float magnitude { get { return (float)Math.Sqrt(x * x + y * y); } }
        public float sqrMagnitude { get { return x * x + y * y; } }

        public Vector2 normalized
        {
            get
            {
                float m = magnitude;
                return m > 0f ? new Vector2(x / m, y / m) : zero;
            }
        }

        public static Vector2 operator +(Vector2 a, Vector2 b) { return new Vector2(a.x + b.x, a.y + b.y); }
        public static Vector2 operator -(Vector2 a, Vector2 b) { return new Vector2(a.x - b.x, a.y - b.y); }
        public static Vector2 operator -(Vector2 a) { return new Vector2(-a.x, -a.y); }
        public static Vector2 operator *(Vector2 a, float d) { return new Vector2(a.x * d, a.y * d); }
        public static Vector2 operator *(float d, Vector2 a) { return a * d; }
        public static Vector2 operator /(Vector2 a, float d) { return new Vector2(a.x / d, a.y / d); }

        public static float Distance(Vector2 a, Vector2 b) { return (a - b).magnitude; }
        public static float Dot(Vector2 a, Vector2 b) { return a.x * b.x + a.y * b.y; }

        public static Vector2 MoveTowards(Vector2 current, Vector2 target, float maxDelta)
        {
            Vector2 d = target - current;
            float m = d.magnitude;
            if (m <= maxDelta || m == 0f) return target;
            return current + d / m * maxDelta;
        }

        public static explicit operator Vector3(Vector2 v) { return new Vector3(v.x, v.y, 0f); }
        public static explicit operator Vector2(Vector3 v) { return new Vector2(v.x, v.y); }

        public override string ToString() { return "(" + x + ", " + y + ")"; }
    }

    public struct Quaternion
    {
        public float x, y, z, w;

        public static Quaternion identity
        {
            get { Quaternion q; q.x = 0; q.y = 0; q.z = 0; q.w = 1; return q; }
        }

        public static Quaternion Euler(float x, float y, float z)
        {
            return identity;
        }

        public static Quaternion LookRotation(Vector3 forward)
        {
            return identity;
        }
    }

    public static class Mathf
    {
        public const float Deg2Rad = 0.0174532924f;
        public const float Rad2Deg = 57.29578f;
        public const float Infinity = float.PositiveInfinity;

        public static float Abs(float f) { return Math.Abs(f); }
        public static float Min(float a, float b) { return a < b ? a : b; }
        public static float Max(float a, float b) { return a > b ? a : b; }
        public static float Floor(float f) { return (float)Math.Floor(f); }
        public static float Ceil(float f) { return (float)Math.Ceiling(f); }
        public static float Round(float f) { return (float)Math.Round(f); }
        public static float Sign(float f) { return f < 0f ? -1f : (f > 0f ? 1f : 0f); }
        public static float Sqrt(float f) { return (float)Math.Sqrt(f); }
        public static float Pow(float b, float e) { return (float)Math.Pow(b, e); }
        public static float Sin(float f) { return (float)Math.Sin(f); }
        public static float Cos(float f) { return (float)Math.Cos(f); }
        public static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static float Clamp01(float v) { return Clamp(v, 0f, 1f); }
        public static float Lerp(float a, float b, float t) { return a + (b - a) * Clamp01(t); }
        public static float MoveTowards(float current, float target, float maxDelta)
        {
            if (Abs(target - current) <= maxDelta) return target;
            return current + Sign(target - current) * maxDelta;
        }
        public static bool Approximately(float a, float b) { return Abs(a - b) < 1e-5f; }
    }

    // ---- rendering / misc data ----

    public struct Color
    {
        public float r, g, b, a;

        public Color(float r, float g, float b, float a = 1f)
        {
            this.r = r; this.g = g; this.b = b; this.a = a;
        }

        public static Color white { get { return new Color(1f, 1f, 1f, 1f); } }
        public static Color black { get { return new Color(0f, 0f, 0f, 1f); } }
        public static Color red { get { return new Color(1f, 0f, 0f, 1f); } }
        public static Color green { get { return new Color(0f, 1f, 0f, 1f); } }
        public static Color blue { get { return new Color(0f, 0f, 1f, 1f); } }
        public static Color clear { get { return new Color(0f, 0f, 0f, 0f); } }
    }

    public struct Rect
    {
        public float x, y, width, height;

        public Rect(float x, float y, float width, float height)
        {
            this.x = x; this.y = y; this.width = width; this.height = height;
        }
    }

    public class Sprite : Object { }

    public class Animator : Behaviour
    {
        public float speed = 1f;

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

    // ---- physics ----

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

    public enum CollisionFlags
    {
        None = 0,
        Sides = 1,
        Above = 2,
        Below = 4,
    }

    public enum CapsuleDirection2D
    {
        Vertical = 0,
        Horizontal = 1,
    }

    public class Rigidbody : Component
    {
        public Vector3 velocity;
        public float mass = 1f;
        public float drag;
        public bool isKinematic;
        public bool useGravity = true;

        public int movePositionCalls;         // harness assertions
        public Vector3 lastMovePosition;

        public Vector3 position
        {
            get { return transform != null ? transform.position : Vector3.zero; }
            set { if (transform != null) transform.position = value; }
        }

        public void AddForce(Vector3 f) { }
        public void AddForce(Vector3 f, ForceMode mode) { }

        public void MovePosition(Vector3 p)
        {
            movePositionCalls++;
            lastMovePosition = p;
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
        public float drag;
        public bool isKinematic;
        public float gravityScale = 1f;

        public int movePositionCalls;         // harness assertions
        public Vector2 lastMovePosition;

        public Vector2 position
        {
            get
            {
                return transform != null
                    ? new Vector2(transform.position.x, transform.position.y)
                    : Vector2.zero;
            }
            set
            {
                if (transform != null)
                    transform.position = new Vector3(value.x, value.y, transform.position.z);
            }
        }

        public void AddForce(Vector2 f) { }
        public void AddForce(Vector2 f, ForceMode2D mode) { }

        public void MovePosition(Vector2 p)
        {
            movePositionCalls++;
            lastMovePosition = p;
            if (transform != null)
                transform.position = new Vector3(p.x, p.y, transform.position.z);
        }

        public void MoveRotation(float degrees)
        {
            if (transform != null) transform.rotation = Quaternion.Euler(0f, 0f, degrees);
        }
    }

    public class Collider : Component
    {
        public Bounds bounds;
        public bool enabled = true;
        public bool isTrigger;

        public Vector3 ClosestPoint(Vector3 p) { return p; }
    }

    public class Collider2D : Component
    {
        public Bounds bounds;
        public bool enabled = true;
        public bool isTrigger;

        public Vector2 ClosestPoint(Vector2 p) { return p; }
    }

    public class CapsuleCollider : Collider
    {
        public float radius = 0.5f;
        public float height = 2f;
        public Vector3 center;
        public int direction = 1; // 0=X 1=Y 2=Z
    }

    public class CapsuleCollider2D : Collider2D
    {
        public Vector2 size = new Vector2(1f, 2f);
        public Vector2 offset;
        public CapsuleDirection2D direction = CapsuleDirection2D.Vertical;
    }

    /// <summary>Unity's CharacterController IS a Collider — the spawn pipeline
    /// relies on that when it skips the second collider for a 3D non-physics
    /// Player (section 2.5). Move() just translates here.</summary>
    public class CharacterController : Collider
    {
        public float radius = 0.5f;
        public float height = 2f;
        public Vector3 center;
        public float slopeLimit = 45f;
        public float stepOffset = 0.3f;
        public float skinWidth = 0.08f;
        public bool isGrounded;

        public int moveCalls;                 // harness assertions
        public Vector3 lastMotion;

        public CollisionFlags Move(Vector3 motion)
        {
            moveCalls++;
            lastMotion = motion;
            if (transform != null) transform.position = transform.position + motion;
            return CollisionFlags.None;
        }
    }

    public struct Bounds
    {
        public Vector3 center;
        public Vector3 extents;
    }

    // ---- environment ----

    public static class Debug
    {
        public static readonly List<string> Messages = new List<string>();

        private static void Record(string kind, object m)
        {
            string line = kind + ": " + m;
            Messages.Add(line);
            Console.WriteLine(line);
        }

        public static void Log(object m) { Record("[log]", m); }
        public static void LogWarning(object m) { Record("[warn]", m); }
        public static void LogError(object m) { Record("[error]", m); }
    }

    public static class Time
    {
        public static float time;
        public static float deltaTime;
        public static float fixedDeltaTime = 0.02f;
    }
}
