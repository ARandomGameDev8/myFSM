// fsmc — compiler for the engine-agnostic FSM DSL.
//
// Usage:
//   fsmc input.fsm [-o output.fsmb]        compile .fsm source -> .fsmb binary
//   fsmc -d input.fsmb [-o output.fsmd]    disassemble .fsmb -> human-readable text
//
// Exit codes: 0 = success (warnings allowed), 1 = usage or I/O error,
// 2 = compile error (parse/semantic/link) or an invalid/corrupt .fsmb in
// disassemble mode. Never emits a partial binary.

#include <cstdio>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

#include "diagnostics.hpp"
#include "disassembler.hpp"
#include "lexer.hpp"
#include "module_reader.hpp"
#include "parser.hpp"
#include "passes.hpp"

namespace {

void printUsage(std::ostream& os) {
    os << "Usage:\n"
       << "  fsmc input.fsm [-o output.fsmb]        compile .fsm -> .fsmb\n"
       << "  fsmc -d input.fsmb [-o output.fsmd]    disassemble .fsmb -> readable text\n"
       << "                                         (use -o - to print to stdout)\n"
       << "\n"
       << "Compiles one .fsm source file (one AI system) into a little-endian\n"
       << ".fsmb binary module; -d walks a .fsmb back through the module\n"
       << "reader and prints a debuggable, human-readable version of it.\n"
       << "\n"
       << "Options:\n"
       << "  -o, --output FILE   output path (compile: <input>.fsmb by default;\n"
       << "                      disassemble: <input>.fsmd, or stdout with -o -)\n"
       << "  -d, --disassemble   treat the input as a .fsmb module and disassemble\n"
       << "  -h, --help          show this help\n";
}

} // namespace

int main(int argc, char** argv) {
    std::string inputPath;
    std::string outputPath;
    bool disassembleMode = false;

    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        if (a == "-h" || a == "--help") {
            printUsage(std::cout);
            return 0;
        }
        if (a == "-d" || a == "--disassemble" || a == "--dump") {
            disassembleMode = true;
            continue;
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

    // ------------------------------------------------------------------
    // Disassemble mode: fsmc -d module.fsmb [-o out.fsmd | -o -]
    // ------------------------------------------------------------------
    if (disassembleMode) {
        std::vector<uint8_t> bytes;
        {
            std::ifstream bin(inputPath, std::ios::binary);
            if (!bin) {
                std::cerr << "error: cannot open input file '" << inputPath << "'\n";
                return 1;
            }
            bytes.assign(std::istreambuf_iterator<char>(bin), std::istreambuf_iterator<char>());
        }
        fsmc::ReadModule mod;
        std::string err;
        if (!fsmc::readModule(bytes, mod, err)) {
            std::cerr << "error: '" << inputPath << "' is not a valid .fsmb module: " << err
                      << "\n";
            return 2;
        }
        const bool validated = fsmc::validateModule(mod, err);
        if (!validated) {
            std::cerr << "error: module failed validation: " << err << "\n";
            return 2;
        }
        const std::string text = fsmc::disassemble(mod, inputPath, validated);

        const bool toStdout = (outputPath == "-");
        if (outputPath.empty()) {
            outputPath = inputPath;
            const std::string kExt = ".fsmb";
            if (outputPath.size() < kExt.size() ||
                outputPath.substr(outputPath.size() - kExt.size()) != kExt) {
                outputPath += ".fsmd";
            } else {
                outputPath.replace(outputPath.size() - kExt.size(), kExt.size(), ".fsmd");
            }
        }
        if (toStdout) {
            std::cout << text;
            if (!std::cout) {
                std::cerr << "error: failed writing stdout\n";
                return 1;
            }
        } else {
            std::ofstream out(outputPath, std::ios::trunc);
            if (!out) {
                std::cerr << "error: cannot open output file '" << outputPath << "'\n";
                return 1;
            }
            out << text;
            out.close();
            if (!out) {
                std::remove(outputPath.c_str());
                std::cerr << "error: failed writing '" << outputPath << "'\n";
                return 1;
            }
        }
        uint32_t instrTotal = 0;
        for (const fsmc::ReadStateInstrs& f : mod.stateInstrs)
            instrTotal += uint32_t(f.instrs.size());
        std::cerr << inputPath << " -> " << (toStdout ? "stdout" : outputPath) << " ("
                  << mod.states.size() << " states, " << mod.ast.size() << " ast tokens, "
                  << instrTotal << " instructions)\n";
        return 0;
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
