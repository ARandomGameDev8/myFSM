// myFSM Unity Runtime — AIInstance: the MonoBehaviour every AI extends.
//
// One .fsmb module loaded onto one GameObject is one AI. Generated classes
// (see ClassGenerator) inherit from here and only pin the module's resource
// path plus state/slot name constants; FsmbAIInstance is the generic,
// inspector-driven version of the same thing.
//
// Lifecycle: Unity Start() -> BootFromAsset() -> MainServer registry.
// Each AI then ticks ITSELF in its own Update(): movement advance first
// (fresh positions for decisions), then the Update + Traversals rounds,
// then its own DB/broadcast bookkeeping. The main server never ticks AIs;
// it only keeps the registry and runs the query scheduler in LateUpdate
// (after every AI). Bindings must be applied before boot because the entry
// state's Start{} runs on the very first Update; the journal replays them
// across hot reloads.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using MyFSM.Core;

namespace MyFSM.Unity
{
    public sealed class UnityTimeProvider : ITimeProvider
    {
        public float Time { get { return UnityEngine.Time.time; } }
        public float DeltaTime { get { return UnityEngine.Time.deltaTime; } }
    }

    public sealed class UnityExecutionLog : IExecutionLog
    {
        private readonly string _prefix;

        public UnityExecutionLog(string prefix)
        {
            _prefix = prefix;
        }

        public void Info(string message) { Debug.Log(_prefix + message); }
        public void Warn(string message) { Debug.LogWarning(_prefix + message); }
        public void Error(string message) { Debug.LogError(_prefix + message); }
    }

    public abstract class AIInstance : MonoBehaviour
    {
        [SerializeField] private TextAsset _moduleAsset;
        [SerializeField] private List<UnityEngine.Object> _slotBindings =
            new List<UnityEngine.Object>();
        [SerializeField] private string _moduleNameOverride = string.Empty;

        public AiExecution Execution { get; private set; }
        public HandleTable Handles { get; private set; }
        public MovementSystem Movement { get; private set; }
        public PathTable Paths { get; private set; }
        public FunctionDispatcher Dispatcher { get; private set; }
        public int InstanceId { get; private set; }
        public bool Booted { get; private set; }
        public bool Paused { get; set; }
        public string ModuleName { get; private set; }
        public string BootError { get; private set; }

        /// <summary>Generated classes override this with the module's
        /// Resources path (used when no inspector asset is assigned).</summary>
        protected virtual string ModuleResourcePath { get { return null; } }

        /// <summary>Generated classes override this with the module name
        /// (used when no override/asset name is available).</summary>
        protected virtual string GeneratedModuleName { get { return null; } }

        public string CurrentStateName
        {
            get { return Execution != null ? Execution.States.CurrentStateName : "<none>"; }
        }

        public string DisplayName
        {
            get { return (ModuleName ?? "AI") + "@" + gameObject.name; }
        }

        private struct BindingEntry
        {
            public uint Slot;
            public UnityEngine.Object Obj;
            public bool HasValue;
            public FsmValue Value;
        }

        private readonly List<BindingEntry> _journal = new List<BindingEntry>();

        public int BoundSlotCount
        {
            get
            {
                HashSet<uint> seen = new HashSet<uint>();
                for (int i = 0; i < _journal.Count; i++)
                    seen.Add(_journal[i].Slot);
                return seen.Count;
            }
        }

        protected virtual void Start()
        {
            if (Booted) return; // already booted in code (tests, prewarm)
            if (!BootFromAsset())
                enabled = false;
        }

        protected virtual void OnDestroy()
        {
            if (Booted && MainServer.Instance != null)
                MainServer.Instance.Unregister(this);
            Booted = false;
        }

        /// <summary>
        /// Hook for generated classes: bind scene objects / values in code
        /// here. Runs after the inspector bindings, before boot (the entry
        /// Start{} runs later, on the first Update).
        /// </summary>
        protected virtual void OnBindingsRequired()
        {
        }

        // ----------------------------------------------------------
        // Boot
        // ----------------------------------------------------------

        public bool BootFromAsset()
        {
            TextAsset asset = _moduleAsset;
            if (asset == null && ModuleResourcePath != null)
                asset = Resources.Load<TextAsset>(ModuleResourcePath);
            if (asset == null)
            {
                FailBoot("no .fsmb module asset assigned");
                return false;
            }
            string name = !string.IsNullOrEmpty(_moduleNameOverride)
                ? _moduleNameOverride
                : (GeneratedModuleName ?? asset.name);
            return BootWithBytes(asset.bytes, name);
        }

        public bool BootWithBytes(byte[] bytes, string moduleName)
        {
            return BootCore(bytes, moduleName, -1, false);
        }

        /// <summary>
        /// Hot-reloads a new module onto this AI (query-server ReloadModule).
        /// Replays the binding journal so rebinding survives; the AI re-enters
        /// its entry state. The instance id stays stable.
        /// </summary>
        public bool RebootWithBytes(byte[] bytes)
        {
            MainServer main = MainServer.EnsureExists();
            if (Booted) main.Unregister(this);
            Booted = false;
            Execution = null;
            return BootCore(bytes, ModuleName, InstanceId, true);
        }

        private bool BootCore(byte[] bytes, string moduleName, int keepId, bool replayOnly)
        {
            if (Booted)
            {
                BootError = "already booted (use RebootWithBytes to hot-reload)";
                return false;
            }
            BootError = null;
            if (bytes == null || bytes.Length == 0)
            {
                FailBoot("empty module bytes");
                return false;
            }
            FsmbModule module;
            string readError;
            if (!FsmbReader.Read(bytes, out module, out readError))
            {
                FailBoot(readError);
                return false;
            }
            string validError;
            if (!FsmbReader.Validate(module, out validError))
            {
                FailBoot(validError);
                return false;
            }

            MainServer main = MainServer.EnsureExists();
            InstanceId = keepId > 0 ? keepId : main.AllocInstanceId();
            ModuleName = moduleName ?? "unnamed";
            Handles = new HandleTable();
            Movement = new MovementSystem();
            Paths = new PathTable();
            Dispatcher = new FunctionDispatcher(this);
            UnityExecutionLog log = new UnityExecutionLog("[myFSM " + DisplayName + "] ");
            string createError;
            Execution = AiExecution.Create(module, ModuleName, InstanceId, DisplayName,
                                           Dispatcher, main.TimeProvider, log, out createError);
            if (Execution == null)
            {
                FailBoot(createError);
                return false;
            }

            // Base binding layer, fresh every boot (fresh + reboot), never
            // journaled: unique handle slots bind to this GameObject; the
            // inspector list / replayed journal / OnBindingsRequired apply on
            // top of it and win.
            AutoBindUnambiguousHandles();

            if (replayOnly)
            {
                for (int i = 0; i < _journal.Count; i++)
                    ApplyBinding(_journal[i]);
            }
            else
            {
                for (int i = 0; i < _slotBindings.Count; i++)
                {
                    if (_slotBindings[i] != null)
                        Bind(i, _slotBindings[i]);
                }
                OnBindingsRequired();
            }

            string bootError;
            if (!Execution.Boot(out bootError))
            {
                FailBoot(bootError);
                return false;
            }

            main.RegisterAsset(ModuleName, module, bytes);
            main.Register(this);
            Booted = true;
            return true;
        }

        private void FailBoot(string message)
        {
            BootError = message;
            Execution = null;
            Booted = false;
            Debug.LogError("[myFSM " + gameObject.name + "] boot failed: " + message);
        }

        // ----------------------------------------------------------
        // Bindings (the Controller pushing externals in)
        // ----------------------------------------------------------

        /// <summary>Binds a Unity object to a handle-typed runtime slot.</summary>
        public bool Bind(int slot, UnityEngine.Object obj)
        {
            if (Execution == null)
            {
                Debug.LogError("[myFSM] Bind before execution exists");
                return false;
            }
            int entry;
            if (!Execution.Vars.TryEntryForSlot((uint)slot, out entry))
            {
                Execution.Log.Error("no runtime slot " + slot);
                return false;
            }
            byte tag = Execution.Vars.GetRuntimeTag(entry);
            DslTypeInfo info;
            if (!DslTypes.TryFindByTag(tag, out info) || !info.IsHandle)
            {
                Execution.Log.Error("slot " + slot + " ('" +
                                    Execution.Vars.GetRuntimeName(entry) +
                                    "') is a value slot; use SetBoundValue");
                return false;
            }
            if (obj == null)
            {
                Execution.Log.Error("cannot bind null to slot " + slot);
                return false;
            }
            BindingEntry e = new BindingEntry();
            e.Slot = (uint)slot;
            e.Obj = obj;
            e.HasValue = false;
            _journal.Add(e);
            ApplyBinding(e);
            return true;
        }

        /// <summary>Pushes a value into a value-typed runtime slot.</summary>
        public bool SetBoundValue(int slot, FsmValue value)
        {
            if (Execution == null)
            {
                Debug.LogError("[myFSM] SetBoundValue before execution exists");
                return false;
            }
            int entry;
            if (!Execution.Vars.TryEntryForSlot((uint)slot, out entry))
            {
                Execution.Log.Error("no runtime slot " + slot);
                return false;
            }
            byte tag = Execution.Vars.GetRuntimeTag(entry);
            DslTypeInfo info;
            if (!DslTypes.TryFindByTag(tag, out info) || info.IsHandle)
            {
                Execution.Log.Error("slot " + slot + " ('" +
                                    Execution.Vars.GetRuntimeName(entry) +
                                    "') is a handle slot; use Bind");
                return false;
            }
            if (!AssignCallback.ValueMatchesTag(value, tag))
            {
                Execution.Log.Error("cannot bind " + value.Kind + " to slot " + slot +
                                    " (" + DslTypes.NameOf(tag) + ")");
                return false;
            }
            BindingEntry e = new BindingEntry();
            e.Slot = (uint)slot;
            e.HasValue = true;
            e.Value = value;
            _journal.Add(e);
            ApplyBinding(e);
            return true;
        }

        private void ApplyBinding(BindingEntry e)
        {
            int entry;
            if (!Execution.Vars.TryEntryForSlot(e.Slot, out entry)) return;
            if (e.HasValue)
            {
                Execution.Vars.SetRuntime(entry, e.Value);
                return;
            }
            byte tag = Execution.Vars.GetRuntimeTag(entry);
            int id = Handles.Alloc(e.Obj, tag);
            Execution.Vars.SetRuntime(entry, FsmValue.MakeHandle(tag, id));
        }

        /// <summary>
        /// Auto-bind kill switch. When true (default), every boot first binds
        /// each UNIQUE handle-typed runtime slot to this GameObject (see
        /// ResolveAutoBindTarget). Tags claimed by 2+ slots are ambiguous and
        /// left for manual binding; value slots are never auto-bound.
        /// </summary>
        protected virtual bool AutoBindHandles { get { return true; } }

        private void AutoBindUnambiguousHandles()
        {
            if (!AutoBindHandles || Execution == null) return;
            VariableTable vars = Execution.Vars;
            Dictionary<byte, int> perTag = new Dictionary<byte, int>();
            for (int i = 0; i < vars.RuntimeCount; i++)
            {
                byte tag = vars.GetRuntimeTag(i);
                int n;
                perTag.TryGetValue(tag, out n);
                perTag[tag] = n + 1;
            }
            HashSet<byte> warned = new HashSet<byte>();
            for (int i = 0; i < vars.RuntimeCount; i++)
            {
                byte tag = vars.GetRuntimeTag(i);
                DslTypeInfo info;
                if (!DslTypes.TryFindByTag(tag, out info) || !info.IsHandle)
                    continue; // value slots have no auto-bind rule
                if (perTag[tag] != 1)
                {
                    if (!warned.Contains(tag))
                    {
                        warned.Add(tag);
                        Execution.Log.Warn("slot type '" + info.Name + "' appears " +
                            perTag[tag] + "x; ambiguous, bind manually " +
                            "(inspector, OnBindingsRequired or OnBindingsManual)");
                    }
                    continue;
                }
                UnityEngine.Object target = ResolveAutoBindTarget(tag);
                if (target == null)
                {
                    Execution.Log.Info("slot " + vars.GetRuntimeSlot(i) + " ('" +
                        vars.GetRuntimeName(i) + "', " + info.Name +
                        ") has no host component to auto-bind; bind manually");
                    continue;
                }
                BindingEntry e = new BindingEntry();
                e.Slot = vars.GetRuntimeSlot(i);
                e.Obj = target;
                e.HasValue = false;
                ApplyBinding(e); // base layer: NOT journaled (see BootCore)
                Execution.Log.Info("auto-bound slot " + e.Slot + " ('" +
                    vars.GetRuntimeName(i) + "', " + info.Name + ")");
            }
        }

        /// <summary>
        /// Host-GameObject source per handle tag, or null when the host has
        /// nothing suitable (the caller logs + skips, leaving a null handle).
        /// </summary>
        private UnityEngine.Object ResolveAutoBindTarget(byte tag)
        {
            if (tag == FsmbType.Object2D || tag == FsmbType.Object3D)
                return gameObject;
            if (tag == FsmbType.Transform2D || tag == FsmbType.Transform3D)
                return transform;
            if (tag == FsmbType.Camera2D || tag == FsmbType.Camera3D)
                return GetComponent<Camera>();
            if (tag == FsmbType.Sprite2D || tag == FsmbType.Sprite3D)
                return GetComponent<SpriteRenderer>();
            if (tag == FsmbType.AnimationController2D || tag == FsmbType.AnimationController3D)
                return GetComponent<Animator>();
            if (tag == FsmbType.PhysicsObject2D)
            {
                Rigidbody2D rb = GetComponent<Rigidbody2D>();
                if (rb != null) return rb;
                return GetComponent<Collider2D>();
            }
            if (tag == FsmbType.PhysicsObject3D)
            {
                Rigidbody rb = GetComponent<Rigidbody>();
                if (rb != null) return rb;
                return GetComponent<Collider>();
            }
            if (tag == FsmbType.NavMeshAgent)
                return GetComponent<NavMeshAgent>();
            return null;
        }

        // ----------------------------------------------------------
        // Variable access (queries, debugging, game code)
        // ----------------------------------------------------------

        public bool TryGetVariable(string name, out FsmValue value)
        {
            value = FsmValue.Void;
            if (Execution == null) return false;
            VariableTable vars = Execution.Vars;
            for (int i = 0; i < vars.ConstCount; i++)
            {
                if (vars.GetConstName(i) == name)
                {
                    value = vars.GetConst(i);
                    return true;
                }
            }
            for (int i = 0; i < vars.RuntimeCount; i++)
            {
                if (vars.GetRuntimeName(i) == name)
                {
                    value = vars.GetRuntime(i);
                    return true;
                }
            }
            for (int i = 0; i < vars.TempCount; i++)
            {
                if (vars.IsTempAlive(i) && vars.GetTempName(i) == name)
                {
                    value = vars.GetTemp(i);
                    return true;
                }
            }
            return false;
        }

        public bool TrySetVariable(string name, FsmValue value, out string error)
        {
            error = null;
            if (Execution == null)
            {
                error = "AI is not booted";
                return false;
            }
            VariableTable vars = Execution.Vars;
            for (int i = 0; i < vars.RuntimeCount; i++)
            {
                if (vars.GetRuntimeName(i) == name)
                {
                    byte tag = vars.GetRuntimeTag(i);
                    if (!AssignCallback.ValueMatchesTag(value, tag))
                    {
                        error = "cannot assign " + value.Kind + " to '" + name +
                                "' (" + DslTypes.NameOf(tag) + ")";
                        return false;
                    }
                    vars.SetRuntime(i, value);
                    return true;
                }
            }
            error = "no writable runtime variable '" + name + "'";
            return false;
        }

        // ----------------------------------------------------------
        // Per-frame (self-ticked: Unity calls Update() on every AI;
        // the main server keeps the registry, never ticks AIs)
        // ----------------------------------------------------------

        /// <summary>
        /// The AI's own frame tick: movement advance (fresh positions for
        /// this tick's decisions), then the Update + Traversals rounds with
        /// head-state transitions, then this AI's DB/broadcast bookkeeping.
        /// Subclasses overriding this MUST call base.Update() or the AI
        /// silently stops ticking (Unity invokes only the most-derived
        /// Update). Prefer Paused / enabled=false over overriding.
        /// </summary>
        protected virtual void Update()
        {
            TickInternal();
        }

        internal StateChangeInfo TickInternal()
        {
            if (!Booted || Execution == null) return null;
            MainServer main = MainServer.Instance;
            long calls = Execution.CallCount;
            if (Paused)
            {
                if (main != null)
                    main.Db.OnInstanceTick(InstanceId, CurrentStateName, calls, true, false);
                return null;
            }
            // Fresh positions for this tick's decisions, then serve.
            Movement.Advance(Execution.Time.DeltaTime, Dispatcher, Execution);
            StateChangeInfo change;
            if (!Execution.Tick(out change)) return null;
            if (main != null)
            {
                if (change != null)
                {
                    main.Db.RecordStateChange(new StateChangeEntry
                    {
                        Tick = main.Db.TotalTicks,
                        Time = main.TimeProvider.Time,
                        InstanceId = InstanceId,
                        InstanceName = DisplayName,
                        FromState = change.FromName,
                        ToState = change.ToName,
                        Reason = change.Reason
                    });
                    StateChangeEvent e = new StateChangeEvent
                    {
                        Tick = main.Db.TotalTicks,
                        Time = main.TimeProvider.Time,
                        InstanceId = InstanceId,
                        InstanceName = DisplayName,
                        FromState = change.FromName,
                        ToState = change.ToName,
                        Reason = change.Reason
                    };
                    main.OrderedBroadcast.Publish(e);
                    main.PriorityBroadcast.Publish(e);
                }
                bool suspended = Execution.IsSuspended;
                main.Db.OnInstanceTick(InstanceId, CurrentStateName, calls, false, suspended);
            }
            return change;
        }
    }

    /// <summary>
    /// The generic, inspector-driven AI: assign a .fsmb TextAsset + bindings,
    /// no codegen needed. Generated per-module classes do the same with the
    /// module path pinned and state/slot name constants.
    /// </summary>
    public sealed class FsmbAIInstance : AIInstance
    {
    }
}
