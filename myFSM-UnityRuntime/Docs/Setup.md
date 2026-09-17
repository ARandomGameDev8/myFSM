# Setup — installing & wiring the myFSM Unity runtime

## Requirements

- **Unity 2019.4+**. The runtime is pure C# 7.3, UnityEngine only, no
  packages. It avoids newer .NET APIs (e.g. `BitConverter` instead of
  `Int32BitsToSingle`) so it compiles on Unity's .NET Standard 2.0 profile.
- **The myFSM compiler** (`fsmc`) to produce `.fsmb` modules from `.fsm`
  sources. The runtime reads module v0.5 only.
- Optional: **.NET 8 SDK** to run the headless sandbox harness, and
  **Python 3** (+ `tree-sitter`, `tree-sitter-c-sharp` pip packages) to run
  the static checker.

## Install

**Manual (today):** copy the package's `Runtime/` folder into your project.
Default location: `Assets/MyFSM/` (this is also `defaultInstallDir` in
`myfsm-package.json`, which the installer will use). Copy `Samples/` too if
you want the generated-class examples. Never copy `Sandbox/` into a Unity
project — its `UnityEngineStubs.cs` would collide with the real engine (the
manifest excludes it for the same reason).

**Installer (coming next):** a traditional installer that detects Unity,
discovers your projects, lets you pick a project + in-project folder, and
installs the payload there. Same files, less dragging.

## Adding a brain (.fsmb)

1. Compile: `fsmc patrol.fsm -o patrol.fsmb`, or let the burst compiler do
   it in-editor (see `Compiler.md`).
2. If you use the **generated class**, you need nothing else: the class has
   the module bytes embedded at generation time, so dropping the component on
   a GameObject and pressing Play is the whole setup. No `.fsmb` asset to
   assign, no `Resources/` folder, no path to get right. Assigning a module
   asset in the inspector overrides the embedded bytes — that is the only way
   to swap the brain without regenerating.
3. If you use the inspector-driven `FsmbAIInstance`, put a module file
   anywhere and assign it to the module slot. It must import as a
   `TextAsset`: Unity has no importer for `.fsmb` (it becomes a
   `DefaultAsset`), so assign the `.bytes` twin the burst compiler writes
   next to the `.fsmb` (they are always the same bytes).

> Older generated classes (emitted before modules were embedded) still work
> the old way: the `.fsmb` must sit under a `Resources/` folder because the
> class loads it with `Resources.Load<TextAsset>(ModuleResourcePath)`, e.g.
> `Assets/Resources/MyFSM/Patrol.fsmb` <=> path `"MyFSM/Patrol"`. Regenerate
> the class to make it self-contained.

## Wiring an AI (inspector path)

1. Select a GameObject, Add Component → `FsmbAIInstance`. (Only this
   component has a module slot — a generated class has none, because it
   carries its own module.)
2. Assign the module: a `.bytes` file, not the `.fsmb`.
3. Fill the slot list: **list index = binding slot**. Slot order/names come
   from the module's `var` declarations (see them via the `GetAssetInfo`
   query, or the generated `Slot_*` constants).
4. Press Play. On `Start()`, the AI boots: reads + validates the module,
   applies bindings, enters the `@ENTRY` state and runs its initializers +
   `Start{}`. The main server boots automatically on first use.

## Wiring an AI (generated-class path)

```csharp
// One-time, in the editor (menu item, button, script):
FsmbModule module; string err;
FsmbReader.Read(bytes, out module, out err);
string src = ClassGenerator.GenerateSource(module, "Patrol", "PatrolAI", "MyFSM/Patrol");
System.IO.File.WriteAllText("Assets/MyFSM/PatrolAI.cs", src);
```

Use `PatrolAI` like `FsmbAIInstance`, but with the module path pinned and
`State_*` / `Slot_*` constants instead of magic strings/ints. Override
`OnBindingsRequired()` to bind scene objects in code (runs after the
inspector list, before boot) — see `Samples/PatrolAI.Manual.cs` for the
durable pattern (a hand-written partial file implementing
`OnBindingsManual()`; the generated file itself is rewritten on every
recompile).

## Multiple AIs & scenes

- Add as many AI components as you like; each is an independent instance
  with its own variables, handles and goals. Each ticks itself in its own
  `Update()` (Unity component order); the main server only keeps the
  registry. If you override `Update()` in a subclass, call `base.Update()`
  or that AI silently stops ticking.
- The main server object (`"MyFSM MainServer"`) is created automatically,
  survives scene loads (`DontDestroyOnLoad`), and self-destructs duplicates,
  so exactly one exists at runtime.
- Bindings are per-AI; queries, broadcasts and the DB are global to the
  main server.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `no module: this component has nothing to boot from` | A bare `AIInstance` with neither embedded bytes nor an assigned asset. Use a generated class (self-contained), or `FsmbAIInstance` with a module assigned. |
| A generated class shows no module slot | Correct — it carries its own module. Only `FsmbAIInstance` has a module field. |
| `no module at Resources path 'X'` | The class expects a `Resources/` TextAsset that is not there. Regenerate so the class carries its own bytes, or put the `.bytes` (not the `.fsmb`) under `Resources/`. |
| `boot failed: …` (reader/validator message) | The `.fsmb` is corrupt or not v0.5 — recompile with a matching `fsmc`. |
| `module must declare exactly one entry state` | Module has zero (or several) `@ENTRY` states — fix the source. |
| `…: null handle` spam in the log | A handle slot is unbound (or was destroyed). Bind it; calls fail soft with defaults until you do. |
| AI never leaves its first state | Check Traversals conditions, `wait()` suspensions, and that the target states exist (external transitions to unknown states are rejected). |
| `already booted` | `BootWithBytes` called twice — use `RebootWithBytes` for hot-reload. |
| Query responses never arrive | You may be at the client cap (15) — `Enqueue` returns false; drain responses and retry (see `Docs/QueryServer.md`). |

## Verify the install

Without Unity, from this repo:

```sh
cd Sandbox
dotnet run -- Vectors/   # headless smoke test: reader, ticks, servers
python3 check.py         # .cs syntax + catalog/dispatcher ID consistency
```

## Uninstall

Delete the installed folder (and any generated `*AI.cs` files). The runtime
writes nothing outside its own GameObjects — no project settings touched.
