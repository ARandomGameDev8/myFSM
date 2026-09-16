#include "serializer.hpp"

#include <string>

#include "binary_format.hpp"
#include "builtin_functions.hpp"
#include "builtin_types.hpp"

namespace fsmc {

namespace {

// ---------------------------------------------------------------------------
// emission model
// ---------------------------------------------------------------------------

struct VarAddrRef {
    int kind = -1;   // 0 = global, 1 = runtime, 2 = temp
    int index = -1;
};

struct InstrOperand {
    int kind = -1;   // 0 = ast token, 1 = global var, 2 = runtime var,
                     // 3 = temp var, 4 = state, 9 = raw u32
    int index = -1;
    uint32_t raw = 0;
};

struct EmitInstr {
    uint8_t opcode = 0;
    std::vector<InstrOperand> ops;
};

struct EmitStateStream {
    int stateIndex = -1;
    std::vector<EmitInstr> instrs;
};

// One AST token. Token nesting — and therefore which block owns a temporary —
// is expressed by `children` alone: there is no depth field anywhere.
struct EmitToken {
    uint8_t type = 0;
    std::vector<int> children; // ordered token indices

    bool isEntry = false;          // STATE
    int condTok = -1;              // IF / ELSE_IF
    uint16_t functionId = 0;       // FUNCTION_CALL
    std::vector<int> argToks;      // FUNCTION_CALL
    int gotoState = -1;            // GOTO
    uint8_t declTypeTag = 0;       // TEMP_VAR_DECL
    VarAddrRef declVar;            // TEMP_VAR_DECL
    VarAddrRef assignTarget;       // ASSIGN
    int assignValueTok = -1;       // ASSIGN
    VarAddrRef refVar;             // VAR_REF
    uint8_t litTag = 0;            // LITERAL
    std::vector<uint8_t> litBytes; // LITERAL
    uint8_t opId = 0;              // BINARY_OP / UNARY_OP
    int opLeftTok = -1;
    int opRightTok = -1;
};

struct Emission {
    std::vector<EmitToken> tokens;
    std::vector<EmitStateStream> stateStreams;
    std::vector<int> stateRootTok; // STATE token index per state
};

int varKindOf(const VarInfo& v) {
    switch (v.kind) {
        case VarKind::GlobalConst: return 0;
        case VarKind::Runtime: return 1;
        case VarKind::Temp: return 2;
    }
    return -1;
}

std::vector<uint8_t> exprLiteralBytes(const Expr* e) {
    std::vector<uint8_t> out;
    const TypeDefinition* t = e->type;
    if (!t) return out;
    switch (t->typeTag) {
        case 0x01: fmt::writeU32(out, uint32_t(e->litInt)); break;
        case 0x02: fmt::writeFloat(out, static_cast<float>(e->litReal)); break;
        case 0x03: fmt::writeDouble(out, e->litReal); break;
        case 0x04: fmt::writeU8(out, e->litBool ? 1 : 0); break;
        case 0x05: // string — [4] byte length + UTF-8 bytes (variable width)
            fmt::writeU32(out, uint32_t(e->litStr.size()));
            fmt::writeBytes(out, reinterpret_cast<const uint8_t*>(e->litStr.data()),
                            e->litStr.size());
            break;
        case 0x10: case 0x11: case 0x12:
            for (int k = 0; k < e->litVecCount; ++k) fmt::writeFloat(out, e->litVec[k]);
            break;
        default: break;
    }
    return out;
}

class Walker {
public:
    Walker(const ParsedSource& src, const SymbolTable& sym, Emission& em)
        : src_(src), sym_(sym), em_(em) {
        em_.stateStreams.resize(src_.states.size());
        em_.stateRootTok.resize(src_.states.size());
        for (std::size_t i = 0; i < src_.states.size(); ++i) {
            em_.stateStreams[i].stateIndex = static_cast<int>(i);
        }
    }

    void walkAll() {
        for (std::size_t si = 0; si < src_.states.size(); ++si) {
            em_.stateRootTok[si] = walkState(src_.states[si], static_cast<int>(si));
        }
    }

private:
    int walkExpr(const Expr* e) {
        if (!e || !e->type) return -1; // compile already failed; keep the walk safe
        EmitToken t;
        switch (e->kind) {
            case Expr::Kind::Literal: {
                t.type = uint8_t(fmt::AstTok::Literal);
                t.litTag = e->type->typeTag;
                t.litBytes = exprLiteralBytes(e);
                int idx = int(em_.tokens.size());
                em_.tokens.push_back(std::move(t));
                return idx;
            }
            case Expr::Kind::VarRef: {
                t.type = uint8_t(fmt::AstTok::VarRef);
                t.refVar = {varKindOf(e->var), e->var.index};
                int idx = int(em_.tokens.size());
                em_.tokens.push_back(std::move(t));
                return idx;
            }
            case Expr::Kind::Binary: {
                t.type = uint8_t(fmt::AstTok::BinaryOp);
                t.opId = e->op;
                int idx = int(em_.tokens.size());
                em_.tokens.push_back(std::move(t));
                em_.tokens[idx].opLeftTok = walkExpr(e->left);
                em_.tokens[idx].opRightTok = walkExpr(e->right);
                return idx;
            }
            case Expr::Kind::Unary: {
                t.type = uint8_t(fmt::AstTok::UnaryOp);
                t.opId = e->op;
                int idx = int(em_.tokens.size());
                em_.tokens.push_back(std::move(t));
                em_.tokens[idx].opLeftTok = walkExpr(e->left);
                return idx;
            }
            case Expr::Kind::Call: {
                t.type = uint8_t(fmt::AstTok::FunctionCall);
                t.functionId = e->functionId;
                int idx = int(em_.tokens.size());
                em_.tokens.push_back(std::move(t));
                for (const Expr* a : e->args) {
                    int child = walkExpr(a);
                    em_.tokens[idx].argToks.push_back(child);
                }
                return idx;
            }
        }
        return -1;
    }

    // Walks one statement, emitting its tokens (statement entry first, then
    // its expression subtrees) in pre-order. Returns the pool indices of the
    // top-level tokens added, in order. Callers append those to their own
    // children vector — always by index, never through a pointer held across
    // a push_back (the pool reallocates).
    std::vector<int> walkStmt(const Stmt& st, EmitStateStream* ss) {
        std::vector<int> added;
        switch (st.kind) {
            case Stmt::Kind::TempDecl: {
                EmitToken d;
                d.type = uint8_t(fmt::AstTok::TempVarDecl);
                d.declTypeTag = st.type ? st.type->typeTag : 0;
                d.declVar = {2, st.tempId};
                int dIdx = int(em_.tokens.size());
                em_.tokens.push_back(std::move(d));
                added.push_back(dIdx);
                if (st.init) {
                    EmitToken a;
                    a.type = uint8_t(fmt::AstTok::Assign);
                    a.assignTarget = {2, st.tempId};
                    int aIdx = int(em_.tokens.size());
                    em_.tokens.push_back(std::move(a));
                    em_.tokens[aIdx].assignValueTok = walkExpr(st.init);
                    added.push_back(aIdx);
                    if (ss) {
                        ss->instrs.push_back(
                            {fmt::OpAssign,
                             {{3, st.tempId}, {0, em_.tokens[aIdx].assignValueTok}}});
                    }
                }
                break;
            }
            case Stmt::Kind::Assign: {
                EmitToken a;
                a.type = uint8_t(fmt::AstTok::Assign);
                a.assignTarget = {varKindOf(st.target), st.target.index};
                int aIdx = int(em_.tokens.size());
                em_.tokens.push_back(std::move(a));
                em_.tokens[aIdx].assignValueTok = walkExpr(st.value);
                added.push_back(aIdx);
                if (ss) {
                    ss->instrs.push_back(
                        {fmt::OpAssign,
                         {{1 + a.assignTarget.kind, a.assignTarget.index},
                          {0, em_.tokens[aIdx].assignValueTok}}});
                }
                break;
            }
            case Stmt::Kind::Call: {
                EmitToken c;
                c.type = uint8_t(fmt::AstTok::FunctionCall);
                c.functionId = st.functionId;
                int cIdx = int(em_.tokens.size());
                em_.tokens.push_back(std::move(c));
                for (const Expr* a : st.args) {
                    int child = walkExpr(a);
                    em_.tokens[cIdx].argToks.push_back(child);
                }
                added.push_back(cIdx);
                // A call is a single CALL instruction at every tier: there is
                // no claim/release bracketing any more.
                if (ss) ss->instrs.push_back({fmt::OpCall, {{0, cIdx}}});
                break;
            }
            case Stmt::Kind::Goto: {
                int target = sym_.findState(st.targetName);
                if (target < 0) {
                    // Pass 4 already reported this; keep the walk total.
                    target = 0;
                }
                EmitToken g;
                g.type = uint8_t(fmt::AstTok::Goto);
                g.gotoState = target;
                int gIdx = int(em_.tokens.size());
                em_.tokens.push_back(std::move(g));
                added.push_back(gIdx);
                if (ss) ss->instrs.push_back({fmt::OpGoto, {{4, target}}});
                break;
            }
            case Stmt::Kind::If:
            case Stmt::Kind::ElseIf:
            case Stmt::Kind::Else: {
                uint8_t type = st.kind == Stmt::Kind::If ? uint8_t(fmt::AstTok::If)
                              : st.kind == Stmt::Kind::ElseIf ? uint8_t(fmt::AstTok::ElseIf)
                                                              : uint8_t(fmt::AstTok::Else);
                EmitToken i;
                i.type = type;
                int iIdx = int(em_.tokens.size());
                em_.tokens.push_back(std::move(i));
                if (st.cond) em_.tokens[iIdx].condTok = walkExpr(st.cond);
                // The branch body's tokens are children of the IF/ELSE_IF/ELSE
                // token: that parent edge is the scope of anything declared here.
                for (const Stmt& b : st.body) {
                    for (int c : walkStmt(b, ss)) {
                        em_.tokens[iIdx].children.push_back(c);
                    }
                }
                added.push_back(iIdx);
                break;
            }
        }
        return added;
    }

    int walkState(const StateDef& st, int stateIndex) {
        EmitToken s;
        s.type = uint8_t(fmt::AstTok::State);
        s.isEntry = st.isEntry;
        int sIdx = int(em_.tokens.size());
        em_.tokens.push_back(std::move(s));

        EmitStateStream& ss = em_.stateStreams[stateIndex];

        // Entry phase first: state-level temp declarations (declaration order)
        // run on state entry, before Actions.
        for (const StateBodyItem& item : st.items) {
            if (item.kind == StateBodyItem::Kind::TempDecl) {
                // State-body temps: children of the STATE token, i.e. owned by
                // the state's own block (created on entry, destroyed on exit).
                for (int c : walkStmt(item.decl, &ss)) {
                    em_.tokens[sIdx].children.push_back(c);
                }
            }
        }
        for (const StateBodyItem& item : st.items) {
            if (item.kind == StateBodyItem::Kind::Actions) {
                EmitToken a;
                a.type = uint8_t(fmt::AstTok::Actions);
                int aIdx = int(em_.tokens.size());
                em_.tokens.push_back(std::move(a));
                // Direct children in source order: `temp` declarations (owned by
                // the Actions block itself) interleaved with the two mandatory
                // phase blocks, START and UPDATE. Each phase block is a
                // container, so it is a temporary scope of its own, and its
                // instructions land in the state's stream at its source
                // position — Start's run once on entry, Update's every tick.
                for (const StateBodyItem::Child& child : item.actionsChildren) {
                    if (child.kind == StateBodyItem::Child::Kind::Temp) {
                        const Stmt& stmt = item.stmts[std::size_t(child.tempIndex)];
                        for (int c : walkStmt(stmt, &ss)) {
                            em_.tokens[aIdx].children.push_back(c);
                        }
                        continue;
                    }
                    const bool isStart = child.kind == StateBodyItem::Child::Kind::Start;
                    EmitToken ph;
                    ph.type = uint8_t(isStart ? fmt::AstTok::Start : fmt::AstTok::Update);
                    int pIdx = int(em_.tokens.size());
                    em_.tokens.push_back(std::move(ph));
                    for (const Stmt& stmt : isStart ? item.startStmts : item.updateStmts) {
                        for (int c : walkStmt(stmt, &ss)) {
                            em_.tokens[pIdx].children.push_back(c);
                        }
                    }
                    em_.tokens[aIdx].children.push_back(pIdx);
                }
                em_.tokens[sIdx].children.push_back(aIdx);
            } else if (item.kind == StateBodyItem::Kind::Traversals) {
                EmitToken a;
                a.type = uint8_t(fmt::AstTok::Traversals);
                int aIdx = int(em_.tokens.size());
                em_.tokens.push_back(std::move(a));
                for (const Stmt& stmt : item.stmts) {
                    for (int c : walkStmt(stmt, &ss)) {
                        em_.tokens[aIdx].children.push_back(c);
                    }
                }
                em_.tokens[sIdx].children.push_back(aIdx);
            }
        }
        return sIdx;
    }

    const ParsedSource& src_;
    const SymbolTable& sym_;
    Emission& em_;
};

// ---------------------------------------------------------------------------
// layout
// ---------------------------------------------------------------------------

std::size_t astDataSize(const EmitToken& t) {
    switch (t.type) {
        case uint8_t(fmt::AstTok::State): return 1;
        case uint8_t(fmt::AstTok::Actions):
        case uint8_t(fmt::AstTok::Traversals):
        case uint8_t(fmt::AstTok::Start):
        case uint8_t(fmt::AstTok::Update):
        case uint8_t(fmt::AstTok::Else): return 0;
        case uint8_t(fmt::AstTok::If):
        case uint8_t(fmt::AstTok::ElseIf): return 4;
        case uint8_t(fmt::AstTok::FunctionCall): return 3 + 4 * t.argToks.size();
        case uint8_t(fmt::AstTok::Goto): return 4;
        case uint8_t(fmt::AstTok::TempVarDecl): return 5;
        case uint8_t(fmt::AstTok::Assign): return 8;
        case uint8_t(fmt::AstTok::Return): return 4;
        case uint8_t(fmt::AstTok::BinaryOp): return 9;
        case uint8_t(fmt::AstTok::UnaryOp): return 5;
        case uint8_t(fmt::AstTok::Literal): return 1 + t.litBytes.size();
        case uint8_t(fmt::AstTok::VarRef): return 4;
    }
    return 0;
}

struct Layout {
    uint32_t secStart[8] = {};
    uint32_t fileSize = 0;
    std::vector<uint32_t> globalAddr;
    std::vector<uint32_t> runtimeAddr;
    std::vector<uint32_t> tempAddr;
    std::vector<uint32_t> stateAddr;
    std::vector<uint32_t> astAddr;
};

Layout computeLayout(const ParsedSource& src, const Emission& em, const FsmGraph& fsm) {
    Layout L;
    L.globalAddr.assign(src.globals.size(), 0);
    L.runtimeAddr.assign(src.globals.size(), 0);
    L.tempAddr.resize(src.temps.size());
    L.stateAddr.resize(src.states.size());
    L.astAddr.resize(em.tokens.size());

    std::size_t off = fmt::kHeaderSize;

    // Global Variable Section (static constants, in declaration order)
    L.secStart[fmt::SecGlobal] = uint32_t(off);
    std::size_t e = 0;
    for (std::size_t i = 0; i < src.globals.size(); ++i) {
        const GlobalVar& g = src.globals[i];
        if (!g.isConst) continue;
        L.globalAddr[i] = fmt::makeAddress(fmt::SecGlobal, uint32_t(e));
        // Value width: exactly sizeBytes for fixed-size types; for `string` the
        // folded bytes are `[4] length` + payload, so measure what was folded.
        const std::size_t valueBytes =
            g.type->isVariableSize ? g.constBytes.size() : g.type->sizeBytes;
        e += 4 + 1 + valueBytes + 2 + g.name.size();
    }
    off = L.secStart[fmt::SecGlobal] + e;

    // Runtime Variable Section (runtime vars, in declaration order)
    // Entry: [4] self-addr [1] type tag [4] binding slot [2] name length [·] name
    L.secStart[fmt::SecRuntime] = uint32_t(off);
    e = 0;
    for (std::size_t i = 0; i < src.globals.size(); ++i) {
        const GlobalVar& g = src.globals[i];
        if (g.isConst) continue;
        L.runtimeAddr[i] = fmt::makeAddress(fmt::SecRuntime, uint32_t(e));
        e += 4 + 1 + 4 + 2 + g.name.size();
    }
    off = L.secStart[fmt::SecRuntime] + e;

    // Temporary Variable Section (canonical execution order)
    // Entry: [4] self-addr [1] type tag [2] name length [·] name
    L.secStart[fmt::SecTemp] = uint32_t(off);
    e = 0;
    for (std::size_t i = 0; i < src.temps.size(); ++i) {
        L.tempAddr[i] = fmt::makeAddress(fmt::SecTemp, uint32_t(e));
        e += 4 + 1 + 2 + src.temps[i].name.size();
    }
    off = L.secStart[fmt::SecTemp] + e;

    // State Section (4-byte count prefix + entries)
    L.secStart[fmt::SecState] = uint32_t(off);
    e = 4;
    for (std::size_t i = 0; i < src.states.size(); ++i) {
        L.stateAddr[i] = fmt::makeAddress(fmt::SecState, uint32_t(e));
        e += 4 + 2 + src.states[i].name.size();
    }
    off = L.secStart[fmt::SecState] + e;

    // Token / Instruction Section (per-state framed)
    L.secStart[fmt::SecToken] = uint32_t(off);
    e = 0;
    for (std::size_t i = 0; i < em.stateStreams.size(); ++i) {
        e += 4 + 2;
        for (const EmitInstr& in : em.stateStreams[i].instrs) {
            e += 1 + 1 + 4 * in.ops.size();
        }
    }
    off = L.secStart[fmt::SecToken] + e;

    // AST Adjacency Section (flat token array)
    // Entry: [1] type [2] child-count [4]x child + type-specific data
    L.secStart[fmt::SecAst] = uint32_t(off);
    e = 0;
    for (std::size_t i = 0; i < em.tokens.size(); ++i) {
        L.astAddr[i] = fmt::makeAddress(fmt::SecAst, uint32_t(e));
        e += 1 + 2 + 4 * em.tokens[i].children.size() + astDataSize(em.tokens[i]);
    }
    off = L.secStart[fmt::SecAst] + e;

    // FSM Adjacency Section (4-byte count prefix + entries)
    L.secStart[fmt::SecFsm] = uint32_t(off);
    e = 4;
    for (std::size_t i = 0; i < fsm.adjacency.size(); ++i) {
        e += 4 + 2 + 4 * fsm.adjacency[i].size();
    }
    L.fileSize = uint32_t(off + e);

    L.secStart[fmt::SecNull] = 0;
    return L;
}

// ---------------------------------------------------------------------------
// byte emission
// ---------------------------------------------------------------------------

uint32_t varAddress(const VarAddrRef& v, const Layout& L) {
    switch (v.kind) {
        case 0: return L.globalAddr[static_cast<std::size_t>(v.index)];
        case 1: return L.runtimeAddr[static_cast<std::size_t>(v.index)];
        case 2: return L.tempAddr[static_cast<std::size_t>(v.index)];
    }
    return 0;
}

uint32_t operandAddress(const InstrOperand& op, const Layout& L) {
    switch (op.kind) {
        case 0: return L.astAddr[static_cast<std::size_t>(op.index)];
        case 1: return L.globalAddr[static_cast<std::size_t>(op.index)];
        case 2: return L.runtimeAddr[static_cast<std::size_t>(op.index)];
        case 3: return L.tempAddr[static_cast<std::size_t>(op.index)];
        case 4: return L.stateAddr[static_cast<std::size_t>(op.index)];
        case 9: return op.raw;
    }
    return 0;
}

void writeTokenData(std::vector<uint8_t>& out, const EmitToken& t, const Layout& L) {
    switch (t.type) {
        case uint8_t(fmt::AstTok::State):
            fmt::writeU8(out, t.isEntry ? 1 : 0);
            break;
        case uint8_t(fmt::AstTok::If):
        case uint8_t(fmt::AstTok::ElseIf):
            fmt::writeU32(out, L.astAddr[static_cast<std::size_t>(t.condTok)]);
            break;
        case uint8_t(fmt::AstTok::FunctionCall):
            fmt::writeU16(out, t.functionId);
            fmt::writeU8(out, uint8_t(t.argToks.size()));
            for (int a : t.argToks) fmt::writeU32(out, L.astAddr[static_cast<std::size_t>(a)]);
            break;
        case uint8_t(fmt::AstTok::Goto):
            fmt::writeU32(out, L.stateAddr[static_cast<std::size_t>(t.gotoState)]);
            break;
        case uint8_t(fmt::AstTok::TempVarDecl):
            fmt::writeU8(out, t.declTypeTag);
            fmt::writeU32(out, L.tempAddr[static_cast<std::size_t>(t.declVar.index)]);
            break;
        case uint8_t(fmt::AstTok::Assign):
            fmt::writeU32(out, varAddress(t.assignTarget, L));
            fmt::writeU32(out, L.astAddr[static_cast<std::size_t>(t.assignValueTok)]);
            break;
        case uint8_t(fmt::AstTok::VarRef):
            fmt::writeU32(out, varAddress(t.refVar, L));
            break;
        case uint8_t(fmt::AstTok::BinaryOp):
            fmt::writeU8(out, t.opId);
            fmt::writeU32(out, L.astAddr[static_cast<std::size_t>(t.opLeftTok)]);
            fmt::writeU32(out, L.astAddr[static_cast<std::size_t>(t.opRightTok)]);
            break;
        case uint8_t(fmt::AstTok::UnaryOp):
            fmt::writeU8(out, t.opId);
            fmt::writeU32(out, L.astAddr[static_cast<std::size_t>(t.opLeftTok)]);
            break;
        case uint8_t(fmt::AstTok::Literal):
            fmt::writeU8(out, t.litTag);
            fmt::writeBytes(out, t.litBytes.data(), t.litBytes.size());
            break;
        default:
            break;
    }
}

} // namespace

std::vector<uint8_t> serializeModule(const ParsedSource& src, const AstForest& forest,
                                     const FsmGraph& fsm, const SymbolTable& sym,
                                     Diagnostics& diag) {
    if (diag.hasErrors()) return {};
    // `forest` is part of the Pass 5 boundary but currently unused: the only
    // thing it contributed was the derived runtime ownership, which no longer
    // exists in the module. The AST itself is walked from `src`.
    (void)forest;

    // --- sub-pass A: emission (flat token pool + per-state instruction
    // streams), pre-order DFS, deterministic order ---
    Emission em;
    Walker walker(src, sym, em);
    walker.walkAll();

    // --- sub-pass B: layout (sizes + addresses) ---
    Layout L = computeLayout(src, em, fsm);

    // --- sub-pass C: byte emission ---
    std::vector<uint8_t> out;
    out.reserve(L.fileSize);

    // Header (36 bytes)
    fmt::writeU32(out, fmt::kMagic);
    fmt::writeU16(out, fmt::kVersionMajor);
    fmt::writeU16(out, fmt::kVersionMinor);
    for (int id = 1; id <= 7; ++id) fmt::writeU32(out, L.secStart[uint8_t(id)]);

    // Global Variable Section
    for (std::size_t i = 0; i < src.globals.size(); ++i) {
        const GlobalVar& g = src.globals[i];
        if (!g.isConst) continue;
        fmt::writeU32(out, L.globalAddr[i]);
        fmt::writeU8(out, g.type->typeTag);
        fmt::writeBytes(out, g.constBytes.data(), g.constBytes.size());
        fmt::writeString(out, g.name);
    }

    // Runtime Variable Section — type, binding slot and name; no owner, no
    // dirty flag (ownership is not a property of the module any more).
    for (std::size_t i = 0; i < src.globals.size(); ++i) {
        const GlobalVar& g = src.globals[i];
        if (g.isConst) continue;
        fmt::writeU32(out, L.runtimeAddr[i]);
        fmt::writeU8(out, g.type->typeTag);
        fmt::writeU32(out, uint32_t(g.bindingSlot));
        fmt::writeString(out, g.name);
    }

    // Temporary Variable Section
    for (std::size_t i = 0; i < src.temps.size(); ++i) {
        const TempVar& t = src.temps[i];
        fmt::writeU32(out, L.tempAddr[i]);
        fmt::writeU8(out, t.type ? t.type->typeTag : 0);
        fmt::writeString(out, t.name);
    }

    // State Section
    fmt::writeU32(out, uint32_t(src.states.size()));
    for (std::size_t i = 0; i < src.states.size(); ++i) {
        fmt::writeU32(out, L.astAddr[static_cast<std::size_t>(em.stateRootTok[i])]);
        fmt::writeString(out, src.states[i].name);
    }

    // Token / Instruction Section (per-state framed)
    for (std::size_t i = 0; i < em.stateStreams.size(); ++i) {
        const EmitStateStream& ss = em.stateStreams[i];
        fmt::writeU32(out, L.stateAddr[i]);
        fmt::writeU16(out, uint16_t(ss.instrs.size()));
        for (const EmitInstr& in : ss.instrs) {
            fmt::writeU8(out, in.opcode);
            fmt::writeU8(out, uint8_t(in.ops.size()));
            for (const InstrOperand& op : in.ops) {
                fmt::writeU32(out, operandAddress(op, L));
            }
        }
    }

    // AST Adjacency Section
    for (std::size_t i = 0; i < em.tokens.size(); ++i) {
        const EmitToken& t = em.tokens[i];
        fmt::writeU8(out, t.type);
        fmt::writeU16(out, uint16_t(t.children.size()));
        for (int c : t.children) fmt::writeU32(out, L.astAddr[static_cast<std::size_t>(c)]);
        writeTokenData(out, t, L);
    }

    // FSM Adjacency Section
    fmt::writeU32(out, uint32_t(fsm.adjacency.size()));
    for (std::size_t i = 0; i < fsm.adjacency.size(); ++i) {
        fmt::writeU32(out, L.stateAddr[i]);
        fmt::writeU16(out, uint16_t(fsm.adjacency[i].size()));
        for (int target : fsm.adjacency[i]) {
            fmt::writeU32(out, L.stateAddr[static_cast<std::size_t>(target)]);
        }
    }

    if (out.size() != L.fileSize) {
        diag.error({1, 1}, "internal: serializer size mismatch (layout says " +
                               std::to_string(L.fileSize) + ", wrote " +
                               std::to_string(out.size()) + ")");
        return {};
    }
    return out;
}

} // namespace fsmc
