# Native compiler plugins (`myfsmc`)

The C# wrapper (`Runtime/Compiler/FsmCompiler.cs`, `[DllImport("myfsmc")]`)
loads one of these at edit time / runtime. Filenames per platform:

| Platform | File | Folder |
|---|---|---|
| Windows x86_64 | `myfsmc.dll` | `Plugins/x86_64/` |
| Linux x86_64 | `libmyfsmc.so` | `Plugins/x86_64/` |
| macOS | `libmyfsmc.dylib` | `Plugins/` |

Unity auto-generates the `.meta` import settings on first import.

## Rebuilding

From `fsmc/` with CMake:

```
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --target myfsmc
```

Or with a bare compiler (same sources as `fsmc_lib` + `src/c_api.cpp`):

```
g++ -std=c++17 -O2 -fPIC -shared -Iinclude -Ilib -DMYFSM_BUILDING_LIB \
  lib/*.cpp src/binary_format.cpp src/diagnostics.cpp src/lexer.cpp \
  src/parser.cpp src/ast.cpp src/scope.cpp src/const_fold.cpp \
  src/type_rules.cpp src/passes.cpp src/serializer.cpp \
  src/module_reader.cpp src/disassembler.cpp src/c_api.cpp \
  -o libmyfsmc.so
```

The emitted `.fsmb` format version is pinned by `static_assert` in
`src/c_api.cpp` against `MYFSM_FSMB_MAJOR/MINOR` (currently 0.5).
