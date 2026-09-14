#include "passes.hpp"

#include <functional>
#include <unordered_map>

#include "module_reader.hpp"
#include "serializer.hpp"

namespace fsmc {

// ---------------------------------------------------------------------------
// shared state walk (declaration/execution order is sacred)
// ---------------------------------------------------------------------------
namespace {

// Walks every statement of a state in execution order:
//   state items in source order -> statements in order -> if bodies in order.
void walkStateStmts(const StateDef& st,
                    const std::function<void(const Stmt&)>& visit) {
    for (const StateBodyItem& item : st.items) {
        if (item.kind == StateBodyItem::Kind::TempDecl) {
            visit(item.decl);
        } else {
            for (const Stmt& s : item.stmts) {
                std::function<void(const Stmt&)> rec;
                rec = [&](const Stmt& s2) {
                    visit(s2);
                    for (const Stmt& b : s2.body) rec(b);
                };
                rec(s);
            }
        }
    }
}

} // namespace

// ---------------------------------------------------------------------------
// Pass 1 — Symbol table
// ---------------------------------------------------------------------------

SymbolTable pass1_symbolTable(ParsedSource& src, Diagnostics& diag) {
    SymbolTable sym;

    // Global variable table: all types must be resolved (they are — unknown
    // types were rejected at parse time; this is the pass boundary check).
    for (const GlobalVar& g : src.globals) {
        sym.globalNames.push_back(g.name);
        if (!g.type) {
            diag.error(g.loc, "internal: global variable '" + g.name + "' has no resolved type");
        }
    }

    // State list.
    for (const StateDef& s : src.states) {
        sym.stateNames.push_back(s.name);
    }

    // Exactly one @ENTRY, naming a declared state.
    if (src.entryCount >= 1) {
        int idx = -1;
        for (int i = 0; i < static_cast<int>(src.states.size()); ++i) {
            if (src.states[i].name == src.entryName) { idx = i; break; }
        }
        if (idx < 0) {
            diag.error(src.entryLoc, "@ENTRY must name a declared state: '" + src.entryName + "'");
        } else {
            sym.entryState = idx;
            src.states[idx].isEntry = true;
        }
    }
    return sym;
}

int SymbolTable::findGlobal(const std::string& name) const {
    for (int i = 0; i < static_cast<int>(globalNames.size()); ++i) {
        if (globalNames[i] == name) return i;
    }
    return -1;
}

int SymbolTable::findState(const std::string& name) const {
    for (int i = 0; i < static_cast<int>(stateNames.size()); ++i) {
        if (stateNames[i] == name) return i;
    }
    return -1;
}

// ---------------------------------------------------------------------------
// Pass 2 — AST construction (ordered forest + derived ownership)
// ---------------------------------------------------------------------------

AstForest pass2_buildAstForest(const ParsedSource& src, const SymbolTable& sym,
                               Diagnostics& diag) {
    AstForest forest;
    forest.src = &src;
    forest.entryState = sym.entryState;

    forest.stateRoots.resize(src.states.size());
    for (std::size_t i = 0; i < src.states.size(); ++i) forest.stateRoots[i] = static_cast<int>(i);

    // If parsing already failed, the forest is only partial: build the
    // structural maps (needed so later passes stay total) but skip the
    // internal consistency checks that would only add noise.
    const bool partial = diag.hasErrors();

    // Re-derive the canonical temp order by walking the forest in execution
    // order and verify it matches the ids assigned at parse time.
    std::vector<int> seen;
    for (const StateDef& st : src.states) {
        walkStateStmts(st, [&](const Stmt& s) {
            if (s.kind == Stmt::Kind::TempDecl) {
                if (seen.size() == static_cast<std::size_t>(s.tempId)) {
                    seen.push_back(s.tempId);
                } else if (!partial) {
                    diag.error(s.loc, "internal: temp declaration order mismatch for '" +
                                          s.tempName + "'");
                }
            }
        });
    }
    forest.tempOrder = std::move(seen);
    if (!partial && forest.tempOrder.size() != src.temps.size()) {
        diag.error({1, 1}, "internal: temp table does not match the AST forest");
    }

    // Runtime ownership: a runtime variable that is claimed by any Tier 3
    // call becomes owner = DSL (the Controller hands it over at runtime).
    int runtimeCount = 0;
    for (const GlobalVar& g : src.globals) {
        if (!g.isConst) ++runtimeCount;
    }
    forest.runtimeOwner.assign(runtimeCount, fmt::OwnerExternal);
    // c.var.index is the mixed global-table index; map it to the runtime
    // slot (declaration order among runtime variables only).
    auto runtimeSlot = [&](int globalIndex) {
        int slot = 0;
        for (std::size_t i = 0; i < src.globals.size(); ++i) {
            if (src.globals[i].isConst) continue;
            if (static_cast<int>(i) == globalIndex) return slot;
            ++slot;
        }
        return -1;
    };
    for (const StateDef& st : src.states) {
        walkStateStmts(st, [&](const Stmt& s) {
            if (s.kind != Stmt::Kind::Call) return;
            for (const ClaimBinding& c : s.claims) {
                if (c.var.kind == VarKind::Runtime) {
                    int slot = runtimeSlot(c.var.index);
                    if (slot >= 0) {
                        forest.runtimeOwner[static_cast<std::size_t>(slot)] = fmt::OwnerDsl;
                    }
                }
            }
        });
    }

    // Reference integrity: every variable reference points at a live table.
    std::function<void(const Expr*)> checkExpr;
    checkExpr = [&](const Expr* e) {
        if (!e) return;
        if (e->kind == Expr::Kind::VarRef) {
            bool ok = e->var.kind == VarKind::Temp ?
                          static_cast<std::size_t>(e->var.index) < src.temps.size() :
                          static_cast<std::size_t>(e->var.index) < src.globals.size();
            if (!ok && !partial) {
                diag.error(e->loc, "internal: variable reference '" + e->var.name +
                                       "' does not resolve");
            }
        }
        for (const Expr* a : e->args) checkExpr(a);
        checkExpr(e->left);
        checkExpr(e->right);
    };
    for (const StateDef& st : src.states) {
        walkStateStmts(st, [&](const Stmt& s) {
            if (s.kind == Stmt::Kind::Assign) checkExpr(s.value);
            if (s.kind == Stmt::Kind::Call) {
                for (const Expr* a : s.args) checkExpr(a);
            }
            if (s.kind == Stmt::Kind::TempDecl) checkExpr(s.init);
            if (s.kind == Stmt::Kind::If || s.kind == Stmt::Kind::ElseIf) checkExpr(s.cond);
        });
    }
    return forest;
}

// ---------------------------------------------------------------------------
// Pass 3 — Goto collection
// ---------------------------------------------------------------------------

GotoLists pass3_collectGotos(const ParsedSource& src, const AstForest& forest,
                             Diagnostics& diag) {
    (void)forest;
    (void)diag;
    GotoLists lists;
    lists.targets.resize(src.states.size());
    lists.locs.resize(src.states.size());

    for (std::size_t si = 0; si < src.states.size(); ++si) {
        const StateDef& st = src.states[si];
        // Actions then Traversals in execution order: gotos only exist in
        // Traversals (enforced at parse time), but we walk everything so the
        // collection is complete by construction.
        for (const StateBodyItem& item : st.items) {
            if (item.kind == StateBodyItem::Kind::TempDecl) continue;
            std::function<void(const Stmt&)> rec;
            rec = [&](const Stmt& s) {
                if (s.kind == Stmt::Kind::Goto) {
                    lists.targets[si].push_back(s.targetName);
                    lists.locs[si].push_back(s.loc);
                }
                for (const Stmt& b : s.body) rec(b);
            };
            for (const Stmt& s : item.stmts) rec(s);
        }
    }
    return lists;
}

// ---------------------------------------------------------------------------
// Pass 4 — FSM adjacency
// ---------------------------------------------------------------------------

FsmGraph pass4_buildFsmAdjacency(const ParsedSource& src, const GotoLists& gotos,
                                 const SymbolTable& sym, Diagnostics& diag) {
    FsmGraph fsm;
    fsm.adjacency.resize(src.states.size());
    for (std::size_t si = 0; si < src.states.size(); ++si) {
        for (std::size_t gi = 0; gi < gotos.targets[si].size(); ++gi) {
            const std::string& name = gotos.targets[si][gi];
            int target = sym.findState(name);
            if (target < 0) {
                diag.error(gotos.locs[si][gi], "unknown state '" + name + "' in goto");
                continue;
            }
            fsm.adjacency[si].push_back(target);
        }
    }
    return fsm;
}

// ---------------------------------------------------------------------------
// Pass 5 — Serialization
// ---------------------------------------------------------------------------

std::vector<uint8_t> pass5_serialize(const ParsedSource& src, const AstForest& forest,
                                     const FsmGraph& fsm, const SymbolTable& sym,
                                     Diagnostics& diag) {
    return serializeModule(src, forest, fsm, sym, diag);
}

// ---------------------------------------------------------------------------
// Pass 6 — Linking
// ---------------------------------------------------------------------------

LinkResult pass6_link(const std::vector<uint8_t>& module, Diagnostics& diag) {
    LinkResult result;

    ReadModule read;
    std::string err;
    if (!readModule(module, read, err)) {
        diag.error({1, 1}, "link: failed to read back the module: " + err);
        return result;
    }
    if (!validateModule(read, err)) {
        diag.error({1, 1}, "link: module validation failed: " + err);
        return result;
    }

    for (std::size_t i = 0; i < read.states.size(); ++i) {
        result.stateAddresses.emplace_back(read.states[i].name, read.states[i].addr);
    }
    for (std::size_t i = 0; i < read.globals.size(); ++i) {
        result.globalAddresses.emplace_back(read.globals[i].name, read.globals[i].addr);
    }
    for (std::size_t i = 0; i < read.runtime.size(); ++i) {
        result.runtimeAddresses.emplace_back(read.runtime[i].name, read.runtime[i].addr);
    }
    result.astTokenCount = static_cast<int>(read.ast.size());
    result.instructionCount = 0;
    for (const auto& s : read.stateInstrs) result.instructionCount += static_cast<int>(s.instrs.size());
    result.ok = true;
    return result;
}

} // namespace fsmc
