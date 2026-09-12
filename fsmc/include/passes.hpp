#pragma once

// The six compiler passes. Each pass is a distinct named function with a
// clear input/output boundary; passes are never merged.

#include "ast.hpp"
#include "diagnostics.hpp"
#include "source.hpp"

namespace fsmc {

// ---------------------------------------------------------------------------
// Pass 1 — Symbol table.
// Establishes the global variable table (static constants + runtime
// variables) and the state list. All types are resolved via BuiltinTypes
// (done at parse time; validated here). Resolves the single @ENTRY.
// Mutates state.isEntry on the parsed source.
// ---------------------------------------------------------------------------
struct SymbolTable {
    std::vector<std::string> globalNames; // parallel to ParsedSource::globals
    std::vector<std::string> stateNames;  // parallel to ParsedSource::states
    int entryState = -1;

    int findGlobal(const std::string& name) const; // -1 when absent
    int findState(const std::string& name) const;  // -1 when absent
};

SymbolTable pass1_symbolTable(ParsedSource& src, Diagnostics& diag);

// ---------------------------------------------------------------------------
// Pass 2 — AST construction.
// Builds the ordered AST forest view: one root per State, declaration order
// verified, temp ids validated in canonical serialization order, and runtime
// ownership derived (a runtime variable claimed by any Tier 3 call becomes
// owner = DSL).
// ---------------------------------------------------------------------------
struct AstForest {
    const ParsedSource* src = nullptr;
    std::vector<int> stateRoots;      // one per state, in order
    std::vector<int> tempOrder;       // canonical temp id order (0..N-1)
    std::vector<uint8_t> runtimeOwner; // per runtime var: OwnerExternal / OwnerDsl
    int entryState = -1;
};

AstForest pass2_buildAstForest(const ParsedSource& src, const SymbolTable& sym,
                               Diagnostics& diag);

// ---------------------------------------------------------------------------
// Pass 3 — Goto collection.
// Walks each AST and collects every goto into a per-state goto list, in
// order of appearance (state-level temps first, then Actions, then
// Traversals — the execution order of the state).
// ---------------------------------------------------------------------------
struct GotoLists {
    std::vector<std::vector<std::string>> targets; // [state] -> goto target names
    std::vector<std::vector<SrcLoc>> locs;         // [state] -> positions
};

GotoLists pass3_collectGotos(const ParsedSource& src, const AstForest& forest,
                             Diagnostics& diag);

// ---------------------------------------------------------------------------
// Pass 4 — FSM adjacency.
// Resolves the collected gotos to states and builds the adjacency list
// (state -> ordered target states, duplicates preserved by goto appearance).
// ---------------------------------------------------------------------------
struct FsmGraph {
    std::vector<std::vector<int>> adjacency; // [state] -> target state indices
};

FsmGraph pass4_buildFsmAdjacency(const ParsedSource& src, const GotoLists& gotos,
                                 const SymbolTable& sym, Diagnostics& diag);

// ---------------------------------------------------------------------------
// Pass 5 — Serialization.
// Serializes all sections to binary with addressed entries (two-pass
// internally: layout, then byte emission). Function ids come from
// BuiltinFunctions; type tags and sizes come from BuiltinTypes.
// ---------------------------------------------------------------------------
std::vector<uint8_t> pass5_serialize(const ParsedSource& src, const AstForest& forest,
                                     const FsmGraph& fsm, const SymbolTable& sym,
                                     Diagnostics& diag);

// ---------------------------------------------------------------------------
// Pass 6 — Linking.
// Reads the produced module back (module_reader), verifies that every
// symbolic name and every address in the binary resolves, and produces the
// final name -> binary-address symbol maps.
// ---------------------------------------------------------------------------
struct LinkResult {
    bool ok = false;
    std::vector<std::pair<std::string, uint32_t>> stateAddresses;   // name -> State-section address
    std::vector<std::pair<std::string, uint32_t>> globalAddresses;  // name -> Global-section address
    std::vector<std::pair<std::string, uint32_t>> runtimeAddresses; // name -> Runtime-section address
    int astTokenCount = 0;
    int instructionCount = 0;
};

LinkResult pass6_link(const std::vector<uint8_t>& module, Diagnostics& diag);

} // namespace fsmc
