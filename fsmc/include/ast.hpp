#pragma once

// In-memory AST model produced by the parser and consumed by the six
// compiler passes. Every container keeps declaration/execution order.

#include <array>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#include "builtin_types.hpp"
#include "binary_format.hpp"
#include "source.hpp"

namespace fsmc {

enum class VarKind : uint8_t {
    GlobalConst = 0, // static constant (Global Variable Section)
    Runtime = 1,     // runtime variable (Runtime Variable Section)
    Temp = 2,        // temporary (Temporary Variable Section)
};

struct VarInfo {
    VarKind kind = VarKind::GlobalConst;
    int index = -1; // index within the matching table (globals / runtime / temps)
    std::string name;
    const TypeDefinition* type = nullptr;
};

// A fully folded compile-time constant value.
struct ConstValue {
    const TypeDefinition* type = nullptr;
    int32_t i = 0;
    float f = 0.0f;
    double d = 0.0;
    bool b = false;
    std::array<float, 4> v{};
    int vcount = 0; // 2 / 3 / 4 for Vector2 / Vector3 / Quaternion

    // Little-endian raw bytes, exactly sizeBytes wide (from BuiltinTypes).
    std::vector<uint8_t> toBytes() const;
};

struct Expr;

struct ClaimBinding {
    VarInfo var;           // variable bound to the call's first argument
    uint8_t fieldIndex = 0; // position = 0, velocity = 1, rotation = 2
    std::string claim;     // original claim string, for diagnostics
};

// Expression tree. Ownership: the owning ParsedSource keeps every Expr alive
// in an expr pool; Expr* fields are non-owning.
struct Expr {
    enum class Kind : uint8_t { Literal, VarRef, Binary, Unary, Call } kind = Kind::Literal;
    SrcLoc loc{1, 1};
    const TypeDefinition* type = nullptr; // resolved type; null if this subtree errored

    // Literal payload
    int32_t litInt = 0;
    double litReal = 0.0; // float literals stored as double; doubles as-is
    bool litBool = false;
    std::array<float, 4> litVec{};
    int litVecCount = 0;

    // VarRef payload
    VarInfo var;

    // Binary / Unary payload (Unary uses `left` as its operand)
    uint8_t op = 0; // fmt::OpId
    Expr* left = nullptr;
    Expr* right = nullptr;

    // Call payload
    std::string funcName;
    uint16_t functionId = 0;
    uint8_t tier = 0;
    std::vector<Expr*> args;
};

// Statement. A state's Actions/Traversals body is a vector of Stmt in
// source order.
struct Stmt {
    enum class Kind : uint8_t { TempDecl, Assign, Call, Goto, If, ElseIf, Else } kind = Kind::TempDecl;
    SrcLoc loc{1, 1};
    uint8_t depth = 0; // enclosing block depth: 1 = State body, 2 = Actions/Traversals body, 3 = if/else-if/else body

    // TempDecl
    const TypeDefinition* type = nullptr;
    std::string tempName;
    int tempId = -1; // index into ParsedSource::temps
    Expr* init = nullptr;

    // Assign
    VarInfo target;
    Expr* value = nullptr;

    // Call (statement-level only; void-returning calls can never be expr args)
    std::string funcName;
    uint16_t functionId = 0;
    uint8_t tier = 0;
    std::vector<Expr*> args;
    std::vector<ClaimBinding> claims; // Tier 3 only, bound to the first argument

    // Goto
    std::string targetName;
    int targetState = -1;

    // If / ElseIf / Else
    Expr* cond = nullptr;
    std::vector<Stmt> body;
};

// One item directly inside a State{} body, in source order: a state-level
// temp declaration, the Actions block, or the Traversals block.
struct StateBodyItem {
    enum class Kind : uint8_t { TempDecl, Actions, Traversals } kind = Kind::TempDecl;
    SrcLoc loc{1, 1};
    Stmt decl;            // when kind == TempDecl (carries tempName/type/tempId/init)
    std::vector<Stmt> stmts; // when kind == Actions / Traversals
};

struct StateDef {
    std::string name;
    SrcLoc loc{1, 1};
    bool isEntry = false; // resolved by Pass 1
    std::vector<StateBodyItem> items;
};

struct GlobalVar {
    std::string name;
    SrcLoc loc{1, 1};
    const TypeDefinition* type = nullptr;
    bool isConst = false;
    std::vector<uint8_t> constBytes; // const only: folded value, sizeBytes wide
    int index = -1;                  // index into ParsedSource::globals
    int bindingSlot = -1;            // runtime only: slot id (declaration order)
    uint8_t owner = fmt::OwnerExternal; // runtime only; Pass 2 may promote to DSL
};

struct TempVar {
    std::string name;
    SrcLoc loc{1, 1};
    const TypeDefinition* type = nullptr;
    uint8_t depth = 0;    // 1 = State body, 2 = Actions/Traversals body, 3 = if body
    int stateIndex = -1;
    int index = -1;       // index into ParsedSource::temps
};

// The ordered AST forest: one file = global section + a forest of state roots.
struct ParsedSource {
    std::vector<GlobalVar> globals;
    std::vector<StateDef> states;
    std::vector<TempVar> temps; // assigned in canonical serialization order during parse
    std::vector<std::unique_ptr<Expr>> exprPool;

    std::string entryName; // from @ENTRY
    SrcLoc entryLoc{1, 1};
    int entryCount = 0;
};

} // namespace fsmc
