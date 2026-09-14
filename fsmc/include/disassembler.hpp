#pragma once

// .fsmb disassembler.
//
// Renders a module that readModule() has parsed as human-readable text
// (a ".fsmd"), so a binary is debuggable: every address is resolved to its
// name (states, variables, functions), every literal is decoded to its
// value, and the full AST + instruction streams are laid out per state.
//
// Used by the CLI:  fsmc -d input.fsmb [-o output.fsmd]   (-o - = stdout)

#include <string>

#include "module_reader.hpp"

namespace fsmc {

// `fileName` is only used in the banner; `validated` records whether
// validateModule() passed before rendering. Deterministic: the same module
// always produces the same text.
std::string disassemble(const ReadModule& m, const std::string& fileName, bool validated);

} // namespace fsmc
