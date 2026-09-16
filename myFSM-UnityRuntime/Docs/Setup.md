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

1. Compile: `fsmc patrol.fsm -o patrol.fsmb`.
2. If you use the inspector-driven component, put the `.fsmb` anywhere and
   assign it directly — no Resources folder needed.
3. If you use a **generated class**, the `.fsmb` must live under a
   `Resources/` folder, because the class loads it with
   `Resources.Load<TextAsset>(ModuleResourcePath)`. Example:
   `Assets/Resources/MyFSM/Patrol.fsmb` <=> path `"MyFSM/Patrol"`
   (extension dropped, exactly as `ClassGenerator` emits it).

## Wiring an AI (inspector path)

1. Select a GameObject, Add Component → `FsmbAIInstance`.
2. Assign the `.fsmb` to the module slot.
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
inspector list, before boot) — see `Samples/PatrolAI.cs` for the pattern.

## Multiple AIs & scenes

- Add as many AI components as you like; each is an independent instance
  with its own variables, handles and goals. The main server ticks them in
  registration order.
- The main server object (`"MyFSM MainServer"`) is created automatically,
  survives scene loads (`DontDestroyOnLoad`), and self-destructs duplicates,
  so exactly one exists at runtime.
- Bindings are per-AI; queries, broadcasts and the DB are global to the
  main server.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `no .fsmb module asset assigned` | No TextAsset assigned and (for generated classes) no module at the Resources path. |
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
