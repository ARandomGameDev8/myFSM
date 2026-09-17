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

    // -- 9. environment-aware movement. A tier-3 goal does not move a bare
    // transform: the components on the agent decide how the step is applied
    // (velocity, CharacterController.Move, a swept MovePosition, an engine cast
    // sweep, or the transform), and every contact comes from the engine. These
    // cases cover the sweep, the documented traps around it (SphereCast's
    // normal is not the surface normal; a shape cast is blind to what it
    // overlaps and to non-convex meshes), the driver each component set
    // selects, and end-to-end runs on real compiled bytes -- including the
    // wall-between-the-AI-and-its-target case.
    private static void TestCollisionMovement(string dir)
    {
        const float wall = 2.0f;    // a wall occupies the half-space past this plane
        const float radius = 0.5f;
        Vector3 forward = new Vector3(0f, 0f, 1f);
        bool blocked;

        // ---- the swept step: engine cast + slide ----
        Vector3 p = MovementSystem.SlideStep(Vector3.zero, forward, radius, false,
                                            new NoObstacleProbe(), out blocked);
        Check(!blocked && Math.Abs(p.z - 1f) < 1e-5f, "open step is unhindered");

        p = MovementSystem.SlideStep(Vector3.zero, forward * 3f, radius, false,
                                     new PlaneProbe(forward, wall), out blocked);
        Check(blocked && p.z + radius <= wall + 1e-4f, "head-on: body never overlaps the wall");
        Check(p.z > wall - radius - 0.1f, "head-on: stops AT the wall, not far from it");

        Vector3 touching = new Vector3(0f, 0f, wall - radius - MovementSystem.CollisionSkin);
        for (int i = 0; i < 200; i++)
            touching = MovementSystem.SlideStep(touching, forward * 0.033f, radius, false,
                                                new PlaneProbe(forward, wall), out blocked);
        Check(touching.z + radius <= wall + 1e-4f, "pressed against the wall: no creep");

        p = MovementSystem.SlideStep(Vector3.zero, new Vector3(2f, 0f, 2f), radius, false,
                                     new PlaneProbe(forward, wall), out blocked);
        Check(blocked && p.x > 1f && p.z + radius <= wall + 1e-4f,
              "diagonal: slides along the wall without overlapping it");

        p = MovementSystem.SlideStep(new Vector3(0f, 3f, 0f), forward * 3f, radius, false,
                                     new PlaneProbe(forward, wall, 1f), out blocked);
        Check(!blocked && Math.Abs(p.z - 3f) < 1e-5f, "clear above the obstacle: passes freely");

        p = MovementSystem.SlideStep(Vector3.zero, new Vector3(3f, 0f, 0f), radius, true,
                                     new PlaneProbe(new Vector3(1f, 0f, 0f), wall), out blocked);
        Check(blocked && p.x + radius <= wall + 1e-4f, "2D circle cast stops at the wall");

        // ---- Unity docs, trap 1: a sphere cast's normal is often the
        // contact->centre direction, "misleading if you're using it for
        // sliding". With a deliberately wrong cast normal the slide must still
        // use the ray's true surface normal and stay out of the wall.
        PlaneProbe skewed = new PlaneProbe(forward, wall);
        skewed.SkewCastNormal = true;
        p = MovementSystem.SlideStep(Vector3.zero, new Vector3(2f, 0f, 2f), radius, false,
                                     skewed, out blocked);
        Check(blocked && p.z + radius <= wall + 1e-4f,
              "a wrong cast normal cannot push the slide into the wall");
        Check(p.x > 1f, "the skewed normal still slides along the true surface");

        // ---- Unity docs, trap 2: "SphereCast will not detect colliders for
        // which the sphere overlaps the collider" (and never non-convex
        // meshes). When the shape cast is blind the rays must take over.
        PlaneProbe meshWall = new PlaneProbe(forward, wall);
        meshWall.BlindToSphereCast = true;
        p = MovementSystem.SlideStep(Vector3.zero, forward * 3f, radius, false,
                                     meshWall, out blocked);
        Check(blocked && p.z + radius <= wall + 1e-4f,
              "shape-cast-blind wall is caught by the ray fallback");
        p = MovementSystem.SlideStep(Vector3.zero, new Vector3(2f, 0f, 2f), radius, false,
                                     meshWall, out blocked);
        Check(blocked && p.x > 1f && p.z + radius <= wall + 1e-4f,
              "the ray fallback slides along it too");

        // ---- trap 2b: already inside the wall -> shape cast blind AND rays
        // starting inside a collider do not report it. The overlap push-out is
        // the net that guarantees a tick never ends inside something.
        Vector3 inside = new Vector3(0f, 0f, wall + 0.3f);
        PlaneProbe solidWall = new PlaneProbe(forward, wall);
        Vector3 push;
        bool pushed = solidWall.ResolvePenetration(null, inside, radius, false, out push);
        Check(pushed && inside.z + push.z + radius <= wall + 1e-4f,
              "overlap query pushes a buried body back out of the wall");

        // ---- driver selection: the components decide, not the call ----
        MovementSystem ms = new MovementSystem();

        GameObject bare = new GameObject("Bare");
        Check(ms.Resolve(bare.transform, false).Driver == MotionDriver.Transform,
              "no components -> swept transform step");

        GameObject colOnly = new GameObject("ColOnly");
        colOnly.AddComponent<Collider>();
        Check(ms.Resolve(colOnly.transform, false).Driver == MotionDriver.ColliderSweep,
              "collider, no body -> engine sweep + slide");

        GameObject dynGo = new GameObject("Dyn");
        Rigidbody dynBody = dynGo.AddComponent<Rigidbody>();
        Check(ms.Resolve(dynGo.transform, false).Driver == MotionDriver.Rigidbody,
              "rigidbody -> physics-driven step");
        Check(ms.Resolve(dynGo.transform, false).Owner == dynGo.transform,
              "the body's own transform is what moves");

        dynBody.isKinematic = true;
        Check(ms.Resolve(dynGo.transform, false).Driver == MotionDriver.Rigidbody,
              "kinematic rigidbody keeps the swept MovePosition path");

        GameObject ccGo = new GameObject("CC");
        ccGo.AddComponent<CharacterController>();
        Check(ms.Resolve(ccGo.transform, false).Driver == MotionDriver.CharacterController,
              "CharacterController is chosen before its Collider base class");

        GameObject rb2Go = new GameObject("Rb2");
        rb2Go.AddComponent<Rigidbody2D>();
        Check(ms.Resolve(rb2Go.transform, true).Driver == MotionDriver.Rigidbody2D,
              "rigidbody2D -> physics-driven step (2D)");

        GameObject col2Go = new GameObject("Col2");
        col2Go.AddComponent<Collider2D>();
        Check(ms.Resolve(col2Go.transform, true).Driver == MotionDriver.ColliderSweep,
              "collider2D, no body -> 2D engine sweep");

        // the script can sit on a child: a body on the parent still drives it
        GameObject parentGo = new GameObject("Parent");
        parentGo.AddComponent<Rigidbody>();
        GameObject childGo = new GameObject("Child");
        childGo.transform.parent = parentGo.transform;
        Check(ms.Resolve(childGo.transform, false).Driver == MotionDriver.Rigidbody &&
              ms.Resolve(childGo.transform, false).Owner == parentGo.transform,
              "a body on the parent drives the agent");

        // ...and so does a collider on the parent (the reported setup)
        GameObject parentCol = new GameObject("ParentCollider");
        parentCol.AddComponent<Collider>();
        GameObject childScript = new GameObject("ChildScript");
        childScript.transform.parent = parentCol.transform;
        MotionContext parentCtx = ms.Resolve(childScript.transform, false);
        Check(parentCtx.Driver == MotionDriver.ColliderSweep &&
              parentCtx.Owner == parentCol.transform &&
              parentCtx.Shape != null,
              "a collider on the parent drives the agent and is swept");

        MovementSystem raw = new MovementSystem();
        raw.CollisionAware = false;
        Check(raw.Resolve(colOnly.transform, false).Driver == MotionDriver.Transform,
              "CollisionAware off -> raw transform stepping");

        // ---- how each driver applies a step, end to end on real bytes ----
        string path = Path.Combine(dir, "straightline.fsmb");
        if (File.Exists(path))
        {
            byte[] bytes = File.ReadAllBytes(path);
            Time.deltaTime = 1f / 60f;

            // (a) collider, no body: swept to the wall and held there
            GameObject sweepGo = new GameObject("SweepMover");
            sweepGo.AddComponent<Collider>().size = new Vector3(1f, 2f, 1f);
            FsmbAIInstance sweepAi = sweepGo.AddComponent<FsmbAIInstance>();
            Check(sweepAi.BootWithBytes(bytes, "Smoke_sweep"), "collider-only AI boots");
            sweepAi.Movement.Probe = new PlaneProbe(forward, 5f);
            RunTicks(sweepAi, 180);
            Vector3 sweepEnd = sweepGo.transform.position;
            Console.WriteLine("    collider-only mover ended at z=" + sweepEnd.z.ToString("0.###"));
            Check(sweepEnd.z > 1f, "collider-only mover travelled");
            Check(sweepEnd.z + 0.5f <= 5f + 1e-3f, "collider-only mover stopped at the wall");

            // (a2) a wider collider is stopped further out: the SHAPE decides
            GameObject wideGo = new GameObject("WideMover");
            wideGo.AddComponent<Collider>().size = new Vector3(3f, 2f, 3f);
            FsmbAIInstance wideAi = wideGo.AddComponent<FsmbAIInstance>();
            Check(wideAi.BootWithBytes(bytes, "Smoke_wide"), "wide-collider AI boots");
            wideAi.Movement.Probe = new PlaneProbe(forward, 5f);
            RunTicks(wideAi, 180);
            Vector3 wideEnd = wideGo.transform.position;
            Console.WriteLine("    wide collider (r=1.5) ended at z=" + wideEnd.z.ToString("0.###"));
            Check(wideEnd.z + 1.5f <= 5f + 1e-3f, "wide body never overlaps the wall");
            Check(wideEnd.z < sweepEnd.z - 0.5f,
                  "a wider body is stopped further from the wall");

            // (a3) shape-cast-blind wall (a non-convex MeshCollider behaves this
            // way) with NO collider on the agent, so the cast is the only
            // detector: the ray fallback still stops it
            GameObject meshGo = new GameObject("MeshWallMover");
            FsmbAIInstance meshAi = meshGo.AddComponent<FsmbAIInstance>();
            Check(meshAi.BootWithBytes(bytes, "Smoke_mesh"), "mesh-wall AI boots");
            PlaneProbe blind = new PlaneProbe(forward, 5f);
            blind.BlindToSphereCast = true;
            meshAi.Movement.Probe = blind;
            RunTicks(meshAi, 180);
            Check(meshGo.transform.position.z + 0.5f <= 5f + 1e-3f,
                  "a wall the shape cast cannot see still stops it (ray fallback)");

            // (a3b) a SHAPED body does not depend on casts at all: the engine's
            // overlap query answers, so even a wall the cast cannot see stops it
            GameObject mesh2Go = new GameObject("MeshWallBody");
            mesh2Go.AddComponent<Collider>().size = new Vector3(1f, 2f, 1f);
            FsmbAIInstance mesh2Ai = mesh2Go.AddComponent<FsmbAIInstance>();
            Check(mesh2Ai.BootWithBytes(bytes, "Smoke_mesh2"), "mesh-wall body boots");
            PlaneProbe blind2 = new PlaneProbe(forward, 5f);
            blind2.BlindToSphereCast = true;
            mesh2Ai.Movement.Probe = blind2;
            RunTicks(mesh2Ai, 180);
            Check(mesh2Go.transform.position.z + 0.5f <= 5f + 1e-3f,
                  "a shaped body is stopped by the overlap query, casts or not");

            // (a4) starting INSIDE the wall: the sweep is blind, the overlap net
            // pushes it back out, and it never ends up on the far side
            GameObject buriedGo = new GameObject("BuriedMover");
            buriedGo.AddComponent<Collider>().size = new Vector3(1f, 2f, 1f);
            buriedGo.transform.position = new Vector3(0f, 0f, 5.3f);   // inside the wall
            FsmbAIInstance buriedAi = buriedGo.AddComponent<FsmbAIInstance>();
            Check(buriedAi.BootWithBytes(bytes, "Smoke_buried"), "buried AI boots");
            buriedAi.Movement.Probe = new PlaneProbe(forward, 5f);
            RunTicks(buriedAi, 60);
            Check(buriedGo.transform.position.z <= 5.001f,
                  "a body that starts inside the wall is pushed out, not through");

            // (b) nothing on the agent: no rigidbody and no collider still
            // means "respect collisions" -- it sweeps with the default radius
            GameObject bareGo = new GameObject("BareMover");
            FsmbAIInstance bareAi = bareGo.AddComponent<FsmbAIInstance>();
            Check(bareAi.BootWithBytes(bytes, "Smoke_bare"), "component-free AI boots");
            bareAi.Movement.Probe = new PlaneProbe(forward, 5f);
            RunTicks(bareAi, 180);
            Vector3 bareEnd = bareGo.transform.position;
            Console.WriteLine("    component-free mover ended at z=" + bareEnd.z.ToString("0.###"));
            Check(bareEnd.z + 0.5f <= 5f + 1e-3f,
                  "component-free mover still stops at the wall (default radius)");

            // (b2) the escape hatch: the toggle skips the environment entirely
            GameObject optOutGo = new GameObject("OptOutMover");
            optOutGo.AddComponent<Collider>().size = new Vector3(1f, 2f, 1f);
            FsmbAIInstance optOutAi = optOutGo.AddComponent<FsmbAIInstance>();
            Check(optOutAi.BootWithBytes(bytes, "Smoke_optout"), "opt-out AI boots");
            optOutAi.Movement.CollisionAware = false;
            optOutAi.Movement.Probe = new PlaneProbe(forward, 5f);
            RunTicks(optOutAi, 180);
            Check(optOutGo.transform.position.z > 5f,
                  "CollisionAware off: the collider is ignored and it walks through");

            // (c) dynamic rigidbody: movement is a velocity request to physics
            GameObject bodyGo = new GameObject("BodyMover");
            bodyGo.AddComponent<Collider>().size = new Vector3(1f, 2f, 1f);
            Rigidbody body = bodyGo.AddComponent<Rigidbody>();
            FsmbAIInstance bodyAi = bodyGo.AddComponent<FsmbAIInstance>();
            Check(bodyAi.BootWithBytes(bytes, "Smoke_body"), "rigidbody AI boots");
            bodyAi.Movement.Probe = new PlaneProbe(forward, 5f);
            RunTicks(bodyAi, 3);
            Check(body.velocity.z > 1.5f && Math.Abs(body.velocity.x) < 1e-4f,
                  "dynamic rigidbody is driven by velocity (2 u/s along +Z)");
            Check(Math.Abs(bodyGo.transform.position.z) < 1e-4f,
                  "dynamic rigidbody: movement never teleports the transform");

            // (d) dropping the goal clears the velocity it was driving
            int handleId = 0;
            for (int id = 1; id <= bodyAi.Handles.Count && handleId == 0; id++)
            {
                if (bodyAi.Handles.Resolve(id) == (UnityEngine.Object)bodyGo) handleId = id;
            }
            Check(handleId > 0, "the AI's agent handle was found");
            bodyAi.Movement.ClearGoal(handleId);
            Check(Math.Abs(body.velocity.z) < 1e-6f,
                  "dropping the goal zeroes the velocity it was driving");

            // (e) kinematic rigidbody: swept first (the solver would not), so the stop holds
            GameObject kinGo = new GameObject("KinMover");
            kinGo.AddComponent<Collider>().size = new Vector3(1f, 2f, 1f);
            Rigidbody kinBody = kinGo.AddComponent<Rigidbody>();
            kinBody.isKinematic = true;
            FsmbAIInstance kinAi = kinGo.AddComponent<FsmbAIInstance>();
            Check(kinAi.BootWithBytes(bytes, "Smoke_kin"), "kinematic AI boots");
            kinAi.Movement.Probe = new PlaneProbe(forward, 5f);
            RunTicks(kinAi, 180);
            Check(kinGo.transform.position.z > 1f, "kinematic mover travelled");
            Check(kinGo.transform.position.z + 0.5f <= 5f + 1e-3f,
                  "kinematic mover never passed the wall");

            // (e2) a fast step: the substep cap must not throttle it...
            float savedDt = Time.deltaTime;
            Time.deltaTime = 0.5f;                       // speed 2 -> a 1.0 unit step
            GameObject fastGo = new GameObject("FastMover");
            fastGo.AddComponent<Collider>().size = new Vector3(1f, 2f, 1f);
            FsmbAIInstance fastAi = fastGo.AddComponent<FsmbAIInstance>();
            Check(fastAi.BootWithBytes(bytes, "Smoke_fast"), "fast AI boots");
            fastAi.Movement.Probe = new PlaneProbe(forward, 500f);
            RunTicks(fastAi, 2);
            Console.WriteLine("    fast step moved z=" + fastGo.transform.position.z.ToString("0.###") +
                              " (one tick of speed 2 at dt 0.5)");
            Check(Math.Abs(fastGo.transform.position.z - 1f) < 0.02f,
                  "a fast step is taken in full, not clamped to one substep");

            // ...and the same fast step against a nearby wall must not tunnel
            GameObject ramGo = new GameObject("RamMover");
            ramGo.transform.position = new Vector3(0f, 0f, 4f);
            ramGo.AddComponent<Collider>().size = new Vector3(1f, 2f, 1f);
            FsmbAIInstance ramAi = ramGo.AddComponent<FsmbAIInstance>();
            Check(ramAi.BootWithBytes(bytes, "Smoke_ram"), "ramming AI boots");
            ramAi.Movement.Probe = new PlaneProbe(forward, 5f);
            RunTicks(ramAi, 2);
            Console.WriteLine("    rammer ended at z=" + ramGo.transform.position.z.ToString("0.###"));
            Check(ramGo.transform.position.z + 0.5f <= 5f + 1e-3f,
                  "a fast step into a wall does not tunnel through it");
            Time.deltaTime = savedDt;

            // (f) character controller: the engine's capsule sweep moves it
            GameObject ccMover = new GameObject("CCMover");
            ccMover.AddComponent<CharacterController>();
            FsmbAIInstance ccAi = ccMover.AddComponent<FsmbAIInstance>();
            Check(ccAi.BootWithBytes(bytes, "Smoke_cc"), "character-controller AI boots");
            ccAi.Movement.Probe = new PlaneProbe(forward, 5f);   // this driver needs none
            RunTicks(ccAi, 60);
            Vector3 ccEnd = ccMover.transform.position;
            Console.WriteLine("    CC mover ended at z=" + ccEnd.z.ToString("0.###") +
                              " y=" + ccEnd.y.ToString("0.###"));
            Check(ccEnd.z > 1f, "CharacterController mover advanced through Move()");
            Check(ccEnd.y < -0.5f, "airborne CharacterController falls (gravity applies)");
        }
    }

    private static void RunTicks(AIInstance ai, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            Time.time += Time.deltaTime;
            ai.TickInternal();
        }
    }
}

/// <summary>Test probe: an empty collision world (every query misses).</summary>
public sealed class NoObstacleProbe : MyFSM.Unity.IMotionProbe
{
    public string LastHitName { get { return null; } }

    public bool SphereCast(Vector3 origin, float radius, Vector3 direction, float maxDistance,
                           bool is2D, out float distance, out Vector3 normal)
    {
        distance = 0f;
        normal = Vector3.zero;
        return false;
    }

    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, bool is2D,
                        out float distance, out Vector3 point, out Vector3 normal)
    {
        distance = 0f;
        point = Vector3.zero;
        normal = Vector3.zero;
        return false;
    }

    public bool ResolvePenetration(Transform owner, Vector3 centre, float radius, bool is2D,
                                   out Vector3 push)
    {
        push = Vector3.zero;
        return false;
    }
}

/// <summary>
/// Test probe standing in for the engine: a wall occupying the half-space
/// <c>dot(axis, p) &gt;= offset</c>, facing <c>-axis</c>. It reproduces the
/// behaviours the Unity docs describe, so the movement code is tested against
/// the real contract rather than a convenient one:
///
///  * SphereCast reports how far the sphere's CENTRE may travel before touch
///    (that is what RaycastHit.distance means for a swept volume). With
///    <see cref="SkewCastNormal"/> it reports a contact-to-centre normal
///    instead of the surface normal, as SphereCast sometimes does.
///  * Raycast hits the wall's SURFACE and reports the true normal.
///  * With <see cref="BlindToSphereCast"/> the shape cast sees nothing (a
///    non-convex MeshCollider, or a collider the sphere already overlaps)
///    while rays still work.
///  * ResolvePenetration pushes a body that is inside the wall back out; the
///    harness has no real collider shapes, so the body is a sphere of the
///    radius movement hands it.
///
/// <c>topY</c> makes the wall end at a height, so a body above it passes.
/// </summary>
public sealed class PlaneProbe : MyFSM.Unity.IMotionProbe
{
    private readonly Vector3 _axis;
    private readonly float _offset;
    private readonly float _topY;
    private readonly bool _limited;

    /// <summary>Report a wrong (contact-to-centre) normal, as SphereCast may.</summary>
    public bool SkewCastNormal;
    /// <summary>Pretend the shape cast cannot see the wall (rays still can).</summary>
    public bool BlindToSphereCast;

    public string LastHitName { get; private set; }

    public PlaneProbe(Vector3 axis, float offset) { _axis = axis; _offset = offset; }
    public PlaneProbe(Vector3 axis, float offset, float topY)
    {
        _axis = axis; _offset = offset; _topY = topY; _limited = true;
    }

    private bool Exists(Vector3 at)
    {
        return !_limited || at.y < _topY;
    }

    public bool SphereCast(Vector3 origin, float radius, Vector3 direction, float maxDistance,
                           bool is2D, out float distance, out Vector3 normal)
    {
        distance = 0f;
        normal = Vector3.zero;
        LastHitName = null;
        if (BlindToSphereCast || !Exists(origin)) return false;
        float denom = _axis.x * direction.x + _axis.y * direction.y + _axis.z * direction.z;
        if (denom <= 1e-9f) return false;                  // not moving into the wall
        float start = _axis.x * origin.x + _axis.y * origin.y + _axis.z * origin.z;
        float t = (_offset - radius - start) / denom;      // centre travel, per Unity
        if (t > maxDistance) return false;
        if (t < 0f) t = 0f;                                // already at/inside the margin
        distance = t;
        if (SkewCastNormal)
        {
            // "often the direction from the contact point to the center of the
            // sphere": tilt the normal toward the direction of travel.
            normal = (-_axis + direction * 0.35f).normalized;
        }
        else
        {
            normal = -_axis;
        }
        LastHitName = "Wall";
        return true;
    }

    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, bool is2D,
                        out float distance, out Vector3 point, out Vector3 normal)
    {
        distance = 0f;
        point = Vector3.zero;
        normal = Vector3.zero;
        if (!Exists(origin)) return false;
        float denom = _axis.x * direction.x + _axis.y * direction.y + _axis.z * direction.z;
        if (denom <= 1e-9f) return false;
        float start = _axis.x * origin.x + _axis.y * origin.y + _axis.z * origin.z;
        float t = (_offset - start) / denom;               // the wall's SURFACE
        if (t < 0f || t > maxDistance) return false;
        distance = t;
        point = origin + direction * t;
        normal = -_axis;
        LastHitName = "Wall";
        return true;
    }

    public bool ResolvePenetration(Transform owner, Vector3 centre, float radius, bool is2D,
                                   out Vector3 push)
    {
        push = Vector3.zero;
        LastHitName = null;
        if (!Exists(centre)) return false;
        float start = _axis.x * centre.x + _axis.y * centre.y + _axis.z * centre.z;
        float excess = start + radius - _offset;           // the body is a sphere of
        if (excess <= 1e-4f) return false;                 // `radius` here, as the
        push = -_axis * (excess + 0.02f);                  // harness has no real shape
        LastHitName = "Wall";
        return true;
    }
}
