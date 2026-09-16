// myFSM Unity Runtime — main server.
//
// Owns the registry of every GameObject with an AIInstance, the internal DB
// (asset + instance records, state-change timetable, emit log), and the two
// independent service servers: the query server (external systems use the AI
// system as a service) and the live broadcast servers (state changes pushed
// to subscribers). AIs tick themselves (own Update); the server only ticks
// the query scheduler in LateUpdate, after every AI has ticked.

using System;
using System.Collections.Generic;
using UnityEngine;
using MyFSM.Core;

namespace MyFSM.Unity
{
    public sealed class MainServer : MonoBehaviour, IQueryBackend
    {
        public static MainServer Instance { get; private set; }

        public readonly MainDatabase Db = new MainDatabase();
        public readonly OrderedBroadcastServer OrderedBroadcast = new OrderedBroadcastServer();
        public readonly PriorityBroadcastServer PriorityBroadcast = new PriorityBroadcastServer();

        public QueryServer Queries { get; private set; }
        public ITimeProvider TimeProvider = new UnityTimeProvider();

        private readonly List<AIInstance> _ais = new List<AIInstance>();
        private readonly Dictionary<int, AIInstance> _byId = new Dictionary<int, AIInstance>();
        private int _nextInstanceId = 1;

        public static MainServer EnsureExists()
        {
            if (Instance != null) return Instance;
            MainServer[] found = FindObjectsOfType<MainServer>();
            if (found != null && found.Length > 0)
            {
                Instance = found[0];
                if (Instance.Queries == null)
                    Instance.Queries = new QueryServer(Instance);
                return Instance;
            }
            GameObject go = new GameObject("MyFSM MainServer");
            Instance = go.AddComponent<MainServer>();
            return Instance;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            if (Queries == null)
                Queries = new QueryServer(this);
            DontDestroyOnLoad(gameObject);
        }

        // ----------------------------------------------------------
        // Registry
        // ----------------------------------------------------------

        public int AllocInstanceId()
        {
            return _nextInstanceId++;
        }

        public void RegisterAsset(string assetName, FsmbModule module, byte[] bytes)
        {
            AssetRecord record = new AssetRecord();
            record.AssetName = assetName;
            record.ModuleMajor = module.Major;
            record.ModuleMinor = module.Minor;
            record.StateNames = new string[module.States.Count];
            for (int i = 0; i < module.States.Count; i++)
                record.StateNames[i] = module.States[i].Name;
            for (int i = 0; i < module.States.Count; i++)
            {
                // Entry flag lives on the STATE token's single data byte.
                uint off;
                if (!module.AstIndexByAddress(module.States[i].RootAddr, out off)) continue;
                int idx = module.AstIndexAtOffset(off);
                if (idx >= 0 && module.Ast[idx].Data.Length == 1 &&
                    module.Ast[idx].Data[0] == 1)
                {
                    record.EntryState = module.States[i].Name;
                }
            }
            record.RuntimeSchema = new RuntimeSlotSchema[module.Runtime.Count];
            for (int i = 0; i < module.Runtime.Count; i++)
            {
                record.RuntimeSchema[i] = new RuntimeSlotSchema(
                    module.Runtime[i].BindingSlot, module.Runtime[i].Tag,
                    module.Runtime[i].Name);
            }
            record.SourceHash = HashBytes(bytes);
            record.LoadedAtTick = Db.TotalTicks;
            AssetRecord old;
            if (Db.TryGetAsset(assetName, out old))
                record.InstanceCount = old.InstanceCount;
            Db.RegisterAsset(record);
        }

        public void Register(AIInstance ai)
        {
            if (ai == null || _byId.ContainsKey(ai.InstanceId)) return;
            _ais.Add(ai);
            _byId[ai.InstanceId] = ai;
            InstanceRecord record = new InstanceRecord();
            record.InstanceId = ai.InstanceId;
            record.GameObjectName = ai.gameObject.name;
            record.GameObjectPath = BuildPath(ai.transform);
            record.AssetName = ai.ModuleName;
            record.CurrentState = ai.CurrentStateName;
            record.BoundSlotCount = ai.BoundSlotCount;
            record.RuntimeSlotCount = ai.Execution != null ? ai.Execution.Vars.RuntimeCount : 0;
            record.CreatedTick = Db.TotalTicks;
            Db.RegisterInstance(record);
        }

        public void Unregister(AIInstance ai)
        {
            if (ai == null) return;
            _ais.Remove(ai);
            _byId.Remove(ai.InstanceId);
            Db.UnregisterInstance(ai.InstanceId);
        }

        public bool TryGetAI(int instanceId, out AIInstance ai)
        {
            return _byId.TryGetValue(instanceId, out ai);
        }

        /// <summary>External systems connect through here, then Enqueue.</summary>
        public QueryClient ConnectExternal(string name)
        {
            return Queries.RegisterClient(name);
        }

        private static string BuildPath(Transform t)
        {
            string path = t.name;
            Transform p = t.parent;
            while (p != null)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }

        private static int HashBytes(byte[] bytes)
        {
            unchecked
            {
                const uint prime = 16777619u;
                uint h = 2166136261u;
                if (bytes != null)
                {
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        h ^= bytes[i];
                        h *= prime;
                    }
                }
                return (int)h;
            }
        }

        // ----------------------------------------------------------
        // Frame tick: the server never ticks AIs (each AI ticks itself
        // in its own Update). LateUpdate runs after EVERY AI's Update,
        // so the query scheduler always observes post-tick states.
        // ----------------------------------------------------------

        private void LateUpdate()
        {
            Db.TotalTicks++;
            Queries.Tick();
        }

        // ----------------------------------------------------------
        // Query backend: queries read snapshots, commands validate+apply
        // ----------------------------------------------------------

        public ServerResponse Execute(ServerRequest req)
        {
            if (req == null) return ServerResponse.Fail(req, "null request");
            if (req.IsCommand) return ExecuteCommand(req);
            return ExecuteQuery(req);
        }

        private ServerResponse ExecuteQuery(ServerRequest req)
        {
            switch ((QueryCode)req.Code)
            {
                case QueryCode.GetInstanceState:
                    return QueryInstanceState(req);
                case QueryCode.ListInstances:
                    return QueryListInstances(req);
                case QueryCode.GetAssetInfo:
                    return QueryAssetInfo(req);
                case QueryCode.ListAssets:
                    return QueryListAssets(req);
                case QueryCode.GetStateHistory:
                    return QueryHistory(req);
                case QueryCode.GetVariable:
                    return QueryVariable(req);
                case QueryCode.GetStats:
                    return QueryStats(req);
                case QueryCode.GetEmits:
                    return QueryEmits(req);
                default:
                    return ServerResponse.Fail(req, "unknown query code " + req.Code);
            }
        }

        private ServerResponse QueryInstanceState(ServerRequest req)
        {
            InstanceRecord record;
            if (!Db.TrySnapshotInstance(req.TargetInstanceId, out record))
                return ServerResponse.Fail(req, "unknown instance " + req.TargetInstanceId);
            ServerResponse resp = ServerResponse.Succeed(req, record.CurrentState);
            resp.Lines = new List<string>();
            resp.Lines.Add(DescribeInstance(record));
            return resp;
        }

        private ServerResponse QueryListInstances(ServerRequest req)
        {
            List<InstanceRecord> all = Db.SnapshotInstances();
            ServerResponse resp = ServerResponse.Succeed(req, all.Count + " instance(s)");
            resp.Lines = new List<string>();
            for (int i = 0; i < all.Count; i++)
                resp.Lines.Add(DescribeInstance(all[i]));
            return resp;
        }

        private static string DescribeInstance(InstanceRecord r)
        {
            return r.InstanceId + " | " + r.GameObjectPath + " | " + r.AssetName +
                   " | " + r.CurrentState + " | ticks " + r.TicksActive +
                   " | transitions " + r.TransitionsCount + " | calls " + r.CallCount +
                   (r.IsPaused ? " | paused" : string.Empty) +
                   (r.IsSuspended ? " | suspended" : string.Empty);
        }

        private ServerResponse QueryAssetInfo(ServerRequest req)
        {
            AssetRecord asset;
            if (!Db.TryGetAsset(req.TargetAsset ?? string.Empty, out asset))
                return ServerResponse.Fail(req, "unknown asset '" + req.TargetAsset + "'");
            ServerResponse resp = ServerResponse.Succeed(req, asset.AssetName);
            resp.Lines = new List<string>();
            resp.Lines.Add("module v" + asset.ModuleMajor + "." + asset.ModuleMinor +
                           " | entry " + asset.EntryState +
                           " | instances " + asset.InstanceCount);
            resp.Lines.Add("states: " + string.Join(", ", asset.StateNames));
            for (int i = 0; i < asset.RuntimeSchema.Length; i++)
            {
                RuntimeSlotSchema s = asset.RuntimeSchema[i];
                resp.Lines.Add("slot " + s.Slot + " " + DslTypes.NameOf(s.Tag) + " " + s.Name);
            }
            return resp;
        }

        private ServerResponse QueryListAssets(ServerRequest req)
        {
            List<AssetRecord> all = Db.SnapshotAssets();
            ServerResponse resp = ServerResponse.Succeed(req, all.Count + " asset(s)");
            resp.Lines = new List<string>();
            for (int i = 0; i < all.Count; i++)
            {
                resp.Lines.Add(all[i].AssetName + " v" + all[i].ModuleMajor + "." +
                               all[i].ModuleMinor + " | states " + all[i].StateNames.Length +
                               " | instances " + all[i].InstanceCount);
            }
            return resp;
        }

        private ServerResponse QueryHistory(ServerRequest req)
        {
            int max = req.IntArg <= 0 ? 20 : Math.Min(req.IntArg, 200);
            List<StateChangeEntry> history = Db.SnapshotHistory(req.TargetInstanceId, max);
            ServerResponse resp = ServerResponse.Succeed(req, history.Count + " entries");
            resp.Lines = new List<string>();
            for (int i = 0; i < history.Count; i++)
            {
                StateChangeEntry e = history[i];
                resp.Lines.Add("t" + e.Tick + " " + e.Time.ToString("F2") + " #" +
                               e.InstanceId + " '" + e.FromState + "' -> '" + e.ToState +
                               "' (" + e.Reason + ")");
            }
            return resp;
        }

        private ServerResponse QueryVariable(ServerRequest req)
        {
            AIInstance ai;
            if (!TryGetAI(req.TargetInstanceId, out ai) || !ai.Booted)
                return ServerResponse.Fail(req, "unknown instance " + req.TargetInstanceId);
            FsmValue value;
            if (!ai.TryGetVariable(req.StringArg ?? string.Empty, out value))
                return ServerResponse.Fail(req, "no variable '" + req.StringArg +
                    "' (alive) on instance " + req.TargetInstanceId);
            ServerResponse resp = ServerResponse.Succeed(req, value.ToString());
            resp.Value = value;
            return resp;
        }

        private ServerResponse QueryStats(ServerRequest req)
        {
            List<InstanceRecord> all = Db.SnapshotInstances();
            ServerResponse resp = ServerResponse.Succeed(req, "ok");
            resp.Lines = new List<string>();
            resp.Lines.Add("ticks " + Db.TotalTicks + " | transitions " +
                           Db.TotalTransitions + " | instances " + all.Count +
                           " | ready " + Queries.ReadyCount + "/" + Queries.ReadyTarget +
                           " M=" + Queries.RequestsPerClient +
                           " | long-term " + Queries.LongTermCount +
                           " | ordered-subs " + OrderedBroadcast.SubscriberCount +
                           " | priority-subs " + PriorityBroadcast.SubscriberCount);
            for (int i = 0; i < all.Count; i++)
                resp.Lines.Add(DescribeInstance(all[i]));
            return resp;
        }

        private ServerResponse QueryEmits(ServerRequest req)
        {
            int max = req.IntArg <= 0 ? 20 : Math.Min(req.IntArg, 200);
            List<EmitEntry> emits = Db.SnapshotEmits(max);
            ServerResponse resp = ServerResponse.Succeed(req, emits.Count + " emit(s)");
            resp.Lines = new List<string>();
            for (int i = 0; i < emits.Count; i++)
            {
                EmitEntry e = emits[i];
                resp.Lines.Add("t" + e.Tick + " " + e.Time.ToString("F2") + " #" +
                               e.InstanceId + " event " + e.EventId);
            }
            return resp;
        }

        private ServerResponse ExecuteCommand(ServerRequest req)
        {
            switch ((CommandCode)req.Code)
            {
                case CommandCode.TransitionTo:
                    return CommandTransitionTo(req);
                case CommandCode.SetVariable:
                    return CommandSetVariable(req);
                case CommandCode.Emit:
                    return CommandEmit(req);
                case CommandCode.PauseAI:
                    return CommandPause(req, true);
                case CommandCode.ResumeAI:
                    return CommandPause(req, false);
                case CommandCode.ReloadModule:
                    return CommandReloadModule(req);
                default:
                    return ServerResponse.Fail(req, "unknown command code " + req.Code);
            }
        }

        private ServerResponse CommandTransitionTo(ServerRequest req)
        {
            AIInstance ai;
            if (!TryGetAI(req.TargetInstanceId, out ai) || !ai.Booted)
                return ServerResponse.Fail(req, "unknown instance " + req.TargetInstanceId);
            int target = -1;
            for (int i = 0; i < ai.Execution.Module.States.Count; i++)
            {
                if (ai.Execution.Module.States[i].Name == req.StringArg)
                {
                    target = i;
                    break;
                }
            }
            if (target < 0)
                return ServerResponse.Fail(req, "unknown state '" + req.StringArg + "'");
            ai.Execution.States.RequestExternalTransition(target, "query client #" + req.ClientId);
            return ServerResponse.Succeed(req, "commanded '" + req.StringArg +
                "' (honored next tick)");
        }

        private ServerResponse CommandSetVariable(ServerRequest req)
        {
            AIInstance ai;
            if (!TryGetAI(req.TargetInstanceId, out ai) || !ai.Booted)
                return ServerResponse.Fail(req, "unknown instance " + req.TargetInstanceId);
            string error;
            if (!ai.TrySetVariable(req.StringArg ?? string.Empty, req.ValueArg, out error))
                return ServerResponse.Fail(req, error);
            return ServerResponse.Succeed(req, req.StringArg + " = " + req.ValueArg);
        }

        private ServerResponse CommandEmit(ServerRequest req)
        {
            AIInstance ai;
            if (!TryGetAI(req.TargetInstanceId, out ai) || !ai.Booted)
                return ServerResponse.Fail(req, "unknown instance " + req.TargetInstanceId);
            Db.RecordEmit(new EmitEntry
            {
                Tick = Db.TotalTicks,
                Time = TimeProvider.Time,
                InstanceId = ai.InstanceId,
                InstanceName = ai.DisplayName,
                EventId = req.IntArg
            });
            return ServerResponse.Succeed(req, "emitted " + req.IntArg);
        }

        private ServerResponse CommandPause(ServerRequest req, bool pause)
        {
            AIInstance ai;
            if (!TryGetAI(req.TargetInstanceId, out ai) || !ai.Booted)
                return ServerResponse.Fail(req, "unknown instance " + req.TargetInstanceId);
            ai.Paused = pause;
            return ServerResponse.Succeed(req, pause ? "paused" : "resumed");
        }

        private ServerResponse CommandReloadModule(ServerRequest req)
        {
            if (string.IsNullOrEmpty(req.TargetAsset))
                return ServerResponse.Fail(req, "missing asset name");
            if (req.BytesArg == null || req.BytesArg.Length == 0)
                return ServerResponse.Fail(req, "missing module bytes");
            AssetRecord asset;
            if (!Db.TryGetAsset(req.TargetAsset, out asset))
                return ServerResponse.Fail(req, "unknown asset '" + req.TargetAsset + "'");
            FsmbModule m;
            string error;
            if (!FsmbReader.Read(req.BytesArg, out m, out error))
                return ServerResponse.Fail(req, error);
            if (!FsmbReader.Validate(m, out error))
                return ServerResponse.Fail(req, error);
            if (m.States.Count != asset.StateNames.Length)
                return ServerResponse.Fail(req, "schema mismatch: state count changed");
            for (int i = 0; i < m.States.Count; i++)
            {
                if (m.States[i].Name != asset.StateNames[i])
                    return ServerResponse.Fail(req, "schema mismatch: state '" +
                        asset.StateNames[i] + "' changed");
            }
            if (m.Runtime.Count != asset.RuntimeSchema.Length)
                return ServerResponse.Fail(req, "schema mismatch: runtime slots changed");
            for (int i = 0; i < m.Runtime.Count; i++)
            {
                RuntimeSlotSchema s = asset.RuntimeSchema[i];
                if (m.Runtime[i].BindingSlot != s.Slot || m.Runtime[i].Tag != s.Tag ||
                    m.Runtime[i].Name != s.Name)
                {
                    return ServerResponse.Fail(req, "schema mismatch: slot " + s.Slot +
                        " changed (rebind by restarting instead)");
                }
            }
            int reloaded = 0;
            List<string> failures = new List<string>();
            AIInstance[] snapshot = _ais.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] == null || snapshot[i].ModuleName != req.TargetAsset)
                    continue;
                if (snapshot[i].RebootWithBytes(req.BytesArg))
                    reloaded++;
                else
                    failures.Add("#" + snapshot[i].InstanceId + ": " + snapshot[i].BootError);
            }
            RegisterAsset(req.TargetAsset, m, req.BytesArg);
            if (failures.Count > 0)
                return ServerResponse.Fail(req, "reloaded " + reloaded + ", failed: " +
                    string.Join("; ", failures.ToArray()));
            return ServerResponse.Succeed(req, "reloaded " + reloaded + " instance(s)");
        }
    }
}
