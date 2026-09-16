// myFSM Unity Runtime — .fsmb module reader + validator (module v0.5).
//
// Faithful port of fsmc/src/module_reader.cpp (readModule + validateModule).
// Error strings intentionally mirror the C++ reader so cross-implementation
// reports can be compared line by line.
//
// The reader is byte-level only: it decodes sections into the FsmbModule model
// and validates every internal reference. Decoding values into FsmValue lives
// in FsmValue.cs; executing the module lives in the servers + StateHandler.

using System;
using System.Collections.Generic;
using System.Text;

namespace MyFSM.Core
{
    // ------------------------------------------------------------------
    // Model (mirrors ReadModule + Read* structs)
    // ------------------------------------------------------------------

    public sealed class FsmGlobal
    {
        public uint Addr;
        public byte Tag;
        public byte[] Value; // on-disk form: fixed bytes, or [4] len + payload for string
        public string Name;
    }

    public sealed class FsmRuntime
    {
        public uint Addr;
        public byte Tag;
        public uint BindingSlot;
        public string Name;
    }

    public sealed class FsmTemp
    {
        public uint Addr;
        public byte Tag;
        public string Name;
    }

    public sealed class FsmState
    {
        public uint RootAddr; // AST root address (AST section)
        public string Name;
    }

    public sealed class FsmInstr
    {
        public byte Opcode;
        public uint[] Operands = new uint[0];
    }

    public sealed class FsmStateInstrs
    {
        public uint StateAddr;
        public readonly List<FsmInstr> Instrs = new List<FsmInstr>();
    }

    public sealed class FsmAstToken
    {
        public byte Type;
        public uint[] Children = new uint[0]; // child addresses (ordered)
        public byte[] Data = new byte[0];     // raw data bytes (type-specific)
    }

    public sealed class FsmEntry
    {
        public uint Src;
        public uint[] Targets = new uint[0];
    }

    public sealed class FsmbModule
    {
        public ushort Major;
        public ushort Minor;
        public readonly uint[] SectionOffsets = new uint[FsmbFormat.SectionCount];
        public readonly List<FsmGlobal> Globals = new List<FsmGlobal>();
        public readonly List<FsmRuntime> Runtime = new List<FsmRuntime>();
        public readonly List<FsmTemp> Temps = new List<FsmTemp>();
        public readonly List<FsmState> States = new List<FsmState>();
        public readonly List<FsmStateInstrs> StateInstrs = new List<FsmStateInstrs>();
        public readonly List<FsmAstToken> Ast = new List<FsmAstToken>();
        public readonly List<FsmEntry> Fsm = new List<FsmEntry>();
        public uint FileSize;

        // Entry start offsets within their sections, parallel to the lists.
        public readonly List<uint> GlobalEntryOffsets = new List<uint>();
        public readonly List<uint> RuntimeEntryOffsets = new List<uint>();
        public readonly List<uint> TempEntryOffsets = new List<uint>();
        public readonly List<uint> StateEntryOffsets = new List<uint>();
        public readonly List<uint> AstEntryOffsets = new List<uint>();

        public int AstIndexAtOffset(uint entryOffset)
        {
            for (int i = 0; i < AstEntryOffsets.Count; i++)
                if (AstEntryOffsets[i] == entryOffset) return i;
            return -1;
        }

        public int StateIndexAtOffset(uint entryOffset)
        {
            for (int i = 0; i < StateEntryOffsets.Count; i++)
                if (StateEntryOffsets[i] == entryOffset) return i;
            return -1;
        }

        public bool AstIndexByAddress(uint addr, out uint entryOffset)
        {
            entryOffset = 0;
            if (FsmbFormat.AddressSection(addr) != FsmbFormat.SecAst) return false;
            uint off = FsmbFormat.AddressOffset(addr);
            for (int i = 0; i < AstEntryOffsets.Count; i++)
            {
                if (AstEntryOffsets[i] == off) { entryOffset = off; return true; }
            }
            return false;
        }

        public bool StateIndexByAddress(uint addr, out uint entryOffset)
        {
            entryOffset = 0;
            if (FsmbFormat.AddressSection(addr) != FsmbFormat.SecState) return false;
            uint off = FsmbFormat.AddressOffset(addr);
            for (int i = 0; i < StateEntryOffsets.Count; i++)
            {
                if (StateEntryOffsets[i] == off) { entryOffset = off; return true; }
            }
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Reader
    // ------------------------------------------------------------------

    internal sealed class ByteCursor
    {
        private readonly byte[] _b;
        public uint P;
        public bool Ok = true;

        public ByteCursor(byte[] bytes) { _b = bytes; }

        public int Length { get { return _b.Length; } }

        public bool U8(out byte v)
        {
            v = 0;
            if (P >= _b.Length) { Ok = false; return false; }
            v = _b[P++];
            return true;
        }

        public bool U16(out ushort v)
        {
            byte lo, hi;
            v = 0;
            if (!U8(out lo) || !U8(out hi)) return false;
            v = (ushort)(lo | (hi << 8));
            return true;
        }

        public bool U32(out uint v)
        {
            v = 0;
            byte a0, a1, a2, a3;
            if (!U8(out a0) || !U8(out a1) || !U8(out a2) || !U8(out a3)) return false;
            v = (uint)a0 | ((uint)a1 << 8) | ((uint)a2 << 16) | ((uint)a3 << 24);
            return true;
        }

        public bool Bytes(List<byte> output, int n)
        {
            if (P + n > _b.Length) { Ok = false; return false; }
            for (int i = 0; i < n; i++) output.Add(_b[P + i]);
            P += (uint)n;
            return true;
        }

        public bool Str(out string s)
        {
            s = string.Empty;
            ushort n;
            if (!U16(out n)) return false;
            if (P + n > _b.Length) { Ok = false; return false; }
            s = Encoding.UTF8.GetString(_b, (int)P, n);
            P += n;
            return true;
        }
    }

    public static class FsmbReader
    {
        private static bool TypeSizeByTag(byte tag, out uint size)
        {
            DslTypeInfo info;
            if (DslTypes.TryFindByTag(tag, out info))
            {
                size = info.SizeBytes;
                return true;
            }
            size = 0;
            return false;
        }

        private static bool IsVariableSizeTag(byte tag)
        {
            DslTypeInfo info;
            return DslTypes.TryFindByTag(tag, out info) && info.IsVariableSize;
        }

        private static bool IsExprTok(byte t)
        {
            return FsmbAst.IsExpression(t);
        }

        // Fixed data size for tokens whose data length is known up front.
        // (FUNCTION_CALL and LITERAL are consumed specially by the reader.)
        private static int AstFixedDataSize(byte type)
        {
            switch (type)
            {
                case FsmbAst.State: return 1;
                case FsmbAst.Actions: return 0;
                case FsmbAst.Traversals: return 0;
                case FsmbAst.Start: return 0;
                case FsmbAst.Update: return 0;
                case FsmbAst.Else: return 0;
                case FsmbAst.If: return 4;
                case FsmbAst.ElseIf: return 4;
                case FsmbAst.Goto: return 4;
                case FsmbAst.TempVarDecl: return 5;
                case FsmbAst.Assign: return 8;
                case FsmbAst.Return: return 4;
                case FsmbAst.BinaryOp: return 9;
                case FsmbAst.UnaryOp: return 5;
                case FsmbAst.VarRef: return 4;
                default: return 0; // unknown tags: mirror of the C++ reader
            }
        }

        private static void WriteU32LE(List<byte> output, uint v)
        {
            output.Add((byte)(v & 0xFF));
            output.Add((byte)((v >> 8) & 0xFF));
            output.Add((byte)((v >> 16) & 0xFF));
            output.Add((byte)((v >> 24) & 0xFF));
        }

        public static bool Read(byte[] bytes, out FsmbModule module, out string error)
        {
            module = new FsmbModule();
            error = null;
            if (bytes == null)
            {
                error = "truncated header";
                return false;
            }

            ByteCursor c = new ByteCursor(bytes);
            uint magic;
            if (!c.U32(out magic)) { error = "truncated header"; return false; }
            if (magic != FsmbFormat.Magic) { error = "bad magic number"; return false; }
            ushort major, minor;
            if (!c.U16(out major) || !c.U16(out minor)) { error = "truncated header"; return false; }
            module.Major = major;
            module.Minor = minor;
            if (major != FsmbFormat.VersionMajor || minor != FsmbFormat.VersionMinor)
            {
                error = "unsupported module version " + major + "." + minor +
                        " (this runtime reads v" + FsmbFormat.VersionMajor + "." +
                        FsmbFormat.VersionMinor + ")";
                return false;
            }
            for (int i = 1; i <= 7; i++)
            {
                uint off;
                if (!c.U32(out off)) { error = "truncated header"; return false; }
                module.SectionOffsets[i] = off;
            }
            if (c.P != FsmbFormat.HeaderSize) { error = "header size mismatch"; return false; }
            module.FileSize = (uint)bytes.Length;

            uint g = module.SectionOffsets[FsmbFormat.SecGlobal];
            uint r = module.SectionOffsets[FsmbFormat.SecRuntime];
            uint t = module.SectionOffsets[FsmbFormat.SecTemp];
            uint s = module.SectionOffsets[FsmbFormat.SecState];
            uint tok = module.SectionOffsets[FsmbFormat.SecToken];
            uint ast = module.SectionOffsets[FsmbFormat.SecAst];
            uint fsm = module.SectionOffsets[FsmbFormat.SecFsm];
            if (g < FsmbFormat.HeaderSize || g > r || r > t || t > s || s > tok ||
                tok > ast || ast > fsm || fsm > module.FileSize)
            {
                error = "section offsets are not monotone or out of range";
                return false;
            }

            // --- Global Variable Section ---
            c.P = g;
            while (c.P < r)
            {
                uint start = c.P;
                FsmGlobal ge = new FsmGlobal();
                byte tag;
                if (!c.U32(out ge.Addr) || !c.U8(out tag)) { error = "truncated global entry"; return false; }
                ge.Tag = tag;
                uint sz;
                if (!TypeSizeByTag(tag, out sz)) { error = "global entry has unknown type tag"; return false; }
                List<byte> value = new List<byte>();
                if (IsVariableSizeTag(tag))
                {
                    uint len;
                    if (!c.U32(out len)) { error = "truncated global value"; return false; }
                    if (len > bytes.Length) { error = "global string length out of range"; return false; }
                    WriteU32LE(value, len);
                    if (!c.Bytes(value, (int)len)) { error = "truncated global value"; return false; }
                }
                else if (!c.Bytes(value, (int)sz))
                {
                    error = "truncated global value";
                    return false;
                }
                ge.Value = value.ToArray();
                if (!c.Str(out ge.Name)) { error = "truncated global name"; return false; }
                module.GlobalEntryOffsets.Add(start - g);
                module.Globals.Add(ge);
            }

            // --- Runtime Variable Section ---
            c.P = r;
            while (c.P < t)
            {
                uint start = c.P;
                FsmRuntime re = new FsmRuntime();
                byte tag;
                if (!c.U32(out re.Addr) || !c.U8(out tag) || !c.U32(out re.BindingSlot))
                {
                    error = "truncated runtime entry";
                    return false;
                }
                re.Tag = tag;
                uint dummy;
                if (!TypeSizeByTag(tag, out dummy)) { error = "runtime entry has unknown type tag"; return false; }
                if (!c.Str(out re.Name)) { error = "truncated runtime name"; return false; }
                module.RuntimeEntryOffsets.Add(start - r);
                module.Runtime.Add(re);
            }

            // --- Temporary Variable Section ---
            c.P = t;
            while (c.P < s)
            {
                uint start = c.P;
                FsmTemp te = new FsmTemp();
                byte tag;
                if (!c.U32(out te.Addr) || !c.U8(out tag))
                {
                    error = "truncated temp entry";
                    return false;
                }
                te.Tag = tag;
                uint dummy;
                if (!TypeSizeByTag(tag, out dummy)) { error = "temp entry has unknown type tag"; return false; }
                if (!c.Str(out te.Name)) { error = "truncated temp name"; return false; }
                module.TempEntryOffsets.Add(start - t);
                module.Temps.Add(te);
            }

            // --- State Section ---
            c.P = s;
            uint stateCount;
            if (!c.U32(out stateCount)) { error = "truncated state count"; return false; }
            for (uint i = 0; i < stateCount; i++)
            {
                uint start = c.P;
                FsmState se = new FsmState();
                if (!c.U32(out se.RootAddr)) { error = "truncated state entry"; return false; }
                if (!c.Str(out se.Name)) { error = "truncated state name"; return false; }
                module.StateEntryOffsets.Add(start - s);
                module.States.Add(se);
            }

            // --- Token / Instruction Section (per-state framed) ---
            c.P = tok;
            while (c.P < ast)
            {
                uint stateAddr;
                ushort instrCount;
                if (!c.U32(out stateAddr) || !c.U16(out instrCount))
                {
                    error = "truncated instruction frame";
                    return false;
                }
                FsmStateInstrs frame = new FsmStateInstrs();
                frame.StateAddr = stateAddr;
                for (int i = 0; i < instrCount; i++)
                {
                    FsmInstr instr = new FsmInstr();
                    byte opCount;
                    if (!c.U8(out instr.Opcode) || !c.U8(out opCount))
                    {
                        error = "truncated instruction";
                        return false;
                    }
                    List<uint> ops = new List<uint>(opCount);
                    for (int k = 0; k < opCount; k++)
                    {
                        uint op;
                        if (!c.U32(out op)) { error = "truncated instruction operand"; return false; }
                        ops.Add(op);
                    }
                    instr.Operands = ops.ToArray();
                    frame.Instrs.Add(instr);
                }
                module.StateInstrs.Add(frame);
            }

            // --- AST Adjacency Section ---
            c.P = ast;
            while (c.P < fsm)
            {
                uint start = c.P;
                FsmAstToken at = new FsmAstToken();
                byte type;
                ushort childCount;
                if (!c.U8(out type)) { error = "truncated ast entry"; return false; }
                at.Type = type;
                if (!c.U16(out childCount)) { error = "truncated ast entry"; return false; }
                List<uint> children = new List<uint>(childCount);
                for (int k = 0; k < childCount; k++)
                {
                    uint child;
                    if (!c.U32(out child)) { error = "truncated ast child"; return false; }
                    children.Add(child);
                }
                at.Children = children.ToArray();

                List<byte> data = new List<byte>();
                if (type == FsmbAst.FunctionCall)
                {
                    ushort id;
                    byte argc;
                    if (!c.U16(out id) || !c.U8(out argc)) { error = "truncated ast data"; return false; }
                    data.Add((byte)(id & 0xFF));
                    data.Add((byte)((id >> 8) & 0xFF));
                    data.Add(argc);
                    for (int k = 0; k < argc; k++)
                    {
                        uint a;
                        if (!c.U32(out a)) { error = "truncated ast data"; return false; }
                        WriteU32LE(data, a);
                    }
                }
                else if (type == FsmbAst.Literal)
                {
                    byte litTag;
                    if (!c.U8(out litTag)) { error = "truncated ast data"; return false; }
                    uint sz;
                    if (!TypeSizeByTag(litTag, out sz)) { error = "ast literal has unknown type tag"; return false; }
                    data.Add(litTag);
                    if (IsVariableSizeTag(litTag))
                    {
                        uint len;
                        if (!c.U32(out len)) { error = "truncated ast literal"; return false; }
                        if (len > bytes.Length) { error = "ast string literal length out of range"; return false; }
                        WriteU32LE(data, len);
                        if (!c.Bytes(data, (int)len)) { error = "truncated ast literal"; return false; }
                    }
                    else if (!c.Bytes(data, (int)sz))
                    {
                        error = "truncated ast literal";
                        return false;
                    }
                }
                else
                {
                    if (!c.Bytes(data, AstFixedDataSize(type))) { error = "truncated ast data"; return false; }
                }
                at.Data = data.ToArray();
                module.AstEntryOffsets.Add(start - ast);
                module.Ast.Add(at);
            }

            // --- FSM Adjacency Section ---
            c.P = fsm;
            uint fsmCount;
            if (!c.U32(out fsmCount)) { error = "truncated fsm count"; return false; }
            for (uint i = 0; i < fsmCount; i++)
            {
                FsmEntry e = new FsmEntry();
                ushort targetCount;
                if (!c.U32(out e.Src) || !c.U16(out targetCount))
                {
                    error = "truncated fsm entry";
                    return false;
                }
                List<uint> targets = new List<uint>(targetCount);
                for (int k = 0; k < targetCount; k++)
                {
                    uint tgt;
                    if (!c.U32(out tgt)) { error = "truncated fsm target"; return false; }
                    targets.Add(tgt);
                }
                e.Targets = targets.ToArray();
                module.Fsm.Add(e);
            }
            if (c.P != module.FileSize) { error = "trailing bytes after FSM section"; return false; }
            return true;
        }

        private static bool InSet(List<uint> offsets, uint off)
        {
            for (int i = 0; i < offsets.Count; i++)
                if (offsets[i] == off) return true;
            return false;
        }

        private static uint ReadU32LE(byte[] data, int at)
        {
            return (uint)data[at] | ((uint)data[at + 1] << 8) |
                   ((uint)data[at + 2] << 16) | ((uint)data[at + 3] << 24);
        }

        private static bool VarRefOk(FsmbModule m, uint addr)
        {
            switch (FsmbFormat.AddressSection(addr))
            {
                case FsmbFormat.SecGlobal:
                    return InSet(m.GlobalEntryOffsets, FsmbFormat.AddressOffset(addr));
                case FsmbFormat.SecRuntime:
                    return InSet(m.RuntimeEntryOffsets, FsmbFormat.AddressOffset(addr));
                case FsmbFormat.SecTemp:
                    return InSet(m.TempEntryOffsets, FsmbFormat.AddressOffset(addr));
                default:
                    return false;
            }
        }

        public static bool Validate(FsmbModule m, out string error)
        {
            error = null;

            // --- self addresses of variable entries + global value widths ---
            for (int i = 0; i < m.Globals.Count; i++)
            {
                FsmGlobal ge = m.Globals[i];
                if (FsmbFormat.AddressSection(ge.Addr) != FsmbFormat.SecGlobal ||
                    !InSet(m.GlobalEntryOffsets, FsmbFormat.AddressOffset(ge.Addr)))
                {
                    error = "global entry " + i + " has a malformed self-address";
                    return false;
                }
                uint sz;
                if (!TypeSizeByTag(ge.Tag, out sz))
                {
                    error = "global entry " + i + " has an unknown type tag";
                    return false;
                }
                if (IsVariableSizeTag(ge.Tag))
                {
                    if (ge.Value.Length < 4)
                    {
                        error = "global entry " + i + " has a truncated string value";
                        return false;
                    }
                    uint len = ReadU32LE(ge.Value, 0);
                    if (ge.Value.Length != 4 + (int)len)
                    {
                        error = "global entry " + i +
                                " has a string length that does not match its payload";
                        return false;
                    }
                }
                else if (ge.Value.Length != (int)sz)
                {
                    error = "global entry " + i + " has a value of the wrong width";
                    return false;
                }
            }
            for (int i = 0; i < m.Runtime.Count; i++)
            {
                FsmRuntime re = m.Runtime[i];
                if (FsmbFormat.AddressSection(re.Addr) != FsmbFormat.SecRuntime ||
                    !InSet(m.RuntimeEntryOffsets, FsmbFormat.AddressOffset(re.Addr)))
                {
                    error = "runtime entry " + i + " has a malformed self-address";
                    return false;
                }
            }
            for (int i = 0; i < m.Temps.Count; i++)
            {
                FsmTemp te = m.Temps[i];
                if (FsmbFormat.AddressSection(te.Addr) != FsmbFormat.SecTemp ||
                    !InSet(m.TempEntryOffsets, FsmbFormat.AddressOffset(te.Addr)))
                {
                    error = "temp entry " + i + " has a malformed self-address";
                    return false;
                }
            }
            for (int i = 0; i < m.States.Count; i++)
            {
                FsmState se = m.States[i];
                if (FsmbFormat.AddressSection(se.RootAddr) != FsmbFormat.SecAst)
                {
                    error = "state entry " + i + " root does not point into the AST section";
                    return false;
                }
                uint rootOff;
                if (!m.AstIndexByAddress(se.RootAddr, out rootOff))
                {
                    error = "state " + se.Name + " AST root does not resolve";
                    return false;
                }
                int rootIdx = m.AstIndexAtOffset(rootOff);
                if (m.Ast[rootIdx].Type != FsmbAst.State)
                {
                    error = "state " + se.Name + " AST root is not a STATE token";
                    return false;
                }
            }

            // --- AST tokens ---
            int stateTokens = 0;
            for (int i = 0; i < m.Ast.Count; i++)
            {
                FsmAstToken tok = m.Ast[i];
                for (int k = 0; k < tok.Children.Length; k++)
                {
                    uint child = tok.Children[k];
                    if (FsmbFormat.AddressSection(child) != FsmbFormat.SecAst ||
                        !InSet(m.AstEntryOffsets, FsmbFormat.AddressOffset(child)))
                    {
                        error = "ast entry " + i + " has a bad child address";
                        return false;
                    }
                }
                switch (tok.Type)
                {
                    case FsmbAst.State:
                        stateTokens++;
                        if (tok.Data.Length != 1 || (tok.Data[0] != 0 && tok.Data[0] != 1))
                        {
                            error = "STATE token has a malformed entry flag";
                            return false;
                        }
                        break;
                    case FsmbAst.If:
                    case FsmbAst.ElseIf:
                        {
                            if (tok.Data.Length != 4)
                            {
                                error = "IF/ELSE_IF token has malformed data";
                                return false;
                            }
                            uint cond = ReadU32LE(tok.Data, 0);
                            uint off;
                            if (!m.AstIndexByAddress(cond, out off))
                            {
                                error = "condition address does not resolve";
                                return false;
                            }
                            int idx = m.AstIndexAtOffset(off);
                            if (!IsExprTok(m.Ast[idx].Type))
                            {
                                error = "condition does not point at an expression token";
                                return false;
                            }
                            break;
                        }
                    case FsmbAst.FunctionCall:
                        {
                            if (tok.Data.Length < 3)
                            {
                                error = "FUNCTION_CALL token has malformed data";
                                return false;
                            }
                            ushort id = (ushort)(tok.Data[0] | (tok.Data[1] << 8));
                            byte argc = tok.Data[2];
                            if (tok.Data.Length != 3 + 4 * argc)
                            {
                                error = "FUNCTION_CALL data size does not match argument count";
                                return false;
                            }
                            if (FunctionCatalog.FindById(id) == null)
                            {
                                error = "FUNCTION_CALL references unknown function id " + id;
                                return false;
                            }
                            for (int k = 0; k < argc; k++)
                            {
                                uint a = ReadU32LE(tok.Data, 3 + 4 * k);
                                uint off;
                                if (!m.AstIndexByAddress(a, out off))
                                {
                                    error = "FUNCTION_CALL argument address does not resolve";
                                    return false;
                                }
                            }
                            break;
                        }
                    case FsmbAst.Goto:
                        {
                            if (tok.Data.Length != 4)
                            {
                                error = "GOTO token has malformed data";
                                return false;
                            }
                            uint target = ReadU32LE(tok.Data, 0);
                            uint off;
                            if (!m.StateIndexByAddress(target, out off))
                            {
                                error = "GOTO target does not resolve to a state entry";
                                return false;
                            }
                            break;
                        }
                    case FsmbAst.TempVarDecl:
                        {
                            if (tok.Data.Length != 5)
                            {
                                error = "TEMP_VAR_DECL token has malformed data";
                                return false;
                            }
                            uint dummy;
                            if (!TypeSizeByTag(tok.Data[0], out dummy))
                            {
                                error = "TEMP_VAR_DECL has an unknown type tag";
                                return false;
                            }
                            uint var = ReadU32LE(tok.Data, 1);
                            if (FsmbFormat.AddressSection(var) != FsmbFormat.SecTemp ||
                                !InSet(m.TempEntryOffsets, FsmbFormat.AddressOffset(var)))
                            {
                                error = "TEMP_VAR_DECL variable address does not resolve";
                                return false;
                            }
                            break;
                        }
                    case FsmbAst.Assign:
                        {
                            if (tok.Data.Length != 8)
                            {
                                error = "ASSIGN token has malformed data";
                                return false;
                            }
                            uint target = ReadU32LE(tok.Data, 0);
                            uint value = ReadU32LE(tok.Data, 4);
                            if (!VarRefOk(m, target))
                            {
                                error = "ASSIGN target address does not resolve";
                                return false;
                            }
                            uint off;
                            if (!m.AstIndexByAddress(value, out off))
                            {
                                error = "ASSIGN value address does not resolve";
                                return false;
                            }
                            int idx = m.AstIndexAtOffset(off);
                            if (!IsExprTok(m.Ast[idx].Type))
                            {
                                error = "ASSIGN value does not point at an expression token";
                                return false;
                            }
                            break;
                        }
                    case FsmbAst.VarRef:
                        {
                            if (tok.Data.Length != 4)
                            {
                                error = "VAR_REF token has malformed data";
                                return false;
                            }
                            uint var = ReadU32LE(tok.Data, 0);
                            if (!VarRefOk(m, var))
                            {
                                error = "VAR_REF address does not resolve";
                                return false;
                            }
                            break;
                        }
                    case FsmbAst.BinaryOp:
                        {
                            if (tok.Data.Length != 9)
                            {
                                error = "BINARY_OP token has malformed data";
                                return false;
                            }
                            if (tok.Data[0] < 1 || tok.Data[0] > 15)
                            {
                                error = "BINARY_OP has an unknown operator id";
                                return false;
                            }
                            for (int k = 0; k < 2; k++)
                            {
                                uint a = ReadU32LE(tok.Data, 1 + 4 * k);
                                uint off;
                                if (!m.AstIndexByAddress(a, out off))
                                {
                                    error = "BINARY_OP operand address does not resolve";
                                    return false;
                                }
                            }
                            break;
                        }
                    case FsmbAst.UnaryOp:
                        {
                            if (tok.Data.Length != 5)
                            {
                                error = "UNARY_OP token has malformed data";
                                return false;
                            }
                            if (tok.Data[0] != FsmbOp.Not && tok.Data[0] != FsmbOp.Minus)
                            {
                                error = "UNARY_OP has an unknown operator id";
                                return false;
                            }
                            uint a = ReadU32LE(tok.Data, 1);
                            uint off;
                            if (!m.AstIndexByAddress(a, out off))
                            {
                                error = "UNARY_OP operand address does not resolve";
                                return false;
                            }
                            break;
                        }
                    case FsmbAst.Literal:
                        {
                            if (tok.Data.Length < 1)
                            {
                                error = "LITERAL token has malformed data";
                                return false;
                            }
                            uint sz;
                            if (!TypeSizeByTag(tok.Data[0], out sz))
                            {
                                error = "LITERAL has an unknown type tag";
                                return false;
                            }
                            if (IsVariableSizeTag(tok.Data[0]))
                            {
                                if (tok.Data.Length < 5)
                                {
                                    error = "LITERAL string is truncated";
                                    return false;
                                }
                                uint len = ReadU32LE(tok.Data, 1);
                                if (tok.Data.Length != 5 + (int)len)
                                {
                                    error = "LITERAL string length does not match its payload";
                                    return false;
                                }
                            }
                            else if (tok.Data.Length != 1 + (int)sz)
                            {
                                error = "LITERAL size does not match its type";
                                return false;
                            }
                            break;
                        }
                    case FsmbAst.Actions:
                        {
                            if (tok.Data.Length != 0)
                            {
                                error = "container token has unexpected data";
                                return false;
                            }
                            int startIdx = -1;
                            int updateIdx = -1;
                            for (int k = 0; k < tok.Children.Length; k++)
                            {
                                uint off;
                                if (!m.AstIndexByAddress(tok.Children[k], out off)) continue;
                                int idx = m.AstIndexAtOffset(off);
                                if (idx < 0) continue;
                                byte ct = m.Ast[idx].Type;
                                if (ct == FsmbAst.Start)
                                {
                                    if (startIdx >= 0)
                                    {
                                        error = "ACTIONS has more than one START child";
                                        return false;
                                    }
                                    startIdx = idx;
                                }
                                else if (ct == FsmbAst.Update)
                                {
                                    if (updateIdx >= 0)
                                    {
                                        error = "ACTIONS has more than one UPDATE child";
                                        return false;
                                    }
                                    updateIdx = idx;
                                }
                            }
                            if (startIdx < 0 || updateIdx < 0)
                            {
                                error = "ACTIONS token is missing its START/UPDATE phase blocks";
                                return false;
                            }
                            if (startIdx > updateIdx)
                            {
                                error = "START phase block does not precede UPDATE under ACTIONS";
                                return false;
                            }
                            break;
                        }
                    case FsmbAst.Traversals:
                    case FsmbAst.Start:
                    case FsmbAst.Update:
                    case FsmbAst.Else:
                        if (tok.Data.Length != 0)
                        {
                            error = "container token has unexpected data";
                            return false;
                        }
                        break;
                    default:
                        break;
                }
            }
            if (stateTokens != m.States.Count)
            {
                error = "STATE token count does not match the state directory";
                return false;
            }

            // --- instruction stream (cross-check only; execution is AST-driven) ---
            if (m.StateInstrs.Count != m.States.Count)
            {
                error = "instruction frame count does not match the state directory";
                return false;
            }
            for (int i = 0; i < m.StateInstrs.Count; i++)
            {
                FsmStateInstrs frame = m.StateInstrs[i];
                if (!InSet(m.StateEntryOffsets, FsmbFormat.AddressOffset(frame.StateAddr)) ||
                    FsmbFormat.AddressSection(frame.StateAddr) != FsmbFormat.SecState)
                {
                    error = "instruction frame " + i + " references an unknown state";
                    return false;
                }
                for (int j = 0; j < frame.Instrs.Count; j++)
                {
                    FsmInstr instr = frame.Instrs[j];
                    switch (instr.Opcode)
                    {
                        case FsmbOpcode.OpCall:
                            {
                                if (instr.Operands.Length != 1)
                                {
                                    error = "CALL must have one operand";
                                    return false;
                                }
                                uint off;
                                if (!m.AstIndexByAddress(instr.Operands[0], out off))
                                {
                                    error = "CALL operand does not resolve";
                                    return false;
                                }
                                break;
                            }
                        case FsmbOpcode.OpAssign:
                            {
                                if (instr.Operands.Length != 2)
                                {
                                    error = "ASSIGN must have two operands";
                                    return false;
                                }
                                if (!VarRefOk(m, instr.Operands[0]))
                                {
                                    error = "ASSIGN instruction target does not resolve";
                                    return false;
                                }
                                uint off;
                                if (!m.AstIndexByAddress(instr.Operands[1], out off))
                                {
                                    error = "ASSIGN instruction value does not resolve";
                                    return false;
                                }
                                break;
                            }
                        case FsmbOpcode.OpGoto:
                            {
                                if (instr.Operands.Length != 1)
                                {
                                    error = "GOTO must have one operand";
                                    return false;
                                }
                                uint off;
                                if (!m.StateIndexByAddress(instr.Operands[0], out off))
                                {
                                    error = "GOTO instruction target does not resolve";
                                    return false;
                                }
                                break;
                            }
                        case FsmbOpcode.OpEval:
                            {
                                if (instr.Operands.Length != 1)
                                {
                                    error = "EVAL must have one operand";
                                    return false;
                                }
                                uint off;
                                if (!m.AstIndexByAddress(instr.Operands[0], out off))
                                {
                                    error = "EVAL operand does not resolve";
                                    return false;
                                }
                                break;
                            }
                        case FsmbOpcode.OpAdd:
                        case FsmbOpcode.OpSub:
                        case FsmbOpcode.OpMul:
                        case FsmbOpcode.OpDiv:
                        case FsmbOpcode.OpPower:
                        case FsmbOpcode.OpFloordiv:
                        case FsmbOpcode.OpAnd:
                        case FsmbOpcode.OpOr:
                        case FsmbOpcode.OpNegate:
                        case FsmbOpcode.OpEq:
                        case FsmbOpcode.OpNeq:
                        case FsmbOpcode.OpLess:
                        case FsmbOpcode.OpGreater:
                        case FsmbOpcode.OpLte:
                        case FsmbOpcode.OpGte:
                            if (instr.Operands.Length > 3)
                            {
                                error = "too many arithmetic operands";
                                return false;
                            }
                            break;
                        default:
                            error = "unknown or retired opcode 0x" + instr.Opcode.ToString("X2");
                            return false;
                    }
                }
            }

            // --- FSM section ---
            if (m.Fsm.Count != m.States.Count)
            {
                error = "FSM entry count does not match the state directory";
                return false;
            }
            for (int i = 0; i < m.Fsm.Count; i++)
            {
                uint off;
                if (!m.StateIndexByAddress(m.Fsm[i].Src, out off))
                {
                    error = "FSM source does not resolve";
                    return false;
                }
                for (int k = 0; k < m.Fsm[i].Targets.Length; k++)
                {
                    if (!m.StateIndexByAddress(m.Fsm[i].Targets[k], out off))
                    {
                        error = "FSM target does not resolve";
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
