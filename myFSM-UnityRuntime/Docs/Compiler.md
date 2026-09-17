# Compiler — `.fsm` to `.fsmb` to scripts to GameObjects

The runtime executes `.fsmb` binaries; this pipeline produces them and wires
them up. Five stages, each with one owner:

```
.fsm source --[1. compile]--> .fsmb --[2. validate]--> bytes OK?
  --[3. codegen]--> XxxAI.cs --[4. attach]--> GameObject --[5. bind]--> Play
     (C++ fsmc)      (C# loader)   (ClassGenerator)   (you/inspector)  (auto+manual)
```

## 1. The native compiler + the C# wrapper

Real compilation is C++ (`fsmc/`), exposed as a C ABI (`myfsm_compile`,
`myfsm_free`, `myfsm_version`) and shipped as a native plugin:

| Platform | File | Folder |
|---|---|---|
| Windows x86_64 | `myfsmc.dll` | `Plugins/x86_64/` |
| Linux x86_64 | `libmyfsmc.so` | `Plugins/x86_64/` (shipped, verified) |
| macOS | `libmyfsmc.dylib` | `Plugins/` |

Rebuild: `cmake --build build --target myfsmc` from `fsmc/` (details in
`Plugins/README.md`). The emitted format is pinned to 0.5 by `static_assert`.

`Runtime/Compiler/FsmCompiler.cs` is the C# wrapper (engine-free, no Unity
dependency). Everything returns a result object — it never throws:

```csharp
FsmCompileResult r = FsmCompiler.CompileSource(sourceText, "Guard.fsm");
if (!r.Ok) Debug.LogError(r.Error ?? r.Diagnostics);
else File.WriteAllBytes("Guard.fsmb", r.Module); // + r.Diagnostics = warnings
```

- `FsmCompileResult`: `Ok`, `Module` (bytes, only when `Ok`),
  `Diagnostics` (warnings when `Ok`, `file:line:col` errors when not),
  `Error` (wrapper-level failure: missing lib, I/O…).
- `FsmCompiler.IsAvailable` / `NativeVersion` — probe once, cached. When the
  plugin is missing for the platform, every call fails gracefully with
  `Error` set ("not found for this platform; precompile with the fsmc CLI").
- `CompileFile(fsmPath, fsmbPath)` — the manual one-shot: reads, compiles,
  writes only on success, deletes partials on write failure (CLI contract).

## 2. Manual compile (one file, your trigger)

Three equivalent ways, same engine underneath:

1. **CLI**: `fsmc Guard.fsm -o Guard.fsmb` (no Unity needed).
2. **API**: `FsmCompiler.CompileFile(inPath, outPath)` from any editor script,
   menu item, or build step.
3. **Inspector**: one burst entry → its **Compile** button (below).

## 3. Dynamic compile (any script, at will)

Call `FsmCompiler.CompileSource` / `CompileFile` from anything, edit time or
runtime: procedural AI variants, player-authored logic, live tuning tools.
Limits, stated plainly:

- Runtime needs the native plugin shipped with the player → realistic on
  desktop; constrained platforms (mobile/console/WebGL) must use
  precompiled `.fsmb` (compile in the editor, ship the bytes).
- The native side holds no shared state, so concurrent compiles are safe.
- Compiling is heavier than loading: compile rarely (on demand), cache the
  bytes, feed them to `BootWithBytes` / `RebootWithBytes` often.

## 4. Burst compile (folders in, modules + scripts out)

`Runtime/Compiler/FsmBurstCompiler.cs` — put it on any GameObject. No
UnityEditor references, so the component itself is runtime-safe; the fancy
inspector lives in `Editor/` and never ships in builds.

Component fields:

| Field | Default | Meaning |
|---|---|---|
| `Entries` | — | One mapping each: `Name`, `FsmPath`, `OutputFolder`, `ClassName`, `ScriptFolder`, `Target` (empty fields fall back to the defaults / derived values) |
| `DefaultOutputFolder` | `Assets/MyFSM/Resources/MyFSM` | `.fsmb` land here. Under `Resources/` so generated classes load by path |
| `DefaultScriptFolder` | `Assets/MyFSM` | Generated `XxxAI.cs` land here |
| `InputFolder` | `Assets/MyFSM/Fsm` | `SweepFolder()` adds one entry per `*.fsm` found here (skips files that already have entries) |

Per entry, `CompileEntry` does: read `.fsm` → `FsmCompiler` → write `.fsmb`
→ **re-validate the fresh bytes through the real `FsmbReader`** (loader and
compiler must agree, or it fails loudly) → write the `.bytes` import twin →
`ClassGenerator` → write `XxxAI.cs`. `ClassName` defaults to `<FileStem>AI`.
Every entry reports `LastOk` / `LastStatus` (byte count, state count, output
paths, warnings). `CompileAll()` compiles entries in order and returns
tallies.

Three files per entry, one validated byte array:

| File | Why |
|---|---|
| `Xxx.fsmb` | The module, as the CLI/other tools know it |
| `XxxAI.cs` | The class, with those bytes embedded (base64): attach and Play, nothing to assign |
| `Xxx.bytes` | Unity's importer only makes a `TextAsset` out of `.bytes`; assign this twin to a plain `FsmbAIInstance` (a `.fsmb` lands as a `DefaultAsset` and cannot be assigned) |

## 5. The inspector workflow (edit mode)

Add `FsmBurstCompiler` to a GameObject (conventionally the burst object next
to your AI work). Top row: **Sweep Folder** (folder → entries), **Compile
All** (entries → `.fsmb` + `.bytes` + scripts), **Sweep + Compile** (both).
Per entry: file/folder pickers, fields, **Compile**, status line.

**Compile attaches.** With a `Target` set, Compile adds the generated
component to it: immediately when the class already exists (a recompile), or
automatically as soon as Unity finishes importing the freshly written script
on the first compile — the first pass cannot attach because the type does not
exist until Unity reimports, so the request is parked in `SessionState` and
picked up by an `[InitializeOnLoadMethod]` hook after the domain reload. If
two objects in the scene share the target's name, the deferred pass refuses
to guess and leaves you the **Attach** button. **Generate** (no target set)
creates a new GameObject at the scene pivot with the component on it.
Attaching is edit-mode only.

Linking scripts to GameObjects:

1. Fill `Target` by dragging an existing GameObject, **or**
2. Leave `Target` empty → a **Generate** button appears next to it (edit mode
   only, hidden in play mode). It creates a GameObject named after the entry
   at the **SceneView pivot** (visible coordinates — where you're looking;
   `(0,1,0)` with no SceneView open), Undo-registered, selected, scene
   dirtied, and attaches the generated script when it's already compiled.
3. If Unity hasn't finished compiling the fresh script yet, Generate still
   links the GameObject and tells you to wait, then press **Attach** (which
   appears when a linked GameObject lacks the script component).

Timing note: Compile writes `.cs` files and refreshes the AssetDatabase;
Unity then compiles scripts asynchronously. Generate/Attach immediately after
Compile may find the type missing — wait for the compile spinner, then click.
This is Unity's pipeline, not a bug in the burst flow.

## Expected behavior + failure modes

| You see | It means |
|---|---|
| `ok: 392 bytes, 2 states -> ... + ...` | Compiled, validated, script written |
| `[warnings]` suffix | Compiled fine; check the `.fsm` (bare gotos etc.) |
| `self-contained: the module bytes are embedded` | Compile wrote a class that carries its own module: attach it and Play, nothing to assign |
| `does not exist yet - Unity is still importing it` | Normal on the FIRST compile: the type only exists after Unity reimports the new `.cs`. The entry attaches itself automatically once that finishes |
| `Cannot attach while in play mode` | Exit play mode, then press **Attach** |
| `file:line:col: error: …` | Compiler rejected the source; nothing written — fix the `.fsm` |
| `missing .fsm: Assets/… (looked for /abs/path)` | The entry's `FsmPath` does not resolve to a real file. The status prints the absolute path it checked: compare it with the Project window (a project-relative path resolves against the project root, so `Assets/MyFSM/Fsm/Test.fsm` must map to `<project>/Assets/MyFSM/Fsm/Test.fsm`) |
| `native compiler library 'myfsmc' not found…` | Plugin missing for this platform — add it (see table) or precompile via CLI |
| `fresh .fsmb failed loader validation: …` | Compiler/loader version skew — should never happen on 0.5; report it |
| `'XxxAI' is not compiled yet` | Normal right after Compile — wait for Unity's script compile, then Attach/Generate again |
| Entry `Target` survives, script lost after recompile | You edited the generated file inline — move durable edits to the partial file (see `AIInstance.md`) |
