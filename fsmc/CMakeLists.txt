cmake_minimum_required(VERSION 3.16)
project(fsmc LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE RelWithDebInfo)
endif()

# ---------------------------------------------------------------------------
# Compiler library: built-in registries + lexer/parser/passes/serializer/reader
# ---------------------------------------------------------------------------
add_library(fsmc_lib STATIC
  lib/builtin_types.cpp
  lib/builtin_functions.cpp
  src/binary_format.cpp
  src/diagnostics.cpp
  src/lexer.cpp
  src/parser.cpp
  src/ast.cpp
  src/scope.cpp
  src/const_fold.cpp
  src/type_rules.cpp
  src/passes.cpp
  src/serializer.cpp
  src/module_reader.cpp
  src/disassembler.cpp
)
target_include_directories(fsmc_lib
  PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/lib
)

# ---------------------------------------------------------------------------
# CLI: fsmc input.fsm -o output.fsmb
# ---------------------------------------------------------------------------
add_executable(fsmc src/main.cpp)
target_link_libraries(fsmc PRIVATE fsmc_lib)

# ---------------------------------------------------------------------------
# Tests (hand-rolled framework, no external dependencies) via CTest
# ---------------------------------------------------------------------------
enable_testing()
add_executable(fsmc_tests
  tests/test_main.cpp
  tests/test_lib_types.cpp
  tests/test_lib_functions.cpp
  tests/test_lexer.cpp
  tests/test_parser.cpp
  tests/test_scope.cpp
  tests/test_overloads.cpp
  tests/test_traversals.cpp
  tests/test_passes.cpp
  tests/test_golden_bytes.cpp
  tests/test_disassemble.cpp
)
target_link_libraries(fsmc_tests PRIVATE fsmc_lib)
target_compile_definitions(fsmc_tests PRIVATE
  FSMC_FIXTURE_DIR="${CMAKE_CURRENT_SOURCE_DIR}/tests/fixtures"
)

set(FSMC_TEST_SUITES
  lib_types lib_functions lexer parser scope overloads traversals passes golden_bytes
  disassemble
)
foreach(suite IN LISTS FSMC_TEST_SUITES)
  add_test(NAME ${suite} COMMAND fsmc_tests ${suite})
endforeach()
add_test(NAME all COMMAND fsmc_tests)

if(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
  target_compile_options(fsmc_lib PRIVATE -Wall -Wextra)
  target_compile_options(fsmc_tests PRIVATE -Wall -Wextra)
endif()
