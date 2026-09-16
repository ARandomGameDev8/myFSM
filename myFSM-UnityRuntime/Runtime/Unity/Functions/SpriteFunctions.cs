// myFSM Unity Runtime — Sprite category (0x0200-0x020B, 12 overloads).
// SpriteRenderer: color (RGB, alpha preserved), visibility (= enabled),
// world bounds and sprite size in units.

using UnityEngine;
using MyFSM.Core;

namespace MyFSM.Unity
{
    public static class SpriteFunctions
    {
        public static FsmValue Run(FunctionDispatcher d, ushort id, FsmValue[] args, AiExecution exec)
        {
            switch (id)
            {
                case 0x0200: return GetColor(d, args, exec);
                case 0x0201: return GetColor(d, args, exec);
                case 0x0202: return SetColor(d, args, exec);
                case 0x0203: return SetColor(d, args, exec);
                case 0x0204: return IsVisible(d, args, exec);
                case 0x0205: return IsVisible(d, args, exec);
                case 0x0206: return SetVisible(d, args, exec);
                case 0x0207: return SetVisible(d, args, exec);
                case 0x0208: return GetBounds3(d, args, exec);
                case 0x0209: return GetBounds2(d, args, exec);
                case 0x020A: return GetSize(d, args, exec);
                case 0x020B: return GetSize(d, args, exec);
                default:
                    exec.Log.Error("unknown Sprite function id " + id.ToString("X4"));
                    return FsmValue.Void;
            }
        }

        private static FsmValue GetColor(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            SpriteRenderer r = d.ResolveComponent<SpriteRenderer>(args[0], exec, "getColor");
            if (r == null) return FsmValue.MakeVec3(0f, 0f, 0f);
            return FsmValue.MakeVec3(r.color.r, r.color.g, r.color.b);
        }

        private static FsmValue SetColor(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            SpriteRenderer r = d.ResolveComponent<SpriteRenderer>(args[0], exec, "setColor");
            if (r != null)
            {
                Color c = r.color;
                c.r = args[1].X;
                c.g = args[1].Y;
                c.b = args[1].Z;
                r.color = c;
            }
            return FsmValue.Void;
        }

        private static FsmValue IsVisible(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            SpriteRenderer r = d.ResolveComponent<SpriteRenderer>(args[0], exec, "isVisible");
            if (r == null) return FsmValue.MakeBool(false);
            return FsmValue.MakeBool(r.enabled);
        }

        private static FsmValue SetVisible(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            SpriteRenderer r = d.ResolveComponent<SpriteRenderer>(args[0], exec, "setVisible");
            if (r != null) r.enabled = args[1].B;
            return FsmValue.Void;
        }

        private static FsmValue GetBounds3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            SpriteRenderer r = d.ResolveComponent<SpriteRenderer>(args[0], exec, "getBounds");
            if (r == null) return FsmValue.MakeVec3(0f, 0f, 0f);
            return FsmConvert.FromV3(r.bounds.size);
        }

        private static FsmValue GetBounds2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            SpriteRenderer r = d.ResolveComponent<SpriteRenderer>(args[0], exec, "getBounds");
            if (r == null) return FsmValue.MakeVec2(0f, 0f);
            return FsmValue.MakeVec2(r.bounds.size.x, r.bounds.size.y);
        }

        private static FsmValue GetSize(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            SpriteRenderer r = d.ResolveComponent<SpriteRenderer>(args[0], exec, "getSize");
            if (r == null || r.sprite == null)
            {
                if (r != null) exec.Log.Error("getSize: no sprite assigned");
                return FsmValue.MakeVec2(0f, 0f);
            }
            Vector2 px = r.sprite.rect.size;
            float ppu = r.sprite.pixelsPerUnit;
            if (ppu <= 0f) ppu = 1f;
            return FsmValue.MakeVec2(px.x / ppu, px.y / ppu);
        }
    }
}
