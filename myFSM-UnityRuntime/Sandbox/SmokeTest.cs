// myFSM sandbox smoke test: exercises the Runtime headless (stub engine).
// Usage: dotnet run -- <vectors-dir>
// Sections: reader+validator on real compiler output, Core execution with a
// logging dispatcher, query-server scheduling rules, broadcast servers, the
// class generator, and an end-to-end AIInstance run on the real dispatcher.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        TestDuplicateClassGuard();
        TestCollisionMovement(dir);

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
        string src = ClassGenerator.GenerateSource(m, bytes, "Minimal", "MinimalAI",
                                                   "MyFSM/Minimal");
        Console.WriteLine("---- generated ----");
        Console.WriteLine(src);
        Console.WriteLine("-------------------");
        Check(src.Contains("class MinimalAI : AIInstance") && src.Contains("State_"),
            "generator emits named subclass + state constants");
        Check(src.Contains("FromBase64String") && src.Contains("EmbeddedModule"),
            "generated class embeds its own module bytes");
        // A self-contained class must not carry the legacy Resources plumbing:
        // no module asset to assign, no path to get wrong.
        Check(!src.Contains("ModuleResourcePath") && !src.Contains("AssetResourcePath"),
            "embedded class carries no module-asset plumbing");
        Check(src.IndexOf("EmbeddedModule", StringComparison.Ordinal) >
              src.IndexOf("OnBindingsManual", StringComparison.Ordinal),
            "the blob is emitted below the readable members, not at the top");
        // The legacy overload (no bytes passed) must still emit the Resources
        // path, since that is the only way such a class can boot.
        string legacy = ClassGenerator.GenerateSource(m, "Minimal", "LegacyAI",
                                                      "MyFSM/Minimal");
        Check(legacy.Contains("ModuleResourcePath") &&
              !legacy.Contains("EmbeddedModule"),
            "generator without bytes still emits the legacy Resources path");
        // The embedded blob must decode back to the exact module: the class is
        // the whole AI, so a wrong round trip means a silently different brain.
        string blob = ExtractBase64(src);
        byte[] back = Convert.FromBase64String(blob);
        Check(back.Length == bytes.Length, "embedded blob decodes to the same length");
        bool same = back.Length == bytes.Length;
        for (int i = 0; same && i < back.Length; i++)
            if (back[i] != bytes[i]) same = false;
        Check(same, "embedded blob is byte-identical to the compiled module");
    }

    /// <summary>Pulls the base64 payload out of generated source (test only).</summary>
    private static string ExtractBase64(string src)
    {
        int at = src.IndexOf("FromBase64String(", StringComparison.Ordinal);
        if (at < 0) return "";
        at += "FromBase64String(".Length;
        int end = src.IndexOf(");", at, StringComparison.Ordinal);
        if (end < 0) return "";
        string body = src.Substring(at, end - at);
        StringBuilder sb = new StringBuilder();
        string[] lines = body.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.EndsWith("+", StringComparison.Ordinal))
                line = line.Substring(0, line.Length - 1).Trim();
            line = line.Trim('"');
            sb.Append(line);
        }
        return sb.ToString();
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
        Time.fixedDeltaTime = 1f / 60f;      // the harness runs one physics step per tick
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

    // -- 8. duplicate-class guard: a second GENERATED file declaring the same
    // class (e.g. from an earlier Script Folder) must be reported before it is
    // written, since the two files do not merge - every member collides
    // (CS0102/CS0111/CS0756). Hand-written partials must NOT be reported: they
    // are the documented extension point.
    private static void TestDuplicateClassGuard()
    {
        string root = Path.Combine(Path.GetTempPath(), "MyFSMDupProj");
        string assets = Path.Combine(root, "Assets");
        string oldDir = Path.Combine(assets, "MyFSM", "Scripts");
        string newDir = Path.Combine(assets, "Scripts");
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(oldDir);
            Directory.CreateDirectory(newDir);
        }
        catch (Exception ex)
        {
            Check(false, "dup fixture setup: " + ex.Message);
            return;
        }

        string saved = Application.dataPath;
        Application.dataPath = assets;

        // A stale generated file in the OLD script folder.
        string staleFile = Path.Combine(oldDir, "TestAI.cs");
        File.WriteAllText(staleFile,
            "// <auto-generated> myFSM class generator\n" +
            "public sealed partial class TestAI : AIInstance\n{\n}\n");
        string target = Path.Combine(newDir, "TestAI.cs");
        string found = FsmBurstCompiler.FindStaleGeneratedClass("TestAI", target);
        Check(found != null && found.Replace('\\', '/').EndsWith("MyFSM/Scripts/TestAI.cs"),
            "stale generated duplicate is found");
        Check(FsmBurstCompiler.FindStaleGeneratedClass("TestAI", staleFile) == null,
            "a file does not conflict with itself");

        // A hand-written partial for the same class is legal and must pass.
        File.WriteAllText(Path.Combine(newDir, "TestAI.Manual.cs"),
            "public sealed partial class TestAI\n{\n    partial void OnBindingsManual() { }\n}\n");
        Check(FsmBurstCompiler.FindStaleGeneratedClass("TestAI", target) != null,
            "hand-written partial does not mask a real stale duplicate");

        // ...and with the stale file gone, the manual partial alone is fine.
        File.Delete(staleFile);
        Check(FsmBurstCompiler.FindStaleGeneratedClass("TestAI", target) == null,
            "hand-written partial alone is not a conflict");

        // A name mentioned only in a comment must not trip the scan.
        File.WriteAllText(Path.Combine(oldDir, "NotesAI.cs"),
            "// <auto-generated> myFSM class generator\n" +
            "// this is what class TestAI used to look like\n" +
            "public sealed partial class NotesAI : AIInstance\n{\n}\n");
        Check(FsmBurstCompiler.FindStaleGeneratedClass("TestAI", target) == null,
            "a commented-out class name is not a conflict");
        Check(FsmBurstCompiler.FindStaleGeneratedClass("NotesAI", target) != null,
            "the real class in that file is still found");

        Application.dataPath = saved;
        try { Directory.Delete(root, true); } catch { }
    }

    // -- 9. environment-aware movement. Tier-3 goals do not move a bare
    // transform: each agent's movement is handed to the Unity component that
    // owns that kind of motion, and this section proves which call is made,
    // with what arguments, for every component set. The two documented limits
    // are pinned too: a kinematic body is not stopped by collisions, and a
    // collider with no body is static geometry that Unity cannot move.
    private static void TestCollisionMovement(string dir)
    {
        // ---- the environment check: component set -> Unity call ----
        MovementSystem ms = new MovementSystem();

        GameObject bare = new GameObject("Bare");
        MotionContext bareCtx = ms.Resolve(bare.transform, false);
        Check(bareCtx.Driver == MotionDriver.Transform,
              "nothing attached -> transform step");

        GameObject colOnly = new GameObject("ColOnly");
        colOnly.AddComponent<Collider>();
        Check(ms.Resolve(colOnly.transform, false).Driver == MotionDriver.ColliderNoBody,
              "collider, no body -> Unity has no move for it (warn + transform)");

        GameObject dynGo = new GameObject("Dyn");
        Rigidbody dynBody = dynGo.AddComponent<Rigidbody>();
        MotionContext dynCtx = ms.Resolve(dynGo.transform, false);
        Check(dynCtx.Driver == MotionDriver.Rigidbody && !dynCtx.Kinematic,
              "rigidbody -> Rigidbody.MovePosition (physics resolves collisions)");
        Check(dynCtx.Owner == dynGo.transform, "the body's own transform is what moves");

        dynBody.isKinematic = true;
        MotionContext kinCtx = ms.Resolve(dynGo.transform, false);
        Check(kinCtx.Driver == MotionDriver.Rigidbody && kinCtx.Kinematic,
              "kinematic rigidbody -> Rigidbody.MovePosition");

        GameObject ccGo = new GameObject("CC");
        ccGo.AddComponent<CharacterController>();
        Check(ms.Resolve(ccGo.transform, false).Driver == MotionDriver.CharacterController,
              "CharacterController -> CharacterController.Move (chosen before Collider)");

        GameObject rb2Go = new GameObject("Rb2");
        rb2Go.AddComponent<Rigidbody2D>();
        Check(ms.Resolve(rb2Go.transform, true).Driver == MotionDriver.Rigidbody2D,
              "rigidbody2D -> MovePosition (2D)");

        GameObject col2Go = new GameObject("Col2");
        col2Go.AddComponent<Collider2D>();
        Check(ms.Resolve(col2Go.transform, true).Driver == MotionDriver.ColliderNoBody,
              "collider2D, no body -> same limit in 2D");

        // a collider on the PARENT is not this object's body: in Unity it
        // belongs to the parent (that was the bug behind "walks through walls")
        GameObject parentCol = new GameObject("ParentCollider");
        parentCol.AddComponent<Collider>();
        GameObject childScript = new GameObject("ChildScript");
        childScript.transform.parent = parentCol.transform;
        Check(ms.Resolve(childScript.transform, false).Driver == MotionDriver.Transform,
              "a collider on the parent is not the agent's body");

        // ...but a BODY on the parent does drive the agent (it rides it)
        GameObject parentBody = new GameObject("ParentBody");
        parentBody.AddComponent<Rigidbody>();
        GameObject childOnBody = new GameObject("ChildOnBody");
        childOnBody.transform.parent = parentBody.transform;
        MotionContext rideCtx = ms.Resolve(childOnBody.transform, false);
        Check(rideCtx.Driver == MotionDriver.Rigidbody && rideCtx.Owner == parentBody.transform,
              "a body on the parent drives the agent");

        MovementSystem raw = new MovementSystem();
        raw.CollisionAware = false;
        Check(raw.Resolve(colOnly.transform, false).Driver == MotionDriver.Transform,
              "CollisionAware off -> transform step, no component consulted");

        // ---- end to end on real compiled bytes: which call, and with what ----
        string path = Path.Combine(dir, "straightline.fsmb");
        if (!File.Exists(path)) return;

        byte[] bytes = File.ReadAllBytes(path);
        // Two clocks, deliberately different: a frame is 1/60 s, a physics step
        // is Unity's default 0.02 s. A rigidbody steps in FixedUpdate with
        // Time.fixedDeltaTime; everything else steps per frame with Time.deltaTime.
        Time.deltaTime = 1f / 60f;
        Time.fixedDeltaTime = 0.02f;
        const float speed = 2f;                       // straightline.fsm
        float step = speed * Time.deltaTime;          // 0.0333 per frame
        float bodyStep = speed * Time.fixedDeltaTime; // 0.04 per physics step
        const int ticks = 31;                         // first tick posts the goal
        int steps = ticks - 1;

        // (a) dynamic rigidbody: the follower script, verbatim, in FixedUpdate —
        //   dir = (target - rb.position).normalized;
        //   rb.MovePosition(rb.position + dir * speed * Time.fixedDeltaTime);
        // one MovePosition per physics step, no velocity written by the runtime
        GameObject bodyGo = new GameObject("BodyMover");
        Rigidbody body = bodyGo.AddComponent<Rigidbody>();
        FsmbAIInstance bodyAi = bodyGo.AddComponent<FsmbAIInstance>();
        Check(bodyAi.BootWithBytes(bytes, "Smoke_body"), "rigidbody AI boots");
        RunTicks(bodyAi, ticks);
        Console.WriteLine("    dynamic mover: " + body.movePositionCalls +
                          " MovePosition calls, z=" + bodyGo.transform.position.z.ToString("0.###"));
        Check(body.movePositionCalls == steps && Math.Abs(body.velocity.z) < 1e-6f,
              "dynamic body: one MovePosition per physics step, never given a velocity");
        Check(Math.Abs(bodyGo.transform.position.z - steps * bodyStep) < 0.02f,
              "dynamic body travels normalized direction * speed * fixedDeltaTime per step");
        Check(Math.Abs(bodyGo.transform.position.z - steps * step) > 0.1f,
              "the body's step is on the physics clock, not the frame clock");

        // the frame pass leaves a body alone: Update alone never calls MovePosition
        int callsBeforeFrames = body.movePositionCalls;
        for (int i = 0; i < 5; i++) { Time.time += Time.deltaTime; bodyAi.TickInternal(); }
        Check(body.movePositionCalls == callsBeforeFrames,
              "frames without a physics step do not move a body");
        for (int i = 0; i < 3; i++) { Time.fixedTime += Time.fixedDeltaTime; bodyAi.FixedTickInternal(); }
        Check(body.movePositionCalls == callsBeforeFrames + 3,
              "every physics step moves the body once, however many frames sit between");

        // the move is measured from the body itself: exactly rb.position + dir * speed * dt
        Vector3 before = body.position;
        Time.fixedTime += Time.fixedDeltaTime;
        bodyAi.FixedTickInternal();
        Vector3 expected = before + new Vector3(0f, 0f, 1f) * speed * Time.fixedDeltaTime;
        Check((body.lastMovePosition - expected).magnitude < 1e-4f,
              "MovePosition receives rb.position + dir.normalized * speed * fixedDeltaTime");

        int handleId = 0;
        for (int id = 1; id <= bodyAi.Handles.Count && handleId == 0; id++)
        {
            if (bodyAi.Handles.Resolve(id) == (UnityEngine.Object)bodyGo) handleId = id;
        }
        Check(handleId > 0, "the AI's agent handle was found");

        // like the follower, a body has no "arrived": a goal on the spot it stands
        // on stays posted and moves it by a zero-length step (normalized zero = zero),
        // never overshooting, never stopping short, never dropped
        MoveGoal onTheSpot = new MoveGoal();
        onTheSpot.Mode = MoveMode.Point;
        onTheSpot.Agent = FsmValue.MakeHandle(FsmbType.Object3D, handleId);
        onTheSpot.Destination = body.position;
        onTheSpot.Speed = speed;
        bodyAi.Movement.SetGoal(handleId, onTheSpot);
        Vector3 spot = body.position;
        int callsAtSpot = body.movePositionCalls;
        Time.fixedTime += Time.fixedDeltaTime;
        bodyAi.FixedTickInternal();
        Check(body.movePositionCalls == callsAtSpot + 1 && (body.position - spot).magnitude < 1e-6f,
              "a body on its destination is moved by a zero-length step");
        Check(bodyAi.Movement.HasGoal(handleId), "the body's goal is not dropped on arrival");

        // dropping the goal stops the move that was being requested: with no goal,
        // physics steps call nothing (the follower with its target cleared) —
        // physics steps only here, because the FSM's next Update re-posts the goal
        bodyAi.Movement.ClearGoal(handleId);
        int callsAtClear = body.movePositionCalls;
        for (int i = 0; i < 5; i++) { Time.fixedTime += Time.fixedDeltaTime; bodyAi.FixedTickInternal(); }
        Check(body.movePositionCalls == callsAtClear,
              "dropping the goal stops the move (no further MovePosition)");
        Check(Math.Abs(body.velocity.z) < 1e-6f && Math.Abs(body.velocity.y) < 1e-6f,
              "dropping the goal writes nothing to the body");

        // ...and the re-post idiom: the FSM's next Update posts a fresh goal, so the
        // physics step after it moves the body again
        Time.time += Time.deltaTime;
        bodyAi.TickInternal();
        Check(body.movePositionCalls == callsAtClear, "the frame that re-posts does not move a body");
        Time.fixedTime += Time.fixedDeltaTime;
        bodyAi.FixedTickInternal();
        Check(body.movePositionCalls == callsAtClear + 1,
              "the next moveTowards call posts a fresh goal and the next physics step moves the body");

        // (b) kinematic rigidbody: the same MovePosition, from the same FixedUpdate
        GameObject kinGo = new GameObject("KinMover");
        Rigidbody kinBody = kinGo.AddComponent<Rigidbody>();
        kinBody.isKinematic = true;
        FsmbAIInstance kinAi = kinGo.AddComponent<FsmbAIInstance>();
        Check(kinAi.BootWithBytes(bytes, "Smoke_kin"), "kinematic AI boots");
        RunTicks(kinAi, ticks);
        Console.WriteLine("    kinematic mover: " + kinBody.movePositionCalls +
                          " MovePosition calls, z=" + kinGo.transform.position.z.ToString("0.###"));
        Check(kinBody.movePositionCalls == steps, "kinematic body is moved by MovePosition");
        Check(Math.Abs(kinGo.transform.position.z - steps * bodyStep) < 0.02f,
              "kinematic MovePosition receives rb.position + dir * speed * fixedDeltaTime each step");
        Check(Math.Abs(kinBody.velocity.z) < 1e-6f,
              "kinematic body is never given a velocity (Unity ignores it)");

        // (c) CharacterController: Move, with gravity while airborne
        GameObject ccMover = new GameObject("CCMover");
        CharacterController cc = ccMover.AddComponent<CharacterController>();
        FsmbAIInstance ccAi = ccMover.AddComponent<FsmbAIInstance>();
        Check(ccAi.BootWithBytes(bytes, "Smoke_cc"), "character-controller AI boots");
        RunTicks(ccAi, ticks);
        Console.WriteLine("    CC mover: " + cc.moveCalls + " Move calls, z=" +
                          ccMover.transform.position.z.ToString("0.###") +
                          " y=" + ccMover.transform.position.y.ToString("0.###"));
        Check(cc.moveCalls == steps, "CharacterController is moved by Move()");
        Check(Math.Abs(ccMover.transform.position.z - steps * step) < 0.02f,
              "CC.Move receives the step towards the goal");
        Check(ccMover.transform.position.y < -9f,
              "airborne CharacterController falls (gravity is still applied)");

        // (d) collider with no body: warn once, then the transform step
        GameObject colMover = new GameObject("ColMover");
        colMover.AddComponent<Collider>().size = new Vector3(1f, 2f, 1f);
        FsmbAIInstance colAi = colMover.AddComponent<FsmbAIInstance>();
        Check(colAi.BootWithBytes(bytes, "Smoke_col"), "collider-only AI boots");
        Debug.Messages.Clear();
        RunTicks(colAi, ticks);
        Check(colAi.Movement.Resolve(colMover.transform, false).Driver == MotionDriver.ColliderNoBody,
              "collider-only mover is reported as collider-without-a-body");
        int warnings = 0;
        for (int i = 0; i < Debug.Messages.Count; i++)
        {
            if (Debug.Messages[i].Contains("has a Collider but no Rigidbody")) warnings++;
        }
        Console.WriteLine("    collider-only mover: " + warnings + " warning(s), z=" +
                          colMover.transform.position.z.ToString("0.###"));
        Check(warnings == 1, "the missing-body warning is logged exactly once");
        Check(Math.Abs(colMover.transform.position.z - steps * step) < 0.02f,
              "collider-only mover still advances (by transform, nothing invented)");

        // (e) component-free mover: same transform step, no warning
        GameObject bareGo = new GameObject("BareMover");
        FsmbAIInstance bareAi = bareGo.AddComponent<FsmbAIInstance>();
        Check(bareAi.BootWithBytes(bytes, "Smoke_bare"), "component-free AI boots");
        Debug.Messages.Clear();
        RunTicks(bareAi, ticks);
        int bareWarnings = 0;
        for (int i = 0; i < Debug.Messages.Count; i++)
        {
            if (Debug.Messages[i].Contains("has a Collider")) bareWarnings++;
        }
        Check(bareWarnings == 0, "an object with no collider is not warned about");
        Check(Math.Abs(bareGo.transform.position.z - steps * step) < 0.02f,
              "component-free mover advances by transform");

        // (f) NavMeshAgent: the mesh does the moving, so the transform is not
        // touched here and SetDestination is what the AI produces
        GameObject navGo = new GameObject("NavMover");
        NavMeshAgent nav = navGo.AddComponent<NavMeshAgent>();
        nav.isOnNavMesh = true;
        nav.remainingDistance = float.MaxValue;      // still travelling
        FsmbAIInstance navAi = navGo.AddComponent<FsmbAIInstance>();
        Check(navAi.BootWithBytes(bytes, "Smoke_nav"), "navmesh AI boots");
        RunTicks(navAi, 3);
        Console.WriteLine("    nav mover: " + nav.setDestinationCalls + " SetDestination calls, dest z=" +
                          nav.lastDestination.z.ToString("0.###"));
        Check(nav.setDestinationCalls == 2, "NavMeshAgent is steered with SetDestination");
        Check(Math.Abs(nav.lastDestination.z - 10f) < 0.1f,
              "SetDestination receives the goal point");
        Check(Math.Abs(navGo.transform.position.z) < 1e-4f,
              "with a live agent nothing here moves the transform");
    }

    /// <summary>
    /// One Unity frame per tick, in Unity's order: the physics step (FixedUpdate,
    /// where a Rigidbody goal takes its step) and then the frame (Update, where the
    /// FSM runs and everything else moves).
    /// </summary>
    private static void RunTicks(AIInstance ai, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            Time.fixedTime += Time.fixedDeltaTime;   // one physics step per tick
            ai.FixedTickInternal();
            Time.time += Time.deltaTime;
            ai.TickInternal();
        }
    }
}
