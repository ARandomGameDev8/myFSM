// myFSM Unity Runtime — Object category (0x0100-0x0115, 22 overloads).
// Transforms, activity, tags (stable FNV-1a int of the tag string) and layers.

using UnityEngine;
using MyFSM.Core;

namespace MyFSM.Unity
{
    public static class ObjectFunctions
    {
        public static FsmValue Run(FunctionDispatcher d, ushort id, FsmValue[] args, AiExecution exec)
        {
            switch (id)
            {
                case 0x0100: return GetPosition3(d, args, exec);
                case 0x0101: return GetPosition2(d, args, exec);
                case 0x0102: return GetRotation(d, args, exec);
                case 0x0103: return GetScale3(d, args, exec);
                case 0x0104: return GetScale2(d, args, exec);
                case 0x0105: return SetPosition3(d, args, exec);
                case 0x0106: return SetPosition2(d, args, exec);
                case 0x0107: return SetRotation(d, args, exec);
                case 0x0108: return SetScale3(d, args, exec);
                case 0x0109: return SetScale2(d, args, exec);
                case 0x010A: return DistanceTo3(d, args, exec);
                case 0x010B: return DistanceTo2(d, args, exec);
                case 0x010C: return DirectionTo3(d, args, exec);
                case 0x010D: return DirectionTo2(d, args, exec);
                case 0x010E: return IsActive(d, args, exec);
                case 0x010F: return IsActive(d, args, exec);
                case 0x0110: return SetActive(d, args, exec);
                case 0x0111: return SetActive(d, args, exec);
                case 0x0112: return GetTag(d, args, exec);
                case 0x0113: return GetTag(d, args, exec);
                case 0x0114: return GetLayer(d, args, exec);
                case 0x0115: return GetLayer(d, args, exec);
                default:
                    exec.Log.Error("unknown Object function id " + id.ToString("X4"));
                    return FsmValue.Void;
            }
        }

        /// <summary>True when every component is a real, usable number.</summary>
        private static bool Finite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        private static FsmValue GetPosition3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform t = d.ResolveTransform(args[0], exec, "getPosition");
            if (t == null) return FunctionDispatcher.InvalidPosition3(exec, "getPosition");
            return FsmConvert.FromV3(t.position);
        }

        private static FsmValue GetPosition2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform t = d.ResolveTransform(args[0], exec, "getPosition");
            if (t == null) return FunctionDispatcher.InvalidPosition2(exec, "getPosition");
            return FsmValue.MakeVec2(t.position.x, t.position.y);
        }

        private static FsmValue GetRotation(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform t = d.ResolveTransform(args[0], exec, "getRotation");
            if (t == null) return FsmValue.MakeQuat(0f, 0f, 0f, 1f);
            return FsmConvert.FromQ(t.rotation);
        }

        private static FsmValue GetScale3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform t = d.ResolveTransform(args[0], exec, "getScale");
            if (t == null) return FsmValue.MakeVec3(1f, 1f, 1f);
            return FsmConvert.FromV3(t.localScale);
        }

        private static FsmValue GetScale2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform t = d.ResolveTransform(args[0], exec, "getScale");
            if (t == null) return FsmValue.MakeVec2(1f, 1f);
            return FsmValue.MakeVec2(t.localScale.x, t.localScale.y);
        }

        private static FsmValue SetPosition3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform t = d.ResolveTransform(args[0], exec, "setPosition");
            if (t == null) return FsmValue.Void;
            Vector3 p = FsmConvert.ToV3(args[1]);
            if (!Finite(p))
            {
                exec.ErrorOnce("setPosition:invalid",
                               "setPosition: the value is not a finite position (NaN/Infinity) — "
                               + "it was most likely read from an unbound slot. Not applied: a "
                               + "NaN transform breaks the object in Unity.");
                return FsmValue.Void;
            }
            t.position = p;
            return FsmValue.Void;
        }

        private static FsmValue SetPosition2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform t = d.ResolveTransform(args[0], exec, "setPosition");
            if (t == null) return FsmValue.Void;
            Vector3 p = t.position;
            p.x = args[1].X;
            p.y = args[1].Y;
            if (!Finite(p))
            {
                exec.ErrorOnce("setPosition:invalid",
                               "setPosition: the value is not a finite position (NaN/Infinity) — "
                               + "it was most likely read from an unbound slot. Not applied: a "
                               + "NaN transform breaks the object in Unity.");
                return FsmValue.Void;
            }
            t.position = p;
            return FsmValue.Void;
        }

        private static FsmValue SetRotation(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform t = d.ResolveTransform(args[0], exec, "setRotation");
            if (t != null) t.rotation = FsmConvert.ToQ(args[1]);
            return FsmValue.Void;
        }

        private static FsmValue SetScale3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform t = d.ResolveTransform(args[0], exec, "setScale");
            if (t != null) t.localScale = FsmConvert.ToV3(args[1]);
            return FsmValue.Void;
        }

        private static FsmValue SetScale2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform t = d.ResolveTransform(args[0], exec, "setScale");
            if (t != null)
            {
                Vector3 s = t.localScale;
                s.x = args[1].X;
                s.y = args[1].Y;
                t.localScale = s;
            }
            return FsmValue.Void;
        }

        private static FsmValue DistanceTo3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform a = d.ResolveTransform(args[0], exec, "distanceTo");
            Transform b = d.ResolveTransform(args[1], exec, "distanceTo");
            if (a == null || b == null) return FsmValue.MakeFloat(0f);
            return FsmValue.MakeFloat(Vector3.Distance(a.position, b.position));
        }

        private static FsmValue DistanceTo2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform a = d.ResolveTransform(args[0], exec, "distanceTo");
            Transform b = d.ResolveTransform(args[1], exec, "distanceTo");
            if (a == null || b == null) return FsmValue.MakeFloat(0f);
            return FsmValue.MakeFloat(Vector2.Distance(
                new Vector2(a.position.x, a.position.y),
                new Vector2(b.position.x, b.position.y)));
        }

        private static FsmValue DirectionTo3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform a = d.ResolveTransform(args[0], exec, "directionTo");
            Transform b = d.ResolveTransform(args[1], exec, "directionTo");
            if (a == null || b == null) return FsmValue.MakeVec3(0f, 0f, 0f);
            return FsmConvert.FromV3((b.position - a.position).normalized);
        }

        private static FsmValue DirectionTo2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform a = d.ResolveTransform(args[0], exec, "directionTo");
            Transform b = d.ResolveTransform(args[1], exec, "directionTo");
            if (a == null || b == null) return FsmValue.MakeVec2(0f, 0f);
            Vector2 dir = new Vector2(b.position.x - a.position.x, b.position.y - a.position.y);
            return FsmConvert.FromV2(dir.normalized);
        }

        private static FsmValue IsActive(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            GameObject go = d.ResolveGameObject(args[0], exec, "isActive");
            if (go == null) return FsmValue.MakeBool(false);
            return FsmValue.MakeBool(go.activeSelf);
        }

        private static FsmValue SetActive(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            GameObject go = d.ResolveGameObject(args[0], exec, "setActive");
            if (go != null) go.SetActive(args[1].B);
            return FsmValue.Void;
        }

        private static FsmValue GetTag(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            GameObject go = d.ResolveGameObject(args[0], exec, "getTag");
            if (go == null) return FsmValue.MakeInt(0);
            return FsmValue.MakeInt(AstCodec.Fnv1a32(go.tag));
        }

        private static FsmValue GetLayer(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            GameObject go = d.ResolveGameObject(args[0], exec, "getLayer");
            if (go == null) return FsmValue.MakeInt(0);
            return FsmValue.MakeInt(go.layer);
        }
    }
}
