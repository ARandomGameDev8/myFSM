#!/usr/bin/env python3
"""Checks that a Unity project actually holds every file the tests need.

A test folder copied by hand is easy to get nearly right: one missing script
(typically a shared one) or one file copied from an older checkout turns into
CS0246 / CS0103 errors in Unity, which point at the file that *uses* the type
rather than at the file that is missing. This tool reads the checkout you are
standing in and compares it with the project.

    python3 Tests/Tools/check_project.py /path/to/UnityProject

Required = everything under Tests/ and Runtime/ except Tests/Tools/ (the Python
helpers; they are only needed to regenerate a class, never to run a test).
Matching is by FILE NAME, so renamed or reorganised folders are fine.

What it reports:

  MISSING   a file the tests/runtime need is nowhere in the project (by file
            name, so renamed folders are fine). It also names the public types
            that file declares, i.e. the ones Unity will fail to find.
  STALE     the file is there but its bytes differ from this checkout — usually
            an older copy (the fix for a bug you already have, not yet).
  EXTRA     informational only: project files with these names that this
            checkout does not know about.

Exit code 0 = nothing missing and nothing stale, 1 = problems, 2 = bad usage.
"""
import hashlib
import os
import re
import sys

TESTS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PACKAGE = os.path.dirname(TESTS)
REPO = os.path.dirname(PACKAGE)

# What the four test cases actually need to compile and run.
REQUIRED_ROOTS = [
    (os.path.join(PACKAGE, "Tests"), "tests"),
    (os.path.join(PACKAGE, "Runtime"), "runtime"),
]
INFORMATIONAL_ROOTS = [
    (os.path.join(PACKAGE, "Samples"), "samples"),
    (os.path.join(PACKAGE, "Editor"), "editor"),
]

TYPE_DECL = re.compile(
    r"^\s*(?:public|internal)\s+"
    r"(?:(?:sealed|static|abstract|partial|readonly|unsafe)\s+)*"
    r"(?:class|struct|enum|interface)\s+([A-Za-z_][A-Za-z0-9_]*)", re.M)


def collect(root, skip_dirs=()):
    """{basename: [full paths]} for a source root, skipping build leftovers."""
    found = {}
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames
                       if d not in ("obj", "bin", ".git") and d not in skip_dirs]
        for name in filenames:
            if name.endswith(".meta"):
                continue
            found.setdefault(name, []).append(os.path.join(dirpath, name))
    return found


def best_candidate(expected_path, candidates, project_root):
    """The candidate sharing the longest tail with the expected path.

    A basename can repeat across a payload (README.md lives at the package root
    AND under Tests/): pick the one that is actually this file, so a stale
    report names the right copy.
    """
    want = expected_path.replace(os.sep, "/").split("/")
    best, best_score = candidates[0], -1
    for candidate in candidates:
        have = os.path.relpath(candidate, project_root).replace(os.sep, "/").split("/")
        score = 0
        while score < len(want) and score < len(have) and want[-1 - score] == have[-1 - score]:
            score += 1
        if score > best_score:
            best, best_score = candidate, score
    return best


def digest(path):
    with open(path, "rb") as handle:
        return hashlib.sha256(handle.read()).hexdigest()


def declared_types(path):
    if not path.endswith(".cs"):
        return []
    try:
        with open(path, encoding="utf-8") as handle:
            return TYPE_DECL.findall(handle.read())
    except OSError:
        return []


def compare(expected_root, label, project, problems, stale, missing):
    expected = collect(expected_root)
    for name in sorted(expected):
        sources = expected[name]
        candidates = project.get(name, [])
        if not candidates:
            for source in sources:
                types = declared_types(source)
                missing.append((label, os.path.relpath(source, REPO), types))
            continue
        want = digest(sources[0])
        if not any(digest(c) == want for c in candidates):
            chosen = best_candidate(sources[0], candidates, project_root_placeholder)
            stale.append((label, os.path.relpath(sources[0], REPO),
                          os.path.relpath(chosen, project_root_placeholder)))


project_root_placeholder = None


def main(argv):
    global project_root_placeholder
    if len(argv) != 2:
        print(__doc__.strip().splitlines()[-1])
        return 2
    root = os.path.abspath(argv[1])
    if not os.path.isdir(os.path.join(root, "Assets")):
        print("%s does not look like a Unity project (no Assets/ folder)." % root)
        return 2
    project_root_placeholder = root

    project = collect(root, skip_dirs=(".git", "Library", "Temp", "obj", "bin"))
    missing, stale, informational = [], [], []
    for expected_root, label in REQUIRED_ROOTS:
        if not os.path.isdir(expected_root):
            print("this checkout has no %s — run the tool from the repo" % expected_root)
            return 2
        skip = ("Tools",) if label == "tests" else ()
        expected = collect(expected_root, skip_dirs=skip)
        for name in sorted(expected):
            sources = [p for p in expected[name] if not any(
                ("/%s/" % d) in p.replace(os.sep, "/") for d in skip)]
            if not sources:
                continue  # a skipped helper (Tests/Tools/*.py)
            candidates = project.get(name, [])
            if not candidates:
                for source in sources:
                    missing.append((label, os.path.relpath(source, REPO),
                                    declared_types(source)))
                continue
            want = digest(sources[0])
            if not any(digest(c) == want for c in candidates):
                chosen = best_candidate(sources[0], candidates, root)
                stale.append((label, os.path.relpath(sources[0], REPO),
                              os.path.relpath(chosen, root)))
            else:
                informational.append("present: " + name)
    informational = [i for i in informational if False]  # keep quiet when present

    print("project : %s" % root)
    print("checkout: %s" % PACKAGE)

    if missing:
        print("\nMISSING (%d) — copy these into the project (any folder under Assets/):" % len(missing))
        for label, rel, types in missing:
            print("  - %s   [%s]" % (rel, label))
            if types:
                print("      declares: %s  -> referencing it fails with CS0246"
                      % ", ".join(types[:6]))
    if stale:
        print("\nSTALE (%d) — present, but not the copy this checkout has:" % len(stale))
        for label, rel, found in stale:
            print("  - %s   [%s]" % (rel, label))
            print("      project copy: %s" % found)

    optional = []
    for expected_root, label in INFORMATIONAL_ROOTS:
        if os.path.isdir(expected_root):
            for name in collect(expected_root):
                if name not in project:
                    optional.append("%s   [%s]" % (name, label))
    informational = optional
    if informational:
        print("\nnot installed (fine unless you want them): %d file(s), e.g. %s"
              % (len(informational), ", ".join(informational[:5])))

    if missing or stale:
        print("\n---- %d missing, %d stale: Unity will report errors in the files that"
              "\n     REFERENCE these types, not in the missing ones." % (len(missing), len(stale)))
        return 1
    print("\n---- every test and runtime file the tests need is present and up to date")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
