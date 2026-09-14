#include "test_helpers.hpp"

#include <string>
#include <vector>

#include "disassembler.hpp"
#include "module_reader.hpp"

using namespace fsmc;

namespace {

fh::CompileResult compileFixture(const char* name) { return fh::compile(fh::readFixture(name), name); }

bool parseAndValidate(const fh::CompileResult& r, ReadModule& mod, std::string& err) {
    return readModule(r.module, mod, err) && validateModule(mod, err);
}

} // namespace

TEST(disassemble, minimal_module_resolves_names_and_decodes_values) {
    auto r = compileFixture("minimal.fsm");
    ASSERT_TRUE(r.ok);
    ReadModule mod;
    std::string err;
    ASSERT_TRUE(parseAndValidate(r, mod, err));

    const std::string t = disassemble(mod, "minimal.fsmb", true);
    ASSERT_TRUE(t.find("0x46534D44") != std::string::npos); // magic in banner
    ASSERT_TRUE(t.find("Idle") != std::string::npos);       // state name resolved
    ASSERT_TRUE(t.find("tickLimit") != std::string::npos);  // global const name
    ASSERT_TRUE(t.find("= 10") != std::string::npos);       // decoded const value
    ASSERT_TRUE(t.find("wait") != std::string::npos);       // function name resolved
    ASSERT_TRUE(t.find("GOTO") != std::string::npos);       // instruction decoded
    ASSERT_TRUE(t.find("[ENTRY]") != std::string::npos);    // entry flag decoded
    ASSERT_TRUE(t.find("SECTION MAP") != std::string::npos);
    ASSERT_TRUE(t.find("FSM ADJACENCY") != std::string::npos);
}

TEST(disassemble, two_state_shows_calls_without_claims_or_ownership) {
    auto r = compileFixture("two_state.fsm");
    ASSERT_TRUE(r.ok);
    ReadModule mod;
    std::string err;
    ASSERT_TRUE(parseAndValidate(r, mod, err));

    const std::string t = disassemble(mod, "two_state.fsmb", true);
    ASSERT_TRUE(t.find("fsmb v0.3") != std::string::npos); // banner shows the format version
    // calls survive, with their resolved function ids
    ASSERT_TRUE(t.find("CALL") != std::string::npos);
    ASSERT_TRUE(t.find("moveTowards") != std::string::npos);
    ASSERT_TRUE(t.find("followTarget") != std::string::npos);
    ASSERT_TRUE(t.find("stopMovement") != std::string::npos);
    // ... but the claim/ownership vocabulary is gone from the dump entirely
    ASSERT_TRUE(t.find("CLAIM") == std::string::npos);
    ASSERT_TRUE(t.find("RELEASE") == std::string::npos);
    ASSERT_TRUE(t.find("owner=") == std::string::npos);
    ASSERT_TRUE(t.find("DIRTY") == std::string::npos);
    // runtime variables are listed as type + binding slot only
    ASSERT_TRUE(t.find("SECTION [2] RUNTIME VARIABLES (4)") != std::string::npos);
    ASSERT_TRUE(t.find("slot 2") != std::string::npos); // agent
}

// The Temporary section has no depth column any more: each temp is listed with
// the block that owns it, recovered from the AST parent edge of its
// TEMP_VAR_DECL token.
TEST(disassemble, temps_are_listed_with_their_owning_block) {
    auto r = compileFixture("two_state.fsm");
    ASSERT_TRUE(r.ok);
    ReadModule mod;
    std::string err;
    ASSERT_TRUE(parseAndValidate(r, mod, err));

    const std::string t = disassemble(mod, "two_state.fsmb", true);
    ASSERT_TRUE(t.find("scope state body") != std::string::npos); // lastDist
    ASSERT_TRUE(t.find("scope Actions") != std::string::npos);    // dist, away
    ASSERT_TRUE(t.find("scope if body") != std::string::npos);    // dir
    ASSERT_TRUE(t.find("depth") == std::string::npos);            // no depth anywhere
    ASSERT_TRUE(t.find("state: Chase") != std::string::npos);
}

TEST(disassemble, vector_literals_are_decoded_to_values) {
    auto r = fh::compile(
        "const Vector3 origin = Vector3(1.5f, -2.5f, 0.0f);\n"
        "var Vector3 pos;\n"
        "State A {\n"
        "    Actions { pos = origin; }\n"
        "    Traversals { goto A; }\n"
        "}\n"
        "@ENTRY A\n",
        "vec.fsm");
    ASSERT_TRUE(r.ok);
    ReadModule mod;
    std::string err;
    ASSERT_TRUE(parseAndValidate(r, mod, err));
    const std::string t = disassemble(mod, "vec.fsmb", true);
    ASSERT_TRUE(t.find("Vector3(1.5f, -2.5f, 0f)") != std::string::npos);
    ASSERT_TRUE(t.find("origin") != std::string::npos);
}

TEST(disassemble, corrupted_module_is_rejected) {
    auto r = compileFixture("minimal.fsm");
    ASSERT_TRUE(r.ok);
    ReadModule mod;
    std::string err;

    std::vector<uint8_t> bad = r.module;
    bad[0] = uint8_t(bad[0] ^ 0xFF); // corrupt the magic
    ASSERT_FALSE(readModule(bad, mod, err));
    ASSERT_TRUE(!err.empty());

    std::vector<uint8_t> trunc(r.module.begin(), r.module.begin() + 20);
    err.clear();
    ASSERT_FALSE(readModule(trunc, mod, err));

    // A text file is not a module either.
    err.clear();
    ASSERT_FALSE(readModule(std::vector<uint8_t>{97, 98, 99}, mod, err));
}

TEST(disassemble, deterministic_output) {
    auto r1 = compileFixture("two_state.fsm");
    auto r2 = compileFixture("two_state.fsm");
    ASSERT_TRUE(r1.ok && r2.ok);
    ReadModule m1, m2;
    std::string err;
    ASSERT_TRUE(parseAndValidate(r1, m1, err));
    ASSERT_TRUE(parseAndValidate(r2, m2, err));
    ASSERT_EQ(disassemble(m1, "x.fsmb", true), disassemble(m2, "x.fsmb", true));
}
