# myFSM

`fsmc` — a C++17 compiler for a text-based, engine-agnostic FSM DSL (spec v0.3).
Reads one `.fsm` source file, emits one little-endian `.fsmb` binary (36-byte
header + 7 sections), byte-for-byte deterministic.

- **Writing `.fsm` files?** → [`fsmc/LANGUAGE.md`](fsmc/LANGUAGE.md) — the full
  language reference: syntax of every feature, all 21 types, all 179 function
  overloads, type rules, scoping, and every compile error.
- **Binary format / design** → [`fsmc/DESIGN.md`](fsmc/DESIGN.md)

## Compile a .fsm file (terminal)

Works exactly like `g++ foo.cpp -o foo`:

```sh
# from the repo root — builds the C++ compiler on first run, then compiles:
./bin/fsmc my_state.fsm                 # → my_state.fsmb (next to the source)
./bin/fsmc my_state.fsm -o out.fsmb     # → explicit output path
./bin/fsmc --help                       # usage
```

Prefer a plain `fsmc` command from any directory? Install it once:

```sh
make install                            # → ~/.local/bin/fsmc
fsmc my_state.fsm                       # works anywhere (add ~/.local/bin to PATH if needed)
```

Or use the Makefile directly:

```sh
make                                    # build the compiler
make compile INPUT=my_state.fsm         # compile (OUT=my_state.fsmb by default)
make compile INPUT=my_state.fsm OUT=/tmp/out.fsmb
make test                               # run all 10 CTest suites
make clean                              # remove fsmc/build/
```

### Exit codes

| code | meaning |
|------|---------|
| 0 | compiled successfully |
| 1 | usage / I/O error (bad arguments, missing input file) |
| 2 | compile error — `file:line:col: message` on stderr; **no `.fsmb` is ever emitted**, not even partially |

### Try the bundled fixtures

```sh
./bin/fsmc fsmc/tests/fixtures/minimal.fsm   # → minimal.fsmb (401 bytes, golden-pinned)
./bin/fsmc fsmc/tests/fixtures/two_state.fsm # → two_state.fsmb (1246 bytes)
./bin/fsmc fsmc/tests/fixtures/errors/bad_overload.fsm   # exits 2 with a diagnostic
```

## Layout

```
fsmc/                  the compiler (CMake project)
  CMakeLists.txt       fsmc_lib + fsmc CLI + fsmc_tests, CTest wiring
  LANGUAGE.md          the full language reference (syntax of every feature)
  DESIGN.md            normative binary layout, full 179-entry function ID
                       table, CLAIM/RELEASE encoding, scope + overload rules,
                       and the 7 documented deviations from spec v0.3
  lib/                 BuiltinTypes (21) / BuiltinFunctions (179) — pure data,
                       the single source of truth for all types & functions
  include/  src/       hand-rolled lexer, recursive-descent parser, scoping,
                       six named compiler passes (symbols → AST → goto
                       collection → FSM adjacency → serialization → linking),
                       byte-exact two-phase serializer, module reader
  tests/               10 CTest suites + fixtures (golden bytes pinned)
bin/fsmc               terminal driver: auto-builds the compiler once, then
                       runs it (this is what makes the g++-like workflow)
Makefile               repo-root convenience targets (see above)
```

## Requirements

- C++17 compiler (g++/clang++)
- CMake ≥ 3.13 (`pip3 install cmake` works if it's missing — the `bin/fsmc`
  driver even tries that for you)
