// myFSM compiler C API (see myfsm_c_api.h): the CLI pipeline as a library.
#include "myfsm_c_api.h"

#include <cstdlib>
#include <cstring>
#include <sstream>
#include <string>
#include <vector>

#include "binary_format.hpp"
#include "diagnostics.hpp"
#include "lexer.hpp"
#include "parser.hpp"
#include "passes.hpp"

static_assert(fsmc::fmt::kVersionMajor == MYFSM_FSMB_MAJOR &&
                  fsmc::fmt::kVersionMinor == MYFSM_FSMB_MINOR,
              "C API version drift: update MYFSM_FSMB_MAJOR/MINOR");

namespace {

void* dupBytes(const void* src, size_t n) {
    void* p = std::malloc(n > 0 ? n : 1);
    if (p && src && n > 0) std::memcpy(p, src, n);
    return p;
}

char* dupString(const std::string& s) {
    char* p = static_cast<char*>(std::malloc(s.size() + 1));
    if (p) std::memcpy(p, s.c_str(), s.size() + 1);
    return p;
}

} // namespace

extern "C" {

int myfsm_compile(const char* source_utf8, const char* source_name,
                  unsigned char** out_bytes, size_t* out_len,
                  char** out_diagnostics) {
    if (!source_utf8 || !out_bytes || !out_len || !out_diagnostics)
        return MYFSM_INTERNAL_ERROR;
    *out_bytes = nullptr;
    *out_len = 0;
    *out_diagnostics = nullptr;

    unsigned char* bytes = nullptr;
    char* diags = nullptr;
    size_t len = 0;
    int rc = MYFSM_INTERNAL_ERROR;
    try {
        const std::string source(source_utf8);
        const std::string name = source_name ? source_name : "<source>";
        fsmc::Diagnostics diag(name);

        // --- same pipeline as src/main.cpp ---
        std::vector<fsmc::lex::Token> tokens = fsmc::lex::tokenize(source, name, diag);
        fsmc::Parser parser(tokens, diag);
        fsmc::ParsedSource parsed = parser.parse();
        fsmc::SymbolTable sym = fsmc::pass1_symbolTable(parsed, diag);
        fsmc::AstForest forest = fsmc::pass2_buildAstForest(parsed, sym, diag);
        fsmc::GotoLists gotos = fsmc::pass3_collectGotos(parsed, forest, diag);
        fsmc::FsmGraph fsm = fsmc::pass4_buildFsmAdjacency(parsed, gotos, sym, diag);
        std::vector<uint8_t> module;
        if (!diag.hasErrors())
            module = fsmc::pass5_serialize(parsed, forest, fsm, sym, diag);
        if (!diag.hasErrors()) {
            fsmc::LinkResult link = fsmc::pass6_link(module, diag);
            if (!link.ok && !diag.hasErrors())
                diag.error(fsmc::SrcLoc{1, 1}, "link step failed; no binary emitted");
        }

        std::ostringstream oss;
        diag.print(oss);
        diags = dupString(oss.str());
        if (!diags) {
            rc = MYFSM_INTERNAL_ERROR;
        } else if (diag.hasErrors()) {
            rc = MYFSM_COMPILE_ERROR;
        } else if (module.empty()) {
            rc = MYFSM_INTERNAL_ERROR; // valid source always emits sections
        } else {
            bytes = static_cast<unsigned char*>(dupBytes(module.data(), module.size()));
            if (bytes) {
                len = module.size();
                rc = MYFSM_OK;
            } else {
                rc = MYFSM_INTERNAL_ERROR;
            }
        }
    } catch (...) {
        rc = MYFSM_INTERNAL_ERROR; // never let exceptions cross the ABI
    }
    if (rc == MYFSM_INTERNAL_ERROR) {
        std::free(bytes);
        std::free(diags);
        bytes = nullptr;
        diags = nullptr;
        len = 0;
    }
    *out_bytes = bytes;
    *out_len = len;
    *out_diagnostics = diags;
    return rc;
}

void myfsm_free(void* ptr) {
    if (ptr) std::free(ptr);
}

const char* myfsm_version(void) {
    static const char kVersion[] = "0.5";
    return kVersion;
}

} // extern "C"
