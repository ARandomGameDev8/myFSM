// myFSM sandbox smoke test: exercises the Runtime headless (stub engine).
// Usage: dotnet run -- <vectors-dir>
// Sections: reader+validator on real compiler output, Core execution with a
// logging dispatcher, query-server scheduling rules, broadcast servers, the
// class generator, and an end-to-end AIInstance run on the real dispatcher.
using System;
using System.Collections.Generic;
using System.IO;
using MyFSM.Core;
using MyFSM.Unity;
using UnityEngine;

public sealed class ManualTime : ITimeProvider
{
    public float Time { get; set; }
    public float DeltaTime { get; set; }
}

public sealed class QuietLog : IExecutionLog
{
    public int Errors;
    public List<string> Lines = new List<string>();

    public void Info(string message) { Lines.Add("[i] " + message); }
    public void Warn(string message) { Lines.Add("[w] " + message); }
    public void Error(string message) { Errors++; Lines.Add("[E] " + message); }
}

public sealed class StubDispatcher : IFunctionDispatcher
{
    public int Calls;
    private readonly QuietLog _log;

    public StubDispatcher(QuietLog log) { _log = log; }

    public FsmValue Dispatch(ushort id, FsmValue[] args, AiExecution exec)
    {
        Calls++;
        FunctionOverload o = FunctionCatalog.FindById(id);
        if (o == null)
        {
            _log.Error("stub: unknown id " + id);
            return FsmValue.Void;
        }
        _log.Info("stub call " + o.ToString());
        if (o.ReturnType == "void") return FsmValue.Void;
        DslTypeInfo t;
        if (DslTypes.TryFindByName(o.ReturnType, out t))
            return FsmValue.DefaultForTag(t.Tag);
        return FsmValue.Void;
    }
}

public sealed class FakeBackend : IQueryBackend
{
    public QueryServer Server;
    public int Executed;

    public ServerResponse Execute(ServerRequest req)
    {
        Executed++;
        // Re-entrant enqueue from inside the serving client's own slice:
        // the near rule must defer it to the next tick, never serve inline.
        if (req.ClientId == 1 && req.Code == 999 && Server != null)
        {
            ServerRequest r2 = new ServerRequest();
            r2.IsCommand = false;
            r2.Code = 1000;
            Server.Enqueue(1, r2);
        }
        return ServerResponse.Succeed(req, "done-" + req.Code);
    }
}

public sealed class TestVerdictSub : PriorityStateSubscriber
{
    public PriorityVerdict Mode;
    private readonly Action _cb;

    public TestVerdictSub(int target, int priority, PriorityVerdict mode, Action cb)
        : base(target, priority)
    {
        Mode = mode;
        _cb = cb;
    }

    public override void Notify(StateChangeEvent e)
    {
        if (_cb != null) _cb();
        NextVerdict = Mode;
    }
}

public static class SmokeTest
{
    private static int _fails;

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _fails++;
    }

    public static int Main(string[] argv)
    {
        string dir = argv.Length > 0 ? argv[0] : "Vectors";
        string[] files = Directory.GetFiles(dir, "*.fsmb");
        Array.Sort(files);
        Console.WriteLine("vectors: " + files.Length);
        Check(files.Length > 0, "vectors present");
        Check(FunctionCatalog.All.Length == 179, "catalog has 179 overloads");

        TestReader(files);
        TestCoreExecution(files);
        TestQueryServer();
        TestBroadcast();
        TestGenerator(files);
        TestAIInstance(dir);
        TestPathMapping();

        Console.WriteLine(_fails == 0 ? "SMOKE OK" : "SMOKE FAILED (" + _fails + ")");
        return _fails == 0 ? 0 : 1;
    }

    // -- 1. reader: parse + validate genuine compiler output; reject garbage.
    private static void TestReader(string[] files)
    {
        foreach (string f in files)
        {
            byte[] bytes = File.ReadAllBytes(f);
            string err;
            FsmbModule m;
            bool ok = FsmbReader.Read(bytes, out m, out err);
            bool valid = ok && FsmbReader.Validate(m, out err);
            Console.WriteLine(Path.GetFileName(f) + ": states=" + m.States.Count +
                " runtime=" + m.Runtime.Count + " consts=" + m.Globals.Count +
                " temps=" + m.Temps.Count + " ast=" + m.Ast.Count +
                " frames=" + m.StateInstrs.Count + " fsm=" + m.Fsm.Count);
            Check(valid, "read+validate " + Path.GetFileName(f));
        }
        string e2;
        FsmbModule g;
        Check(!FsmbReader.Read(new byte[16], out g, out e2), "reject truncated file");
        byte[] bad = File.ReadAllBytes(files[0]);
        bad[0] = (byte)~bad[0];
        Check(!FsmbReader.Read(bad, out g, out e2), "reject corrupt magic");
    }

    // -- 2. Core: boot each vector with a logging dispatcher, tick 12 frames.
    private static void TestCoreExecution(string[] files)
    {
        for (int i = 0; i < files.Length; i++)
        {
            byte[] bytes = File.ReadAllBytes(files[i]);
            string err;
            FsmbModule m;
            FsmbReader.Read(bytes, out m, out err);
            QuietLog log = new QuietLog();
            ManualTime time = new ManualTime();
            time.Time = 0f;
            time.DeltaTime = 1f / 60f;
            StubDispatcher d = new StubDispatcher(log);
            AiExecution exec = AiExecution.Create(m, "Smoke" + i, 100 + i, "Smoke" + i,
                                                  d, time, log, out err);
            Check(exec != null, "create " + Path.GetFileName(files[i]));
            if (exec == null) { Console.WriteLine("    " + err); continue; }
            Check(exec.Boot(out err), "boot " + Path.GetFileName(files[i]));
            bool tickOk = true;
            for (int t = 0; t < 12; t++)
            {
                time.Time += time.DeltaTime;
                StateChangeInfo change;
                if (!exec.Tick(out change)) tickOk = false;
            }
            Check(tickOk, "tick x12 " + Path.GetFileName(files[i]));
            Console.WriteLine("    head=" + exec.States.CurrentStateName + " calls=" + d.Calls +
                " logErrors=" + log.Errors);
        }
    }

    // -- 3. query server: near deferral, caps, congestion growth.
    private static void TestQueryServer()
    {
        FakeBackend backend = new FakeBackend();
        QueryServer s = new QueryServer(backend);
        backend.Server = s;

        QueryClient c1 = s.RegisterClient("near");
        ServerRequest q = new ServerRequest();
        q.IsCommand = false;
        q.Code = 999;
        Check(s.Enqueue(c1.ClientId, q), "enqueue near-probe");
        s.Tick();
        Check(Drain(c1) == 1 && backend.Executed == 1, "near re-enqueue deferred a tick");
        s.Tick();
        Check(Drain(c1) == 1 && backend.Executed == 2, "deferred request served next tick");

        QueryClient cap = s.RegisterClient("cap");
        bool filled = true;
        for (int i = 0; i < 15; i++)
        {
            ServerRequest r = new ServerRequest();
            r.IsCommand = false;
            r.Code = 200 + i;
            filled &= s.Enqueue(cap.ClientId, r);
        }
        ServerRequest over = new ServerRequest();
        over.IsCommand = false;
        over.Code = 216;
        bool rejected = !s.Enqueue(cap.ClientId, over);
        Check(filled && rejected, "client cap 15 enforced");
        int ok = 0;
        int fail = 0;
        for (int t = 0; t < 12; t++)
        {
            s.Tick();
            ServerResponse resp;
            while (cap.TryTakeResponse(out resp))
            {
                if (resp.Ok) ok++; else fail++;
            }
        }
        Check(ok == 15 && fail == 1, "cap drain 15 ok + 1 fail (got " + ok + "/" + fail + ")");

        QueryServer big = new QueryServer(backend);
        for (int i = 0; i < 40; i++)
        {
            QueryClient c = big.RegisterClient("load" + i);
            for (int k = 0; k < 5; k++)
            {
                ServerRequest r = new ServerRequest();
                r.IsCommand = false;
                r.Code = k;
                big.Enqueue(c.ClientId, r);
            }
        }
        int before = big.ReadyTarget;
        big.Tick();
        Check(big.ReadyTarget > before, "congestion grows N (" + before + "->" + big.ReadyTarget + ")");
    }

    private static int Drain(QueryClient c)
    {
        int n = 0;
        ServerResponse r;
        while (c.TryTakeResponse(out r)) n++;
        return n;
    }

    // -- 4. broadcasts: ordered order, priority order + verdicts.
    private static void TestBroadcast()
    {
        List<string> order = new List<string>();
        OrderedBroadcastServer o = new OrderedBroadcastServer();
        o.Subscribe(new ActionSubscriber(1, delegate { order.Add("A"); }));
        o.Subscribe(new ActionWithStateSubscriber(1, delegate(string s) { order.Add("B:" + s); }));
        o.Subscribe(new ActionSubscriber(StateSubscriber.AnyTarget, delegate { order.Add("C"); }));
        StateChangeEvent e = new StateChangeEvent();
        e.InstanceId = 1;
        e.ToState = "Chase";
        o.Publish(e);
        Check(order.Count == 3 && order[0] == "A" && order[1] == "B:Chase" && order[2] == "C",
            "ordered serves target list then any-target");

        List<string> calls = new List<string>();
        PriorityBroadcastServer p = new PriorityBroadcastServer();
        p.Subscribe(new PriorityActionSubscriber(1, 0, delegate { calls.Add("low"); }));
        TestVerdictSub high = new TestVerdictSub(1, 10, PriorityVerdict.SkipRestThisTick,
            delegate { calls.Add("high"); });
        p.Subscribe(high);
        Check(p.SubscriberCount == 2, "priority subscriber count");
        p.Publish(e);
        Check(calls.Count == 1 && calls[0] == "high", "priority order + skip verdict");
        high.Mode = PriorityVerdict.ServeRest;
        p.Publish(e);
        Check(calls.Count == 3 && calls[1] == "high" && calls[2] == "low",
            "serve-rest reaches lower priority");
        high.Mode = PriorityVerdict.DeregisterLower;
        p.Publish(e);
        Check(p.SubscriberCount == 1, "deregister-lower removes strictly lower");
    }

    // -- 5. generator: emits a sealed AIInstance subclass for a real module.
    private static void TestGenerator(string[] files)
    {
        string pick = files[0];
        foreach (string f in files)
        {
            if (Path.GetFileName(f) == "minimal.fsmb") pick = f;
        }
        byte[] bytes = File.ReadAllBytes(pick);
        string err;
        FsmbModule m;
        FsmbReader.Read(bytes, out m, out err);
        string src = ClassGenerator.GenerateSource(m, "Minimal", "MinimalAI", "MyFSM/Minimal");
        Console.WriteLine("---- generated ----");
        Console.WriteLine(src);
        Console.WriteLine("-------------------");
        Check(src.Contains("class MinimalAI : AIInstance") && src.Contains("State_"),
            "generator emits named subclass + state constants");
    }

    // -- 6. end to end: FsmbAIInstance on the REAL dispatcher (stub engine).
    private static void TestAIInstance(string dir)
    {
        MainServer main = MainServer.EnsureExists();
        Check(main != null && main.Queries != null, "main server boots in stub engine");

        string[] names = new string[] { "patrol.fsmb", "hunter.fsmb", "guard.fsmb" };
        foreach (string name in names)
        {
            string path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            byte[] bytes = File.ReadAllBytes(path);
            GameObject go = new GameObject("AI_" + name);
            FsmbAIInstance ai = go.AddComponent<FsmbAIInstance>();
            bool booted = ai.BootWithBytes(bytes, "Smoke_" + name);
            Check(booted, "AIInstance boot " + name);
            if (!booted) continue;
            Time.deltaTime = 1f / 60f;
            for (int t = 0; t < 30; t++)
            {
                Time.time += Time.deltaTime;
                ai.TickInternal();
            }
            Console.WriteLine("    " + name + ": id=" + ai.InstanceId +
                " head=" + ai.CurrentStateName + " suspended=" + ai.Execution.IsSuspended);
            Check(ai.RebootWithBytes(bytes), "AIInstance reboot " + name);
        }

        // Bindings + variable access on hunter (float health @ slot 3).
        string hunter = Path.Combine(dir, "hunter.fsmb");
        if (File.Exists(hunter))
        {
            byte[] bytes = File.ReadAllBytes(hunter);
            GameObject go = new GameObject("AI_hunterBound");
            FsmbAIInstance ai = go.AddComponent<FsmbAIInstance>();
            ai.BootWithBytes(bytes, "Smoke_hunterBound");
            GameObject agent = new GameObject("Agent");
            Check(ai.Bind(0, agent), "bind handle slot 0");
            Check(ai.SetBoundValue(3, FsmValue.MakeFloat(100f)), "set float slot 3");
            FsmValue v;
            Check(ai.TryGetVariable("health", out v) && v.F > 99f, "read back health");
            string setErr;
            Check(!ai.TrySetVariable("attackRange", FsmValue.MakeFloat(1f), out setErr),
                "const slot rejects writes");
            Check(main.Db.SnapshotInstances().Count >= 4, "db tracks instances");
        }
    }

    // -- 7. burst compiler path mapping: a project-relative "Assets/..." path
    // must land INSIDE the project's Assets folder. It used to strip the
    // "Assets/" prefix and join with the project root, pointing one level
    // above Assets — every entry then failed with "missing .fsm" for a file
    // that was sitting right there in the Project window.
    private static void TestPathMapping()
    {
        string saved = Application.dataPath;
        Application.dataPath = Path.Combine(Path.GetTempPath(), "MyFSMProj", "Assets");

        string asset = "Assets/MyFSM/Fsm/Test.fsm";
        string abs = FsmBurstCompiler.ResolveProjectPath(asset).Replace('\\', '/');
        string want = Path.Combine(Path.GetTempPath(), "MyFSMProj", "Assets",
                                   "MyFSM", "Fsm", "Test.fsm").Replace('\\', '/');
        Check(abs == want, "ResolveProjectPath keeps the Assets/ segment");
        Check(FsmBurstCompiler.ToAssetPath(abs) == asset,
            "absolute -> Assets/... round-trips");

        string outside = Path.Combine(Path.GetTempPath(), "elsewhere.fsm");
        Check(FsmBurstCompiler.ResolveProjectPath(outside).Replace('\\', '/')
              == outside.Replace('\\', '/'), "absolute paths pass through untouched");

        // The failure path must still fail, and name the path it looked at.
        GameObject go = new GameObject("BurstCompiler");
        FsmBurstCompiler c = go.AddComponent<FsmBurstCompiler>();
        FsmBurstEntry e = new FsmBurstEntry();
        e.FsmPath = "Assets/MyFSM/Fsm/NotThere.fsm";
        Check(!c.CompileEntry(e), "missing .fsm is reported, not fabricated");
        Check(e.LastStatus != null && e.LastStatus.Contains("NotThere.fsm") &&
              e.LastStatus.Contains("looked for"),
            "missing .fsm status names the resolved path");

        Application.dataPath = saved;
    }
}
