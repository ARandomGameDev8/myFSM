// fsmc — compiler for the engine-agnostic FSM DSL.
//
// Usage: fsmc input.fsm [-o output.fsmb]
//
// Exit codes: 0 = success (warnings allowed), 1 = usage or I/O error,
// 2 = compile error (parse/semantic/link). Never emits a partial binary.

#include <cstdio>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

#include "diagnostics.hpp"
#include "lexer.hpp"
#include "parser.hpp"
#include "passes.hpp"

namespace {

void printUsage(std::ostream& os) {
    os << "Usage: fsmc input.fsm [-o output.fsmb]\n"
       << "\n"
       << "Compiles one .fsm source file (one AI system) into a little-endian\n"
       << ".fsmb binary module.\n"
       << "\n"
       << "Options:\n"
       << "  -o, --output FILE   output path (default: <input>.fsmb)\n"
       << "  -h, --help          show this help\n";
}

} // namespace

int main(int argc, char** argv) {
    std::string inputPath;
    std::string outputPath;

    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        if (a == "-h" || a == "--help") {
            printUsage(std::cout);
            return 0;
        }
        if (a == "-o" || a == "--output") {
            if (i + 1 >= argc) {
                std::cerr << "error: " << a << " requires a file argument\n";
                return 1;
            }
            outputPath = argv[++i];
            continue;
        }
        if (!a.empty() && a[0] == '-') {
            std::cerr << "error: unknown option '" << a << "'\n";
            printUsage(std::cerr);
            return 1;
        }
        if (!inputPath.empty()) {
            std::cerr << "error: multiple input files given ('" << inputPath << "', '" << a << "')\n";
            return 1;
        }
        inputPath = a;
    }

    if (inputPath.empty()) {
        printUsage(std::cerr);
        return 1;
    }
    if (outputPath.empty()) {
        outputPath = inputPath;
        const std::string kExt = ".fsm";
        if (outputPath.size() < kExt.size() ||
            outputPath.substr(outputPath.size() - kExt.size()) != kExt) {
            outputPath += ".fsmb";
        } else {
            outputPath.replace(outputPath.size() - kExt.size(), kExt.size(), ".fsmb");
        }
    }

    std::ifstream in(inputPath, std::ios::binary);
    if (!in) {
        std::cerr << "error: cannot open input file '" << inputPath << "'\n";
        return 1;
    }
    std::string source((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    in.close();

    // --- pipeline: lex -> parse -> pass1..pass6 ---
    fsmc::Diagnostics diag(inputPath);

    std::vector<fsmc::lex::Token> tokens = fsmc::lex::tokenize(source, inputPath, diag);

    fsmc::Parser parser(tokens, diag);
    fsmc::ParsedSource src = parser.parse();

    fsmc::SymbolTable sym = fsmc::pass1_symbolTable(src, diag);
    fsmc::AstForest forest = fsmc::pass2_buildAstForest(src, sym, diag);
    fsmc::GotoLists gotos = fsmc::pass3_collectGotos(src, forest, diag);
    fsmc::FsmGraph fsm = fsmc::pass4_buildFsmAdjacency(src, gotos, sym, diag);

    std::vector<uint8_t> module;
    if (!diag.hasErrors()) {
        module = fsmc::pass5_serialize(src, forest, fsm, sym, diag);
    }
    if (!diag.hasErrors()) {
        fsmc::LinkResult link = fsmc::pass6_link(module, diag);
        if (!link.ok) {
            std::cerr << "error: link step failed; no binary emitted\n";
        }
    }

    if (diag.hasErrors()) {
        diag.print(std::cerr);
        return 2;
    }

    // Write the finished module in one shot; on any I/O failure remove the
    // (possibly partial) file so a broken module is never left behind.
    {
        std::ofstream out(outputPath, std::ios::binary | std::ios::trunc);
        if (!out) {
            std::cerr << "error: cannot open output file '" << outputPath << "'\n";
            return 1;
        }
        out.write(reinterpret_cast<const char*>(module.data()),
                  static_cast<std::streamsize>(module.size()));
        out.close();
        if (!out) {
            std::remove(outputPath.c_str());
            std::cerr << "error: failed writing '" << outputPath << "'\n";
            return 1;
        }
    }

    if (diag.warningCount() > 0) diag.print(std::cerr);
    std::cerr << inputPath << " -> " << outputPath << " (" << module.size() << " bytes)\n";
    return 0;
}
