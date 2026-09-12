#include "disassembler.hpp"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <iomanip>
#include <sstream>
#include <string>
#include <unordered_map>
#include <vector>

#include "binary_format.hpp"
#include "builtin_functions.hpp"
#include "builtin_types.hpp"

namespace fsmc {
namespace {

// ---------------------------------------------------------------------------
// small formatting helpers
// ---------------------------------------------------------------------------

std::string hexU(uint32_t v, int width = 8) {
    char buf[16] = {};
    std::snprintf(buf, sizeof(buf), "0x%0*X", width, static_cast<unsigned>(v));
    return buf;
}
std::string hexU8(uint8_t v) {
    char buf[8] = {};
    std::snprintf(buf, sizeof(buf), "0x%02X", v);
    return buf;
}

uint32_t rd32(const std::vector<uint8_t>& b, std::size_t p) {
    return uint32_t(b[p]) | (uint32_t(b[p + 1]) << 8) |
           (uint32_t(b[p + 2]) << 16) | (uint32_t(b[p + 3]) << 24);
}
uint64_t rd64(const std::vector<uint8_t>& b, std::size_t p) {
    return uint64_t(rd32(b, p)) | (uint64_t(rd32(b, p + 4)) << 32);
}

std::string pad(const std::string& s, std::size_t w) {
    return s.size() >= w ? s : s + std::string(w - s.size(), ' ');
}

std::string typeNameByTag(uint8_t tag) {
    for (const TypeDefinition& t : BuiltinTypes::instance().all()) {
        if (t.typeTag == tag) return t.name;
    }
    return std::string("<tag ") + hexU8(tag) + ">";
}

std::string fmtFloat(float f) {
    std::ostringstream os;
    os << std::setprecision(6) << f;
    return os.str() + "f";
}
std::string fmtDouble(double d) {
    std::ostringstream os;
    os << std::setprecision(9) << d;
    return os.str();
}

// Decodes a value's little-endian bytes (tag excluded) to text.
std::string decodeValue(uint8_t tag, const std::vector<uint8_t>& b, std::size_t off) {
    const std::size_t n = b.size() - off;
    switch (tag) {
        case 0x01:
            if (n < 4) return "<malformed>";
            return std::to_string(static_cast<int32_t>(rd32(b, off)));
        case 0x02:
            if (n < 4) return "<malformed>";
            return fmtFloat(fmt::bitsToFloat(rd32(b, off)));
        case 0x03:
            if (n < 8) return "<malformed>";
            return fmtDouble(fmt::bitsToDouble(rd64(b, off)));
        case 0x04:
            if (n < 1) return "<malformed>";
            return b[off] != 0 ? "true" : "false";
        case 0x10:
        case 0x11:
        case 0x12: {
            const int comps = static_cast<int>(n / 4);
            std::string s = typeNameByTag(tag) + "(";
            for (int k = 0; k < comps; ++k) {
                if (k) s += ", ";
                s += fmtFloat(fmt::bitsToFloat(rd32(b, off + std::size_t(k) * 4)));
            }
            return s + ")";
        }
        default:
            return std::string("<") + typeNameByTag(tag) + " " + std::to_string(n) + "B>";
    }
}

const char* opSymbol(uint8_t id) {
    switch (id) {
        case fmt::OpPlus: return "+";
        case fmt::OpMinus: return "-";
        case fmt::OpStar: return "*";
        case fmt::OpPow: return "**";
        case fmt::OpSlash: return "/";
        case fmt::OpFloorDiv: return "//";
        case fmt::OpLogicalAnd: return "&&";
        case fmt::OpLogicalOr: return "||";
        case fmt::OpNot: return "!";
        case fmt::OpEqEq: return "==";
        case fmt::OpNotEq: return "!=";
        case fmt::OpLt: return "<";
        case fmt::OpGt: return ">";
        case fmt::OpLe: return "<=";
        case fmt::OpGe: return ">=";
        default: return "?";
    }
}

const char* claimFieldName(uint32_t idx) {
    switch (idx) {
        case fmt::ClaimFieldPosition: return "position";
        case fmt::ClaimFieldVelocity: return "velocity";
        case fmt::ClaimFieldRotation: return "rotation";
        default: return "?";
    }
}

const char* opcodeName(uint8_t op) {
    switch (op) {
        case fmt::OpCall: return "CALL";
        case fmt::OpAssign: return "ASSIGN";
        case fmt::OpGoto: return "GOTO";
        case fmt::OpEval: return "EVAL";
        case fmt::OpClaim: return "CLAIM";
        case fmt::OpRelease: return "RELEASE";
        default: return "OP";
    }
}

const char* ownerName(uint8_t o) { return o == fmt::OwnerDsl ? "dsl (Controller-owned)" : "external"; }

std::string fnName(uint16_t id) {
    const FunctionDefinition* fn = BuiltinFunctions::instance().findById(id);
    return fn ? fn->name : hexU(id, 4);
}
std::string fnCallText(uint16_t id) {
    const FunctionDefinition* fn = BuiltinFunctions::instance().findById(id);
    if (!fn) return std::string(hexU(id, 4)) + "(<unknown function>)";
    std::string s = fn->name + "(";
    for (std::size_t i = 0; i < fn->params.size(); ++i) {
        if (i) s += ", ";
        s += fn->params[i].typeName;
    }
    return s + ")";
}

// ---------------------------------------------------------------------------
// the renderer
// ---------------------------------------------------------------------------

struct Dis {
    const ReadModule& m;
    std::ostringstream os;

    // section-relative offset -> index / name lookups
    std::unordered_map<uint32_t, uint32_t> astIdx;
    std::unordered_map<uint32_t, uint32_t> stateIdx;
    std::unordered_map<uint32_t, uint32_t> tempIdx;
    std::unordered_map<uint32_t, std::string> globalName;
    std::unordered_map<uint32_t, std::string> runtimeName;
    std::unordered_map<uint32_t, std::string> tempName;
    std::vector<int> tempState;  // temp index -> state index (-1 if not found)
    std::vector<uint32_t> guard; // renderExpr recursion guard

    explicit Dis(const ReadModule& mod) : m(mod) { init(); }

    void init() {
        for (std::size_t i = 0; i < m.astEntryOffsets.size(); ++i)
            astIdx[m.astEntryOffsets[i]] = uint32_t(i);
        for (std::size_t i = 0; i < m.stateEntryOffsets.size(); ++i)
            stateIdx[m.stateEntryOffsets[i]] = uint32_t(i);
        for (std::size_t i = 0; i < m.tempEntryOffsets.size(); ++i)
            tempIdx[m.tempEntryOffsets[i]] = uint32_t(i);
        for (std::size_t i = 0; i < m.globalEntryOffsets.size(); ++i)
            globalName[m.globalEntryOffsets[i]] = m.globals[i].name;
        for (std::size_t i = 0; i < m.runtimeEntryOffsets.size(); ++i)
            runtimeName[m.runtimeEntryOffsets[i]] = m.runtime[i].name;
        for (std::size_t i = 0; i < m.tempEntryOffsets.size(); ++i)
            tempName[m.tempEntryOffsets[i]] = m.temps[i].name;

        tempState.assign(m.temps.size(), -1);
        for (std::size_t si = 0; si < m.states.size(); ++si) {
            uint32_t root = 0;
            if (astIndex(m.states[si].addr, root)) walkCollectTemps(root, int(si));
        }
    }

    void walkCollectTemps(uint32_t idx, int si) {
        const ReadAstToken& t = m.ast[idx];
        if (t.type == uint8_t(fmt::AstTok::TempVarDecl) && t.data.size() == 5) {
            const uint32_t va = rd32(t.data, 1);
            if (fmt::addressSection(va) == fmt::SecTemp) {
                auto it = tempIdx.find(fmt::addressOffset(va));
                if (it != tempIdx.end() && tempState[it->second] < 0)
                    tempState[it->second] = si;
            }
        }
        for (uint32_t c : t.children) {
            uint32_t ci = 0;
            if (astIndex(c, ci)) walkCollectTemps(ci, si);
        }
    }

    // ---- address -> symbol ------------------------------------------------
    bool astIndex(uint32_t addr, uint32_t& idx) const {
        if (fmt::addressSection(addr) != fmt::SecAst) return false;
        auto it = astIdx.find(fmt::addressOffset(addr));
        if (it == astIdx.end()) return false;
        idx = it->second;
        return true;
    }
    std::string stateName(uint32_t addr) const {
        if (fmt::addressSection(addr) != fmt::SecState) return hexU(addr);
        auto it = stateIdx.find(fmt::addressOffset(addr));
        if (it == stateIdx.end()) return hexU(addr);
        return m.states[it->second].name;
    }
    std::string varName(uint32_t addr) const {
        uint8_t sec = fmt::addressSection(addr);
        uint32_t off = fmt::addressOffset(addr);
        const std::unordered_map<uint32_t, std::string>* mp = nullptr;
        if (sec == fmt::SecGlobal) mp = &globalName;
        else if (sec == fmt::SecRuntime) mp = &runtimeName;
        else if (sec == fmt::SecTemp) mp = &tempName;
        if (!mp) return hexU(addr);
        auto it = mp->find(off);
        if (it == mp->end()) return hexU(addr);
        return it->second;
    }

    // ---- expression rendering (memo-friendly, cycle-guarded) ---------------
    std::string renderExpr(uint32_t idx) {
        if (std::find(guard.begin(), guard.end(), idx) != guard.end()) return "<cycle>";
        guard.push_back(idx);
        std::string s;
        const ReadAstToken& t = m.ast[idx];
        switch (t.type) {
            case uint8_t(fmt::AstTok::Literal):
                s = decodeValue(t.data[0], t.data, 1);
                break;
            case uint8_t(fmt::AstTok::VarRef):
                s = varName(rd32(t.data, 0));
                break;
            case uint8_t(fmt::AstTok::BinaryOp): {
                uint32_t li = 0, ri = 0;
                const std::string l =
                    astIndex(rd32(t.data, 1), li) ? renderExpr(li) : hexU(rd32(t.data, 1));
                const std::string r =
                    astIndex(rd32(t.data, 5), ri) ? renderExpr(ri) : hexU(rd32(t.data, 5));
                s = "(" + l + " " + opSymbol(t.data[0]) + " " + r + ")";
                break;
            }
            case uint8_t(fmt::AstTok::UnaryOp): {
                uint32_t oi = 0;
                s = std::string(opSymbol(t.data[0])) +
                    (astIndex(rd32(t.data, 1), oi) ? renderExpr(oi) : hexU(rd32(t.data, 1)));
                break;
            }
            case uint8_t(fmt::AstTok::FunctionCall): {
                // Arguments live in the data bytes: [u16 fnId][u8 argc][u32 args...]
                uint16_t id = uint16_t(t.data[0] | (t.data[1] << 8));
                const uint8_t argc = t.data[2];
                s = fnName(id) + "(";
                for (uint8_t k = 0; k < argc && 3 + 4 * std::size_t(k) < t.data.size(); ++k) {
                    if (k) s += ", ";
                    const uint32_t a = rd32(t.data, 3 + std::size_t(k) * 4);
                    uint32_t ci = 0;
                    s += astIndex(a, ci) ? renderExpr(ci) : hexU(a);
                }
                s += ")";
                break;
            }
            default:
                s = "ast#" + std::to_string(idx);
                break;
        }
        guard.pop_back();
        return s;
    }

    // ---- AST token one-line summary ---------------------------------------
    std::string tokSummary(uint32_t idx) {
        const ReadAstToken& t = m.ast[idx];
        switch (t.type) {
            case uint8_t(fmt::AstTok::State):
                return std::string("STATE") +
                       (t.data.size() == 1 && t.data[0] == 1 ? "   [ENTRY]" : "");
            case uint8_t(fmt::AstTok::Actions): return "ACTIONS";
            case uint8_t(fmt::AstTok::Traversals): return "TRAVERSALS";
            case uint8_t(fmt::AstTok::If):
            case uint8_t(fmt::AstTok::ElseIf): {
                std::string s = t.type == uint8_t(fmt::AstTok::If) ? "IF" : "ELSE_IF";
                if (t.data.size() == 4) {
                    uint32_t ci = 0;
                    if (astIndex(rd32(t.data, 0), ci)) s += "    cond: " + renderExpr(ci);
                }
                return s;
            }
            case uint8_t(fmt::AstTok::Else): return "ELSE";
            case uint8_t(fmt::AstTok::FunctionCall): {
                uint16_t id = uint16_t(t.data[0] | (t.data[1] << 8));
                return "CALL " + fnCallText(id);
            }
            case uint8_t(fmt::AstTok::Goto):
                return "GOTO " + stateName(rd32(t.data, 0));
            case uint8_t(fmt::AstTok::TempVarDecl):
                return "TEMP " + typeNameByTag(t.data[0]) + " " + varName(rd32(t.data, 1));
            case uint8_t(fmt::AstTok::Assign): {
                std::string s = "ASSIGN " + varName(rd32(t.data, 0)) + " = ";
                uint32_t vi = 0;
                if (astIndex(rd32(t.data, 4), vi)) s += renderExpr(vi);
                return s;
            }
            case uint8_t(fmt::AstTok::Return): return "RETURN (reserved)";
            case uint8_t(fmt::AstTok::BinaryOp): return std::string("BINOP ") + opSymbol(t.data[0]);
            case uint8_t(fmt::AstTok::UnaryOp): return std::string("UNARY ") + opSymbol(t.data[0]);
            case uint8_t(fmt::AstTok::Literal):
                return "LITERAL " + typeNameByTag(t.data[0]) + " = " +
                       decodeValue(t.data[0], t.data, 1);
            case uint8_t(fmt::AstTok::VarRef):
                return "VAR_REF " + varName(rd32(t.data, 0));
            default:
                return std::string("<tok ") + hexU8(t.type) + ">";
        }
    }

    static std::string ind(int n) { return std::string(std::size_t(n) * 3, ' '); }

    void dumpAstTree(uint32_t idx, int depth) {
        os << ind(depth) << "#[" << idx << "] " << tokSummary(idx) << "\n";
        for (uint32_t c : m.ast[idx].children) {
            uint32_t ci = 0;
            if (astIndex(c, ci)) dumpAstTree(ci, depth + 1);
        }
    }

    // ---- instruction stream -------------------------------------------------
    void dumpInstrFrame(std::size_t si) {
        const ReadStateInstrs& f = m.stateInstrs[si];
        os << "--- state #" << si << " '" << m.states[si].name << "' — "
           << f.instrs.size() << " instruction(s) ---\n";
        for (std::size_t i = 0; i < f.instrs.size(); ++i) {
            const ReadInstr& in = f.instrs[i];
            os << "   " << pad(std::to_string(i), 3) << " " << pad(opcodeName(in.opcode), 8);
            bool described = false;
            switch (in.opcode) {
                case fmt::OpCall: {
                    if (!in.operands.empty()) {
                        uint32_t ai = 0;
                        if (astIndex(in.operands[0], ai)) {
                            const ReadAstToken& t = m.ast[ai];
                            uint16_t id =
                                t.data.size() >= 2 ? uint16_t(t.data[0] | (t.data[1] << 8)) : 0;
                            os << renderExpr(ai) << "   [fn " << hexU(id, 4) << "]";
                            described = true;
                        }
                    }
                    break;
                }
                case fmt::OpAssign: {
                    if (in.operands.size() == 2) {
                        os << varName(in.operands[0]) << " = ";
                        uint32_t ai = 0;
                        os << (astIndex(in.operands[1], ai) ? renderExpr(ai) : hexU(in.operands[1]));
                        described = true;
                    }
                    break;
                }
                case fmt::OpGoto: {
                    if (!in.operands.empty()) { os << stateName(in.operands[0]); described = true; }
                    break;
                }
                case fmt::OpClaim:
                case fmt::OpRelease: {
                    if (in.operands.size() == 2) {
                        os << varName(in.operands[0]) << "." << claimFieldName(in.operands[1]);
                        described = true;
                    }
                    break;
                }
                case fmt::OpEval: {
                    if (!in.operands.empty()) {
                        uint32_t ai = 0;
                        if (astIndex(in.operands[0], ai)) { os << renderExpr(ai); described = true; }
                    }
                    break;
                }
                default:
                    break;
            }
            if (!described) {
                for (std::size_t k = 0; k < in.operands.size(); ++k) {
                    if (k) os << " ";
                    os << hexU(in.operands[k]);
                }
            }
            os << "\n";
        }
    }

    // ---- top level ----------------------------------------------------------
    void run(const std::string& fileName, bool validated) {
        const std::string bar(70, '=');
        os << bar << "\n";
        os << " FSMC DISASSEMBLY\n";
        os << " file     " << fileName << "  (" << m.fileSize << " bytes)\n";
        os << " format   fsmb v" << m.major << "." << m.minor
           << " — magic 0x46534D44 (\"FSMD\"), little-endian\n";
        os << " check    "
           << (validated
                   ? "OK — read back + full validation passed (every address, reference, "
                     "function id and token type resolves)"
                   : "NOT validated")
           << "\n";
        os << bar << "\n\n";

        // SECTION MAP
        os << " SECTION MAP — 7 sections tile the file after the 36-byte header\n";
        os << "   [id]  section    file range           size     entries\n";
        const char* names[8] = {"", "Global", "Runtime", "Temporary", "State", "Token", "AST",
                                "FSM"};
        uint32_t counts[8] = {};
        counts[1] = uint32_t(m.globals.size());
        counts[2] = uint32_t(m.runtime.size());
        counts[3] = uint32_t(m.temps.size());
        counts[4] = uint32_t(m.states.size());
        for (const ReadStateInstrs& f : m.stateInstrs) counts[5] += uint32_t(f.instrs.size());
        counts[6] = uint32_t(m.ast.size());
        counts[7] = uint32_t(m.fsm.size());
        for (int id = 1; id <= 7; ++id) {
            const uint32_t start = m.sectionOffsets[uint8_t(id)];
            const uint32_t end = (id < 7) ? m.sectionOffsets[uint8_t(id + 1)] : m.fileSize;
            os << "   [" << id << "]  " << pad(names[id], 10) << " " << hexU(start) << " .. "
               << hexU(end) << "   " << pad(std::to_string(end - start) + " B", 6) << " "
               << counts[id] << "\n";
        }
        os << "\n";

        // GLOBALS
        os << " SECTION [1] GLOBAL VARIABLES (" << m.globals.size() << ")\n";
        for (std::size_t i = 0; i < m.globals.size(); ++i) {
            const ReadGlobal& g = m.globals[i];
            os << "   #" << i << "  " << pad(g.name, 16) << pad(typeNameByTag(g.tag), 14)
               << "= " << decodeValue(g.tag, g.value, 0) << "\n";
        }
        os << "\n";

        // RUNTIME
        os << " SECTION [2] RUNTIME VARIABLES (" << m.runtime.size() << ")\n";
        for (std::size_t i = 0; i < m.runtime.size(); ++i) {
            const ReadRuntime& r = m.runtime[i];
            os << "   #" << i << "  " << pad(r.name, 16) << pad(typeNameByTag(r.tag), 22)
               << "owner=" << ownerName(r.owner) << "  slot " << r.bindingSlot
               << (r.dirty ? "  DIRTY" : "") << "\n";
        }
        os << "\n";

        // TEMPS
        os << " SECTION [3] TEMPORARY VARIABLES (" << m.temps.size() << ")\n";
        for (std::size_t i = 0; i < m.temps.size(); ++i) {
            const ReadTemp& t = m.temps[i];
            os << "   #" << i << "  " << pad(t.name, 16) << pad(typeNameByTag(t.tag), 14)
               << "depth " << int(t.depth);
            if (i < tempState.size() && tempState[i] >= 0)
                os << "   state: " << m.states[tempState[i]].name;
            os << "\n";
        }
        os << "\n";

        // STATES
        os << " SECTION [4] STATES (" << m.states.size() << ")\n";
        for (std::size_t i = 0; i < m.states.size(); ++i) {
            const ReadState& s = m.states[i];
            uint32_t root = 0;
            os << "   #" << i << "  " << pad(s.name, 16)
               << (m.ast.size() && astIndex(s.addr, root) ? std::string("ast root #") +
                                                                std::to_string(root)
                                                          : std::string(hexU(s.addr)))
               << "\n";
        }
        os << "\n";

        // INSTRUCTION STREAMS
        os << " SECTION [5] TOKENS / INSTRUCTION STREAMS (per state, execution order)\n";
        for (std::size_t si = 0; si < m.stateInstrs.size(); ++si) {
            dumpInstrFrame(si);
            os << "\n";
        }

        // AST
        os << " SECTION [6] AST (" << m.ast.size()
           << " tokens) — per-state tree, pre-order (#[n] = token index)\n";
        for (std::size_t si = 0; si < m.states.size(); ++si) {
            uint32_t root = 0;
            if (!astIndex(m.states[si].addr, root)) continue;
            os << "--- state #" << si << " '" << m.states[si].name << "' ---\n";
            dumpAstTree(root, 0);
            os << "\n";
        }

        // FSM
        os << " SECTION [7] FSM ADJACENCY (goto transitions, in source order)\n";
        for (std::size_t i = 0; i < m.fsm.size(); ++i) {
            const ReadFsmEntry& e = m.fsm[i];
            for (std::size_t k = 0; k < e.targets.size(); ++k) {
                os << "   " << pad(stateName(e.src), 12) << " -> " << stateName(e.targets[k])
                   << "\n";
            }
        }
    }
};

} // namespace

std::string disassemble(const ReadModule& m, const std::string& fileName, bool validated) {
    Dis d(m);
    d.run(fileName, validated);
    return d.os.str();
}

} // namespace fsmc
