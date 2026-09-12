#pragma once

// Binary serializer (Pass 5). Two-pass internally:
//   1. layout  — compute section sizes and assign addresses to every entry
//   2. emit    — write bytes (little-endian, explicit writers, no backpatch)

#include <cstdint>
#include <vector>

#include "ast.hpp"
#include "diagnostics.hpp"
#include "passes.hpp"

namespace fsmc {

std::vector<uint8_t> serializeModule(const ParsedSource& src, const AstForest& forest,
                                     const FsmGraph& fsm, const SymbolTable& sym,
                                     Diagnostics& diag);

} // namespace fsmc
