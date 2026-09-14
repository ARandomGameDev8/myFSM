#pragma once

// Binary module reader + validator.
//
// Used by Pass 6 (linking: read the produced module back and verify that every
// address and symbolic reference resolves) and by the round-trip tests. This
// reader is the reference decoder for the .fsmb format.

#include <array>
#include <cstdint>
#include <string>
#include <vector>

namespace fsmc {

struct ReadGlobal {
    uint32_t addr = 0;
    uint8_t tag = 0;
    std::vector<uint8_t> value;
    std::string name;
};

struct ReadRuntime {
    uint32_t addr = 0;
    uint8_t tag = 0;
    uint8_t owner = 0;
    uint8_t dirty = 0;
    uint32_t bindingSlot = 0;
    std::string name;
};

// Entry: [4] self-addr [1] type tag [2] name length [·] name.
// No scope-depth field: a temp's block is structural (its TEMP_VAR_DECL token
// is a child of the AST token of the block that declared it).
struct ReadTemp {
    uint32_t addr = 0;
    uint8_t tag = 0;
    std::string name;
};

struct ReadState {
    uint32_t addr = 0; // AST root address
    std::string name;
};

struct ReadInstr {
    uint8_t opcode = 0;
    std::vector<uint32_t> operands;
};

struct ReadStateInstrs {
    uint32_t stateAddr = 0;
    std::vector<ReadInstr> instrs;
};

// Entry: [1] type [2] child-count [4]x child address + type-specific data.
struct ReadAstToken {
    uint8_t type = 0;
    std::vector<uint32_t> children; // child addresses (ordered)
    std::vector<uint8_t> data;      // raw data bytes (type-specific)
};

struct ReadFsmEntry {
    uint32_t src = 0;
    std::vector<uint32_t> targets;
};

struct ReadModule {
    uint16_t major = 0;
    uint16_t minor = 0;
    std::array<uint32_t, 8> sectionOffsets{}; // by section id (1..7 used)
    std::vector<ReadGlobal> globals;
    std::vector<ReadRuntime> runtime;
    std::vector<ReadTemp> temps;
    std::vector<ReadState> states;
    std::vector<ReadStateInstrs> stateInstrs;
    std::vector<ReadAstToken> ast;
    std::vector<ReadFsmEntry> fsm;
    uint32_t fileSize = 0;

    // Offsets (within their sections) of each AST entry, parallel to ast.
    std::vector<uint32_t> astEntryOffsets;
    // Offsets of each state-section entry, parallel to states.
    std::vector<uint32_t> stateEntryOffsets;
    std::vector<uint32_t> globalEntryOffsets;
    std::vector<uint32_t> runtimeEntryOffsets;
    std::vector<uint32_t> tempEntryOffsets;

    bool astIndexByAddress(uint32_t addr, uint32_t& entryOffset) const;
    bool stateIndexByAddress(uint32_t addr, uint32_t& entryOffset) const;
};

// Parses the whole module. Returns false with err set on structural problems
// (bad magic, truncated data, offsets that do not tile the file, ...).
bool readModule(const std::vector<uint8_t>& bytes, ReadModule& out, std::string& err);

// Deep validation: every internal reference (addresses, child pointers,
// condition pointers, call/function ids, operand kinds per opcode) must
// resolve to a plausible target. Returns false with err set on the first
// violation.
bool validateModule(const ReadModule& m, std::string& err);

} // namespace fsmc
