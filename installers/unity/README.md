# myFSM Unity installer

Single-file Python installer (stdlib only, 3.8+) for the Unity runtime.
Download `install.py` and run it — no dependencies, no build step.

```bash
python3 install.py                  # fully interactive (recommended)
python3 install.py --help           # all flags
```

## What it does (6 stages)

1. **Detects Unity** installs (Hub dirs, well-known paths, PATH) per OS.
   Informational only — installing is file copy, so it proceeds with a
   warning when nothing is found.
2. **Fetches the payload** from one source (default: internet download of
   the tool archive; or point it at a local folder/zip, or a git URL +
   branch). Reads `myfsm-package.json` to learn what to copy and the
   default location.
3. **Installs the host compiler toolchain when missing.** Detects `fsmc`
   on PATH and in the platform-standard homes; if absent, installs the
   payload's `Tools/<rid>/` prebuilts (CLI + native lib + C header) to
   `%ProgramFiles%\myFSM\` (Windows) or `/usr/local` (macOS/Linux),
   falling back to the per-user home (`%LOCALAPPDATA%\myFSM\` / `~/.local`)
   when the system location isn't writable. Override with
   `--compiler-home`, supply prebuilts with `--compiler-url`, skip with
   `--no-compiler`. Never blocks the runtime install: problems here warn
   and continue.
4. **Discovers Unity projects** via Unity Hub recents (tolerant of Hub
   schema drift) or a path you type. The folder must contain `Assets/`.
5. **Picks the in-project location** (default `Assets/MyFSM` from the
   manifest). Hard rule: the destination must stay **inside the project
   folder** — escapes are rejected. Outside `Assets/` needs explicit
   confirmation. Existing installs offer overwrite-merge / clean / abort
   (clean refuses protected roots like the project or `Assets/` itself).
6. **Installs**: copies the manifest's `files` minus `exclude`, writes a
   `.myfsm-install.json` receipt (source, version, date, file count),
   prints next steps.

## Unity script GUID safety

Unity serializes a scene or prefab's `MonoBehaviour` reference using the GUID
in that script's `.meta` file. The source package currently does not ship
`.meta` files, so Unity creates project-local ones on first import. A clean
installer upgrade now preserves matching existing `.meta` sidecars (and their
GUIDs), while dropping metadata for assets removed from the new payload. The
existing project's metadata wins for retained assets; payload metadata is used
for new assets and on first install.

This protection applies to clean upgrades through this installer. If you
manually delete the installed folder or its `.meta` files, Unity will mint new
GUIDs and existing scene/prefab components can become **Missing Script**.
Keep the local metadata, and check Console for compile errors if a component is
missing despite its GUID still resolving. The installer tests can be run with:

```bash
python3 -m unittest discover -s tests
```

## Non-interactive use

```bash
python3 install.py --source local --local-path ./myFSM-UnityRuntime \
  --project ~/Unity/MyGame --yes
python3 install.py --source git --git-url <url> --branch test \
  --project "C:\Unity\MyGame" --dest Assets/ThirdParty/MyFSM --yes
python3 install.py --list-editors --list-projects   # diagnostics only
```

`--yes` still requires `--project` (there is no safe default project).
Exit code is 0 on success, 1 with an `error:` line otherwise.
At any `[default]:` prompt, Enter (or bare `y`) accepts the default;
invalid input explains itself and asks again instead of crashing.
