// myFSM Unity Runtime — variable table: constants, runtime bindings and
// temporaries with C-like lifetimes.
//
// Temporaries live in scope frames that mirror the AST containers: a temp is
// declared into the currently open frame (its direct parent block) and dies
// when that frame pops (the parent's closing brace). Lookup is innermost
// first, but since every VAR_REF carries the temp entry's address, the table
// only has to answer "is this entry alive right now" plus its value.

using System;
using System.Collections.Generic;

namespace MyFSM.Core
{
    /// <summary>A resolved variable reference: section + entry index.</summary>
    public struct VarRef
    {
        public byte Section;
        public int Index;

        public VarRef(byte section, int index)
        {
            Section = section;
            Index = index;
        }
    }

    public sealed class VariableTable
    {
        private readonly FsmbModule _module;
        private readonly FsmValue[] _consts;   // parallel to module.Globals
        private readonly FsmValue[] _runtime;  // parallel to module.Runtime
        private readonly Dictionary<uint, int> _slotToEntry =
            new Dictionary<uint, int>();
        private readonly FsmValue[] _temps;    // parallel to module.Temps
        private readonly bool[] _tempAlive;
        private readonly List<List<int>> _frames = new List<List<int>>();
        private int _nextFrameId;

        private readonly Dictionary<uint, int> _globalByOffset =
            new Dictionary<uint, int>();
        private readonly Dictionary<uint, int> _runtimeByOffset =
            new Dictionary<uint, int>();
        private readonly Dictionary<uint, int> _tempByOffset =
            new Dictionary<uint, int>();

        public int ConstCount { get { return _consts.Length; } }
        public int RuntimeCount { get { return _runtime.Length; } }
        public int TempCount { get { return _temps.Length; } }
        public int OpenFrameCount { get { return _frames.Count; } }

        public VariableTable(FsmbModule module, out string error)
        {
            error = null;
            _module = module;
            _consts = new FsmValue[module.Globals.Count];
            _runtime = new FsmValue[module.Runtime.Count];
            _temps = new FsmValue[module.Temps.Count];
            _tempAlive = new bool[module.Temps.Count];

            for (int i = 0; i < module.GlobalEntryOffsets.Count; i++)
                _globalByOffset[module.GlobalEntryOffsets[i]] = i;
            for (int i = 0; i < module.RuntimeEntryOffsets.Count; i++)
                _runtimeByOffset[module.RuntimeEntryOffsets[i]] = i;
            for (int i = 0; i < module.TempEntryOffsets.Count; i++)
                _tempByOffset[module.TempEntryOffsets[i]] = i;

            for (int i = 0; i < module.Globals.Count; i++)
            {
                string derr;
                _consts[i] = FsmValue.DecodeGlobal(module.Globals[i], out derr);
                if (derr != null)
                {
                    error = "global '" + module.Globals[i].Name + "': " + derr;
                    return;
                }
            }
            for (int i = 0; i < module.Runtime.Count; i++)
            {
                _runtime[i] = FsmValue.DefaultForTag(module.Runtime[i].Tag);
                _slotToEntry[module.Runtime[i].BindingSlot] = i;
            }
            for (int i = 0; i < module.Temps.Count; i++)
            {
                _temps[i] = FsmValue.DefaultForTag(module.Temps[i].Tag);
                _tempAlive[i] = false;
            }
        }

        // ----------------------------------------------------------
        // Address resolution
        // ----------------------------------------------------------

        public bool TryResolveAddress(uint addr, out VarRef varRef)
        {
            varRef = default(VarRef);
            byte section = FsmbFormat.AddressSection(addr);
            uint offset = FsmbFormat.AddressOffset(addr);
            int index;
            switch (section)
            {
                case FsmbFormat.SecGlobal:
                    if (_globalByOffset.TryGetValue(offset, out index))
                    {
                        varRef = new VarRef(section, index);
                        return true;
                    }
                    return false;
                case FsmbFormat.SecRuntime:
                    if (_runtimeByOffset.TryGetValue(offset, out index))
                    {
                        varRef = new VarRef(section, index);
                        return true;
                    }
                    return false;
                case FsmbFormat.SecTemp:
                    if (_tempByOffset.TryGetValue(offset, out index))
                    {
                        varRef = new VarRef(section, index);
                        return true;
                    }
                    return false;
                default:
                    return false;
            }
        }

        // ----------------------------------------------------------
        // Constants (read-only)
        // ----------------------------------------------------------

        public FsmValue GetConst(int index)
        {
            return _consts[index];
        }

        public string GetConstName(int index)
        {
            return _module.Globals[index].Name;
        }

        // ----------------------------------------------------------
        // Runtime variables (binding slots; the Controller pushes values in)
        // ----------------------------------------------------------

        public bool TryEntryForSlot(uint slot, out int entry)
        {
            return _slotToEntry.TryGetValue(slot, out entry);
        }

        public FsmValue GetRuntime(int entry)
        {
            return _runtime[entry];
        }

        public void SetRuntime(int entry, FsmValue value)
        {
            _runtime[entry] = value;
        }

        public string GetRuntimeName(int entry)
        {
            return _module.Runtime[entry].Name;
        }

        public byte GetRuntimeTag(int entry)
        {
            return _module.Runtime[entry].Tag;
        }

        public uint GetRuntimeSlot(int entry)
        {
            return _module.Runtime[entry].BindingSlot;
        }

        // ----------------------------------------------------------
        // Temporaries + scope frames (C block scoping)
        // ----------------------------------------------------------

        /// <summary>Opens a scope frame for a block; returns its id.</summary>
        public int PushFrame()
        {
            _frames.Add(new List<int>());
            return _nextFrameId++;
        }

        /// <summary>
        /// Closes the innermost frame: every temp declared in it dies
        /// (marked dead + reset to its zero default, exactly like storage
        /// going out of scope in C).
        /// </summary>
        public void PopFrame()
        {
            if (_frames.Count == 0) return;
            List<int> frame = _frames[_frames.Count - 1];
            _frames.RemoveAt(_frames.Count - 1);
            for (int i = 0; i < frame.Count; i++)
            {
                int tempEntry = frame[i];
                _tempAlive[tempEntry] = false;
                _temps[tempEntry] = FsmValue.DefaultForTag(_module.Temps[tempEntry].Tag);
            }
        }

        /// <summary>
        /// Declares a temp entry into the innermost open frame. Redeclaring an
        /// already-alive entry in the same frame is a contract violation
        /// (the compiler rejects same-block redeclaration).
        /// </summary>
        public bool DeclareTemp(int tempEntry, out string error)
        {
            error = null;
            if (_frames.Count == 0)
            {
                error = "temp '" + _module.Temps[tempEntry].Name +
                        "' declared with no open scope frame";
                return false;
            }
            if (_tempAlive[tempEntry])
            {
                error = "temp '" + _module.Temps[tempEntry].Name +
                        "' redeclared while still alive";
                return false;
            }
            _tempAlive[tempEntry] = true;
            _temps[tempEntry] = FsmValue.DefaultForTag(_module.Temps[tempEntry].Tag);
            _frames[_frames.Count - 1].Add(tempEntry);
            return true;
        }

        public bool IsTempAlive(int tempEntry)
        {
            return tempEntry >= 0 && tempEntry < _tempAlive.Length && _tempAlive[tempEntry];
        }

        public FsmValue GetTemp(int tempEntry)
        {
            return _temps[tempEntry];
        }

        public void SetTemp(int tempEntry, FsmValue value)
        {
            _temps[tempEntry] = value;
        }

        public string GetTempName(int tempEntry)
        {
            return _module.Temps[tempEntry].Name;
        }

        public byte GetTempTag(int tempEntry)
        {
            return _module.Temps[tempEntry].Tag;
        }
    }
}
