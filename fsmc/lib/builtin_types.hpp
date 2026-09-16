#pragma once

// BuiltinTypes — variable type registry.
//
// Pure data registry: no compiler internals, no runtime dependencies. This is
// the single source of truth for every type the compiler understands; the
// runtime, editor, docs generators and IDE plugins all read from here.

#include <cstdint>
#include <string>
#include <vector>

namespace fsmc {

struct TypeDefinition {
    std::string name;        // "int", "Vector3", "Object3D", ...
    uint8_t     typeTag;     // binary type tag
    uint32_t    sizeBytes;   // storage size in the binary (0 when variableSize)
    bool        isHandle;    // true for engine-owned reference types
    bool        isNumeric;   // true for numeric value types (arithmetic allowed)
    bool        isValueType; // true for inline-stored types, false for handles
    std::string category;    // "primitive", "vector", "object", ...
    // True when a value of this type has no fixed width: it is stored as
    // `[4] byte length` + payload instead of exactly `sizeBytes` bytes. Only
    // `string` sets this. Readers must branch on it before using `sizeBytes`.
    bool        isVariableSize = false;
};

class BuiltinTypes {
public:
    static const BuiltinTypes& instance();

    // Returns nullptr when the name is not a known type.
    const TypeDefinition* find(const std::string& name) const;

    // Every registered type, in canonical (tag) order.
    const std::vector<TypeDefinition>& all() const { return types_; }

private:
    BuiltinTypes();
    std::vector<TypeDefinition> types_;
};

} // namespace fsmc
