#!/usr/bin/env python3
"""myFSM-UnityRuntime static checks (no Unity/dotnet needed).

1. tree-sitter parses every .cs file (Runtime + Sandbox + Samples + Tests)
   without errors. The Tests folder holds the Play-mode test cases, whose
   generated/manual classes and scene scripts must at least be well-formed.
2. FunctionCatalog holds exactly 179 unique overload rows.
3. Every catalog ID has exactly one `case` in the Run dispatchers and vice versa.
4. Every catalog ID falls inside exactly one category dispatch range.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUNTIME = os.path.join(ROOT, "Runtime")
SANDBOX = os.path.join(ROOT, "Sandbox")
SAMPLES = os.path.join(ROOT, "Samples")
TESTS = os.path.join(ROOT, "Tests")

RANGES = [
    (0x0000, 0x0018), (0x0100, 0x0115), (0x0200, 0x020B), (0x0300, 0x0311),
    (0x0400, 0x0411), (0x0500, 0x0509), (0x0600, 0x062C), (0x0700, 0x070D),
    (0x0800, 0x0809), (0x0900, 0x0901), (0x0A00, 0x0A02),
]


def make_parser():
    import tree_sitter as ts
    import tree_sitter_c_sharp as tscs
    try:  # tree-sitter >= 0.25 API
        return ts.Parser(ts.Language(tscs.language()))
    except Exception:  # older API
        p = ts.Parser()
        p.set_language(tscs.language())
        return p


def parse_errors(parser, path):
    with open(path, "rb") as f:
        src = f.read()
    tree = parser.parse(src)
    errs = []

    def walk(node):
        if node.type == "ERROR" or node.is_missing:
            snippet = src[node.start_byte:node.start_byte + 60].decode(
                "utf8", "replace").replace("\n", "\\n")
            errs.append("%s %s @%s" % (node.type, snippet, node.start_point))
        for child in node.children:
            walk(child)

    walk(tree.root_node)
    return errs


def collect_cs_files():
    files = []
    for base in (RUNTIME, SANDBOX, SAMPLES, TESTS):
        for dirpath, _, names in os.walk(base):
            for name in names:
                if name.endswith(".cs"):
                    files.append(os.path.join(dirpath, name))
    return sorted(files)


def main():
    fails = 0

    # 1. syntax
    parser = make_parser()
    checked = 0
    for path in collect_cs_files():
        checked += 1
        errs = parse_errors(parser, path)
        rel = os.path.relpath(path, ROOT)
        if errs:
            fails += 1
            print("SYNTAX FAIL %s" % rel)
            for e in errs[:5]:
                print("    " + e)
        else:
            print("syntax ok  %s" % rel)
    print("parsed %d .cs files" % checked)

    # 2. catalog rows
    with open(os.path.join(RUNTIME, "Core/FunctionCatalog.cs")) as f:
        cat = f.read()
    ids = [int(x, 16) for x in re.findall(r"O\(0x([0-9A-Fa-f]+),", cat)]
    if len(ids) != 179 or len(set(ids)) != 179:
        fails += 1
        print("CATALOG FAIL: %d rows, %d unique (want 179/179)"
              % (len(ids), len(set(ids))))
    else:
        print("catalog ok: 179 unique overload rows")

    # 3. dispatcher cases both directions
    cases = []
    for name in os.listdir(os.path.join(RUNTIME, "Unity/Functions")):
        if not name.endswith(".cs"):
            continue
        with open(os.path.join(RUNTIME, "Unity/Functions", name)) as f:
            src = f.read()
        found = [int(x, 16) for x in re.findall(r"case (0x[0-9A-Fa-f]+):", src)]
        print("cases %-38s %d" % (name, len(found)))
        cases.extend(found)
    missing = sorted(set(ids) - set(cases))
    extra = sorted(set(cases) - set(ids))
    dupes = len(cases) != len(set(cases))
    if missing or extra or dupes:
        fails += 1
        print("DISPATCH FAIL: missing=%s extra=%s dupes=%s"
              % ([hex(x) for x in missing], [hex(x) for x in extra], dupes))
    else:
        print("dispatch ok: 179 catalog IDs <=> 179 cases")

    # 4. range coverage
    bad = [i for i in ids
           if sum(1 for lo, hi in RANGES if lo <= i <= hi) != 1]
    if bad:
        fails += 1
        print("RANGE FAIL: %s" % [hex(x) for x in bad])
    else:
        print("ranges ok: every ID in exactly one category range")

    print("RESULT: %s" % ("OK" if fails == 0 else "FAIL (%d)" % fails))
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
