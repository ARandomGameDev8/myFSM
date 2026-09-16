#!/usr/bin/env python3
"""myFSM Unity-runtime installer (Python 3.8+, stdlib only, single file).

Traditional UX: the installer detects Unity, fetches the myFSM-UnityRuntime
payload from one of three sources, discovers your Unity projects, lets you
pick one, lets you pick an in-project install location (default from the
payload manifest), and copies the files in. Nothing is installed outside the
project folder you choose.

Payload sources:
  internet   download a release archive (default URL below, overridable)
  local      use a folder or .zip/.tar.gz you already have on disk
  git        shallow-clone a repo URL + branch

Examples:
  python3 install.py                                  fully interactive
  python3 install.py --source local --local-path ./pkg --project ~/Proj --yes
  python3 install.py --list-editors --list-projects   diagnostics only
"""
import argparse
import datetime
import json
import os
import platform
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
import zipfile

APP_NAME = "myFSM Unity Runtime"
MANIFEST_NAME = "myfsm-package.json"
RECEIPT_NAME = ".myfsm-install.json"
# Where the runtime lives today (see repo branches); override with --url.
DEFAULT_URL = "https://github.com/ARandomGameDev8/MyFSM-UnityRuntime/archive/refs/heads/test.zip"
DEFAULT_GIT_URL = "https://github.com/ARandomGameDev8/MyFSM-UnityRuntime.git"
DEFAULT_GIT_BRANCH = "test"


# --------------------------------------------------------------------------
# Small helpers
# --------------------------------------------------------------------------

def host_os():
    sysname = platform.system().lower()
    if sysname.startswith("win"):
        return "windows"
    if sysname == "darwin":
        return "macos"
    return "linux"


def expand(path):
    return os.path.abspath(os.path.expandvars(os.path.expanduser(path))) if path else path


def info(msg):
    print(msg)


def warn(msg):
    print("warning: " + msg)


def fail(msg):
    print("error: " + msg, file=sys.stderr)
    return 1


def prompt(text, default=None):
    suffix = " [%s]: " % default if default else ": "
    try:
        answer = input(text + suffix).strip()
    except EOFError:
        answer = ""
    return answer if answer else (default or "")


def confirm(text, default_yes=True, auto_yes=False):
    if auto_yes:
        return True
    hint = "Y/n" if default_yes else "y/N"
    try:
        answer = input(text + " [%s]: " % hint).strip().lower()
    except EOFError:
        answer = ""
    if not answer:
        return default_yes
    return answer in ("y", "yes")


def pick_numbered(title, items, allow_custom=False, auto_yes=False):
    """items: list of (label, value). Returns value, custom string, or None."""
    print(title)
    for i, (label, _value) in enumerate(items, 1):
        print("  %d) %s" % (i, label))
    if allow_custom:
        print("  (or type a path directly)")
    while True:
        if auto_yes:
            return None
        choice = prompt("Pick a number%s" % (" or a path" if allow_custom else ""), "")
        if choice.isdigit():
            idx = int(choice) - 1
            if 0 <= idx < len(items):
                return items[idx][1]
        elif allow_custom and choice:
            return choice
        print("Please enter 1-%d%s." % (len(items), " or a path" if allow_custom else ""))


def is_within(child, parent):
    try:
        return os.path.commonpath([os.path.abspath(child)]) == os.path.abspath(parent) or \
            os.path.commonpath([os.path.abspath(child), os.path.abspath(parent)]) == os.path.abspath(parent)
    except ValueError:
        return False


# --------------------------------------------------------------------------
# Stage 1: Unity detection (best effort, never fatal)
# --------------------------------------------------------------------------

def hub_data_dirs():
    home = os.path.expanduser("~")
    if host_os() == "windows":
        base = os.environ.get("APPDATA", os.path.join(home, "AppData", "Roaming"))
        return [os.path.join(base, "UnityHub")]
    if host_os() == "macos":
        return [os.path.join(home, "Library", "Application Support", "UnityHub")]
    return [os.path.join(home, ".config", "UnityHub"),
            os.path.join(home, ".config", "Unity Hub")]


def guess_version(path):
    # .../Hub/Editor/2022.3.11f1/... -> 2022.3.11f1 (best effort)
    parts = path.replace("\\", "/").split("/")
    for i, part in enumerate(parts):
        if part == "Editor" and i + 1 < len(parts) and parts[i + 1][0:1].isdigit():
            return parts[i + 1]
    return "unknown"


def find_unity_editors():
    found = {}  # exe path -> version
    roots = []
    if host_os() == "windows":
        pf = os.environ.get("ProgramFiles", r"C:\Program Files")
        roots += [os.path.join(pf, "Unity", "Hub", "Editor"),
                  os.path.join(pf, "Unity")]
        # Legacy registry keys (Unity 5.x era, harmless if absent).
        try:
            import winreg
            for hive in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
                for key_path in (r"SOFTWARE\Unity Technologies\Unity Editor 5.x",
                                 r"SOFTWARE\WOW6432Node\Unity Technologies\Unity Editor 5.x"):
                    try:
                        with winreg.OpenKey(hive, key_path) as key:
                            loc, _ = winreg.QueryValueEx(key, "Location")
                            exe = os.path.join(loc, "Editor", "Unity.exe")
                            if os.path.isfile(exe):
                                found[os.path.abspath(exe)] = "registry"
                    except OSError:
                        pass
        except ImportError:
            pass
        for probe in ("where Unity.exe",):
            try:
                out = subprocess.run(probe, shell=True, capture_output=True,
                                     text=True, timeout=10).stdout
                for line in out.splitlines():
                    line = line.strip()
                    if line.lower().endswith("unity.exe") and os.path.isfile(line):
                        found[os.path.abspath(line)] = guess_version(line)
            except (OSError, subprocess.SubprocessError):
                pass
    elif host_os() == "macos":
        home = os.path.expanduser("~")
        roots += ["/Applications/Unity/Hub/Editor",
                  os.path.join(home, "Applications", "Unity", "Hub", "Editor"),
                  "/Applications/Unity"]
    else:
        home = os.path.expanduser("~")
        roots += [os.path.join(home, "Unity", "Hub", "Editor"),
                  "/opt/Unity", "/opt/unity", "/usr/share/unity"]
        for name in ("unity", "Unity"):
            exe = shutil.which(name)
            if exe:
                found[os.path.abspath(exe)] = guess_version(exe)
    for root in roots:
        if not os.path.isdir(root):
            continue
        try:
            children = sorted(os.listdir(root))
        except OSError:
            continue
        for child in children:
            full = os.path.join(root, child)
            exe = None
            if host_os() == "windows" and os.path.isfile(os.path.join(full, "Editor", "Unity.exe")):
                exe = os.path.join(full, "Editor", "Unity.exe")
            elif host_os() == "macos":
                cand = os.path.join(full, "Unity.app", "Contents", "MacOS", "Unity")
                if os.path.isfile(cand):
                    exe = cand
            else:
                cand = os.path.join(full, "Editor", "Unity")
                if os.path.isfile(cand) and os.access(cand, os.X_OK):
                    exe = cand
            if exe:
                found[os.path.abspath(exe)] = guess_version(full)
    return sorted(found.items(), key=lambda kv: kv[0])


def walk_strings(obj, out, depth=0):
    if depth > 6 or len(out) > 2000:
        return
    if isinstance(obj, str):
        out.append(obj)
    elif isinstance(obj, dict):
        for value in obj.values():
            walk_strings(value, out, depth + 1)
    elif isinstance(obj, (list, tuple)):
        for value in obj:
            walk_strings(value, out, depth + 1)


def hub_recent_projects():
    """Best-effort Unity Hub recent-project list (tolerant of schema drift)."""
    projects = []
    seen = set()
    for hubdir in hub_data_dirs():
        if not os.path.isdir(hubdir):
            continue
        for dirpath, _dirnames, filenames in os.walk(hubdir):
            if dirpath.count(os.sep) - hubdir.count(os.sep) > 3:
                continue
            for filename in filenames:
                if not filename.endswith(".json"):
                    continue
                path = os.path.join(dirpath, filename)
                try:
                    if os.path.getsize(path) > 5 * 1024 * 1024:
                        continue
                    with open(path, "r", encoding="utf-8", errors="replace") as fh:
                        data = json.load(fh)
                except (OSError, ValueError):
                    continue
                strings = []
                walk_strings(data, strings)
                for s in strings:
                    cand = s.strip().strip('"')
                    if cand in seen or len(cand) < 3 or len(cand) > 512:
                        continue
                    seen.add(cand)
                    if is_unity_project(cand):
                        projects.append(os.path.abspath(cand))
    return sorted(set(projects))


def is_unity_project(path):
    if not path or not os.path.isdir(path):
        return False
    return os.path.isdir(os.path.join(path, "Assets"))


# --------------------------------------------------------------------------
# Stage 2: payload acquisition
# --------------------------------------------------------------------------

def download_file(url, dest):
    req = urllib.request.Request(url, headers={"User-Agent": "myfsm-installer/0.1"})
    with urllib.request.urlopen(req, timeout=60) as resp, open(dest, "wb") as out:
        total = resp.headers.get("Content-Length")
        total = int(total) if total and total.isdigit() else 0
        got = 0
        while True:
            chunk = resp.read(1024 * 256)
            if not chunk:
                break
            out.write(chunk)
            got += len(chunk)
            if total:
                print("\r  %.1f/%.1f MB (%d%%)" % (got / 1e6, total / 1e6, got * 100 // total),
                      end="", flush=True)
            else:
                print("\r  %.1f MB" % (got / 1e6,), end="", flush=True)
    print()


def assert_safe_member(name):
    norm = os.path.normpath(name)
    if os.path.isabs(norm) or norm.startswith("..") or ".." + os.sep in norm:
        raise ValueError("unsafe archive member: %r" % name)


def collapse_single_top_dir(directory):
    try:
        children = [c for c in os.listdir(directory) if c != "__MACOSX"]
    except OSError:
        return directory
    if len(children) == 1 and os.path.isdir(os.path.join(directory, children[0])):
        return os.path.join(directory, children[0])
    return directory


def extract_archive(archive, dest):
    lower = archive.lower()
    if lower.endswith(".zip"):
        with zipfile.ZipFile(archive) as zf:
            for member in zf.namelist():
                assert_safe_member(member)
            zf.extractall(dest)
    elif lower.endswith((".tar.gz", ".tgz", ".tar")):
        with tarfile.open(archive) as tf:
            for member in tf.getnames():
                assert_safe_member(member)
            tf.extractall(dest)
    else:
        raise ValueError("unsupported archive type (want .zip/.tar.gz): %s" % archive)
    return collapse_single_top_dir(dest)


class Payload(object):
    def __init__(self, root, label, tempdir=None):
        self.root = root
        self.label = label
        self.tempdir = tempdir  # TemporaryDirectory or None (caller-owned path)

    def cleanup(self):
        if self.tempdir is not None:
            self.tempdir.cleanup()


def acquire_internet(url, workdir):
    info("Downloading %s" % url)
    archive = os.path.join(workdir, "payload" + os.path.splitext(url.split("?")[0])[1].lower())
    if not archive.lower().endswith((".zip", ".tar.gz", ".tgz", ".tar")):
        archive += ".zip"  # GitHub codeload URLs carry no suffix; they serve zips
    download_file(url, archive)
    outdir = os.path.join(workdir, "extracted")
    os.makedirs(outdir, exist_ok=True)
    root = extract_archive(archive, outdir)
    return Payload(root, "internet: " + url)


def acquire_local(path, workdir):
    path = expand(path)
    if os.path.isdir(path):
        return Payload(path, "local folder: " + path)
    if os.path.isfile(path):
        outdir = os.path.join(workdir, "extracted")
        os.makedirs(outdir, exist_ok=True)
        root = extract_archive(path, outdir)
        return Payload(root, "local archive: " + path)
    raise ValueError("local path does not exist: %s" % path)


def acquire_git(url, branch, workdir):
    if not shutil.which("git"):
        raise ValueError("git not found on PATH; use the internet or local source instead")
    dest = os.path.join(workdir, "clone")
    cmd = ["git", "clone", "--depth", "1"]
    if branch:
        cmd += ["--branch", branch]
    cmd += [url, dest]
    info("Running: " + " ".join(cmd))
    proc = subprocess.run(cmd, capture_output=True, text=True, timeout=300)
    if proc.returncode != 0:
        raise ValueError("git clone failed: " + (proc.stderr or proc.stdout).strip()[-500:])
    return Payload(dest, "git: %s (%s)" % (url, branch or "default branch"))


def load_manifest(payload_root):
    path = os.path.join(payload_root, MANIFEST_NAME)
    if not os.path.isfile(path):
        raise ValueError("payload has no %s at its root (%s) — wrong folder/archive?"
                         % (MANIFEST_NAME, payload_root))
    with open(path, "r", encoding="utf-8") as fh:
        manifest = json.load(fh)
    if not isinstance(manifest.get("files"), list) or not manifest["files"]:
        raise ValueError("%s lists no files to install" % MANIFEST_NAME)
    manifest.setdefault("exclude", [])
    manifest.setdefault("defaultInstallDir", "Assets/MyFSM")
    manifest.setdefault("version", "unknown")
    manifest.setdefault("name", "myFSM runtime")
    return manifest


# --------------------------------------------------------------------------
# Stages 3-5: project + location + install
# --------------------------------------------------------------------------

def choose_project(discovered, preset, auto_yes):
    if preset:
        path = expand(preset)
        if not is_unity_project(path):
            raise ValueError("not a Unity project (no Assets/ folder): %s" % path)
        return path
    if auto_yes:
        raise ValueError("--yes needs --project <path> (no safe default project)")
    items = [(p, p) for p in discovered]
    if items:
        choice = pick_numbered("Discovered Unity projects:", items, allow_custom=True)
    else:
        warn("no projects found via Unity Hub; type one in.")
        choice = prompt("Unity project folder")
    path = expand(choice)
    if not is_unity_project(path):
        raise ValueError("not a Unity project (no Assets/ folder): %s" % path)
    if not os.path.isdir(os.path.join(path, "ProjectSettings")):
        warn("no ProjectSettings/ here; continuing, but this may not be a real project.")
    return path


def choose_location(project, manifest, preset, auto_yes):
    default = manifest.get("defaultInstallDir") or "Assets/MyFSM"
    location = preset if preset else (default if auto_yes else prompt(
        "Install location inside the project", default))
    dest = os.path.abspath(os.path.join(project, location.replace("\\", "/")))
    if not is_within(dest, project):
        raise ValueError("location escapes the project folder; it must stay inside:\n  %s" % project)
    assets = os.path.join(project, "Assets")
    if os.path.abspath(dest) != os.path.abspath(assets) and not is_within(dest, assets):
        warn("outside Assets/ — Unity only imports files under Assets/ (or Packages/).")
        if not auto_yes and not confirm("Install outside Assets/ anyway?", default_yes=False):
            raise ValueError("aborted by user")
    return dest


def existing_install(dest):
    receipt = os.path.join(dest, RECEIPT_NAME)
    if os.path.isfile(receipt):
        try:
            with open(receipt, "r", encoding="utf-8") as fh:
                return json.load(fh)
        except (OSError, ValueError):
            return {"unreadable_receipt": True}
    if os.path.isdir(dest) and os.listdir(dest):
        return {"unreceipted_files": True}
    return None


def resolve_overwrite(dest, project, auto_yes):
    """Returns 'merge', 'clean', or raises to abort. Never cleans risky roots."""
    if auto_yes:
        return "merge"
    print("Destination already has files:\n  %s" % dest)
    print("  1) Overwrite-merge (copy over; stale files may linger)")
    print("  2) Clean first, then install (deletes the destination dir)")
    print("  3) Abort")
    while True:
        choice = prompt("Pick", "1")
        if choice == "1":
            return "merge"
        if choice == "3":
            raise ValueError("aborted by user")
        if choice == "2":
            norm = os.path.normpath(os.path.abspath(dest))
            protected = {os.path.normpath(os.path.abspath(project)),
                         os.path.normpath(os.path.join(project, "Assets")),
                         os.path.normpath(os.path.join(project, "Packages"))}
            if norm in protected:
                print("Refusing to clean a protected root; pick merge or abort.")
                continue
            if confirm("Delete %s and reinstall?" % norm, default_yes=False):
                return "clean"


def should_skip(rel, excludes):
    rel = rel.replace(os.sep, "/")
    for ex in excludes:
        ex = ex.strip().replace("\\", "/").rstrip("/")
        if not ex:
            continue
        if rel == ex or rel.startswith(ex + "/"):
            return True
    return False


def copy_entries(payload_root, manifest, dest):
    excludes = manifest.get("exclude") or []
    copied = 0
    for entry in manifest["files"]:
        rel = entry.strip().replace("\\", "/").rstrip("/")
        if not rel or should_skip(rel, excludes):
            continue
        src = os.path.join(payload_root, rel)
        target = os.path.join(dest, rel)
        if os.path.isdir(src):
            for dirpath, _dirnames, filenames in os.walk(src):
                for filename in filenames:
                    full = os.path.join(dirpath, filename)
                    relpath = os.path.relpath(full, payload_root).replace(os.sep, "/")
                    if should_skip(relpath, excludes):
                        continue
                    out = os.path.join(dest, relpath)
                    os.makedirs(os.path.dirname(out), exist_ok=True)
                    shutil.copy2(full, out)
                    copied += 1
        elif os.path.isfile(src):
            os.makedirs(os.path.dirname(target) or dest, exist_ok=True)
            shutil.copy2(src, target)
            copied += 1
        else:
            warn("manifest entry missing from payload, skipped: %s" % rel)
    return copied


def write_receipt(dest, manifest, label, copied):
    receipt = {"tool": manifest.get("name"), "version": manifest.get("version"),
               "source": label,
               "installed_at_utc": datetime.datetime.now(datetime.timezone.utc).strftime(
                   "%Y-%m-%dT%H:%M:%SZ"),
               "files_copied": copied}
    with open(os.path.join(dest, RECEIPT_NAME), "w", encoding="utf-8") as fh:
        json.dump(receipt, fh, indent=2)
    return receipt


# --------------------------------------------------------------------------
# Main flow
# --------------------------------------------------------------------------

def parse_args(argv):
    ap = argparse.ArgumentParser(description="Install the myFSM Unity runtime into a Unity project.")
    ap.add_argument("--source", choices=["internet", "local", "git"], default=None,
                    help="payload source (default: ask, or internet with --yes)")
    ap.add_argument("--url", default=None, help="payload archive URL (internet source)")
    ap.add_argument("--local-path", default=None, help="folder or archive (local source)")
    ap.add_argument("--git-url", default=None, help="repo URL (git source)")
    ap.add_argument("--branch", default=None, help="git branch (git source)")
    ap.add_argument("--project", default=None, help="Unity project folder")
    ap.add_argument("--dest", default=None, help="in-project install location")
    ap.add_argument("--yes", action="store_true", help="accept defaults (needs --project)")
    ap.add_argument("--list-editors", action="store_true", help="print Unity installs and exit")
    ap.add_argument("--list-projects", action="store_true", help="print Hub projects and exit")
    return ap.parse_args(argv)


def main(argv=None):
    args = parse_args(argv or sys.argv[1:])
    auto_yes = args.yes

    if args.list_editors or args.list_projects:
        if args.list_editors:
            editors = find_unity_editors()
            print("Unity editors (%d):" % len(editors))
            for path, version in editors:
                print("  %s  [%s]" % (path, version))
        if args.list_projects:
            projects = hub_recent_projects()
            print("Hub projects (%d):" % len(projects))
            for path in projects:
                print("  %s" % path)
        return 0

    print("== %s installer ==" % APP_NAME)

    # --- Stage 1: Unity detection (informational; install is just file copy)
    print("\n[1/5] Detecting Unity (%s)..." % host_os())
    editors = find_unity_editors()
    if editors:
        for path, version in editors:
            print("  editor %s  [%s]" % (version, path))
    else:
        warn("no Unity installs found; install still works (files only), "
             "but open the project in Unity afterwards to compile.")

    # --- Stage 2: payload
    print("\n[2/5] Fetching the payload...")
    source = args.source
    if source is None:
        if auto_yes:
            source = "internet"
        else:
            print("  1) internet (download the tool)")
            print("  2) local folder or archive (already downloaded)")
            print("  3) git clone (repo + branch)")
            choice = prompt("Payload source", "1")
            source = {"1": "internet", "2": "local", "3": "git"}.get(choice, "internet")
    workdir = tempfile.TemporaryDirectory(prefix="myfsm-install-")
    try:
        if source == "internet":
            url = args.url or (None if not auto_yes else DEFAULT_URL)
            if url is None:
                url = prompt("Archive URL", DEFAULT_URL)
            payload = acquire_internet(url, workdir.name)
        elif source == "local":
            local_path = args.local_path
            if local_path is None:
                if auto_yes:
                    return fail("--yes needs --local-path with --source local")
                local_path = prompt("Folder or archive path")
            payload = acquire_local(local_path, workdir.name)
        else:
            git_url = args.git_url
            branch = args.branch
            if git_url is None:
                if auto_yes:
                    return fail("--yes needs --git-url with --source git")
                git_url = prompt("Repo URL", DEFAULT_GIT_URL)
                branch = prompt("Branch", branch or DEFAULT_GIT_BRANCH)
            elif branch is None:
                branch = DEFAULT_GIT_BRANCH
            payload = acquire_git(git_url, branch, workdir.name)
        try:
            manifest = load_manifest(payload.root)
        except ValueError as ex:
            return fail(str(ex))
        info("Payload: %s v%s (%s)" % (manifest.get("name"), manifest.get("version"),
                                       payload.label))

        # --- Stage 3: project
        print("\n[3/5] Finding your Unity project...")
        discovered = hub_recent_projects()
        try:
            project = choose_project(discovered, args.project, auto_yes)
        except ValueError as ex:
            return fail(str(ex))
        info("Project: %s" % project)

        # --- Stage 4: location
        print("\n[4/5] Install location...")
        try:
            dest = choose_location(project, manifest, args.dest, auto_yes)
        except ValueError as ex:
            return fail(str(ex))
        info("Destination: %s" % dest)
        mode = "fresh"
        if existing_install(dest):
            try:
                mode = resolve_overwrite(dest, project, auto_yes)
            except ValueError as ex:
                return fail(str(ex))
            if mode == "clean":
                shutil.rmtree(dest)

        # --- Stage 5: install
        print("\n[5/5] Installing (%s)..." % ("overwrite-merge" if mode == "merge"
                                             else "fresh" if mode == "fresh" else "clean"))
        if not auto_yes:
            print("  %s v%s  ->  %s" % (manifest.get("name"), manifest.get("version"), dest))
            if not confirm("Install?", default_yes=True):
                return fail("aborted by user")
        copied = copy_entries(payload.root, manifest, dest)
        if copied == 0:
            return fail("nothing was copied; refusing to write a receipt")
        receipt = write_receipt(dest, manifest, payload.label, copied)
    finally:
        try:
            workdir.cleanup()
        except OSError:
            pass

    print("\nInstalled %s v%s: %d files -> %s" % (receipt["tool"], receipt["version"],
                                                 copied, dest))
    print("Next: open the project in Unity, let scripts compile, then add the")
    print("FsmBurstCompiler component (see Docs/Compiler.md in the payload).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
