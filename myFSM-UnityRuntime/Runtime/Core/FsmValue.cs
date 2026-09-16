// myFSM Unity Runtime — runtime value representation.
//
// A tagged value covering all 22 DSL types. Handles (engine-owned references)
// are stored as (type tag, handle id); id 0 is the reserved null handle.
// Resolution of a handle id to a Unity object lives in the Unity layer
// (HandleTable); the Core layer never touches UnityEngine.

using System;

namespace MyFSM.Core
{
    public enum FsmValueKind : byte
    {
        Void,
        Int,
        Float,
        Double,
        Bool,
        String,
        Vec2,
        Vec3,
        Quat,
        Handle
    }

    public struct FsmValue
    {
        public FsmValueKind Kind;
        public byte TypeTag;
        public int I;
        public float F;
        public double D;
        public bool B;
        public string S;
        public float X;
        public float Y;
        public float Z;
        public float W;
        public int HandleId;

        /// <summary>Reserved null handle id. Functions returning an object
        /// type produce this when there is nothing to return (raycast miss,
        /// no nearest tag, ...). Using it as a call target is a runtime error
        /// that yields a default value, never an exception in the player.</summary>
        public const int NullHandleId = 0;

        public static readonly FsmValue Void = new FsmValue
        {
            Kind = FsmValueKind.Void,
            TypeTag = FsmbType.Void
        };

        public static FsmValue MakeInt(int v)
        {
            return new FsmValue { Kind = FsmValueKind.Int, TypeTag = FsmbType.Int, I = v };
        }

        public static FsmValue MakeFloat(float v)
        {
            return new FsmValue { Kind = FsmValueKind.Float, TypeTag = FsmbType.Float, F = v };
        }

        public static FsmValue MakeDouble(double v)
        {
            return new FsmValue { Kind = FsmValueKind.Double, TypeTag = FsmbType.Double, D = v };
        }

        public static FsmValue MakeBool(bool v)
        {
            return new FsmValue { Kind = FsmValueKind.Bool, TypeTag = FsmbType.Bool, B = v };
        }

        public static FsmValue MakeString(string v)
        {
            return new FsmValue
            {
                Kind = FsmValueKind.String,
                TypeTag = FsmbType.String,
                S = v ?? string.Empty
            };
        }

        public static FsmValue MakeVec2(float x, float y)
        {
            return new FsmValue
            {
                Kind = FsmValueKind.Vec2, TypeTag = FsmbType.Vector2, X = x, Y = y
            };
        }

        public static FsmValue MakeVec3(float x, float y, float z)
        {
            return new FsmValue
            {
                Kind = FsmValueKind.Vec3, TypeTag = FsmbType.Vector3, X = x, Y = y, Z = z
            };
        }

        public static FsmValue MakeQuat(float x, float y, float z, float w)
        {
            return new FsmValue
            {
                Kind = FsmValueKind.Quat, TypeTag = FsmbType.Quaternion,
                X = x, Y = y, Z = z, W = w
            };
        }

        public static FsmValue MakeHandle(byte typeTag, int handleId)
        {
            return new FsmValue
            {
                Kind = FsmValueKind.Handle, TypeTag = typeTag, HandleId = handleId
            };
        }

        public static FsmValue NullHandle(byte typeTag)
        {
            return MakeHandle(typeTag, NullHandleId);
        }

        public bool IsNullHandle()
        {
            return Kind == FsmValueKind.Handle && HandleId == NullHandleId;
        }

        /// <summary>C-like zero default for a type tag (uninitialized temps
        /// and unbound value slots). Handles default to the null handle.</summary>
        public static FsmValue DefaultForTag(byte tag)
        {
            switch (tag)
            {
                case FsmbType.Int: return MakeInt(0);
                case FsmbType.Float: return MakeFloat(0f);
                case FsmbType.Double: return MakeDouble(0.0);
                case FsmbType.Bool: return MakeBool(false);
                case FsmbType.String: return MakeString(string.Empty);
                case FsmbType.Vector2: return MakeVec2(0f, 0f);
                case FsmbType.Vector3: return MakeVec3(0f, 0f, 0f);
                case FsmbType.Quaternion: return MakeQuat(0f, 0f, 0f, 0f);
                default:
                    {
                        DslTypeInfo info;
                        if (DslTypes.TryFindByTag(tag, out info) && info.IsHandle)
                            return NullHandle(tag);
                        return Void;
                    }
            }
        }

        /// <summary>Numeric promotion rank: int 0, float 1, double 2, else -1.</summary>
        public static int RankOf(FsmValueKind kind)
        {
            switch (kind)
            {
                case FsmValueKind.Int: return 0;
                case FsmValueKind.Float: return 1;
                case FsmValueKind.Double: return 2;
                default: return -1;
            }
        }

        public double ToDouble()
        {
            switch (Kind)
            {
                case FsmValueKind.Int: return I;
                case FsmValueKind.Float: return F;
                case FsmValueKind.Double: return D;
                default: return 0.0;
            }
        }

        public float ToFloat()
        {
            switch (Kind)
            {
                case FsmValueKind.Int: return I;
                case FsmValueKind.Float: return F;
                case FsmValueKind.Double: return (float)D;
                default: return 0f;
            }
        }

        public int ToInt()
        {
            switch (Kind)
            {
                case FsmValueKind.Int: return I;
                case FsmValueKind.Float: return (int)F;
                case FsmValueKind.Double: return (int)D;
                default: return 0;
            }
        }

        public static FsmValue FromDoubleAtRank(double v, int rank)
        {
            switch (rank)
            {
                case 0: return MakeInt((int)v);
                case 1: return MakeFloat((float)v);
                default: return MakeDouble(v);
            }
        }

        /// <summary>
        /// Decodes a LITERAL token's data bytes. data[0] is the type tag,
        /// followed by the value (for strings: [4] byte length + UTF-8).
        /// </summary>
        public static FsmValue DecodeLiteral(byte[] data, out string error)
        {
            error = null;
            if (data == null || data.Length < 1)
            {
                error = "LITERAL token has malformed data";
                return Void;
            }
            byte tag = data[0];
            switch (tag)
            {
                case FsmbType.Int:
                    return MakeInt(AstCodec.ReadI32LE(data, 1));
                case FsmbType.Float:
                    return MakeFloat(AstCodec.ReadF32LE(data, 1));
                case FsmbType.Double:
                    return MakeDouble(AstCodec.ReadF64LE(data, 1));
                case FsmbType.Bool:
                    return MakeBool(data[1] != 0);
                case FsmbType.String:
                    {
                        uint len = AstCodec.ReadU32LE(data, 1);
                        return MakeString(AstCodec.ReadUtf8(data, 5, (int)len));
                    }
                case FsmbType.Vector2:
                    return MakeVec2(AstCodec.ReadF32LE(data, 1), AstCodec.ReadF32LE(data, 5));
                case FsmbType.Vector3:
                    return MakeVec3(AstCodec.ReadF32LE(data, 1), AstCodec.ReadF32LE(data, 5),
                                            AstCodec.ReadF32LE(data, 9));
                case FsmbType.Quaternion:
                    return MakeQuat(AstCodec.ReadF32LE(data, 1), AstCodec.ReadF32LE(data, 5),
                                            AstCodec.ReadF32LE(data, 9), AstCodec.ReadF32LE(data, 13));
                default:
                    error = "LITERAL has an unsupported type tag 0x" + tag.ToString("X2");
                    return DefaultForTag(tag);
            }
        }

        /// <summary>
        /// Decodes a Global Variable entry's stored value. The entry keeps the
        /// on-disk form: fixed bytes, or [4] length + payload for strings.
        /// </summary>
        public static FsmValue DecodeGlobal(FsmGlobal g, out string error)
        {
            error = null;
            if (g.Tag == FsmbType.String)
            {
                uint len = AstCodec.ReadU32LE(g.Value, 0);
                return MakeString(AstCodec.ReadUtf8(g.Value, 4, (int)len));
            }
            byte[] withTag = new byte[g.Value.Length + 1];
            withTag[0] = g.Tag;
            Array.Copy(g.Value, 0, withTag, 1, g.Value.Length);
            return DecodeLiteral(withTag, out error);
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case FsmValueKind.Void: return "void";
                case FsmValueKind.Int: return I.ToString();
                case FsmValueKind.Float: return F.ToString("R");
                case FsmValueKind.Double: return D.ToString("R");
                case FsmValueKind.Bool: return B ? "true" : "false";
                case FsmValueKind.String: return "\"" + (S ?? string.Empty) + "\"";
                case FsmValueKind.Vec2: return "Vector2(" + X + ", " + Y + ")";
                case FsmValueKind.Vec3: return "Vector3(" + X + ", " + Y + ", " + Z + ")";
                case FsmValueKind.Quat:
                    return "Quaternion(" + X + ", " + Y + ", " + Z + ", " + W + ")";
                case FsmValueKind.Handle:
                    return DslTypes.NameOf(TypeTag) + "#" + HandleId;
                default: return "?";
            }
        }
    }
}
