// myFSM Unity Runtime — Math category (0x0000-0x0018, 25 overloads).
// Pure computation over UnityEngine.Mathf / Vector3.

using UnityEngine;
using MyFSM.Core;

namespace MyFSM.Unity
{
    public static class MathFunctions
    {
        public static FsmValue Run(FunctionDispatcher d, ushort id, FsmValue[] args, AiExecution exec)
        {
            switch (id)
            {
                case 0x0000: return FsmValue.MakeFloat(Mathf.Sin(args[0].F));
                case 0x0001: return FsmValue.MakeFloat(Mathf.Cos(args[0].F));
                case 0x0002: return FsmValue.MakeFloat(Mathf.Tan(args[0].F));
                case 0x0003: return FsmValue.MakeFloat(Mathf.Asin(args[0].F));
                case 0x0004: return FsmValue.MakeFloat(Mathf.Acos(args[0].F));
                case 0x0005: return FsmValue.MakeFloat(Mathf.Atan(args[0].F));
                case 0x0006: return FsmValue.MakeFloat(Mathf.Atan2(args[0].F, args[1].F));
                case 0x0007: return FsmValue.MakeFloat(Mathf.Sqrt(args[0].F));
                case 0x0008: return FsmValue.MakeFloat(Mathf.Pow(args[0].F, args[1].F));
                case 0x0009: return FsmValue.MakeFloat(Mathf.Abs(args[0].F));
                case 0x000A: return FsmValue.MakeFloat(Mathf.Sign(args[0].F));
                case 0x000B: return FsmValue.MakeFloat(Mathf.Clamp(args[0].F, args[1].F, args[2].F));
                case 0x000C: return FsmValue.MakeFloat(Mathf.Lerp(args[0].F, args[1].F, args[2].F));
                case 0x000D: return FsmValue.MakeFloat(Mathf.Min(args[0].F, args[1].F));
                case 0x000E: return FsmValue.MakeFloat(Mathf.Max(args[0].F, args[1].F));
                case 0x000F: return FsmValue.MakeFloat(Mathf.Floor(args[0].F));
                case 0x0010: return FsmValue.MakeFloat(Mathf.Ceil(args[0].F));
                case 0x0011: return FsmValue.MakeFloat(Mathf.Round(args[0].F));
                case 0x0012: return FsmConvert.FromV3(FsmConvert.ToV3(args[0]).normalized);
                case 0x0013: return FsmValue.MakeFloat(Vector3.Dot(FsmConvert.ToV3(args[0]), FsmConvert.ToV3(args[1])));
                case 0x0014: return FsmConvert.FromV3(Vector3.Cross(FsmConvert.ToV3(args[0]), FsmConvert.ToV3(args[1])));
                case 0x0015: return FsmValue.MakeFloat(Vector3.Distance(FsmConvert.ToV3(args[0]), FsmConvert.ToV3(args[1])));
                case 0x0016: return FsmValue.MakeFloat(FsmConvert.ToV3(args[0]).magnitude);
                case 0x0017: return FsmValue.MakeFloat(Random.value);
                case 0x0018: return FsmValue.MakeFloat(Random.Range(args[0].F, args[1].F));
                default:
                    exec.Log.Error("unknown Math function id " + id.ToString("X4"));
                    return FsmValue.Void;
            }
        }
    }
}
