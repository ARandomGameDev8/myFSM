#pragma once

// Operator type rules (spec section 0.3). Shared by the parser (type-checking)
// and the constant folder (which recomputes result types).

#include <cstdint>
#include <string>

#include "builtin_types.hpp"

namespace fsmc {

// Result type of `a op b`, or nullptr when the combination is rejected
// (errMsg is filled with a human-readable reason).
const TypeDefinition* binaryOpType(uint8_t opId, const TypeDefinition* a,
                                   const TypeDefinition* b, std::string& errMsg,
                                   const char* opSymbol);

// Result type of unary `op a`, or nullptr when rejected.
const TypeDefinition* unaryOpType(uint8_t opId, const TypeDefinition* a,
                                  std::string& errMsg, const char* opSymbol);

// Scalar promotion used by the arithmetic/relational operators.
const TypeDefinition* promoteScalars(const TypeDefinition* a, const TypeDefinition* b);

} // namespace fsmc
