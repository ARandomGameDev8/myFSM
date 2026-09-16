// myFSM Unity Runtime — Animation category (0x0300-0x0311, 18 overloads).
// Animator over a per-handle playback channel: play/stop/pause/resume/speed,
// current clip (state fullPathHash), progress (loop-normalized time) and
// setAnimation (Play by state-name hash).

using UnityEngine;
using MyFSM.Core;

namespace MyFSM.Unity
{
    public static class AnimationFunctions
    {
        public static FsmValue Run(FunctionDispatcher d, ushort id, FsmValue[] args, AiExecution exec)
        {
            switch (id)
            {
                case 0x0300: return Play(d, args, exec);
                case 0x0301: return Play(d, args, exec);
                case 0x0302: return Stop(d, args, exec);
                case 0x0303: return Stop(d, args, exec);
                case 0x0304: return Pause(d, args, exec);
                case 0x0305: return Pause(d, args, exec);
                case 0x0306: return Resume(d, args, exec);
                case 0x0307: return Resume(d, args, exec);
                case 0x0308: return IsPlaying(d, args, exec);
                case 0x0309: return IsPlaying(d, args, exec);
                case 0x030A: return GetCurrentClip(d, args, exec);
                case 0x030B: return GetCurrentClip(d, args, exec);
                case 0x030C: return SetSpeed(d, args, exec);
                case 0x030D: return SetSpeed(d, args, exec);
                case 0x030E: return GetProgress(d, args, exec);
                case 0x030F: return GetProgress(d, args, exec);
                case 0x0310: return SetAnimation(d, args, exec);
                case 0x0311: return SetAnimation(d, args, exec);
                default:
                    exec.Log.Error("unknown Animation function id " + id.ToString("X4"));
                    return FsmValue.Void;
            }
        }

        private static FsmValue Play(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Animator a = d.ResolveComponent<Animator>(args[0], exec, "play");
            if (a != null)
            {
                a.enabled = true;
                a.speed = d.AnimFor(args[0].HandleId).Speed;
            }
            return FsmValue.Void;
        }

        private static FsmValue Stop(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Animator a = d.ResolveComponent<Animator>(args[0], exec, "stop");
            if (a != null)
            {
                a.Rebind();
                a.enabled = false;
            }
            return FsmValue.Void;
        }

        private static FsmValue Pause(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Animator a = d.ResolveComponent<Animator>(args[0], exec, "pause");
            if (a != null)
            {
                AnimChannel ch = d.AnimFor(args[0].HandleId);
                ch.SavedSpeed = a.speed;
                ch.HasSaved = true;
                a.speed = 0f;
            }
            return FsmValue.Void;
        }

        private static FsmValue Resume(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Animator a = d.ResolveComponent<Animator>(args[0], exec, "resume");
            if (a != null)
            {
                AnimChannel ch = d.AnimFor(args[0].HandleId);
                a.enabled = true;
                a.speed = ch.HasSaved ? ch.SavedSpeed : ch.Speed;
            }
            return FsmValue.Void;
        }

        private static FsmValue IsPlaying(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Animator a = d.ResolveComponent<Animator>(args[0], exec, "isPlaying");
            if (a == null) return FsmValue.MakeBool(false);
            return FsmValue.MakeBool(a.enabled && a.speed > 0f);
        }

        private static FsmValue GetCurrentClip(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Animator a = d.ResolveComponent<Animator>(args[0], exec, "getCurrentClip");
            if (a == null || a.layerCount == 0) return FsmValue.MakeInt(0);
            return FsmValue.MakeInt(a.GetCurrentAnimatorStateInfo(0).fullPathHash);
        }

        private static FsmValue SetSpeed(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Animator a = d.ResolveComponent<Animator>(args[0], exec, "setSpeed");
            if (a != null)
            {
                d.AnimFor(args[0].HandleId).Speed = args[1].F;
                a.speed = args[1].F;
            }
            return FsmValue.Void;
        }

        private static FsmValue GetProgress(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Animator a = d.ResolveComponent<Animator>(args[0], exec, "getProgress");
            if (a == null || a.layerCount == 0) return FsmValue.MakeFloat(0f);
            AnimatorStateInfo st = a.GetCurrentAnimatorStateInfo(0);
            if (st.length <= 0f) return FsmValue.MakeFloat(0f);
            float n = st.normalizedTime % 1f;
            if (n < 0f) n += 1f;
            return FsmValue.MakeFloat(n);
        }

        private static FsmValue SetAnimation(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Animator a = d.ResolveComponent<Animator>(args[0], exec, "setAnimation");
            if (a != null)
            {
                a.enabled = true;
                a.Play(args[1].I, 0, 0f);
            }
            return FsmValue.Void;
        }
    }
}
