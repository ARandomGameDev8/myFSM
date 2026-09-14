#include "test_helpers.hpp"

#include <string>
#include <vector>

#include "binary_format.hpp"
#include "builtin_types.hpp"
#include "disassembler.hpp"
#include "module_reader.hpp"

using namespace fsmc;

namespace {

// Wraps statement text into a minimal one-state file with a self-loop. The
// statements land in Update{}: Start{} and Update{} are both mandatory.
std::string wrap(const std::string& actions) {
    return "State A {\n"
           "    Actions {\n"
           "        Start { }\n"
           "        Update {\n" + actions + "        }\n"
           "    }\n"
           "    Traversals {\n"
           "        goto A;\n"
           "    }\n"
           "}\n"
           "@ENTRY A\n";
}

// Reads + validates a compiled module; fails the test if either step fails.
bool read(const fh::CompileResult& r, ReadModule& rm) {
    std::string err;
    if (!readModule(r.module, rm, err)) return false;
    return validateModule(rm, err);
}

// Finds the first AST token of `type`, or -1.
int findTok(const ReadModule& rm, fmt::AstTok type) {
    for (std::size_t i = 0; i < rm.ast.size(); ++i) {
        if (rm.ast[i].type == uint8_t(type)) return int(i);
    }
    return -1;
}

uint32_t rd32(const std::vector<uint8_t>& b, std::size_t p) {
    return uint32_t(b[p]) | (uint32_t(b[p + 1]) << 8) | (uint32_t(b[p + 2]) << 16) |
           (uint32_t(b[p + 3]) << 24);
}

} // namespace

// ---------------------------------------------------------------------------
// the type itself
// ---------------------------------------------------------------------------

TEST(strings, type_is_registered_as_a_variable_width_primitive) {
    const TypeDefinition* t = BuiltinTypes::instance().find("string");
    ASSERT_TRUE(t != nullptr);
    ASSERT_EQ(t->typeTag, uint8_t(0x05)); // next free primitive tag after bool
    ASSERT_EQ(t->sizeBytes, uint32_t(0)); // width is per value, not per type
    ASSERT_TRUE(t->isVariableSize);
    ASSERT_TRUE(t->isValueType);
    ASSERT_TRUE(!t->isHandle);
    ASSERT_TRUE(!t->isNumeric);
    ASSERT_EQ(t->category, std::string("primitive"));
    // the capital-S spelling was never a type and still is not
    ASSERT_TRUE(BuiltinTypes::instance().find("String") == nullptr);
}

// ---------------------------------------------------------------------------
// parsing / typing
// ---------------------------------------------------------------------------

TEST(strings, literal_is_an_expression_everywhere) {
    auto r = fh::compile(wrap("        temp string s = \"run\";\n"
                              "        s = \"walk\";\n"));
    ASSERT_TRUE(r.ok);
    const auto declsV = fh::actionStmts(r.src);
    const Stmt& decl = declsV[0];
    ASSERT_EQ(decl.kind, Stmt::Kind::TempDecl);
    ASSERT_EQ(decl.init->kind, Expr::Kind::Literal);
    ASSERT_EQ(decl.init->litStr, std::string("run"));
    ASSERT_TRUE(decl.init->type != nullptr);
    ASSERT_EQ(decl.init->type->name, std::string("string"));
    // a string temp is a temp like any other: tag 0x05 in the module
    ReadModule rm;
    ASSERT_TRUE(read(r, rm));
    ASSERT_EQ(rm.temps.size(), std::size_t(1));
    ASSERT_EQ(rm.temps[0].tag, uint8_t(0x05));
    ASSERT_EQ(rm.temps[0].name, std::string("s"));
}

TEST(strings, escapes_are_decoded_once_by_the_lexer) {
    auto r = fh::compile(wrap("        temp string s = \"a\\tb\\nc\\\\d\\\"e\";\n"));
    ASSERT_TRUE(r.ok);
    const auto declsV = fh::actionStmts(r.src);
    const Stmt& decl = declsV[0];
    ASSERT_EQ(decl.init->litStr, std::string("a\tb\nc\\d\"e"));
    // ... and the module stores exactly those bytes, length-prefixed
    ReadModule rm;
    ASSERT_TRUE(read(r, rm));
    const int lit = findTok(rm, fmt::AstTok::Literal);
    ASSERT_TRUE(lit >= 0);
    const std::vector<uint8_t>& d = rm.ast[std::size_t(lit)].data;
    ASSERT_EQ(d[0], uint8_t(0x05));
    ASSERT_EQ(rd32(d, 1), uint32_t(9)); // a,TAB,b,LF,c,BSLASH,d,QUOTE,e
    ASSERT_EQ(d.size(), std::size_t(5 + 9));
    ASSERT_EQ(std::string(d.begin() + 5, d.end()), std::string("a\tb\nc\\d\"e"));
}

TEST(strings, const_string_is_folded_into_the_global_section) {
    auto r = fh::compile("const string greeting = \"hello\";\n" + wrap(""));
    ASSERT_TRUE(r.ok);
    ReadModule rm;
    ASSERT_TRUE(read(r, rm));
    ASSERT_EQ(rm.globals.size(), std::size_t(1));
    ASSERT_EQ(rm.globals[0].tag, uint8_t(0x05));
    ASSERT_EQ(rm.globals[0].name, std::string("greeting"));
    // value bytes: [4] length + UTF-8 payload
    ASSERT_EQ(rm.globals[0].value.size(), std::size_t(4 + 5));
    ASSERT_EQ(rd32(rm.globals[0].value, 0), uint32_t(5));
    ASSERT_EQ(std::string(rm.globals[0].value.begin() + 4, rm.globals[0].value.end()),
              std::string("hello"));
}

TEST(strings, concatenation_of_constants_folds_at_compile_time) {
    auto r = fh::compile("const string a = \"he\";\n"
                         "const string b = a + \"llo\";\n"
                         "const string c = b + b;\n" + wrap(""));
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.src.globals.size(), std::size_t(3));
    ReadModule rm;
    ASSERT_TRUE(read(r, rm));
    auto valueOf = [&](std::size_t i) {
        const auto& v = rm.globals[i].value;
        return std::string(v.begin() + 4, v.end());
    };
    ASSERT_EQ(valueOf(0), std::string("he"));
    ASSERT_EQ(valueOf(1), std::string("hello"));
    ASSERT_EQ(valueOf(2), std::string("hellohello"));
    // folded away: the only literal tokens left are the two operands of `c`
    // (there is no BINOP for a fully constant expression)
    ASSERT_TRUE(findTok(rm, fmt::AstTok::BinaryOp) < 0);
}

TEST(strings, runtime_concatenation_stays_an_ast_binop) {
    auto r = fh::compile("const string joined = \"hello, world\";\n" +
                         wrap("        temp string banner = joined + \"!\";\n"));
    ASSERT_TRUE(r.ok);
    ReadModule rm;
    ASSERT_TRUE(read(r, rm));
    const int bin = findTok(rm, fmt::AstTok::BinaryOp);
    ASSERT_TRUE(bin >= 0);
    // BINOP data: [1] op id [4] left address [4] right address
    const std::vector<uint8_t>& d = rm.ast[std::size_t(bin)].data;
    ASSERT_EQ(d.size(), std::size_t(9));
    ASSERT_EQ(d[0], uint8_t(fmt::OpPlus));
    auto tokenAt = [&](std::size_t at) -> const ReadAstToken* {
        uint32_t off = 0;
        if (!rm.astIndexByAddress(rd32(d, at), off)) return nullptr;
        for (std::size_t i = 0; i < rm.astEntryOffsets.size(); ++i) {
            if (rm.astEntryOffsets[i] == off) return &rm.ast[i];
        }
        return nullptr;
    };
    const ReadAstToken* left = tokenAt(1);
    const ReadAstToken* right = tokenAt(5);
    ASSERT_TRUE(left != nullptr && right != nullptr);
    // one side is a reference to the folded constant, the other a string literal
    bool sawStringLiteral = false, sawVarRef = false;
    for (const ReadAstToken* t : {left, right}) {
        if (t->type == uint8_t(fmt::AstTok::Literal)) {
            ASSERT_EQ(t->data[0], uint8_t(0x05));
            ASSERT_EQ(rd32(t->data, 1), uint32_t(1)); // "!"
            sawStringLiteral = true;
        } else {
            ASSERT_EQ(t->type, uint8_t(fmt::AstTok::VarRef));
            sawVarRef = true;
        }
    }
    ASSERT_TRUE(sawStringLiteral);
    ASSERT_TRUE(sawVarRef);
}

TEST(strings, equality_and_inequality_yield_bool) {
    auto r = fh::compile("const string greeting = \"hello\";\n"
                         "var string label;\n"
                         "State A {\n"
                         "    Actions {\n"
                         "        Start { }\n"
                         "        Update {\n"
                         "        temp string s = \"x\";\n"
                         "        temp bool same = s == greeting;\n"
                         "        temp bool diff = label != s;\n"
                         "        }\n"
                         "    }\n"
                         "    Traversals {\n"
                         "        if (label == greeting) { goto A; }\n"
                         "        goto A;\n"
                         "    }\n"
                         "}\n"
                         "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    ReadModule rm;
    ASSERT_TRUE(read(r, rm));
    ASSERT_EQ(rm.temps.size(), std::size_t(3));
    ASSERT_EQ(rm.temps[1].tag, uint8_t(0x04)); // bool
    ASSERT_EQ(rm.temps[2].tag, uint8_t(0x04)); // bool
}

// ---------------------------------------------------------------------------
// rejections
// ---------------------------------------------------------------------------

TEST(strings, only_plus_and_equality_are_defined) {
    struct Case { const char* actions; const char* message; };
    static const Case cases[] = {
        {"        temp string s = \"a\" + 1;\n",
         "'+' concatenates two strings, got string and int"},
        {"        temp string s = 1 + \"a\";\n",
         "'+' concatenates two strings, got int and string"},
        {"        temp string s = \"a\" * \"b\";\n", "'*' is not defined for strings"},
        {"        temp string s = \"a\" - \"b\";\n", "'-' is not defined for strings"},
        {"        temp string s = \"a\" / \"b\";\n", "'/' is not defined for strings"},
        {"        temp int n = \"a\" // \"b\";\n", "'//' is not defined for strings"},
        {"        temp string s = \"a\" ** \"b\";\n", "'**' is not defined for strings"},
        {"        temp string s = -\"a\";\n",
         "unary '-' requires a numeric operand, got string"},
        {"        temp string a = \"x\";\n        temp string b = \"y\";\n"
         "        temp bool c = a < b;\n", "'<' is not defined for strings"},
        {"        temp string a = \"x\";\n        temp bool c = a && true;\n",
         "logical operator '&&' requires bool operands, got string and bool"},
        {"        temp string a = \"x\";\n        temp bool c = !a;\n",
         "unary '!' requires a bool operand, got string"},
    };
    for (const Case& c : cases) {
        auto r = fh::compile(wrap(c.actions));
        ASSERT_TRUE(!r.ok);
        if (!fh::hasErrorContaining(r, c.message)) {
            ASSERT_TRUE(false); // message not found; diagnostics are in r.diagnostics
        }
    }
}

TEST(strings, type_mismatches_are_rejected) {
    auto r1 = fh::compile(wrap("        temp int n = \"a\";\n"));
    ASSERT_TRUE(!r1.ok);
    ASSERT_TRUE(fh::hasErrorContaining(
        r1, "cannot initialize 'n' of type 'int' with expression of type 'string'"));

    auto r2 = fh::compile("const int n = \"a\";\n" + wrap(""));
    ASSERT_TRUE(!r2.ok);
    ASSERT_TRUE(fh::hasErrorContaining(
        r2, "cannot initialize constant 'n' of type 'int' with expression of type 'string'"));

    auto r3 = fh::compile("const string s = 5;\n" + wrap(""));
    ASSERT_TRUE(!r3.ok);
    ASSERT_TRUE(fh::hasErrorContaining(
        r3, "cannot initialize constant 's' of type 'string' with expression of type 'int'"));

    // a string is not a valid vector component
    auto r4 = fh::compile(wrap("        temp Vector2 v = Vector2(1.0f, \"a\");\n"));
    ASSERT_TRUE(!r4.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r4, "components must be float, got string"));
}

TEST(strings, no_builtin_accepts_a_string_yet) {
    // The registry is unchanged by this feature: passing a string where a
    // Vector3 is expected lists the real candidates instead of silently
    // converting.
    auto r = fh::compile("var Object3D o;\n" +
                         wrap("        temp string s = \"x\";\n"
                              "        setPosition(o, s);\n"));
    ASSERT_TRUE(!r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(
        r, "no overload of 'setPosition' matches arguments (Object3D, string)"));
    ASSERT_TRUE(fh::hasErrorContaining(r, "setPosition(Object3D obj, Vector3 pos)"));
}

TEST(strings, capital_String_is_still_not_a_type) {
    auto r = fh::compile(wrap("        temp String s = \"a\";\n"));
    ASSERT_TRUE(!r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown type 'String' in declaration"));
}

// ---------------------------------------------------------------------------
// module level: fixture round trip + disassembly
// ---------------------------------------------------------------------------

TEST(strings, fixture_round_trips_and_validates) {
    auto r = fh::compile(fh::readFixture("strings.fsm"), "strings.fsm");
    ASSERT_TRUE(r.ok);
    ReadModule rm;
    ASSERT_TRUE(read(r, rm));

    // two string constants, folded ("hello", "hello, world")
    ASSERT_EQ(rm.globals.size(), std::size_t(2));
    ASSERT_EQ(rm.globals[0].tag, uint8_t(0x05));
    ASSERT_EQ(rm.globals[1].tag, uint8_t(0x05));
    ASSERT_EQ(rd32(rm.globals[1].value, 0), uint32_t(12));
    ASSERT_EQ(std::string(rm.globals[1].value.begin() + 4, rm.globals[1].value.end()),
              std::string("hello, world"));

    // runtime string keeps its binding slot; the handle next to it is unaffected
    ASSERT_EQ(rm.runtime.size(), std::size_t(2));
    ASSERT_EQ(rm.runtime[0].name, std::string("label"));
    ASSERT_EQ(rm.runtime[0].tag, uint8_t(0x05));
    ASSERT_EQ(rm.runtime[0].bindingSlot, uint32_t(0));
    ASSERT_EQ(rm.runtime[1].name, std::string("target"));
    ASSERT_EQ(rm.runtime[1].tag, uint8_t(0x21)); // Object3D
    ASSERT_EQ(rm.runtime[1].bindingSlot, uint32_t(1));

    // three string temps, each owned by the block that declared it
    ASSERT_EQ(rm.temps.size(), std::size_t(3));
    for (const ReadTemp& t : rm.temps) ASSERT_EQ(t.tag, uint8_t(0x05));
    ASSERT_EQ(rm.temps[0].name, std::string("tag"));    // Actions body
    ASSERT_EQ(rm.temps[1].name, std::string("banner")); // Actions body
    ASSERT_EQ(rm.temps[2].name, std::string("esc"));    // if body
}

TEST(strings, every_string_literal_in_the_fixture_is_length_prefixed) {
    auto r = fh::compile(fh::readFixture("strings.fsm"), "strings.fsm");
    ASSERT_TRUE(r.ok);
    ReadModule rm;
    ASSERT_TRUE(read(r, rm));
    std::size_t literals = 0;
    for (const ReadAstToken& t : rm.ast) {
        if (t.type != uint8_t(fmt::AstTok::Literal)) continue;
        ASSERT_TRUE(!t.data.empty());
        if (t.data[0] != 0x05) continue; // not a string literal
        ++literals;
        ASSERT_TRUE(t.data.size() >= 5);
        const uint32_t len = rd32(t.data, 1);
        ASSERT_EQ(t.data.size(), std::size_t(5) + len);
    }
    ASSERT_TRUE(literals >= 5); // "idle", "!", "hello, world!", escapes, "run"
}

TEST(strings, disassembly_prints_quoted_and_re_escaped_strings) {
    auto r = fh::compile(fh::readFixture("strings.fsm"), "strings.fsm");
    ASSERT_TRUE(r.ok);
    ReadModule rm;
    ASSERT_TRUE(read(r, rm));
    const std::string t = disassemble(rm, "strings.fsmb", true);
    ASSERT_TRUE(t.find("fsmb v0.5") != std::string::npos);
    ASSERT_TRUE(t.find("greeting        string        = \"hello\"") != std::string::npos);
    ASSERT_TRUE(t.find("= \"hello, world\"") != std::string::npos); // folded constant
    // literals render inline in the instruction / AST dumps
    ASSERT_TRUE(t.find("ASSIGN tag = \"idle\"") != std::string::npos);
    ASSERT_TRUE(t.find("ASSIGN banner = (joined + \"!\")") != std::string::npos);
    ASSERT_TRUE(t.find("(banner == \"hello, world!\")") != std::string::npos);
    ASSERT_TRUE(t.find("TEMP string esc") != std::string::npos);
    ASSERT_TRUE(t.find("\"a\\tb\\nc\\\\d\\\"e\"") != std::string::npos); // escapes re-encoded
    ASSERT_TRUE(t.find("label           string") != std::string::npos); // runtime var
}

TEST(strings, a_corrupt_string_length_is_rejected) {
    auto r = fh::compile(fh::readFixture("strings.fsm"), "strings.fsm");
    ASSERT_TRUE(r.ok);
    // Inflate the length prefix of the first global's string value: the entry
    // then claims more bytes than the section holds.
    std::vector<uint8_t> bad = r.module;
    const uint32_t globalOff = fh::le32(bad, 8); // section 1
    ASSERT_EQ(bad[globalOff + 4], uint8_t(0x05)); // type tag of entry #0
    bad[globalOff + 5] = 0xFF; // length low byte: 5 -> 0xFF
    ReadModule rm;
    std::string err;
    ASSERT_TRUE(!readModule(bad, rm, err) || !validateModule(rm, err));
}
