#!/usr/bin/env python3
"""Checks that every test case's compiled module and generated class agree.

For each Tests/<case>/Fsm/*.fsm it verifies, in order:

1. the committed .fsmb IS what the committed .fsm compiles to (fsmc is
   deterministic, so this catches "edited the .fsm, forgot to recompile");
2. the committed .fsmd is the current disassembly of that .fsmb (so the file a
   human reads describes the bytes that are actually shipped);
3. the generated class in Scripts/ matches that disassembly: module name and
   version, state count, one State_<name> const per state, one
   Slot_<name> = <n>; // <Type> const per runtime variable, and
4. the base64 blob inside the class decodes to bytes IDENTICAL to the .fsmb, so
   the class really does carry this module and not an older one.

    python3 Tests/Tools/verify_classes.py [--fsmc PATH]

Exit code 0 = every case consistent, 1 = at least one problem (printed).
"""
import base64
import os
import re
import subprocess
import sys
import tempfile

TESTS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO_ROOT = os.path.dirname(os.path.dirname(TESTS))
DEFAULT_FSMC = os.path.join(REPO_ROOT, "bin", "fsmc")


def parse_disassembly(text):
    """States and runtime variables (name, type, slot) out of a .fsmd."""
    states, runtime = [], []
    section = None
    for line in text.splitlines():
        head = re.match(r"\s*SECTION \[(\d+)\] (\w+)", line)
        if head:
            section = head.group(2)
            continue
        row = re.match(r"\s*#(\d+)\s+(\S+)\s+(.*)$", line)
        if not row or section is None:
            continue
        if section == "STATES":
            states.append(row.group(2))
        elif section == "RUNTIME":
            slot = re.search(r"slot\s+(\d+)", row.group(3))
            if slot:
                runtime.append((row.group(2), row.group(3).split()[0], int(slot.group(1))))
    version = re.search(r"format\s+fsmb v(\d+)\.(\d+)", text)
    return (tuple(int(g) for g in version.groups()) if version else None), states, runtime


def normalize_disassembly(text, path):
    """The `file <path> (n bytes)` header line depends on how fsmc was called."""
    return re.sub(r"(?m)^\s*file\s+.*$", "file <normalized>", text).replace(path, "<path>")


def run_fsmc(fsmc, args):
    result = subprocess.run([fsmc] + args, capture_output=True, text=True)
    return result.returncode == 0, (result.stdout + result.stderr)


def check_case(fsmc, test_dir, problems):
    fsm_dir = os.path.join(test_dir, "Fsm")
    scripts_dir = os.path.join(test_dir, "Scripts")
    name = os.path.basename(test_dir.rstrip(os.sep))
    sources = sorted(f for f in os.listdir(fsm_dir) if f.endswith(".fsm"))

    for source in sources:
        stem = source[:-4]
        fsm = os.path.join(fsm_dir, source)
        fsmb = os.path.join(fsm_dir, stem + ".fsmb")
        fsmd = os.path.join(fsm_dir, stem + ".fsmd")

        for path in (fsmb, fsmd):
            if not os.path.exists(path):
                problems.append("%s: missing %s" % (name, os.path.basename(path)))
        if problems:
            continue

        # 1. the .fsmb is what the .fsm compiles to
        with tempfile.TemporaryDirectory() as tmp:
            rebuilt = os.path.join(tmp, stem + ".fsmb")
            ok, output = run_fsmc(fsmc, [fsm, "-o", rebuilt])
            if not ok:
                problems.append("%s: fsmc failed on %s: %s" % (name, source, output.strip()))
                continue
            if open(rebuilt, "rb").read() != open(fsmb, "rb").read():
                problems.append("%s: %s is STALE — recompile it (the bytes differ)" % (name, os.path.basename(fsmb)))
                continue

            # 2. the .fsmd is the current disassembly
            fresh_fsmd = os.path.join(tmp, stem + ".fsmd")
            ok, output = run_fsmc(fsmc, ["-d", fsmb, "-o", fresh_fsmd])
            if not ok:
                problems.append("%s: fsmc -d failed on %s: %s" % (name, os.path.basename(fsmb), output.strip()))
                continue
            fresh_text = open(fresh_fsmd, encoding="utf-8").read()
            committed_text = open(fsmd, encoding="utf-8").read()
            if normalize_disassembly(fresh_text, fsmb) != normalize_disassembly(committed_text, fsmb):
                problems.append("%s: %s is stale — re-disassemble the module" % (name, os.path.basename(fsmd)))

        version, states, runtime = parse_disassembly(committed_text)
        if version is None:
            problems.append("%s: %s has no format line" % (name, os.path.basename(fsmd)))
            continue

        # 3. the generated class describes this module, and 4. carries its bytes
        classes = [f for f in sorted(os.listdir(scripts_dir))
                   if f.endswith("AI.cs") and not f.endswith(".Manual.cs")]
        if not classes:
            problems.append("%s: no generated class in Scripts/" % name)
            continue
        class_path = os.path.join(scripts_dir, classes[0])
        src = open(class_path, encoding="utf-8").read()

        head = re.search(r"// Module: (\S+) \(v(\d+)\.(\d+), (\d+) states\)", src)
        if not head:
            problems.append("%s: %s has no generated module header" % (name, classes[0]))
        else:
            if (int(head.group(2)), int(head.group(3))) != version:
                problems.append("%s: %s is for v%s.%s, module is v%d.%d"
                                % (name, classes[0], head.group(2), head.group(3), version[0], version[1]))
            if int(head.group(4)) != len(states):
                problems.append("%s: %s header says %s states, module has %d"
                                % (name, classes[0], head.group(4), len(states)))

        for state in states:
            needle = 'public const string State_%s = "%s";' % (state, state)
            if needle not in src:
                problems.append("%s: %s lacks `%s`" % (name, classes[0], needle))
        for var, type_name, slot in runtime:
            needle = "public const int Slot_%s = %d; // %s" % (var, slot, type_name)
            if needle not in src:
                problems.append("%s: %s lacks `%s`" % (name, classes[0], needle))

        region = re.search(r"FromBase64String\((.*?)\);", src, re.S)
        blob = "".join(re.findall(r'"([^"]*)"', region.group(1))) if region else ""
        try:
            embedded = base64.b64decode(blob)
        except Exception as exc:  # noqa: BLE001 - report and keep going
            embedded = b""
            problems.append("%s: %s blob does not decode: %s" % (name, classes[0], exc))
        module_bytes = open(fsmb, "rb").read()
        if embedded != module_bytes:
            problems.append("%s: %s embeds %d bytes, the module is %d — regenerate the class"
                            % (name, classes[0], len(embedded), len(module_bytes)))

        if not problems:
            print("ok   %s/%s: module %d bytes in sync, class %s matches "
                  "(%d states, %d runtime slots, blob identical)"
                  % (name, stem, len(module_bytes), classes[0], len(states), len(runtime)))


def main(argv):
    fsmc = DEFAULT_FSMC
    if len(argv) > 1:
        if len(argv) == 3 and argv[1] == "--fsmc":
            fsmc = argv[2]
        else:
            print(__doc__.strip().splitlines()[-2].strip())
            return 2
    if not os.path.exists(fsmc):
        print("fsmc not found at %s — build it with `make` first." % fsmc)
        return 2

    problems = []
    cases = sorted(
        os.path.join(TESTS, d) for d in os.listdir(TESTS)
        if os.path.isdir(os.path.join(TESTS, d, "Fsm"))
    )
    if not cases:
        print("no test cases found under %s" % TESTS)
        return 2
    for case in cases:
        before = len(problems)
        check_case(fsmc, case, problems)
        if len(problems) > before:
            print("FAIL %s" % os.path.basename(case))

    if problems:
        print("")
        for problem in problems:
            print("- " + problem)
        print("---- %d problem(s)" % len(problems))
        return 1

    print("---- every compiled module and generated class is consistent")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
