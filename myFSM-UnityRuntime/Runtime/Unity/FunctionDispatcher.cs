// myFSM Unity Runtime — function dispatcher.
//
// Routes calls by stable function ID to the per-category implementations.
// The compiler guarantees argument types, so dispatch trusts them and only
// guards runtime engine state (null handles, missing components, ...),
// which is reported through the execution log with a default value —
// a failing call never throws inside the player loop.

using System;
using System.Collections.Generic;
using UnityEngine;
using MyFSM.Core;

namespace MyFSM.Unity
{
    internal static class FsmConvert
    {
        public static Vector3 ToV3(FsmValue v) { return new Vector3(v.X, v.Y, v.Z); }
        public static Vector2 ToV2(FsmValue v) { return new Vector2(v.X, v.Y); }
        public static Quaternion ToQ(FsmValue v) { return new Quaternion(v.X, v.Y, v.Z, v.W); }
        public static FsmValue FromV3(Vector3 v) { return FsmValue.MakeVec3(v.x, v.y, v.z); }
        public static FsmValue FromV2(Vector2 v) { return FsmValue.MakeVec2(v.x, v.y); }
        public static FsmValue FromQ(Quaternion q) { return FsmValue.MakeQuat(q.x, q.y, q.z, q.w); }
    }

    /// <summary>Per-animator playback channel (pause/resume memory).</summary>
    public sealed class AnimChannel
    {
        public float Speed = 1f;
        public float SavedSpeed = 1f;
        public bool HasSaved;
    }

    public sealed class FunctionDispatcher : IFunctionDispatcher
    {
        public const float DefaultMoveSpeed = 3.5f;
        public const float DefaultStopDistance = 0.2f;
        public const float DefaultLookSpeed = 360f; // degrees per second
        public const float DefaultSeparationRadius = 5f;

        public readonly AIInstance Instance;
        public readonly HandleTable Handles;

        private readonly Dictionary<int, AnimChannel> _anims =
            new Dictionary<int, AnimChannel>();

        public MovementSystem Movement { get { return Instance.Movement; } }
        public PathTable Paths { get { return Instance.Paths; } }

        public FunctionDispatcher(AIInstance instance)
        {
            Instance = instance;
            Handles = instance.Handles;
        }

        public AnimChannel AnimFor(int handleId)
        {
            AnimChannel ch;
            if (!_anims.TryGetValue(handleId, out ch))
            {
                ch = new AnimChannel();
                _anims[handleId] = ch;
            }
            return ch;
        }

        public FsmValue Dispatch(ushort id, FsmValue[] args, AiExecution exec)
        {
            if (id <= 0x0018) return MathFunctions.Run(this, id, args, exec);
            if (id >= 0x0100 && id <= 0x0115) return ObjectFunctions.Run(this, id, args, exec);
            if (id >= 0x0200 && id <= 0x020B) return SpriteFunctions.Run(this, id, args, exec);
            if (id >= 0x0300 && id <= 0x0311) return AnimationFunctions.Run(this, id, args, exec);
            if (id >= 0x0400 && id <= 0x0411) return PhysicsFunctions.Run(this, id, args, exec);
            if (id >= 0x0500 && id <= 0x0509) return CameraFunctions.Run(this, id, args, exec);
            if (id >= 0x0600 && id <= 0x062C) return NavigationFunctions.Run(this, id, args, exec);
            if (id >= 0x0700 && id <= 0x070D) return PerceptionFunctions.Run(this, id, args, exec);
            if (id >= 0x0800 && id <= 0x0809) return SteeringFunctions.Run(this, id, args, exec);
            if (id >= 0x0900 && id <= 0x0901) return SensingFunctions.Run(this, id, args, exec);
            if (id >= 0x0A00 && id <= 0x0A02) return ControlFunctions.Run(this, id, args, exec);
            exec.Log.Error("unknown function id " + id);
            return FsmValue.Void;
        }

        // ----------------------------------------------------------
        // Resolution helpers
        // ----------------------------------------------------------

        public GameObject ResolveGameObject(FsmValue h, AiExecution exec, string fn)
        {
            UnityEngine.Object o = Handles.Resolve(h.HandleId);
            if (o == null)
            {
                // Once per function per AI: an unbound slot is re-read every tick, and
                // a scene with hundreds of AIs would bury the console otherwise. Note
                // for callers — this returning null is NOT "the origin": functions that
                // read a position return an invalid (NaN) vector so nothing can quietly
                // aim at (0,0,0) (see ObjectFunctions.InvalidPosition).
                ErrorOnce(exec, "nullhandle:" + fn,
                          fn + ": the object handle is null — that slot was never bound, or the "
                          + "object was destroyed. Nothing is applied to it.");
                return null;
            }
            GameObject go = o as GameObject;
            if (go == null)
            {
                Component c = o as Component;
                if (c != null) go = c.gameObject;
            }
            if (go == null)
                ErrorOnce(exec, "notsceneobject:" + fn,
                          fn + ": the bound object is not a scene object (bind a "
                          + "GameObject or a Component).");
            return go;
        }

        public Transform ResolveTransform(FsmValue h, AiExecution exec, string fn)
        {
            UnityEngine.Object o = Handles.Resolve(h.HandleId);
            if (o == null)
            {
                // Once per function per AI: an unbound slot is re-read every tick, and
                // a scene with hundreds of AIs would bury the console otherwise. Note
                // for callers — this returning null is NOT "the origin": functions that
                // read a position hand back an invalid (NaN) vector, so nothing can
                // quietly aim at (0,0,0) (see ObjectFunctions.InvalidPosition).
                ErrorOnce(exec, "nullhandle:" + fn,
                          fn + ": the object handle is null — that slot was never bound, or the "
                          + "object was destroyed. Nothing is applied to it.");
                return null;
            }
            Transform t = o as Transform;
            if (t != null) return t;
            GameObject go = o as GameObject;
            if (go != null) return go.transform;
            Component c = o as Component;
            if (c != null) return c.transform;
            ErrorOnce(exec, "notransform:" + fn,
                      fn + ": the bound object has no Transform (bind a GameObject or a "
                      + "Component, not a resource).");
            return null;
        }

        /// <summary>
        /// A world-space position that has no answer because its source is not there
        /// (unbound slot, destroyed object, unknown path id, no camera): an INVALID
        /// (NaN) vector, never (0,0,0).
        ///
        /// (0,0,0) is the world origin — a perfectly good coordinate — so returning it
        /// for "nothing there" silently aims the caller at the centre of the scene.
        /// That is how an unbound slot becomes an invisible gravity well: every AI
        /// holding one marched to the middle of the plane, and (when the same slot
        /// drove a player-shaped object) so did that. NaN cannot be mistaken for a
        /// place: it propagates through arithmetic, every comparison is false, and the
        /// movement calls refuse it with an error of their own (see PostPoint), so the
        /// object simply stays where it is.
        ///
        /// Logged once per function per AI — these fire every tick otherwise.
        /// </summary>
        public static FsmValue InvalidPosition3(AiExecution exec, string fn)
        {
            if (exec != null)
                exec.ErrorOnce("invalidpos3:" + fn,
                               fn + ": no live source to read a position from, so the position "
                               + "is unknown. Returning an INVALID (NaN) vector rather than "
                               + "(0,0,0), which is the WORLD ORIGIN — using it would send the "
                               + "caller to the centre of the scene. Bind the slot (or check it) "
                               + "before reading.");
            return FsmValue.MakeVec3(float.NaN, float.NaN, float.NaN);
        }

        /// <summary>2D twin of <see cref="InvalidPosition3"/>.</summary>
        public static FsmValue InvalidPosition2(AiExecution exec, string fn)
        {
            if (exec != null)
                exec.ErrorOnce("invalidpos2:" + fn,
                               fn + ": no live source to read a position from, so the position "
                               + "is unknown. Returning an INVALID (NaN) vector rather than "
                               + "(0,0), which is the WORLD ORIGIN. Bind the slot (or check it) "
                               + "before reading.");
            return FsmValue.MakeVec2(float.NaN, float.NaN);
        }

        /// <summary>Per-AI one-shot error (see AiExecution.ErrorOnce).</summary>
        private static void ErrorOnce(AiExecution exec, string key, string message)
        {
            if (exec != null) exec.ErrorOnce(key, message);
        }

        /// <summary>
        /// Resolves a component, forgiving about what was bound: a direct
        /// component, a GameObject (GetComponent), or a sibling component
        /// (via its GameObject) all work.
        /// </summary>
        public T ResolveComponent<T>(FsmValue h, AiExecution exec, string fn)
            where T : UnityEngine.Object
        {
            UnityEngine.Object o = Handles.Resolve(h.HandleId);
            if (o == null)
            {
                // Once per function per AI: an unbound slot is re-read every tick, and
                // a scene with hundreds of AIs would bury the console otherwise. Note
                // for callers — this returning null is NOT "the origin": functions that
                // read a position hand back an invalid (NaN) vector, so nothing can
                // quietly aim at (0,0,0) (see ObjectFunctions.InvalidPosition).
                ErrorOnce(exec, "nullhandle:" + fn,
                          fn + ": the object handle is null — that slot was never bound, or the "
                          + "object was destroyed. Nothing is applied to it.");
                return null;
            }
            T direct = o as T;
            if (direct != null) return direct;
            GameObject go = o as GameObject;
            if (go == null)
            {
                Component c = o as Component;
                if (c != null) go = c.gameObject;
            }
            if (go == null)
            {
                exec.Log.Error(fn + ": handle is not a scene object");
                return null;
            }
            T found = go.GetComponent<T>();
            if (found == null)
                exec.Log.Error(fn + ": no " + typeof(T).Name + " on '" + go.name + "'");
            return found;
        }
    }
}
