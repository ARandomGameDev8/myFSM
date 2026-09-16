// myFSM Unity Runtime — .fsmb binary format constants (module v0.5).
//
// This file mirrors fsmc/include/binary_format.hpp and fsmc/lib/builtin_types
// exactly. If the compiler's format constants change, this file must change
// with them (and the package version must bump). Do not invent parallel
// constants elsewhere.
//
// Language level: C# 7.3 (compiles in Unity 2019+ and the sandbox harness).

using System;

namespace MyFSM.Core
{
    /// <summary>Module header + 32-bit packed-address helpers for .fsmb v0.5.</summary>
    public static class FsmbFormat
    {
        public const uint Magic = 0x46534D44u; // "FSMD"
        public const ushort VersionMajor = 0;
        public const ushort VersionMinor = 5;
        public const int HeaderSize = 36;
        public const int SectionCount = 8; // sections 0..7, header stores 1..7

        // Section IDs: [3 bits section][29 bits offset from section start].
        public const byte SecNull = 0;
        public const byte SecGlobal = 1;
        public const byte SecRuntime = 2;
        public const byte SecTemp = 3;
        public const byte SecState = 4;
        public const byte SecToken = 5;
        public const byte SecAst = 6;
        public const byte SecFsm = 7;

        public static uint MakeAddress(byte section, uint offset)
        {
            return ((uint)section << 29) | (offset & 0x1FFFFFFFu);
        }

        public static byte AddressSection(uint addr)
        {
            return (byte)(addr >> 29);
        }

        public static uint AddressOffset(uint addr)
        {
            return addr & 0x1FFFFFFFu;
        }

        public static string SectionName(byte id)
        {
            switch (id)
            {
                case SecNull: return "null";
                case SecGlobal: return "Global";
                case SecRuntime: return "Runtime";
                case SecTemp: return "Temporary";
                case SecState: return "State";
                case SecToken: return "Token";
                case SecAst: return "AST";
                case SecFsm: return "FSM";
                default: return "?";
            }
        }
    }

    /// <summary>Token/Instruction section opcodes (mirrors fmt::Opcode).</summary>
    public static class FsmbOpcode
    {
        public const byte OpCall = 0x01;
        public const byte OpAssign = 0x02;
        public const byte OpGoto = 0x03;
        public const byte OpEval = 0x04; // reserved, never emitted
        public const byte OpAdd = 0x05;
        public const byte OpSub = 0x06;
        public const byte OpMul = 0x07;
        public const byte OpDiv = 0x08;
        public const byte OpPower = 0x09;
        public const byte OpFloordiv = 0x0A;
        public const byte OpAnd = 0x0B;
        public const byte OpOr = 0x0C;
        public const byte OpNegate = 0x0D;
        public const byte OpEq = 0x0E;
        public const byte OpNeq = 0x0F;
        public const byte OpLess = 0x10;
        public const byte OpGreater = 0x11;
        public const byte OpLte = 0x12;
        public const byte OpGte = 0x13;
        // 0x14 (OpClaim) and 0x15 (OpRelease) are RETIRED as of module v0.3:
        // never reused, a module containing one is invalid.
        public const byte OpClaimRetired = 0x14;
        public const byte OpReleaseRetired = 0x15;
    }

    /// <summary>AST adjacency section token types (mirrors fmt::AstTok).</summary>
    public static class FsmbAst
    {
        public const byte State = 0x01;
        public const byte Actions = 0x02;
        public const byte Traversals = 0x03;
        public const byte If = 0x04;
        public const byte ElseIf = 0x05;
        public const byte Else = 0x06;
        public const byte Start = 0x07;  // runs once, when the state is entered
        public const byte Update = 0x08; // runs every tick
        public const byte FunctionCall = 0x10;
        public const byte Goto = 0x11;
        public const byte TempVarDecl = 0x12;
        public const byte Assign = 0x13;
        public const byte Return = 0x14; // reserved, never emitted
        public const byte BinaryOp = 0x20;
        public const byte UnaryOp = 0x21;
        public const byte Literal = 0x22;
        public const byte VarRef = 0x23;

        public static bool IsContainer(byte t)
        {
            return t == State || t == Actions || t == Traversals || t == If ||
                   t == ElseIf || t == Else || t == Start || t == Update;
        }

        public static bool IsExpression(byte t)
        {
            return t == FunctionCall || t == BinaryOp || t == UnaryOp ||
                   t == Literal || t == VarRef;
        }

        public static string TokenName(byte t)
        {
            switch (t)
            {
                case State: return "STATE";
                case Actions: return "ACTIONS";
                case Traversals: return "TRAVERSALS";
                case If: return "IF";
                case ElseIf: return "ELSE_IF";
                case Else: return "ELSE";
                case Start: return "START";
                case Update: return "UPDATE";
                case FunctionCall: return "FUNCTION_CALL";
                case Goto: return "GOTO";
                case TempVarDecl: return "TEMP_VAR_DECL";
                case Assign: return "ASSIGN";
                case Return: return "RETURN";
                case BinaryOp: return "BINARY_OP";
                case UnaryOp: return "UNARY_OP";
                case Literal: return "LITERAL";
                case VarRef: return "VAR_REF";
                default: return "UNKNOWN(0x" + t.ToString("X2") + ")";
            }
        }
    }

    /// <summary>Operator IDs used in BINARY_OP / UNARY_OP data (mirrors fmt::OpId).</summary>
    public static class FsmbOp
    {
        public const byte Plus = 0x01;
        public const byte Minus = 0x02;
        public const byte Star = 0x03;
        public const byte Pow = 0x04;
        public const byte Slash = 0x05;
        public const byte FloorDiv = 0x06;
        public const byte LogicalAnd = 0x07;
        public const byte LogicalOr = 0x08;
        public const byte Not = 0x09;
        public const byte EqEq = 0x0A;
        public const byte NotEq = 0x0B;
        public const byte Lt = 0x0C;
        public const byte Gt = 0x0D;
        public const byte Le = 0x0E;
        public const byte Ge = 0x0F;
    }

    /// <summary>Binary type tags (mirrors the BuiltinTypes registry).</summary>
    public static class FsmbType
    {
        public const byte Void = 0x00;
        public const byte Int = 0x01;
        public const byte Float = 0x02;
        public const byte Double = 0x03;
        public const byte Bool = 0x04;
        public const byte String = 0x05;
        public const byte Vector2 = 0x10;
        public const byte Vector3 = 0x11;
        public const byte Quaternion = 0x12;
        public const byte Object2D = 0x20;
        public const byte Object3D = 0x21;
        public const byte Transform2D = 0x22;
        public const byte Transform3D = 0x23;
        public const byte Camera2D = 0x30;
        public const byte Camera3D = 0x31;
        public const byte Sprite2D = 0x40;
        public const byte Sprite3D = 0x41;
        public const byte AnimationController2D = 0x50;
        public const byte AnimationController3D = 0x51;
        public const byte PhysicsObject2D = 0x60;
        public const byte PhysicsObject3D = 0x61;
        public const byte NavMeshAgent = 0x70;
    }

    /// <summary>
    /// The 22 DSL types: name, tag, storage width and kind flags.
    /// Single source of truth for the runtime (mirrors BuiltinTypes).
    /// </summary>
    public struct DslTypeInfo
    {
        public readonly string Name;
        public readonly byte Tag;
        public readonly uint SizeBytes; // 0 when IsVariableSize
        public readonly bool IsHandle;
        public readonly bool IsNumeric;
        public readonly bool IsValueType;
        public readonly bool IsVariableSize;
        public readonly string Category;

        public DslTypeInfo(string name, byte tag, uint sizeBytes, bool isHandle,
                           bool isNumeric, bool isValueType, bool isVariableSize,
                           string category)
        {
            Name = name;
            Tag = tag;
            SizeBytes = sizeBytes;
            IsHandle = isHandle;
            IsNumeric = isNumeric;
            IsValueType = isValueType;
            IsVariableSize = isVariableSize;
            Category = category;
        }
    }

    public static class DslTypes
    {
        public static readonly DslTypeInfo[] All = new DslTypeInfo[]
        {
            new DslTypeInfo("void",                  FsmbType.Void,                   0, false, false, false, false, "primitive"),
            new DslTypeInfo("int",                   FsmbType.Int,                    4, false, true,  true,  false, "primitive"),
            new DslTypeInfo("float",                 FsmbType.Float,                  4, false, true,  true,  false, "primitive"),
            new DslTypeInfo("double",                FsmbType.Double,                 8, false, true,  true,  false, "primitive"),
            new DslTypeInfo("bool",                  FsmbType.Bool,                   1, false, false, true,  false, "primitive"),
            new DslTypeInfo("string",                FsmbType.String,                 0, false, false, true,  true,  "primitive"),
            new DslTypeInfo("Vector2",               FsmbType.Vector2,                8, false, true,  true,  false, "vector"),
            new DslTypeInfo("Vector3",               FsmbType.Vector3,               12, false, true,  true,  false, "vector"),
            new DslTypeInfo("Quaternion",            FsmbType.Quaternion,            16, false, true,  true,  false, "vector"),
            new DslTypeInfo("Object2D",              FsmbType.Object2D,               4, true,  false, false, false, "object"),
            new DslTypeInfo("Object3D",              FsmbType.Object3D,               4, true,  false, false, false, "object"),
            new DslTypeInfo("Transform2D",           FsmbType.Transform2D,            4, true,  false, false, false, "object"),
            new DslTypeInfo("Transform3D",           FsmbType.Transform3D,            4, true,  false, false, false, "object"),
            new DslTypeInfo("Camera2D",              FsmbType.Camera2D,               4, true,  false, false, false, "camera"),
            new DslTypeInfo("Camera3D",              FsmbType.Camera3D,               4, true,  false, false, false, "camera"),
            new DslTypeInfo("Sprite2D",              FsmbType.Sprite2D,               4, true,  false, false, false, "sprite"),
            new DslTypeInfo("Sprite3D",              FsmbType.Sprite3D,               4, true,  false, false, false, "sprite"),
            new DslTypeInfo("AnimationController2D", FsmbType.AnimationController2D, 4, true,  false, false, false, "animation"),
            new DslTypeInfo("AnimationController3D", FsmbType.AnimationController3D, 4, true,  false, false, false, "animation"),
            new DslTypeInfo("PhysicsObject2D",       FsmbType.PhysicsObject2D,        4, true,  false, false, false, "physics"),
            new DslTypeInfo("PhysicsObject3D",       FsmbType.PhysicsObject3D,        4, true,  false, false, false, "physics"),
            new DslTypeInfo("NavMeshAgent",          FsmbType.NavMeshAgent,           4, true,  false, false, false, "navigation"),
        };

        public static bool TryFindByTag(byte tag, out DslTypeInfo info)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Tag == tag)
                {
                    info = All[i];
                    return true;
                }
            }
            info = default(DslTypeInfo);
            return false;
        }

        public static bool TryFindByName(string name, out DslTypeInfo info)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Name == name)
                {
                    info = All[i];
                    return true;
                }
            }
            info = default(DslTypeInfo);
            return false;
        }

        public static string NameOf(byte tag)
        {
            DslTypeInfo info;
            return TryFindByTag(tag, out info) ? info.Name : "unknown(0x" + tag.ToString("X2") + ")";
        }
    }
}
