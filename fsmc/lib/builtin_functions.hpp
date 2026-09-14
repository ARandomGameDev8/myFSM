#pragma once

// BuiltinFunctions — function registry.
//
// Pure data registry: no compiler internals, no runtime dependencies. This is
// the single source of truth for every built-in function (including all
// overloads). Overload resolution, docs generation and the runtime
// implementation dispatch all read from here.

#include <cstdint>
#include <string>
#include <vector>

namespace fsmc {

struct FunctionParam {
    std::string name;
    std::string typeName; // resolved against BuiltinTypes
};

struct FunctionDefinition {
    std::string name;
    std::string category;
    std::vector<FunctionParam> params;
    std::string returnType; // resolved against BuiltinTypes; "void" for none
    uint8_t      tier;      // 1, 2, or 3
    uint16_t     functionId;
    // Tier 3 only: the call drives the object handed to it as its FIRST
    // argument, so that argument must be a runtime or temporary variable (not a
    // static constant, literal or call result). This is a source-level rule
    // only — nothing about it is encoded in the module: there are no claims,
    // no ownership flags and no CLAIM/RELEASE instructions in the binary.
    bool requiresVariableTarget = false;
};

class BuiltinFunctions {
public:
    static const BuiltinFunctions& instance();

    // Returns ALL overloads with this name, in registry order. May be empty.
    const std::vector<const FunctionDefinition*> find(const std::string& name) const;

    // Unique lookup. Returns nullptr if not found.
    const FunctionDefinition* findById(uint16_t id) const;

    // Every registered overload, in registry (ID-assignment) order.
    const std::vector<FunctionDefinition>& all() const { return functions_; }

private:
    BuiltinFunctions();
    std::vector<FunctionDefinition> functions_;
};

} // namespace fsmc
