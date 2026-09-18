// myFSM Unity Runtime — Camera category (0x0500-0x0509, 10 overloads).
// Positions, viewport tests, screen<->world conversion (screenToWorld uses
// nearClipPlane + 1 as its depth plane) and viewport size in pixels.

using UnityEngine;
using MyFSM.Core;

namespace MyFSM.Unity
{
    public static class CameraFunctions
    {
        public static FsmValue Run(FunctionDispatcher d, ushort id, FsmValue[] args, AiExecution exec)
        {
            switch (id)
            {
                case 0x0500: return GetPosition3(d, args, exec);
                case 0x0501: return GetPosition2(d, args, exec);
                case 0x0502: return IsInView(d, args, exec);
                case 0x0503: return IsInView(d, args, exec);
                case 0x0504: return ScreenToWorld3(d, args, exec);
                case 0x0505: return ScreenToWorld2(d, args, exec);
                case 0x0506: return WorldToScreen(d, args, exec);
                case 0x0507: return WorldToScreen(d, args, exec);
                case 0x0508: return GetViewport(d, args, exec);
                case 0x0509: return GetViewport(d, args, exec);
                default:
                    exec.Log.Error("unknown Camera function id " + id.ToString("X4"));
                    return FsmValue.Void;
            }
        }

        private static FsmValue GetPosition3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Camera cam = d.ResolveComponent<Camera>(args[0], exec, "getPosition");
            if (cam == null) return FunctionDispatcher.InvalidPosition3(exec, "getPosition");
            return FsmConvert.FromV3(cam.transform.position);
        }

        private static FsmValue GetPosition2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Camera cam = d.ResolveComponent<Camera>(args[0], exec, "getPosition");
            if (cam == null) return FunctionDispatcher.InvalidPosition2(exec, "getPosition");
            return FsmValue.MakeVec2(cam.transform.position.x, cam.transform.position.y);
        }

        private static FsmValue IsInView(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Camera cam = d.ResolveComponent<Camera>(args[0], exec, "isInView");
            Transform t = d.ResolveTransform(args[1], exec, "isInView");
            if (cam == null || t == null) return FsmValue.MakeBool(false);
            Vector3 vp = cam.WorldToViewportPoint(t.position);
            return FsmValue.MakeBool(vp.z > 0f && vp.x >= 0f && vp.x <= 1f &&
                                     vp.y >= 0f && vp.y <= 1f);
        }

        private static FsmValue ScreenToWorld3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Camera cam = d.ResolveComponent<Camera>(args[0], exec, "screenToWorld");
            if (cam == null) return FunctionDispatcher.InvalidPosition3(exec, "screenToWorld");
            Vector3 screen = new Vector3(args[1].X, args[1].Y, cam.nearClipPlane + 1f);
            return FsmConvert.FromV3(cam.ScreenToWorldPoint(screen));
        }

        private static FsmValue ScreenToWorld2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Camera cam = d.ResolveComponent<Camera>(args[0], exec, "screenToWorld");
            if (cam == null) return FunctionDispatcher.InvalidPosition2(exec, "screenToWorld");
            Vector3 screen = new Vector3(args[1].X, args[1].Y, cam.nearClipPlane + 1f);
            Vector3 world = cam.ScreenToWorldPoint(screen);
            return FsmValue.MakeVec2(world.x, world.y);
        }

        private static FsmValue WorldToScreen(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Camera cam = d.ResolveComponent<Camera>(args[0], exec, "worldToScreen");
            if (cam == null) return FsmValue.MakeVec2(0f, 0f);
            Vector3 sp = cam.WorldToScreenPoint(FsmConvert.ToV3(args[1]));
            return FsmValue.MakeVec2(sp.x, sp.y);
        }

        private static FsmValue GetViewport(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Camera cam = d.ResolveComponent<Camera>(args[0], exec, "getViewport");
            if (cam == null) return FsmValue.MakeVec2(0f, 0f);
            return FsmValue.MakeVec2(cam.pixelWidth, cam.pixelHeight);
        }
    }
}
