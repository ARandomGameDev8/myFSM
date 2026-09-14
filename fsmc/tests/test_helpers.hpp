#pragma once

// Helpers shared by the test suites: run a source string through the whole
// pipeline (lex -> parse -> passes 1..6) and capture the result.

#include <fstream>
#include <sstream>
#include <string>
#include <vector>

#include "test_framework.hpp"

#include "diagnostics.hpp"
#include "lexer.hpp"
#include "parser.hpp"
#include "passes.hpp"

// Make the AST enum classes streamable for ASSERT_EQ messages.
inline std::ostream& operator<<(std::ostream& os, fsmc::VarKind k) {
    return os << "VarKind(" << static_cast<int>(k) << ")";
}
inline std::ostream& operator<<(std::ostream& os, fsmc::Stmt::Kind k) {
    return os << "Stmt::Kind(" << static_cast<int>(k) << ")";
}
inline std::ostream& operator<<(std::ostream& os, fsmc::Expr::Kind k) {
    return os << "Expr::Kind(" << static_cast<int>(k) << ")";
}
inline std::ostream& operator<<(std::ostream& os, fsmc::StateBodyItem::Kind k) {
    return os << "StateBodyItem::Kind(" << static_cast<int>(k) << ")";
}
inline std::ostream& operator<<(std::ostream& os, fsmc::fmt::AstTok t) {
    return os << "AstTok(0x" << std::hex << static_cast<int>(t) << std::dec << ")";
}

namespace fh {

struct CompileResult {
    bool ok = false;
    std::vector<uint8_t> module;
    fsmc::ParsedSource src;
    fsmc::SymbolTable sym;
    fsmc::AstForest forest;
    fsmc::GotoLists gotos;
    fsmc::FsmGraph fsm;
    fsmc::LinkResult link;
    std::string diagnostics; // formatted, in generation order
    int errors = 0;
    int warnings = 0;
};

inline CompileResult compile(const std::string& source, const std::string& file = "test.fsm") {
    CompileResult r;
    fsmc::Diagnostics diag(file);

    std::vector<fsmc::lex::Token> tokens = fsmc::lex::tokenize(source, file, diag);
    fsmc::Parser parser(tokens, diag);
    r.src = parser.parse();
    r.sym = fsmc::pass1_symbolTable(r.src, diag);
    r.forest = fsmc::pass2_buildAstForest(r.src, r.sym, diag);
    r.gotos = fsmc::pass3_collectGotos(r.src, r.forest, diag);
    r.fsm = fsmc::pass4_buildFsmAdjacency(r.src, r.gotos, r.sym, diag);

    if (!diag.hasErrors()) {
        r.module = fsmc::pass5_serialize(r.src, r.forest, r.fsm, r.sym, diag);
        r.link = fsmc::pass6_link(r.module, diag);
    }

    for (const auto& d : diag.all()) {
        r.diagnostics += fsmc::Diagnostics::format(file, d) + "\n";
    }
    r.errors = diag.errorCount();
    r.warnings = diag.warningCount();
    r.ok = !diag.hasErrors() && !r.module.empty();
    return r;
}

inline bool hasErrorContaining(const CompileResult& r, const std::string& sub) {
    return r.diagnostics.find(sub) != std::string::npos;
}

inline bool hasWarningContaining(const CompileResult& r, const std::string& sub) {
    if (r.diagnostics.find(sub) == std::string::npos) return false;
    return r.diagnostics.find("warning:") != std::string::npos;
}

inline std::string readFixture(const std::string& relPath) {
    std::string path = std::string(FSMC_FIXTURE_DIR) + "/" + relPath;
    std::ifstream in(path, std::ios::binary);
    if (!in) return "<missing fixture: " + path + ">";
    std::ostringstream os;
    os << in.rdbuf();
    return os.str();
}

// Little-endian readers for golden-byte assertions.
inline uint16_t le16(const std::vector<uint8_t>& b, std::size_t off) {
    return uint16_t(b[off] | (b[off + 1] << 8));
}
inline uint32_t le32(const std::vector<uint8_t>& b, std::size_t off) {
    return uint32_t(b[off]) | (uint32_t(b[off + 1]) << 8) | (uint32_t(b[off + 2]) << 16) |
           (uint32_t(b[off + 3]) << 24);
}
inline float leFloat(const std::vector<uint8_t>& b, std::size_t off) {
    union CV {
        float f;
        uint32_t u;
    } cv;
    cv.u = le32(b, off);
    return cv.f;
}

// Every statement of a state's Actions block, in execution order: temps
// declared directly in the body, then Start{}, then Update{}. Statements used
// to sit in one list, so older assertions still read naturally.
template <typename Src>
inline std::vector<fsmc::Stmt> actionStmts(const Src& src, std::size_t state = 0,
                                           std::size_t item = 0) {
    const fsmc::StateBodyItem& it = src.states[state].items[item];
    std::vector<fsmc::Stmt> out;
    out.insert(out.end(), it.stmts.begin(), it.stmts.end());
    out.insert(out.end(), it.startStmts.begin(), it.startStmts.end());
    out.insert(out.end(), it.updateStmts.begin(), it.updateStmts.end());
    return out;
}

} // namespace fh
