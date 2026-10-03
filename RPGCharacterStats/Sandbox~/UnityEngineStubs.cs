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
using System.Globalization;
using System.Reflection;
using System.Text;

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

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class RequireComponentAttribute : Attribute
    {
        public RequireComponentAttribute(Type requiredType) { }
    }

    public sealed class DisallowMultipleComponentAttribute : Attribute { }

    public sealed class AddComponentMenuAttribute : Attribute
    {
        public AddComponentMenuAttribute(string menuName) { }
    }
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

        /// <summary>The non-generic overload CharacterDB's record decoder uses
        /// for definition classes resolved from a saved type name.</summary>
        public static ScriptableObject CreateInstance(Type type)
        {
            return (ScriptableObject)Activator.CreateInstance(type);
        }
    }

    public class TextAsset : Object
    {
        public string text;

        public TextAsset() { }

        public TextAsset(string text)
        {
            this.text = text;
        }
    }

    // ---- JSON (JsonUtility) ----
    //
    // Field-based reflection JSON matching the JsonUtility semantics the
    // package relies on: public instance fields only (no properties), enums
    // as integers, lists, nested [Serializable] classes, no managed
    // UnityEngine.Object references. Records written here read back the same
    // under Unity's real JsonUtility and vice versa (unknown fields are
    // ignored by FromJsonOverwrite on both sides).

    public static class JsonUtility
    {
        public static string ToJson(object obj)
        {
            return JsonWriter.Value(obj);
        }

        public static T FromJson<T>(string json)
        {
            T instance = (T)Activator.CreateInstance(typeof(T));
            JsonBinder.Overwrite(instance, json);
            return instance;
        }

        public static void FromJsonOverwrite(string json, object objectToOverwrite)
        {
            JsonBinder.Overwrite(objectToOverwrite, json);
        }
    }

    internal static class JsonWriter
    {
        public static string Value(object value)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            Type t = value.GetType();

            if (t == typeof(string)) { String(sb, (string)value); return; }
            if (t == typeof(bool)) { sb.Append((bool)value ? "true" : "false"); return; }
            if (t.IsEnum
                || t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
                || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte))
            {
                sb.Append(System.Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (t == typeof(float))
            {
                float f = (float)value;
                sb.Append(float.IsNaN(f) || float.IsInfinity(f)
                    ? "0"
                    : f.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (t == typeof(double))
            {
                double d = (double)value;
                sb.Append(double.IsNaN(d) || double.IsInfinity(d)
                    ? "0"
                    : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (value is System.Collections.IList)
            {
                sb.Append('[');
                System.Collections.IList list = (System.Collections.IList)value;
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, list[i]);
                }
                sb.Append(']');
                return;
            }

            // Plain serializable object: public instance fields, in order.
            sb.Append('{');
            FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            bool first = true;
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                if (f.IsInitOnly || f.IsStatic) continue;
                if (typeof(Object).IsAssignableFrom(f.FieldType)) continue;   // no managed refs in records
                if (!first) sb.Append(',');
                first = false;
                String(sb, f.Name);
                sb.Append(':');
                Write(sb, f.GetValue(value));
            }
            sb.Append('}');
        }

        private static void String(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }

    internal static class JsonParser
    {
        public static object Parse(string json)
        {
            int index = 0;
            return ParseValue(json, ref index);
        }

        private static object ParseValue(string json, ref int i)
        {
            SkipSpace(json, ref i);
            if (i >= json.Length) throw new ArgumentException("unexpected end of JSON");
            char c = json[i];

            if (c == '{') return ParseObject(json, ref i);
            if (c == '[') return ParseArray(json, ref i);
            if (c == '"') return ParseString(json, ref i);
            if (c == 't') { Expect(json, ref i, "true"); return true; }
            if (c == 'f') { Expect(json, ref i, "false"); return false; }
            if (c == 'n') { Expect(json, ref i, "null"); return null; }
            return ParseNumber(json, ref i);
        }

        private static Dictionary<string, object> ParseObject(string json, ref int i)
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            i++;   // {
            SkipSpace(json, ref i);
            if (i < json.Length && json[i] == '}') { i++; return map; }

            while (true)
            {
                SkipSpace(json, ref i);
                string key = ParseString(json, ref i);
                SkipSpace(json, ref i);
                if (i >= json.Length || json[i] != ':')
                    throw new ArgumentException("expected ':' in JSON object");
                i++;
                map[key] = ParseValue(json, ref i);
                SkipSpace(json, ref i);
                if (i >= json.Length) throw new ArgumentException("unterminated JSON object");
                if (json[i] == ',') { i++; continue; }
                if (json[i] == '}') { i++; return map; }
                throw new ArgumentException("expected ',' or '}' in JSON object");
            }
        }

        private static List<object> ParseArray(string json, ref int i)
        {
            List<object> list = new List<object>();
            i++;   // [
            SkipSpace(json, ref i);
            if (i < json.Length && json[i] == ']') { i++; return list; }

            while (true)
            {
                list.Add(ParseValue(json, ref i));
                SkipSpace(json, ref i);
                if (i >= json.Length) throw new ArgumentException("unterminated JSON array");
                if (json[i] == ',') { i++; continue; }
                if (json[i] == ']') { i++; return list; }
                throw new ArgumentException("expected ',' or ']' in JSON array");
            }
        }

        private static string ParseString(string json, ref int i)
        {
            i++;   // opening quote
            StringBuilder sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= json.Length) break;
                    char e = json[i++];
                    if (e == '"') sb.Append('"');
                    else if (e == '\\') sb.Append('\\');
                    else if (e == '/') sb.Append('/');
                    else if (e == 'n') sb.Append('\n');
                    else if (e == 'r') sb.Append('\r');
                    else if (e == 't') sb.Append('\t');
                    else if (e == 'u' && i + 4 <= json.Length)
                    {
                        sb.Append((char)System.Convert.ToInt32(json.Substring(i, 4), 16));
                        i += 4;
                    }
                    else sb.Append(e);
                }
                else sb.Append(c);
            }
            throw new ArgumentException("unterminated JSON string");
        }

        private static object ParseNumber(string json, ref int i)
        {
            int start = i;
            while (i < json.Length && json[i] != ',' && json[i] != ']' && json[i] != '}'
                   && json[i] != ' ' && json[i] != '\t' && json[i] != '\n' && json[i] != '\r') i++;
            string token = json.Substring(start, i - start);

            long l;
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            double d;
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new ArgumentException("bad JSON number: " + token);
        }

        private static void Expect(string json, ref int i, string word)
        {
            if (i + word.Length > json.Length || json.Substring(i, word.Length) != word)
                throw new ArgumentException("bad JSON literal near index " + i);
            i += word.Length;
        }

        private static void SkipSpace(string json, ref int i)
        {
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r')) i++;
        }
    }

    internal static class JsonBinder
    {
        public static void Overwrite(object target, string json)
        {
            if (target == null || string.IsNullOrEmpty(json)) return;
            Apply(target, JsonParser.Parse(json));
        }

        private static void Apply(object target, object graph)
        {
            Dictionary<string, object> map = graph as Dictionary<string, object>;
            if (map == null) return;

            FieldInfo[] fields = target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                if (f.IsInitOnly || f.IsStatic) continue;
                if (!map.ContainsKey(f.Name)) continue;
                object value = map[f.Name];
                if (value == null) continue;
                f.SetValue(target, BindValue(value, f.FieldType));
            }
        }

        private static object BindValue(object value, Type targetType)
        {
            if (targetType == typeof(string)) return value is string ? value : value.ToString();
            if (targetType.IsEnum) return Enum.ToObject(targetType, System.Convert.ToInt64(value));
            if (targetType == typeof(int)) return System.Convert.ToInt32(value);
            if (targetType == typeof(long)) return System.Convert.ToInt64(value);
            if (targetType == typeof(float)) return (float)System.Convert.ToDouble(value);
            if (targetType == typeof(double)) return System.Convert.ToDouble(value);
            if (targetType == typeof(bool)) return System.Convert.ToBoolean(value);

            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
            {
                System.Collections.IList list = (System.Collections.IList)Activator.CreateInstance(targetType);
                List<object> items = value as List<object>;
                if (items != null)
                {
                    Type element = targetType.GetGenericArguments()[0];
                    for (int i = 0; i < items.Count; i++) list.Add(BindValue(items[i], element));
                }
                return list;
            }

            // Nested serializable class: build it and fill its fields.
            object instance = Activator.CreateInstance(targetType);
            Apply(instance, value);
            return instance;
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

    // ---- input ----

    public enum KeyCode
    {
        None = 0,
        Backspace = 8,
        Tab = 9,
        Return = 13,
        Escape = 27,
        Space = 32,
        LeftArrow = 276,
        UpArrow = 273,
        RightArrow = 275,
        DownArrow = 274,
        A = 97,
        D = 100,
        S = 115,
        W = 119,
    }

    /// <summary>Legacy Input Manager stub: always "no key pressed". The real
    /// controller code paths that read it are driven through public Tick
    /// methods in the headless tests instead.</summary>
    public static class Input
    {
        public static bool GetKey(KeyCode key) { return false; }
        public static bool GetKeyDown(KeyCode key) { return false; }
        public static bool GetKeyUp(KeyCode key) { return false; }
        public static float GetAxis(string axisName) { return 0f; }
        public static float GetAxisRaw(string axisName) { return 0f; }
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
