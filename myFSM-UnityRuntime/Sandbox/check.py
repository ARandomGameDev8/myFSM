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
3. Undefined names: a name used as a type or as a static receiver (`Foo.Bar()`)
   has to be declared somewhere in the shipped sources, or listed in
   known_external_names.txt as an engine/framework name. This is the check that
   catches the "renamed a class, forgot a caller" / "the helper file never made
   it into the project" family - Unity reports those as CS0103, and they have
   shipped twice (InputCompat, FsmValue).
4. FunctionCatalog holds exactly 179 unique overload rows.
5. Every catalog ID has exactly one `case` in the Run dispatchers and vice versa.
6. Every catalog ID falls inside exactly one category dispatch range.
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
    try:
        import tree_sitter as ts
        import tree_sitter_c_sharp as tscs
    except ImportError as exc:
        print("cannot run the checks: %s" % exc)
        print("this needs the tree-sitter C# grammar:")
        print("    python3 -m pip install tree_sitter tree_sitter_c_sharp")
        print("or use an interpreter that has it: <python> Sandbox/check.py --baseline <dir>")
        raise SystemExit(2)
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


BASELINE_NAME = "known_external_names.txt"


def collect_declared_and_used(tree, src):
    """(declared names, uppercase names used as types or static receivers).

    Declaration positions cover types (top level and nested), methods,
    properties, events, enum members, fields/locals (variable_declarator),
    using-aliases and generic type parameters - so anything declared in this
    repository resolves. Usage positions cover `Name.Member`, `new Name(...)`
    and base types: exactly the places where a missing declaration becomes
    CS0103 in Unity.
    """
    declared, used = set(), set()

    def text(node):
        return src[node.start_byte:node.end_byte].decode("utf-8", "replace")

    def visit(node):
        kind = node.type
        if kind in NON_CODE_NODES:
            return
        if kind in TYPE_NODES or kind in (
                "method_declaration", "property_declaration", "event_declaration",
                "enum_member_declaration", "variable_declarator",
                "using_alias_directive", "type_parameter"):
            field = node.child_by_field_name("name")
            if field is not None:
                declared.add(text(field).split("<")[0].strip())
        if kind == "member_access_expression":
            first = node.children[0]
            if first.type == "identifier":
                used.add(text(first))
        elif kind == "object_creation_expression":
            type_node = node.child_by_field_name("type")
            if type_node is not None and type_node.type in ("identifier", "generic_name"):
                used.add(text(type_node).split("<")[0].strip())
        elif kind == "base_list":
            for child in node.children:
                if child.type in ("identifier", "generic_name"):
                    used.add(text(child).split("<")[0].strip())
        for child in node.children:
            visit(child)

    visit(tree.root_node)
    return declared, {u for u in used if u[:1].isupper()}


# Members that return a DOUBLE in C#. Assigning one to a float is CS0266 (an
# explicit cast is required) - Unity is the only compiler here, so it is checked.
# Mathf.* is deliberately absent: those overloads are float already. Math.Abs and
# Math.Min/Max are absent too: they have float overloads and would false-positive.
DOUBLE_RETURNS = (
    ".TotalMilliseconds", ".TotalSeconds", ".TotalMinutes", ".TotalHours", ".TotalDays",
    "Math.Sqrt(", "Math.Pow(", "Math.Sin(", "Math.Cos(", "Math.Tan(", "Math.Log(",
    "Math.Exp(", "Math.Round(", "Math.Floor(", "Math.Ceiling(",
)


def scan_double_into_float(parser, files):
    """`float x = <double>;` without a cast - the CS0266 family.

    Checked on declarations (`float name = ...`), which is where it bites and
    where there are no false positives: the declared type is right there in the
    source. Assignments to an existing float field are not modelled (the field's
    type may live in another file).
    """
    problems = []
    for path in files:
        errs, code, tree, src = parse_file(parser, path)
        if errs:
            continue
        rel = os.path.relpath(path, ROOT)

        def visit(node):
            if node.type == "variable_declaration":
                type_node = node.child_by_field_name("type")
                if type_node is not None and _text(type_node, src).strip() == "float":
                    for decl in node.children:
                        if decl.type != "variable_declarator":
                            continue
                        text = _text(decl, src)
                        if "=" not in text:
                            continue
                        value = text.split("=", 1)[1]
                        if value.lstrip().startswith("(float)") or value.lstrip().startswith("(double)"):
                            continue
                        for source in DOUBLE_RETURNS:
                            if source in value:
                                name_node = decl.child_by_field_name("name")
                                name = _text(name_node, src) if name_node is not None else text
                                problems.append(
                                    "%s: float '%s' is assigned a double ('%s') - add an "
                                    "explicit (float) cast (CS0266)"
                                    % (rel, name.split("=")[0].strip(), source.strip("(")))
                                break
            for child in node.children:
                visit(child)

        visit(tree.root_node)
    return problems


NESTED_TYPE_KINDS = frozenset((
    "class_declaration", "struct_declaration", "interface_declaration",
    "enum_declaration", "delegate_declaration", "record_declaration",
))


def _method_signature(node, src):
    """(name, parameter-list text, type-parameter count) - the identity C# uses
    for CS0111. Overloads differ in this tuple, so they are not reported."""
    field = node.child_by_field_name("name")
    if field is None:
        return None
    params = node.child_by_field_name("parameters")
    param_text = " ".join(_text(params, src).split()) if params is not None else "()"
    tparams = node.child_by_field_name("type_parameters")
    arity = 0
    if tparams is not None:
        arity = sum(1 for c in tparams.children if c.type == "type_parameter")
    return (_text(field, src), param_text, arity)


def scan_member_collisions(parser, files):
    """The CS0102 / CS0111 / CS0542 family: names one type cannot hold twice.

    Unity only reports these when it compiles, and a nested type sharing a name
    with one of its own methods is easy to write by accident - that shipped once
    (`PendingStep` was both a method and a private class in MovementSystem.cs).
    Checked per file and per declared type, so partial types in other files
    cannot produce false positives.
    """
    problems = []

    def visit(node, src, rel):
        if node.type in ("class_declaration", "struct_declaration", "interface_declaration",
                         "record_declaration"):
            type_name = _name_of(node, src)
            body = node.child_by_field_name("body")
            if body is None:
                for child in node.children:
                    if child.type == "declaration_list":
                        body = child
                        break
            if body is not None and type_name:
                members = {}   # name -> [(kind, signature or None)]
                nested = {}    # name -> [kind]
                for member in body.children:
                    kind = member.type
                    if kind in NESTED_TYPE_KINDS:
                        name = _name_of(member, src)
                        if name:
                            nested.setdefault(name, []).append(kind)
                        continue
                    if kind == "method_declaration":
                        sig = _method_signature(member, src)
                        if sig:
                            members.setdefault(sig[0], []).append(("method", sig))
                        continue
                    if kind == "field_declaration":
                        for decl in member.children:
                            if decl.type != "variable_declaration":
                                continue
                            for v in decl.children:
                                if v.type == "variable_declarator":
                                    name_node = v.child_by_field_name("name")
                                    if name_node is not None:
                                        members.setdefault(_text(name_node, src), []).append(
                                            ("field", None))
                        continue
                    if kind in ("property_declaration", "event_declaration",
                                "event_field_declaration"):
                        name_node = member.child_by_field_name("name")
                        if name_node is not None:
                            members.setdefault(_text(name_node, src), []).append(
                                (kind.split("_")[0], None))
                        continue
                    # constructors and destructors are SUPPOSED to be named after
                    # the type, so they are skipped on purpose.

                for name, kinds in sorted(nested.items()):
                    if len(kinds) > 1:
                        problems.append(
                            "%s: '%s' is declared as a nested type %d times in '%s' (CS0102)"
                            % (rel, name, len(kinds), type_name))
                    if name in members:
                        problems.append(
                            "%s: '%s' is both a %s and a nested type in '%s' (CS0102 - "
                            "rename one of them)"
                            % (rel, name, members[name][0][0], type_name))
                for name, entries in sorted(members.items()):
                    if name == type_name and any(e[0] != "constructor" for e in entries):
                        problems.append(
                            "%s: a %s in '%s' is named after its own type (CS0542)"
                            % (rel, entries[0][0], type_name))
                    placeholders = [e for e in entries if e[0] in ("field", "property", "event")]
                    if len(placeholders) > 1:
                        problems.append(
                            "%s: '%s' is declared %d times as a %s in '%s' (CS0102)"
                            % (rel, name, len(placeholders), placeholders[0][0], type_name))
                    seen = set()
                    for kind, sig in entries:
                        if sig is None:
                            continue
                        if sig in seen:
                            problems.append(
                                "%s: '%s%s' is declared twice with the same signature in "
                                "'%s' (CS0111)" % (rel, sig[0], sig[1], type_name))
                        seen.add(sig)
        for child in node.children:
            visit(child, src, rel)

    for path in files:
        errs, code, tree, src = parse_file(parser, path)
        if errs:
            continue
        visit(tree.root_node, src, os.path.relpath(path, ROOT))
    return problems


def load_baseline(path):
    """Engine/framework names the repository is allowed to use without declaring."""
    names = set()
    if not os.path.exists(path):
        return names
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.split("#", 1)[0].strip()
            if line:
                names.add(line)
    return names


def collect_cs_files():
    files = []
    for base in (RUNTIME, EDITOR, SANDBOX, SAMPLES, TESTS):
        for dirpath, _, names in os.walk(base):
            for name in names:
                if name.endswith(".cs"):
                    files.append(os.path.join(dirpath, name))
    return sorted(files)


def collect_cs_files(*roots):
    """Every .cs file under the given roots (default: the shipped folders)."""
    bases = roots if roots else (RUNTIME, EDITOR, SANDBOX, SAMPLES, TESTS)
    files = []
    for base in bases:
        if not os.path.isdir(base):
            continue
        for dirpath, _, names in os.walk(base):
            for name in names:
                if name.endswith(".cs"):
                    files.append(os.path.join(dirpath, name))
    return sorted(files)


def load_external_names():
    return load_baseline(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                      BASELINE_NAME))


def external_baseline_size():
    return len(load_external_names())


def scan_undefined_names(parser, files, baseline=frozenset(), declared_extra=frozenset()):
    """(declared, used, undefined) over `files`.

    `declared_extra` holds names that are known-good because they are declared
    outside the scanned set (the repository, when scanning an installed copy);
    `baseline` holds engine/framework names.
    """
    declared_all, used_all = set(), set()
    for path in files:
        _, _, tree, src = parse_file(parser, path)
        declared, used = collect_declared_and_used(tree, src)
        declared_all |= declared
        used_all |= used
    known = declared_all | set(baseline) | set(declared_extra)
    undefined = sorted(used_all - known)
    return declared_all, used_all, undefined


def undefined_message(name, where="Runtime/, Editor/, Tests/, Samples/ or Sandbox/",
                      known_extra=None):
    return ("UNDEFINED NAME %s - used as a type/static receiver but declared nowhere in %s. "
            "Unity reports this as CS0103. Fix the reference, ship the file that declares it, "
            "or add the name to Sandbox/%s if it really is an engine type.%s"
            % (name, where, BASELINE_NAME, known_extra or ""))


def run_project_check(target, parser):
    """`--baseline <dir>`: check an installed copy against this repository.

    Answers "is my copy stale / missing a file" without Unity: every name the
    copy uses that the repository does not declare either is printed with the
    CS0103 note. This is the check that would have caught a project holding
    SpawnStressTest.cs from one commit and StressInput.cs from another.
    """
    if not os.path.isdir(target):
        print("usage error: no such directory: %s" % target)
        print("pass the folder that holds the installed runtime, for example")
        print("    <unity-project>/Assets/MyFSM")
        return 2
    files = collect_cs_files(target)
    if not files:
        print("usage error: no .cs files under %s" % target)
        print("pass the folder that holds the installed runtime, for example")
        print("    <unity-project>/Assets/MyFSM")
        return 2
    # A wrong directory must not pass quietly: the point is to prove that THIS
    # copy is the one Unity compiles.
    declares_myfsm = False
    for path in files:
        try:
            with open(path, encoding="utf-8", errors="replace") as handle:
                if re.search(r"namespace\s+MyFSM\.", handle.read()):
                    declares_myfsm = True
                    break
        except OSError:
            continue
    if not declares_myfsm:
        print("usage error: %s holds no file declaring a MyFSM.* namespace" % target)
        print("pass the folder that holds the installed runtime, for example")
        print("    <unity-project>/Assets/MyFSM")
        return 2
    if not os.path.isdir(os.path.join(target, "Runtime")):
        print("warning: %s has no Runtime/ folder of its own - checking it anyway"
              % target)

    print("checking %s against %s" % (target, ROOT))
    print("")

    fails = 0
    for path in files:
        errs, _, tree, _ = parse_file(parser, path)
        if errs:
            fails += 1
            print("SYNTAX FAIL %s" % path)
            for e in errs[:5]:
                print("    " + e)

    # declared names of the repository: the reference for "should exist"
    repo_files = collect_cs_files()
    repo_declared, _, _ = scan_undefined_names(parser, repo_files)
    declared, used, undefined = scan_undefined_names(
        parser, files, baseline=load_external_names(), declared_extra=repo_declared)

    if undefined:
        fails += len(undefined)
        for name in undefined:
            print(undefined_message(name, where="this copy or in %s" % ROOT))
    else:
        print("names ok: nothing in this copy names a type that neither the copy nor the "
              "repository declares (%d declared here, %d known in the repository, %d external)"
              % (len(declared), len(repo_declared), external_baseline_size()))

    print("")
    print("RESULT: %s" % ("OK" if fails == 0 else "FAIL (%d)" % fails))
    return 1 if fails else 0


def main():
    argv = sys.argv[1:]
    if argv and argv[0] in ("-h", "--help"):
        print("usage: check.py [--baseline <directory>]")
        print("")
        print("  no arguments          check this repository")
        print("  --baseline <dir>      check an installed copy of the runtime (for example")
        print("                        <unity-project>/Assets/MyFSM) against this repository: any")
        print("                        name the copy uses that the repository does not declare is")
        print("                        reported, because Unity reports those as CS0103")
        return 0
    if argv and argv[0] == "--baseline":
        if len(argv) < 2:
            print("usage error: --baseline needs a directory, for example "
                  "<unity-project>/Assets/MyFSM")
            return 2
        return run_project_check(argv[1], make_parser())
    if argv:
        print("usage error: unknown argument %s (try --help)" % argv[0])
        return 2
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

    # 3. undefined names (the CS0103 family: renamed class, missing helper file)
    declared_all, used_all, undefined = scan_undefined_names(
        parser, collect_cs_files(), baseline=load_external_names())
    if undefined:
        fails += len(undefined)
        for name in undefined:
            print(undefined_message(name))
    else:
        print("names ok: every type/static receiver used here is declared here or listed as "
              "external (%d declared, %d external)"
              % (len(declared_all), external_baseline_size()))

    # 3a. double into float without a cast (the CS0266 family: Unity-only errors)
    doubles = scan_double_into_float(parser, collect_cs_files())
    if doubles:
        fails += len(doubles)
        for problem in doubles:
            print("DOUBLE FAIL " + problem)
    else:
        print("numbers ok: no float is assigned a double-returning expression without a cast")

    # 3b. names a type cannot hold twice (the CS0102 family: Unity-only errors)
    collisions = scan_member_collisions(parser, collect_cs_files())
    if collisions:
        fails += len(collisions)
        for problem in collisions:
            print("MEMBER FAIL " + problem)
    else:
        print("members ok: no nested type shares a name with a method, field or "
              "property of the same type")

    # 4. catalog rows
    with open(os.path.join(RUNTIME, "Core/FunctionCatalog.cs")) as f:
        cat = f.read()
    ids = [int(x, 16) for x in re.findall(r"O\(0x([0-9A-Fa-f]+),", cat)]
    if len(ids) != 179 or len(set(ids)) != 179:
        fails += 1
        print("CATALOG FAIL: %d rows, %d unique (want 179/179)"
              % (len(ids), len(set(ids))))
    else:
        print("catalog ok: 179 unique overload rows")

    # 5. dispatcher cases both directions
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
