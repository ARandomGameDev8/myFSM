#include "test_helpers.hpp"

using namespace fsmc;

namespace {
std::string stateWithTrav(const std::string& body, const std::string& extra = "") {
    return "var bool enemyVisible;\n" + extra +
           "State A {\n"
           "    Actions {\n"
           "    }\n"
           "    Traversals {\n" +
           body +
           "    }\n"
           "}\n"
           "State B {\n"
           "    Actions {\n"
           "    }\n"
           "    Traversals {\n        goto A;\n    }\n"
           "}\n"
           "@ENTRY A\n";
}
} // namespace

TEST(traversals, empty_body_fails) {
    auto r = fh::compile(stateWithTrav(""));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "Traversals body must contain at least one goto"));
}

TEST(traversals, no_goto_anywhere_fails) {
    auto r = fh::compile(stateWithTrav("if (enemyVisible) { }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "Traversals if body must contain exactly one goto"));
    ASSERT_TRUE(fh::hasErrorContaining(r, "Traversals body must contain at least one goto"));
}

TEST(traversals, if_body_with_extra_statement_fails) {
    auto r = fh::compile(stateWithTrav(
                           "if (enemyVisible) {\n            wait(1.0f);\n            goto B;\n        }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "Traversals if body must contain exactly one goto"));
}

TEST(traversals, if_body_with_two_gotos_fails) {
    auto r = fh::compile(stateWithTrav("if (enemyVisible) {\n            goto B;\n            goto B;\n        }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "Traversals if body must contain exactly one goto"));
}

TEST(traversals, else_inside_traversals_fails) {
    auto r = fh::compile(stateWithTrav("if (enemyVisible) { goto B; } else { goto B; }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "else is not allowed in Traversals"));
}

TEST(traversals, else_if_inside_traversals_fails) {
    auto r = fh::compile(stateWithTrav("if (enemyVisible) { goto B; } else if (enemyVisible) { goto B; }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "else is not allowed in Traversals"));
}

TEST(traversals, temp_declaration_in_traversals_fails) {
    auto r = fh::compile(stateWithTrav("temp int x = 1;\n        goto B;\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "only 'if' and 'goto' statements are allowed in Traversals"));
}

TEST(traversals, bare_goto_before_if_warns_but_compiles) {
    auto r = fh::compile(stateWithTrav("goto B;\n        if (enemyVisible) { goto B; }\n"));
    ASSERT_TRUE(r.ok);
    ASSERT_TRUE(fh::hasWarningContaining(r, "bare goto before any if"));
}

TEST(traversals, bare_goto_plus_ifs_succeeds) {
    auto r = fh::compile(stateWithTrav(
                           "if (enemyVisible) { goto B; }\n"
                           "if (enemyVisible) { goto B; }\n"
                           "goto B;\n"));
    ASSERT_TRUE(r.ok);
    // State B (fixture) has a lone bare goto -> exactly one warning, from B.
    ASSERT_EQ(r.warnings, 1);
    ASSERT_TRUE(fh::hasWarningContaining(r, "bare goto before any if"));
    // adjacency: three gotos to B (duplicate preserved)
    ASSERT_EQ(r.fsm.adjacency[0].size(), std::size_t(3));
    ASSERT_EQ(r.fsm.adjacency[0][0], 1);
    ASSERT_EQ(r.fsm.adjacency[0][2], 1);
}

TEST(traversals, single_bare_goto_succeeds) {
    auto r = fh::compile(stateWithTrav("goto B;\n"));
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.fsm.adjacency[0].size(), std::size_t(1));
}

TEST(traversals, goto_in_actions_fails) {
    auto r = fh::compile(
        "State A {\n"
        "    Actions {\n"
        "        goto B;\n"
        "    }\n"
        "    Traversals {\n"
        "        goto B;\n"
        "    }\n"
        "}\n"
        "State B {\n    Actions { }\n    Traversals { goto A; }\n}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "goto is only allowed inside Traversals"));
}

TEST(traversals, nested_if_in_traversals_if_body_fails) {
    auto r = fh::compile(stateWithTrav("if (enemyVisible) {\n            if (enemyVisible) { goto B; }\n        }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "Traversals if body must contain exactly one goto"));
}
