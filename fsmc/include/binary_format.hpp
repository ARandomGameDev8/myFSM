#pragma once

#include <cstdint>
#include <cstddef>
#include <string>
#include <vector>

namespace fsmc::fmt {

// ---------------------------------------------------------------------------
// Header
// ---------------------------------------------------------------------------
constexpr uint32_t kMagic = 0x46534D44; // "FSMD"
constexpr uint16_t kVersionMajor = 0;
// v0.2 — scope-depth bytes removed (Temporary Variable entries and AST tokens).
// Temporary lifetime is C block scoping driven by the compiler's scope stack,
// so no fixed depth level exists to record: the block that owns a temp is
// structural (its TEMP_VAR_DECL token is a child of that block's AST token).
// v0.3 — claim/ownership removed: no CLAIM/RELEASE instructions, no `owner`
// byte and no `dirty` byte in Runtime Variable entries. The module no longer
// says anything about who owns a runtime variable or which of its fields a
// function drives.
constexpr uint16_t kVersionMinor = 3;
constexpr std::size_t kHeaderSize = 36;

// ---------------------------------------------------------------------------
// Section IDs.
// Addresses are 32-bit. The spec drafts this as a 2-bit section field, but
// that cannot represent the 8 section IDs (0..7): `4 << 30` overflows a
// uint32. DEVIATION (documented in DESIGN.md): the section field is 3 bits
// and the offset 29 bits (max ~536 MiB per section — far beyond any module):
//
//     [3 bits section ID][29 bits offset, from section start]
// ---------------------------------------------------------------------------
enum SectionId : uint8_t {
    SecNull = 0,
    SecGlobal = 1,
    SecRuntime = 2,
    SecTemp = 3,
    SecState = 4,
    SecToken = 5,
    SecAst = 6,
    SecFsm = 7,
};

inline uint32_t makeAddress(uint8_t section, uint32_t offset) {
    return (uint32_t(section) << 29) | (offset & 0x1FFFFFFFu);
}
inline uint8_t addressSection(uint32_t addr) { return uint8_t(addr >> 29); }
inline uint32_t addressOffset(uint32_t addr) { return addr & 0x1FFFFFFFu; }

inline const char* sectionName(uint8_t id) {
    switch (id) {
        case SecNull: return "null";
        case SecGlobal: return "Global";
        case SecRuntime: return "Runtime";
        case SecTemp: return "Temporary";
        case SecState: return "State";
        case SecToken: return "Token";
        case SecAst: return "AST";
        case SecFsm: return "FSM";
        default: return "?";
    }
}

// ---------------------------------------------------------------------------
// Token / Instruction section opcodes
// ---------------------------------------------------------------------------
// Runtime opcodes. Member names of the arithmetic/comparison subset are
// disambiguated from the OpId token-data op ids (which share the fmt
// namespace) — e.g. OpId::OpPow vs Opcode::OpPower.
enum Opcode : uint8_t {
    OpCall = 0x01,
    OpAssign = 0x02,
    OpGoto = 0x03,
    OpEval = 0x04,     // reserved: evaluate an AST expression, result discarded
    OpAdd = 0x05,
    OpSub = 0x06,
    OpMul = 0x07,
    OpDiv = 0x08,
    OpPower = 0x09,
    OpFloordiv = 0x0A,
    OpAnd = 0x0B,
    OpOr = 0x0C,
    OpNegate = 0x0D,
    OpEq = 0x0E,
    OpNeq = 0x0F,
    OpLess = 0x10,
    OpGreater = 0x11,
    OpLte = 0x12,
    OpGte = 0x13,
    // 0x14 (OpClaim) and 0x15 (OpRelease) are RETIRED as of module v0.3 —
    // claim/ownership encoding was removed from the format. Per the ID policy
    // they are never reused, and a module that contains one is invalid.
};

// ---------------------------------------------------------------------------
// AST adjacency section token types
// ---------------------------------------------------------------------------
enum class AstTok : uint8_t {
    // Containers (have {}, own ordered children)
    State = 0x01,
    Actions = 0x02,
    Traversals = 0x03,
    If = 0x04,
    ElseIf = 0x05,
    Else = 0x06,
    // Leaves
    FunctionCall = 0x10,
    Goto = 0x11,
    TempVarDecl = 0x12,
    Assign = 0x13,
    Return = 0x14, // reserved: no source construct currently emits RETURN
    // Expressions
    BinaryOp = 0x20,
    UnaryOp = 0x21,
    Literal = 0x22,
    VarRef = 0x23,
};

// ---------------------------------------------------------------------------
// Operator IDs (used in BINARY_OP / UNARY_OP data)
// ---------------------------------------------------------------------------
enum OpId : uint8_t {
    OpPlus = 0x01,
    OpMinus = 0x02,
    OpStar = 0x03,
    OpPow = 0x04,
    OpSlash = 0x05,
    OpFloorDiv = 0x06,
    OpLogicalAnd = 0x07,
    OpLogicalOr = 0x08,
    OpNot = 0x09,
    OpEqEq = 0x0A,
    OpNotEq = 0x0B,
    OpLt = 0x0C,
    OpGt = 0x0D,
    OpLe = 0x0E,
    OpGe = 0x0F,
};

// ---------------------------------------------------------------------------
// Runtime-variable ownership: REMOVED (module v0.3).
//
// Runtime Variable entries used to carry an `owner` byte (external / DSL) and
// a `dirty` byte, and Tier 3 calls used to be bracketed by CLAIM / RELEASE
// instructions naming the driven fields. None of that exists any more: a
// runtime variable is described by its type, its binding slot and its name,
// and a call is just a CALL. `enum Owner`, `enum ClaimField` and
// `claimFieldIndex()` were deleted with them.
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Explicit little-endian byte writers. No struct memcpy anywhere.
// ---------------------------------------------------------------------------
void writeU8(std::vector<uint8_t>& out, uint8_t v);
void writeU16(std::vector<uint8_t>& out, uint16_t v);
void writeU32(std::vector<uint8_t>& out, uint32_t v);
void writeU64(std::vector<uint8_t>& out, uint64_t v);
void writeFloat(std::vector<uint8_t>& out, float v);
void writeDouble(std::vector<uint8_t>& out, double v);
void writeBytes(std::vector<uint8_t>& out, const uint8_t* p, std::size_t n);
void writeString(std::vector<uint8_t>& out, const std::string& s); // u16 length + UTF-8 bytes

// Floating-point <-> bit conversions without memcpy (union member access is
// well-defined and does not violate strict aliasing).
uint32_t floatToBits(float f);
float bitsToFloat(uint32_t u);
uint64_t doubleToBits(double d);
double bitsToDouble(uint64_t u);

} // namespace fsmc::fmt
