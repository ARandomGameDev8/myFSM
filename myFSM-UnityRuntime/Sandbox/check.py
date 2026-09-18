#!/usr/bin/env python3
"""myFSM-UnityRuntime static checks (no Unity/dotnet needed).

1. tree-sitter parses every .cs file (Runtime + Editor + Sandbox + Samples +
   Tests) without errors. The Tests folder holds the Play-mode test cases, whose
   generated/manual classes and scene scripts must at least be well-formed.
2. Namespace usage: a file that NAMES a top-level public type of ANY shipped
   MyFSM.* namespace must import it (or declare it). A `using` is per file, so
   `using MyFSM.Unity;` does not bring `FsmValue` (MyFSM.Core) into scope, and a
   test script in MyFSM.Tests does not see a helper in MyFSM.Tests.Stress.
   Both mistakes only ever surface inside Unity as CS0103 (`The name X does not
   exist in the current context`) - the second one shipped. Types declared
   inside another type (a nested enum, a record struct for CSV rows) are not
   importable and are ignored, as are global-namespace types.
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
    """(syntax errors, code with comments/strings blanked, parse tree, raw bytes).

    The raw bytes matter: tree-sitter reports BYTE offsets, so slicing a decoded
    string with them drifts out of sync at the first non-ASCII character (an em
    dash in a comment was enough to turn every extracted name into garbage).
    """
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
    return errs, code.decode("utf-8", "replace"), tree, src


TYPE_NODES = (
    "class_declaration",
    "struct_declaration",
    "interface_declaration",
    "enum_declaration",
    "record_declaration",
)


def _text(node, src):
    """UTF-8 text of a node, from BYTE offsets (see parse_file)."""
    return src[node.start_byte:node.end_byte].decode("utf-8", "replace")


def _name_of(node, src):
    """Declared name of a namespace/type node.

    `namespace MyFSM.Tests` is a qualified_name, not an identifier, so reading
    only identifiers silently found NOTHING and the whole check passed on an
    empty table (it did, once). Preference order: the node's own `name` field,
    then the first identifier-ish child.
    """
    field = node.child_by_field_name("name")
    if field is not None:
        return _text(field, src).split("<")[0].strip()
    for child in node.children:
        if child.type in ("identifier", "qualified_name", "generic_name"):
            return _text(child, src).split("<")[0].strip()
    return None


def scan_declarations(tree, src):
    """(imports, declared namespaces, [(namespace, type name)] for TOP-LEVEL types).

    Types declared inside another type are skipped on purpose: a nested
    `public enum Mode` or a `public class Entry` is not importable, and treating
    them as repository-wide names produced false positives.
    """
    imports = set()
    namespaces = set()
    types = []

    def visit(node, namespace, inside_type):
        for child in node.children:
            kind = child.type
            if kind in ("namespace_declaration", "file_scoped_namespace_declaration"):
                name = _name_of(child, src)
                if name:
                    namespaces.add(name)
                    visit(child, name, inside_type)
                continue
            if kind == "using_directive":
                text = _text(child, src)
                text = text.replace("using", "", 1).replace("static", "", 1)
                text = text.replace(";", "").strip()
                if "=" in text:  # alias: `using Foo = Bar.Baz;`
                    text = text.split("=", 1)[1].strip()
                imports.add(text)
                continue
            if kind in TYPE_NODES:
                if not inside_type:
                    name = _name_of(child, src)
                    if name:
                        types.append((namespace, name))
                visit(child, namespace, True)
                continue
            visit(child, namespace, inside_type)

    visit(tree.root_node, "", False)
    return imports, namespaces, types


def collect_namespace_types(*bases):
    """{namespace: {type names}} for TOP-LEVEL types, read from the files."""
    table = {}
    for base in bases:
        for dirpath, _, filenames in os.walk(base):
            for name in filenames:
                if not name.endswith(".cs"):
                    continue
                with open(os.path.join(dirpath, name), "rb") as f:
                    src = f.read()
                parser = make_parser()
                tree = parser.parse(src)
                _, _, types = scan_declarations(tree, src)
                for namespace, type_name in types:
                    table.setdefault(namespace, set()).add(type_name)
    return table


def missing_namespace_usings(rel, code, imports, namespaces, namespaces_table):
    """Problems for every namespace whose types the file names unimported."""
    problems = []
    for namespace, names in namespaces_table:
        if namespace in imports or namespace in namespaces:
            continue  # imported, or this file declares that namespace
        hits = []
        for name in sorted(names):
            # Not a member access (`Something.Name`) and not a qualified name
            # (`MyFSM.Core.Name`), which need no using.
            pattern = r"(?<![.\w])" + re.escape(name) + r"\b"
            if re.search(pattern, code):
                hits.append(name)
        if hits:
            problems.append("%s names %s but never imports `%s;` (nor declares it) - "
                            "Unity reports this as CS0103"
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

    # Every MyFSM.* namespace we ship (runtime, tests, samples, editor), with the
    # top-level types it declares: a file naming one of them without importing it
    # is caught here. Types in the global namespace are dropped - anything can
    # use those. A name declared in several namespaces is still checked, but only
    # against files that import NONE of them (that is the InputCompat case:
    # per-folder copies in MyFSM.Tests.Stress/.Maze/.Chase).
    namespaces_by_name = {}
    for namespace, names in collect_namespace_types(RUNTIME, TESTS, SAMPLES, EDITOR).items():
        for name in names:
            namespaces_by_name.setdefault(name, set()).add(namespace)
    NAMESPACES = []
    for namespace, names in sorted(collect_namespace_types(
            RUNTIME, TESTS, SAMPLES, EDITOR).items()):
        if not namespace.startswith("MyFSM."):
            continue
        NAMESPACES.append((namespace, set(names)))

    # 1. syntax
    parser = make_parser()
    checked = 0
    namespace_problems = []
    for path in collect_cs_files():
        checked += 1
        errs, code, tree, src = parse_file(parser, path)
        rel = os.path.relpath(path, ROOT)
        if errs:
            fails += 1
            print("SYNTAX FAIL %s" % rel)
            for e in errs[:5]:
                print("    " + e)
        else:
            print("syntax ok  %s" % rel)
        imports, namespaces, _ = scan_declarations(tree, src)
        namespace_problems.extend(
            missing_namespace_usings(rel, code, imports, namespaces, NAMESPACES))
    print("parsed %d .cs files" % checked)

    # 2. namespace usage (the bug class that only ever surfaced inside Unity)
    if namespace_problems:
        fails += len(namespace_problems)
        for problem in namespace_problems:
            print("USING FAIL " + problem)
    else:
        print("usings ok: every file that names a shipped MyFSM.* type imports its "
              "namespace (%d checked)" % len(NAMESPACES))

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
