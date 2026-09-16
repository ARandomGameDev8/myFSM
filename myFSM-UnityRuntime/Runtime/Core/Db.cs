// myFSM Unity Runtime — MainServer database design.
//
// The DB only survives while the scene is playing (in-memory). It holds:
// - asset records: one per loaded .fsmb module (states, binding schema...);
// - instance records: one per AIInstance component (head state, counters...);
// - the state-change timetable: every transition, newest last, capped ring;
// - the emit log: every emit() call, capped ring.
//
// External systems never touch the DB directly: they go through the query
// server, which answers queries from snapshots and applies commands with
// validation. Snapshots are copies, so readers cannot corrupt the live data.

using System;
using System.Collections.Generic;

namespace MyFSM.Core
{
    public struct RuntimeSlotSchema
    {
        public uint Slot;
        public byte Tag;
        public string Name;

        public RuntimeSlotSchema(uint slot, byte tag, string name)
        {
            Slot = slot;
            Tag = tag;
            Name = name;
        }
    }

    public sealed class AssetRecord
    {
        public string AssetName;
        public ushort ModuleMajor;
        public ushort ModuleMinor;
        public string[] StateNames = new string[0];
        public string EntryState;
        public RuntimeSlotSchema[] RuntimeSchema = new RuntimeSlotSchema[0];
        public int SourceHash;
        public long LoadedAtTick;
        public int InstanceCount;

        public AssetRecord Clone()
        {
            return new AssetRecord
            {
                AssetName = AssetName,
                ModuleMajor = ModuleMajor,
                ModuleMinor = ModuleMinor,
                StateNames = (string[])StateNames.Clone(),
                EntryState = EntryState,
                RuntimeSchema = (RuntimeSlotSchema[])RuntimeSchema.Clone(),
                SourceHash = SourceHash,
                LoadedAtTick = LoadedAtTick,
                InstanceCount = InstanceCount
            };
        }
    }

    public sealed class InstanceRecord
    {
        public int InstanceId;
        public string GameObjectName;
        public string GameObjectPath;
        public string AssetName;
        public string CurrentState;
        public int BoundSlotCount;
        public int RuntimeSlotCount;
        public long CreatedTick;
        public long TicksActive;
        public long TransitionsCount;
        public long CallCount;
        public bool IsPaused;
        public bool IsSuspended;

        public InstanceRecord Clone()
        {
            return (InstanceRecord)MemberwiseClone();
        }
    }

    public sealed class StateChangeEntry
    {
        public long Tick;
        public float Time;
        public int InstanceId;
        public string InstanceName;
        public string FromState;
        public string ToState;
        public string Reason;
    }

    public sealed class EmitEntry
    {
        public long Tick;
        public float Time;
        public int InstanceId;
        public string InstanceName;
        public int EventId;
    }

    public sealed class MainDatabase
    {
        public const int TimetableCapacity = 1024;
        public const int EmitLogCapacity = 256;

        private readonly Dictionary<string, AssetRecord> _assets =
            new Dictionary<string, AssetRecord>();
        private readonly Dictionary<int, InstanceRecord> _instances =
            new Dictionary<int, InstanceRecord>();
        private readonly List<StateChangeEntry> _timetable = new List<StateChangeEntry>();
        private readonly List<EmitEntry> _emits = new List<EmitEntry>();

        public long TotalTicks;
        public long TotalTransitions;

        // ----------------------------------------------------------
        // Writers (MainServer only)
        // ----------------------------------------------------------

        public void RegisterAsset(AssetRecord asset)
        {
            _assets[asset.AssetName] = asset;
        }

        public bool TryGetAsset(string name, out AssetRecord asset)
        {
            return _assets.TryGetValue(name, out asset);
        }

        public void RegisterInstance(InstanceRecord record)
        {
            _instances[record.InstanceId] = record;
            AssetRecord asset;
            if (_assets.TryGetValue(record.AssetName, out asset))
                asset.InstanceCount++;
        }

        public void UnregisterInstance(int instanceId)
        {
            InstanceRecord record;
            if (!_instances.TryGetValue(instanceId, out record)) return;
            _instances.Remove(instanceId);
            AssetRecord asset;
            if (_assets.TryGetValue(record.AssetName, out asset) && asset.InstanceCount > 0)
                asset.InstanceCount--;
        }

        public void OnInstanceTick(int instanceId, string state, long calls,
                                   bool paused, bool suspended)
        {
            InstanceRecord record;
            if (!_instances.TryGetValue(instanceId, out record)) return;
            record.CurrentState = state;
            record.CallCount = calls;
            record.IsPaused = paused;
            record.IsSuspended = suspended;
            if (!paused) record.TicksActive++;
        }

        public void RecordStateChange(StateChangeEntry entry)
        {
            if (_timetable.Count >= TimetableCapacity)
                _timetable.RemoveAt(0);
            _timetable.Add(entry);
            TotalTransitions++;
            InstanceRecord record;
            if (_instances.TryGetValue(entry.InstanceId, out record))
            {
                record.CurrentState = entry.ToState;
                record.TransitionsCount++;
            }
        }

        public void RecordEmit(EmitEntry entry)
        {
            if (_emits.Count >= EmitLogCapacity)
                _emits.RemoveAt(0);
            _emits.Add(entry);
        }

        // ----------------------------------------------------------
        // Snapshot readers (query server)
        // ----------------------------------------------------------

        public List<AssetRecord> SnapshotAssets()
        {
            List<AssetRecord> list = new List<AssetRecord>(_assets.Count);
            foreach (KeyValuePair<string, AssetRecord> kv in _assets)
                list.Add(kv.Value.Clone());
            return list;
        }

        public List<InstanceRecord> SnapshotInstances()
        {
            List<InstanceRecord> list = new List<InstanceRecord>(_instances.Count);
            foreach (KeyValuePair<int, InstanceRecord> kv in _instances)
                list.Add(kv.Value.Clone());
            return list;
        }

        public bool TrySnapshotInstance(int instanceId, out InstanceRecord record)
        {
            InstanceRecord live;
            if (_instances.TryGetValue(instanceId, out live))
            {
                record = live.Clone();
                return true;
            }
            record = null;
            return false;
        }

        /// <summary>Newest-first history (all instances when instanceId &lt; 0).</summary>
        public List<StateChangeEntry> SnapshotHistory(int instanceId, int maxCount)
        {
            List<StateChangeEntry> list = new List<StateChangeEntry>();
            for (int i = _timetable.Count - 1; i >= 0 && list.Count < maxCount; i--)
            {
                StateChangeEntry e = _timetable[i];
                if (instanceId >= 0 && e.InstanceId != instanceId) continue;
                list.Add(e);
            }
            return list;
        }

        public List<EmitEntry> SnapshotEmits(int maxCount)
        {
            List<EmitEntry> list = new List<EmitEntry>();
            for (int i = _emits.Count - 1; i >= 0 && list.Count < maxCount; i--)
                list.Add(_emits[i]);
            return list;
        }
    }
}
