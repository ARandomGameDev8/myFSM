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
    std::string str; // string only

    // Little-endian raw bytes: exactly sizeBytes wide for fixed-size types,
    // `[4] byte length` + UTF-8 bytes for `string`.
    std::vector<uint8_t> toBytes() const;
};

struct Expr;

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
    std::string litStr; // string literal (escapes already decoded by the lexer)

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
//
// A statement carries no block-depth level: the block it belongs to is exactly
// the container that holds it (StateDef::items, a StateBodyItem::stmts, or a
// Stmt::body of an if/else-if/else), which is what the scope stack mirrors.
struct Stmt {
    enum class Kind : uint8_t { TempDecl, Assign, Call, Goto, If, ElseIf, Else } kind = Kind::TempDecl;
    SrcLoc loc{1, 1};

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
    // Actions: the `temp` declarations written directly in the Actions body —
    // they live in the Actions frame, so both phase blocks can see them.
    // Traversals: the transition statements.
    std::vector<Stmt> stmts;

    // Actions only: the two mandatory phase blocks. Start{} runs once, when the
    // state is entered; Update{} runs every tick. Each is its own scope, nested
    // inside the Actions frame, so a temp declared in one is invisible in the
    // other.
    std::vector<Stmt> startStmts;
    std::vector<Stmt> updateStmts;
    SrcLoc startLoc{1, 1};
    SrcLoc updateLoc{1, 1};
    bool hasStart = false;
    bool hasUpdate = false;

    // Actions only: the source order of the body's direct children, so the AST
    // mirrors it exactly (a temp may be declared before, between or after the
    // two phase blocks).
    struct Child {
        enum class Kind : uint8_t { Temp, Start, Update } kind = Kind::Temp;
        int tempIndex = -1; // index into stmts, when kind == Temp
    };
    std::vector<Child> actionsChildren;
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
};

// One temporary variable. Lifetime is C block scoping: it is created at its
// declaration and destroyed by the closing '}' of the block that declared it.
struct TempVar {
    std::string name;
    SrcLoc loc{1, 1};
    const TypeDefinition* type = nullptr;
    // Id of the scope-stack frame — the direct parent '{' body — that declared
    // this temp. Internal bookkeeping only: it is NOT serialized (the module has
    // no depth field). The owning block is structural instead: the temp's
    // TEMP_VAR_DECL token is a child of that block's AST token.
    int scopeId = -1;
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
