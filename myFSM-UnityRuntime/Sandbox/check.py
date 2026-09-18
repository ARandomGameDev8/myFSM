#!/usr/bin/env python3
"""myFSM-UnityRuntime static checks (no Unity/dotnet needed).

1. tree-sitter parses every .cs file (Runtime + Editor + Sandbox + Samples +
   Tests) without errors. The Tests folder holds the Play-mode test cases, whose
   generated/manual classes and scene scripts must at least be well-formed.
2. Namespace usage: a file that NAMES a public type of MyFSM.Core/MyFSM.Unity
   must import that namespace. A `using` is per file, so
   `using MyFSM.Unity;` does not bring `FsmValue` (MyFSM.Core) into scope -
   without this check that mistake compiles nowhere and is only found in Unity
   (it shipped once: CS0103 in the tests' hand-written binding files).
3. FunctionCatalog holds exactly 179 unique overload rows.
4. Every catalog ID has exactly one `case` in the Run dispatchers and vice versa.
5. Every catalog ID falls inside exactly one category dispatch range.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUNTIME = os.path.join(ROOT, "Runtime")
SANDBOX = os.path.join(ROOT, "Sandbox")
SAMPLES = os.path.join(ROOT, "Samples")
TESTS = os.path.join(ROOT, "Tests")
EDITOR = os.path.join(ROOT, "Editor")

CORE_NS = "MyFSM.Core"
UNITY_NS = "MyFSM.Unity"

# Nodes whose contents are not code: blanked before the namespace check so a
# type name mentioned in a comment (or inside the generated base64 blob) does
# not count as a usage.
NON_CODE_NODES = (
    "comment",
    "string_literal",
    "verbatim_string_literal",
    "raw_string_literal",
    "interpolated_string_expression",
    "character_literal",
)

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


def parse_file(parser, path):
    """(syntax errors, code with comments and strings blanked out)."""
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

    spans = []

    def collect(node):
        if node.type in NON_CODE_NODES:
            spans.append((node.start_byte, node.end_byte))
            return  # no walking into interpolated expressions: rare, and
                    # over-blanking only makes this check more forgiving
        for child in node.children:
            collect(child)

    collect(tree.root_node)
    code = bytearray(src)
    for start, end in spans:
        for i in range(start, end):
            if code[i] != 0x0A:  # keep line breaks so offsets stay readable
                code[i] = 0x20
    return errs, code.decode("utf-8", "replace")


TYPE_DECL = re.compile(
    r"^\s*(?:public|internal)\s+"
    r"(?:(?:sealed|static|abstract|partial|readonly|unsafe)\s+)*"
    r"(?:class|struct|enum|interface)\s+([A-Za-z_][A-Za-z0-9_]*)", re.M)
NAMESPACE_DECL = re.compile(r"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)", re.M)


def collect_namespace_types(*bases):
    """{namespace: {public/internal type names}} for the given source roots.

    Read from the files themselves (not from their folder), so a type in
    Runtime/Compiler is attributed to MyFSM.Compiler, never to MyFSM.Core -
    folder-based guessing produced exactly that false positive once.
    """
    table = {}
    for base in bases:
        for dirpath, _, filenames in os.walk(base):
            for name in filenames:
                if not name.endswith(".cs"):
                    continue
                with open(os.path.join(dirpath, name), encoding="utf-8") as f:
                    text = f.read()
                namespaces = NAMESPACE_DECL.findall(text)
                if not namespaces:
                    continue  # global-namespace file: nothing to attribute
                types = TYPE_DECL.findall(text)
                for namespace in namespaces:
                    table.setdefault(namespace, set()).update(types)
    return table


def missing_namespace_usings(rel, code, namespaces):
    """Problems for every namespace whose types the file names unimported."""
    problems = []
    for namespace, names in namespaces:
        if ("using %s;" % namespace) in code:
            continue
        if ("namespace %s" % namespace) in code:
            continue  # the file declares that namespace: names resolve locally
        hits = []
        for name in sorted(names):
            # a fully qualified `MyFSM.Core.FsmValue` needs no using
            if re.search(r"(?<!%s\.)\b%s\b" % (re.escape(namespace), re.escape(name)), code):
                hits.append(name)
        if hits:
            problems.append("%s names %s but never has `using %s;`"
                            % (rel, ", ".join(hits[:6]), namespace))
    return problems


def collect_cs_files():
    files = []
    for base in (RUNTIME, EDITOR, SANDBOX, SAMPLES, TESTS):
        for dirpath, _, names in os.walk(base):
            for name in names:
                if name.endswith(".cs"):
                    files.append(os.path.join(dirpath, name))
    return sorted(files)


def main():
    fails = 0

    # The RUNTIME namespaces, with the public types each declares: a file that
    # names one of them without importing it is caught here. Only the runtime
    # (never Tests/Samples) feeds this table, and a name declared in more than
    # one namespace is dropped: a test's nested `Mode` enum must not make every
    # unrelated `Mode` in the codebase look like a missing using.
    runtime_types = collect_namespace_types(RUNTIME)
    declared_in = {}
    for namespace, names in runtime_types.items():
        for name in names:
            declared_in.setdefault(name, set()).add(namespace)
    NAMESPACES = sorted(
        (ns, set(n for n in names if len(declared_in[n]) == 1))
        for ns, names in runtime_types.items()
        if ns.startswith("MyFSM.")
    )

    # 1. syntax
    parser = make_parser()
    checked = 0
    namespace_problems = []
    for path in collect_cs_files():
        checked += 1
        errs, code = parse_file(parser, path)
        rel = os.path.relpath(path, ROOT)
        if errs:
            fails += 1
            print("SYNTAX FAIL %s" % rel)
            for e in errs[:5]:
                print("    " + e)
        else:
            print("syntax ok  %s" % rel)
        namespace_problems.extend(missing_namespace_usings(rel, code, NAMESPACES))
    print("parsed %d .cs files" % checked)

    # 2. namespace usage (the bug class that only ever surfaced inside Unity)
    if namespace_problems:
        fails += len(namespace_problems)
        for problem in namespace_problems:
            print("USING FAIL " + problem)
    else:
        print("usings ok: every file that names MyFSM.Core/MyFSM.Unity types imports them")

    # 3. catalog rows
    with open(os.path.join(RUNTIME, "Core/FunctionCatalog.cs")) as f:
        cat = f.read()
    ids = [int(x, 16) for x in re.findall(r"O\(0x([0-9A-Fa-f]+),", cat)]
    if len(ids) != 179 or len(set(ids)) != 179:
        fails += 1
        print("CATALOG FAIL: %d rows, %d unique (want 179/179)"
              % (len(ids), len(set(ids))))
    else:
        print("catalog ok: 179 unique overload rows")

    # 4. dispatcher cases both directions
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
