#include "module_reader.hpp"

#include <cstdio>
#include <unordered_set>
#include <utility>

#include "binary_format.hpp"
#include "builtin_functions.hpp"
#include "builtin_types.hpp"

namespace fsmc {

namespace {

struct Cursor {
    const std::vector<uint8_t>& b;
    uint32_t p = 0;
    bool ok = true;

    explicit Cursor(const std::vector<uint8_t>& bytes) : b(bytes) {}

    bool u8(uint8_t& v) {
        if (p >= b.size()) { ok = false; return false; }
        v = b[p++];
        return true;
    }
    bool u16(uint16_t& v) {
        uint8_t lo = 0, hi = 0;
        if (!u8(lo) || !u8(hi)) return false;
        v = uint16_t(lo | (hi << 8));
        return true;
    }
    bool u32(uint32_t& v) {
        uint8_t a[4] = {0, 0, 0, 0};
        for (int i = 0; i < 4; ++i) {
            if (!u8(a[i])) return false;
        }
        v = uint32_t(a[0]) | (uint32_t(a[1]) << 8) | (uint32_t(a[2]) << 16) | (uint32_t(a[3]) << 24);
        return true;
    }
    bool bytes(std::vector<uint8_t>& out, std::size_t n) {
        if (p + n > b.size()) { ok = false; return false; }
        out.insert(out.end(), b.begin() + p, b.begin() + p + n);
        p += uint32_t(n);
        return true;
    }
    bool str(std::string& s) {
        uint16_t n = 0;
        if (!u16(n)) return false;
        s.resize(n);
        if (p + n > b.size()) { ok = false; return false; }
        for (std::size_t i = 0; i < n; ++i) s[i] = char(b[p + i]);
        p += n;
        return true;
    }
};

uint32_t typeSizeByTag(uint8_t tag, bool& known) {
    for (const TypeDefinition& t : BuiltinTypes::instance().all()) {
        if (t.typeTag == tag) { known = true; return t.sizeBytes; }
    }
    known = false;
    return 0;
}

bool isExprTok(uint8_t t) {
    return t == uint8_t(fmt::AstTok::FunctionCall) || t == uint8_t(fmt::AstTok::BinaryOp) ||
           t == uint8_t(fmt::AstTok::UnaryOp) || t == uint8_t(fmt::AstTok::Literal) ||
           t == uint8_t(fmt::AstTok::VarRef);
}

std::size_t astDataSize(uint8_t type, const std::vector<uint8_t>& head, bool& knownTag) {
    // head contains the data bytes already consumed for variable-length parts.
    switch (type) {
        case uint8_t(fmt::AstTok::State): return 1;
        case uint8_t(fmt::AstTok::Actions):
        case uint8_t(fmt::AstTok::Traversals):
        case uint8_t(fmt::AstTok::Else): return 0;
        case uint8_t(fmt::AstTok::If):
        case uint8_t(fmt::AstTok::ElseIf): return 4;
        case uint8_t(fmt::AstTok::Goto): return 4;
        case uint8_t(fmt::AstTok::TempVarDecl): return 5;
        case uint8_t(fmt::AstTok::Assign): return 8;
        case uint8_t(fmt::AstTok::Return): return 4;
        case uint8_t(fmt::AstTok::BinaryOp): return 9;
        case uint8_t(fmt::AstTok::UnaryOp): return 5;
        case uint8_t(fmt::AstTok::VarRef): return 4;
        case uint8_t(fmt::AstTok::FunctionCall): {
            if (head.size() < 3) return 3;
            uint8_t argc = head[2];
            return 3 + 4 * std::size_t(argc);
        }
        case uint8_t(fmt::AstTok::Literal): {
            if (head.empty()) return 1;
            uint32_t sz = typeSizeByTag(head[0], knownTag);
            return 1 + sz;
        }
        default: return 0;
    }
}

} // namespace

bool ReadModule::astIndexByAddress(uint32_t addr, uint32_t& entryOffset) const {
    if (fmt::addressSection(addr) != fmt::SecAst) return false;
    uint32_t off = fmt::addressOffset(addr);
    for (std::size_t i = 0; i < astEntryOffsets.size(); ++i) {
        if (astEntryOffsets[i] == off) { entryOffset = off; return true; }
    }
    return false;
}

bool ReadModule::stateIndexByAddress(uint32_t addr, uint32_t& entryOffset) const {
    if (fmt::addressSection(addr) != fmt::SecState) return false;
    uint32_t off = fmt::addressOffset(addr);
    for (std::size_t i = 0; i < stateEntryOffsets.size(); ++i) {
        if (stateEntryOffsets[i] == off) { entryOffset = off; return true; }
    }
    return false;
}

bool readModule(const std::vector<uint8_t>& bytes, ReadModule& out, std::string& err) {
    Cursor c(bytes);
    uint32_t magic = 0;
    if (!c.u32(magic)) { err = "truncated header"; return false; }
    if (magic != fmt::kMagic) { err = "bad magic number"; return false; }
    if (!c.u16(out.major) || !c.u16(out.minor)) { err = "truncated header"; return false; }
    // The entry layouts are version-specific (v0.2 dropped the scope-depth
    // bytes, v0.3 the owner/dirty bytes and the CLAIM/RELEASE opcodes), so
    // refuse anything else instead of misparsing it.
    if (out.major != fmt::kVersionMajor || out.minor != fmt::kVersionMinor) {
        err = "unsupported module version " + std::to_string(out.major) + "." +
              std::to_string(out.minor) + " (this fsmc reads v" +
              std::to_string(fmt::kVersionMajor) + "." +
              std::to_string(fmt::kVersionMinor) + ")";
        return false;
    }
    for (int i = 1; i <= 7; ++i) {
        if (!c.u32(out.sectionOffsets[uint8_t(i)])) { err = "truncated header"; return false; }
    }
    if (c.p != fmt::kHeaderSize) { err = "header size mismatch"; return false; }
    out.fileSize = uint32_t(bytes.size());

    const uint32_t* off = &out.sectionOffsets[1];
    // Section starts must tile the file: 36 <= g <= r <= t <= s <= tok <= ast <= fsm <= size
    if (off[0] < fmt::kHeaderSize || off[0] > off[1] || off[1] > off[2] || off[2] > off[3] ||
        off[3] > off[4] || off[4] > off[5] || off[5] > off[6] || off[6] > out.fileSize) {
        err = "section offsets are not monotone or out of range";
        return false;
    }
    if (off[6] + (out.fileSize - out.fileSize) > out.fileSize) { /* noop */ }

    // --- Global Variable Section ---
    c.p = off[0];
    while (c.p < off[1]) {
        uint32_t start = c.p;
        ReadGlobal g;
        if (!c.u32(g.addr) || !c.u8(g.tag)) { err = "truncated global entry"; return false; }
        bool known = false;
        uint32_t sz = typeSizeByTag(g.tag, known);
        if (!known) { err = "global entry has unknown type tag"; return false; }
        if (!c.bytes(g.value, sz)) { err = "truncated global value"; return false; }
        if (!c.str(g.name)) { err = "truncated global name"; return false; }
        out.globalEntryOffsets.push_back(start - off[0]);
        out.globals.push_back(std::move(g));
    }

    // --- Runtime Variable Section ---
    c.p = off[1];
    while (c.p < off[2]) {
        uint32_t start = c.p;
        ReadRuntime r;
        if (!c.u32(r.addr) || !c.u8(r.tag) || !c.u32(r.bindingSlot)) {
            err = "truncated runtime entry";
            return false;
        }
        bool known = false;
        (void)typeSizeByTag(r.tag, known);
        if (!known) { err = "runtime entry has unknown type tag"; return false; }
        if (!c.str(r.name)) { err = "truncated runtime name"; return false; }
        out.runtimeEntryOffsets.push_back(start - off[1]);
        out.runtime.push_back(std::move(r));
    }

    // --- Temporary Variable Section ---
    c.p = off[2];
    while (c.p < off[3]) {
        uint32_t start = c.p;
        ReadTemp t;
        if (!c.u32(t.addr) || !c.u8(t.tag)) {
            err = "truncated temp entry";
            return false;
        }
        bool known = false;
        (void)typeSizeByTag(t.tag, known);
        if (!known) { err = "temp entry has unknown type tag"; return false; }
        if (!c.str(t.name)) { err = "truncated temp name"; return false; }
        out.tempEntryOffsets.push_back(start - off[2]);
        out.temps.push_back(std::move(t));
    }

    // --- State Section ---
    c.p = off[3];
    uint32_t stateCount = 0;
    if (!c.u32(stateCount)) { err = "truncated state count"; return false; }
    for (uint32_t i = 0; i < stateCount; ++i) {
        uint32_t start = c.p;
        ReadState s;
        if (!c.u32(s.addr)) { err = "truncated state entry"; return false; }
        if (!c.str(s.name)) { err = "truncated state name"; return false; }
        out.stateEntryOffsets.push_back(start - off[3]);
        out.states.push_back(std::move(s));
    }

    // --- Token / Instruction Section (per-state framed) ---
    c.p = off[4];
    while (c.p < off[5]) {
        uint32_t stateAddr = 0;
        uint16_t instrCount = 0;
        if (!c.u32(stateAddr) || !c.u16(instrCount)) { err = "truncated instruction frame"; return false; }
        ReadStateInstrs frame;
        frame.stateAddr = stateAddr;
        for (uint16_t i = 0; i < instrCount; ++i) {
            ReadInstr in;
            uint8_t opCount = 0;
            if (!c.u8(in.opcode) || !c.u8(opCount)) { err = "truncated instruction"; return false; }
            for (uint8_t k = 0; k < opCount; ++k) {
                uint32_t op = 0;
                if (!c.u32(op)) { err = "truncated instruction operand"; return false; }
                in.operands.push_back(op);
            }
            frame.instrs.push_back(std::move(in));
        }
        out.stateInstrs.push_back(std::move(frame));
    }

    // --- AST Adjacency Section ---
    c.p = off[5];
    while (c.p < off[6]) {
        uint32_t start = c.p;
        ReadAstToken t;
        if (!c.u8(t.type)) { err = "truncated ast entry"; return false; }
        uint16_t childCount = 0;
        if (!c.u16(childCount)) { err = "truncated ast entry"; return false; }
        for (uint16_t k = 0; k < childCount; ++k) {
            uint32_t child = 0;
            if (!c.u32(child)) { err = "truncated ast child"; return false; }
            t.children.push_back(child);
        }
        std::vector<uint8_t> head;
        uint8_t first = 0;
        bool knownTag = true;
        // Consume the fixed-size head needed to size variable-length data.
        switch (t.type) {
            case uint8_t(fmt::AstTok::FunctionCall): {
                uint16_t id = 0;
                uint8_t argc = 0;
                if (!c.u16(id) || !c.u8(argc)) { err = "truncated ast data"; return false; }
                t.data.push_back(uint8_t(id & 0xFF));
                t.data.push_back(uint8_t((id >> 8) & 0xFF));
                t.data.push_back(argc);
                for (uint8_t k = 0; k < argc; ++k) {
                    uint32_t a = 0;
                    if (!c.u32(a)) { err = "truncated ast data"; return false; }
                    t.data.push_back(uint8_t(a & 0xFF));
                    t.data.push_back(uint8_t((a >> 8) & 0xFF));
                    t.data.push_back(uint8_t((a >> 16) & 0xFF));
                    t.data.push_back(uint8_t((a >> 24) & 0xFF));
                }
                break;
            }
            case uint8_t(fmt::AstTok::Literal):
                if (!c.u8(first)) { err = "truncated ast data"; return false; }
                head.push_back(first);
                {
                    uint32_t sz = typeSizeByTag(first, knownTag);
                    if (!knownTag) { err = "ast literal has unknown type tag"; return false; }
                    if (!c.bytes(t.data, sz)) { err = "truncated ast literal"; return false; }
                    t.data.insert(t.data.begin(), first);
                }
                break;
            default: {
                std::size_t n = astDataSize(t.type, head, knownTag);
                if (!c.bytes(t.data, n)) { err = "truncated ast data"; return false; }
                break;
            }
        }
        if (t.type != uint8_t(fmt::AstTok::FunctionCall) && t.type != uint8_t(fmt::AstTok::Literal)) {
            t.data.insert(t.data.begin(), head.begin(), head.end());
        }
        out.astEntryOffsets.push_back(start - off[5]);
        out.ast.push_back(std::move(t));
    }

    // --- FSM Adjacency Section ---
    c.p = off[6];
    uint32_t fsmCount = 0;
    if (!c.u32(fsmCount)) { err = "truncated fsm count"; return false; }
    for (uint32_t i = 0; i < fsmCount; ++i) {
        ReadFsmEntry e;
        uint16_t targetCount = 0;
        if (!c.u32(e.src) || !c.u16(targetCount)) { err = "truncated fsm entry"; return false; }
        for (uint16_t k = 0; k < targetCount; ++k) {
            uint32_t t = 0;
            if (!c.u32(t)) { err = "truncated fsm target"; return false; }
            e.targets.push_back(t);
        }
        out.fsm.push_back(std::move(e));
    }
    if (c.p != out.fileSize) { err = "trailing bytes after FSM section"; return false; }
    return true;
}

bool validateModule(const ReadModule& m, std::string& err) {
    auto inSet = [](const std::vector<uint32_t>& offsets, uint32_t off) {
        for (uint32_t o : offsets) if (o == off) return true;
        return false;
    };

    // --- self addresses of variable / state entries ---
    for (std::size_t i = 0; i < m.globals.size(); ++i) {
        if (fmt::addressSection(m.globals[i].addr) != fmt::SecGlobal ||
            !inSet(m.globalEntryOffsets, fmt::addressOffset(m.globals[i].addr))) {
            err = "global entry " + std::to_string(i) + " has a malformed self-address";
            return false;
        }
    }
    for (std::size_t i = 0; i < m.runtime.size(); ++i) {
        if (fmt::addressSection(m.runtime[i].addr) != fmt::SecRuntime ||
            !inSet(m.runtimeEntryOffsets, fmt::addressOffset(m.runtime[i].addr))) {
            err = "runtime entry " + std::to_string(i) + " has a malformed self-address";
            return false;
        }
    }
    for (std::size_t i = 0; i < m.temps.size(); ++i) {
        if (fmt::addressSection(m.temps[i].addr) != fmt::SecTemp ||
            !inSet(m.tempEntryOffsets, fmt::addressOffset(m.temps[i].addr))) {
            err = "temp entry " + std::to_string(i) + " has a malformed self-address";
            return false;
        }
    }
    for (std::size_t i = 0; i < m.states.size(); ++i) {
        // The state entry's address field points at the state's AST root
        // (AST section), not at the entry itself.
        if (fmt::addressSection(m.states[i].addr) != fmt::SecAst) {
            err = "state entry " + std::to_string(i) + " root does not point into the AST section";
            return false;
        }
        uint32_t rootOff = 0;
        if (!m.astIndexByAddress(m.states[i].addr, rootOff)) {
            err = "state " + m.states[i].name + " AST root does not resolve";
            return false;
        }
        // Map root offset back to the parsed token.
        uint32_t rootIdx = 0;
        for (std::size_t k = 0; k < m.astEntryOffsets.size(); ++k) {
            if (m.astEntryOffsets[k] == rootOff) { rootIdx = uint32_t(k); break; }
        }
        if (m.ast[rootIdx].type != uint8_t(fmt::AstTok::State)) {
            err = "state " + m.states[i].name + " AST root is not a STATE token";
            return false;
        }
    }

    auto varRefOk = [&](uint32_t addr) {
        switch (fmt::addressSection(addr)) {
            case fmt::SecGlobal: return inSet(m.globalEntryOffsets, fmt::addressOffset(addr));
            case fmt::SecRuntime: return inSet(m.runtimeEntryOffsets, fmt::addressOffset(addr));
            case fmt::SecTemp: return inSet(m.tempEntryOffsets, fmt::addressOffset(addr));
            default: return false;
        }
    };

    // --- AST tokens ---
    uint32_t stateTokens = 0;
    for (std::size_t i = 0; i < m.ast.size(); ++i) {
        const ReadAstToken& t = m.ast[i];
        for (uint32_t child : t.children) {
            if (fmt::addressSection(child) != fmt::SecAst ||
                !inSet(m.astEntryOffsets, fmt::addressOffset(child))) {
                err = "ast entry " + std::to_string(i) + " has a bad child address";
                return false;
            }
        }
        switch (t.type) {
            case uint8_t(fmt::AstTok::State):
                ++stateTokens;
                if (t.data.size() != 1 || (t.data[0] != 0 && t.data[0] != 1)) {
                    err = "STATE token has a malformed entry flag";
                    return false;
                }
                break;
            case uint8_t(fmt::AstTok::If):
            case uint8_t(fmt::AstTok::ElseIf): {
                if (t.data.size() != 4) { err = "IF/ELSE_IF token has malformed data"; return false; }
                uint32_t cond = uint32_t(t.data[0]) | (uint32_t(t.data[1]) << 8) |
                                (uint32_t(t.data[2]) << 16) | (uint32_t(t.data[3]) << 24);
                uint32_t off = 0;
                if (!m.astIndexByAddress(cond, off)) {
                    err = "condition address does not resolve";
                    return false;
                }
                uint32_t idx = 0;
                for (std::size_t k = 0; k < m.astEntryOffsets.size(); ++k) {
                    if (m.astEntryOffsets[k] == off) { idx = uint32_t(k); break; }
                }
                if (!isExprTok(m.ast[idx].type)) {
                    err = "condition does not point at an expression token";
                    return false;
                }
                break;
            }
            case uint8_t(fmt::AstTok::FunctionCall): {
                if (t.data.size() < 3) { err = "FUNCTION_CALL token has malformed data"; return false; }
                uint16_t id = uint16_t(t.data[0] | (t.data[1] << 8));
                uint8_t argc = t.data[2];
                if (t.data.size() != 3 + 4 * std::size_t(argc)) {
                    err = "FUNCTION_CALL data size does not match argument count";
                    return false;
                }
                if (!BuiltinFunctions::instance().findById(id)) {
                    err = "FUNCTION_CALL references unknown function id " + std::to_string(id);
                    return false;
                }
                for (uint8_t k = 0; k < argc; ++k) {
                    uint32_t a = uint32_t(t.data[3 + 4 * k]) | (uint32_t(t.data[4 + 4 * k]) << 8) |
                                 (uint32_t(t.data[5 + 4 * k]) << 16) |
                                 (uint32_t(t.data[6 + 4 * k]) << 24);
                    uint32_t off = 0;
                    if (!m.astIndexByAddress(a, off)) {
                        err = "FUNCTION_CALL argument address does not resolve";
                        return false;
                    }
                }
                break;
            }
            case uint8_t(fmt::AstTok::Goto): {
                if (t.data.size() != 4) { err = "GOTO token has malformed data"; return false; }
                uint32_t target = uint32_t(t.data[0]) | (uint32_t(t.data[1]) << 8) |
                                  (uint32_t(t.data[2]) << 16) | (uint32_t(t.data[3]) << 24);
                uint32_t off = 0;
                if (!m.stateIndexByAddress(target, off)) {
                    err = "GOTO target does not resolve to a state entry";
                    return false;
                }
                break;
            }
            case uint8_t(fmt::AstTok::TempVarDecl): {
                if (t.data.size() != 5) { err = "TEMP_VAR_DECL token has malformed data"; return false; }
                bool known = false;
                (void)typeSizeByTag(t.data[0], known);
                if (!known) { err = "TEMP_VAR_DECL has an unknown type tag"; return false; }
                uint32_t var = uint32_t(t.data[1]) | (uint32_t(t.data[2]) << 8) |
                               (uint32_t(t.data[3]) << 16) | (uint32_t(t.data[4]) << 24);
                if (fmt::addressSection(var) != fmt::SecTemp ||
                    !inSet(m.tempEntryOffsets, fmt::addressOffset(var))) {
                    err = "TEMP_VAR_DECL variable address does not resolve";
                    return false;
                }
                break;
            }
            case uint8_t(fmt::AstTok::Assign): {
                if (t.data.size() != 8) { err = "ASSIGN token has malformed data"; return false; }
                uint32_t target = uint32_t(t.data[0]) | (uint32_t(t.data[1]) << 8) |
                                  (uint32_t(t.data[2]) << 16) | (uint32_t(t.data[3]) << 24);
                uint32_t value = uint32_t(t.data[4]) | (uint32_t(t.data[5]) << 8) |
                                 (uint32_t(t.data[6]) << 16) | (uint32_t(t.data[7]) << 24);
                if (!varRefOk(target)) {
                    err = "ASSIGN target address does not resolve";
                    return false;
                }
                uint32_t off = 0;
                if (!m.astIndexByAddress(value, off)) {
                    err = "ASSIGN value address does not resolve";
                    return false;
                }
                for (std::size_t k = 0; k < m.astEntryOffsets.size(); ++k) {
                    if (m.astEntryOffsets[k] == off) {
                        if (!isExprTok(m.ast[k].type)) {
                            err = "ASSIGN value does not point at an expression token";
                            return false;
                        }
                        break;
                    }
                }
                break;
            }
            case uint8_t(fmt::AstTok::VarRef): {
                if (t.data.size() != 4) { err = "VAR_REF token has malformed data"; return false; }
                uint32_t var = uint32_t(t.data[0]) | (uint32_t(t.data[1]) << 8) |
                               (uint32_t(t.data[2]) << 16) | (uint32_t(t.data[3]) << 24);
                if (!varRefOk(var)) {
                    err = "VAR_REF address does not resolve";
                    return false;
                }
                break;
            }
            case uint8_t(fmt::AstTok::BinaryOp): {
                if (t.data.size() != 9) { err = "BINARY_OP token has malformed data"; return false; }
                if (t.data[0] < 1 || t.data[0] > 15) {
                    err = "BINARY_OP has an unknown operator id";
                    return false;
                }
                for (int k = 0; k < 2; ++k) {
                    uint32_t a = uint32_t(t.data[1 + 4 * k]) | (uint32_t(t.data[2 + 4 * k]) << 8) |
                                 (uint32_t(t.data[3 + 4 * k]) << 16) |
                                 (uint32_t(t.data[4 + 4 * k]) << 24);
                    uint32_t off = 0;
                    if (!m.astIndexByAddress(a, off)) {
                        err = "BINARY_OP operand address does not resolve";
                        return false;
                    }
                }
                break;
            }
            case uint8_t(fmt::AstTok::UnaryOp): {
                if (t.data.size() != 5) { err = "UNARY_OP token has malformed data"; return false; }
                if (t.data[0] != uint8_t(fmt::OpNot) && t.data[0] != uint8_t(fmt::OpMinus)) {
                    err = "UNARY_OP has an unknown operator id";
                    return false;
                }
                uint32_t a = uint32_t(t.data[1]) | (uint32_t(t.data[2]) << 8) |
                             (uint32_t(t.data[3]) << 16) | (uint32_t(t.data[4]) << 24);
                uint32_t off = 0;
                if (!m.astIndexByAddress(a, off)) {
                    err = "UNARY_OP operand address does not resolve";
                    return false;
                }
                break;
            }
            case uint8_t(fmt::AstTok::Literal): {
                if (t.data.size() < 1) { err = "LITERAL token has malformed data"; return false; }
                bool known = false;
                uint32_t sz = typeSizeByTag(t.data[0], known);
                if (!known || t.data.size() != 1 + sz) {
                    err = "LITERAL size does not match its type";
                    return false;
                }
                break;
            }
            case uint8_t(fmt::AstTok::Actions):
            case uint8_t(fmt::AstTok::Traversals):
            case uint8_t(fmt::AstTok::Else):
                if (!t.data.empty()) { err = "container token has unexpected data"; return false; }
                break;
            default:
                break;
        }
    }
    if (stateTokens != m.states.size()) {
        err = "STATE token count does not match the state directory";
        return false;
    }

    // --- instruction stream ---
    if (m.stateInstrs.size() != m.states.size()) {
        err = "instruction frame count does not match the state directory";
        return false;
    }
    for (std::size_t i = 0; i < m.stateInstrs.size(); ++i) {
        const ReadStateInstrs& frame = m.stateInstrs[i];
        if (!inSet(m.stateEntryOffsets, fmt::addressOffset(frame.stateAddr)) ||
            fmt::addressSection(frame.stateAddr) != fmt::SecState) {
            err = "instruction frame " + std::to_string(i) + " references an unknown state";
            return false;
        }
        for (const ReadInstr& in : frame.instrs) {
            switch (in.opcode) {
                case fmt::OpCall: {
                    if (in.operands.size() != 1) { err = "CALL must have one operand"; return false; }
                    uint32_t off = 0;
                    if (!m.astIndexByAddress(in.operands[0], off)) {
                        err = "CALL operand does not resolve";
                        return false;
                    }
                    break;
                }
                case fmt::OpAssign: {
                    if (in.operands.size() != 2) { err = "ASSIGN must have two operands"; return false; }
                    if (!varRefOk(in.operands[0])) {
                        err = "ASSIGN instruction target does not resolve";
                        return false;
                    }
                    uint32_t off = 0;
                    if (!m.astIndexByAddress(in.operands[1], off)) {
                        err = "ASSIGN instruction value does not resolve";
                        return false;
                    }
                    break;
                }
                case fmt::OpGoto: {
                    if (in.operands.size() != 1) { err = "GOTO must have one operand"; return false; }
                    uint32_t off = 0;
                    if (!m.stateIndexByAddress(in.operands[0], off)) {
                        err = "GOTO instruction target does not resolve";
                        return false;
                    }
                    break;
                }
                case fmt::OpEval: {
                    if (in.operands.size() != 1) { err = "EVAL must have one operand"; return false; }
                    uint32_t off = 0;
                    if (!m.astIndexByAddress(in.operands[0], off)) {
                        err = "EVAL operand does not resolve";
                        return false;
                    }
                    break;
                }
                case fmt::OpAdd: case fmt::OpSub: case fmt::OpMul: case fmt::OpDiv:
                case fmt::OpPower: case fmt::OpFloordiv: case fmt::OpAnd: case fmt::OpOr:
                case fmt::OpNegate: case fmt::OpEq: case fmt::OpNeq: case fmt::OpLess:
                case fmt::OpGreater: case fmt::OpLte: case fmt::OpGte:
                    if (in.operands.size() > 3) { err = "too many arithmetic operands"; return false; }
                    break;
                default: {
                    // Includes the retired opcodes (0x14 CLAIM / 0x15 RELEASE,
                    // removed in v0.3): they are never reused, so a module that
                    // still contains one is invalid.
                    char hex[3] = {};
                    std::snprintf(hex, sizeof(hex), "%02X", unsigned(in.opcode));
                    err = std::string("unknown or retired opcode 0x") + hex;
                    return false;
                }
            }
        }
    }

    // --- FSM section ---
    if (m.fsm.size() != m.states.size()) {
        err = "FSM entry count does not match the state directory";
        return false;
    }
    for (std::size_t i = 0; i < m.fsm.size(); ++i) {
        uint32_t off = 0;
        if (!m.stateIndexByAddress(m.fsm[i].src, off)) {
            err = "FSM source does not resolve";
            return false;
        }
        for (uint32_t t : m.fsm[i].targets) {
            if (!m.stateIndexByAddress(t, off)) {
                err = "FSM target does not resolve";
                return false;
            }
        }
    }
    return true;
}

} // namespace fsmc
