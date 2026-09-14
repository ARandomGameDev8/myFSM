#include "test_helpers.hpp"

using namespace fsmc;

TEST(scope, inner_temp_referenced_after_inner_block_fails) {
    // The inner {} here is an if body: its temps die with the block.
    auto r = fh::compile(
        "var bool ready;\n"
        "State A {\n"
        "    Actions {\n"
        "        if (ready) {\n"
        "            temp int x = 1;\n"
        "        }\n"
        "        x = x + 1;\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown variable 'x'"));
}

TEST(scope, outer_temp_visible_inside_inner_block) {
    auto r = fh::compile(
        "var bool ready;\n"
        "State A {\n"
        "    Actions {\n"
        "        temp int x = 1;\n"
        "        if (ready) {\n"
        "            temp int y = x;\n"
        "        }\n"
        "        x = x + 1;\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    // y lives only in the if body (depth 3), x lives in Actions (depth 2)
    ASSERT_EQ(r.src.temps.size(), std::size_t(2));
    ASSERT_EQ(r.src.temps[0].name, std::string("x"));
    ASSERT_EQ(r.src.temps[0].depth, uint8_t(2));
    ASSERT_EQ(r.src.temps[1].name, std::string("y"));
    ASSERT_EQ(r.src.temps[1].depth, uint8_t(3));
}

TEST(scope, inner_block_may_shadow_outer_temp) {
    auto r = fh::compile(
        "var bool ready;\n"
        "State A {\n"
        "    Actions {\n"
        "        temp int x = 1;\n"
        "        if (ready) {\n"
        "            temp int x = 2;\n"
        "        }\n"
        "        x = x + 1;\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
}

TEST(scope, redeclaration_in_same_block_fails) {
    auto r = fh::compile(
        "State A {\n"
        "    Actions {\n"
        "        temp int x = 1;\n"
        "        temp int x = 2;\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "redeclaration of 'x' in this scope"));
}

TEST(scope, state_level_temp_visible_in_actions_and_traversals) {
    auto r = fh::compile(
        "var bool ready;\n"
        "State A {\n"
        "    temp float lastDist = 99.0f;\n"
        "    Actions {\n"
        "        lastDist = lastDist - 1.0f;\n"
        "    }\n"
        "    Traversals {\n"
        "        if (lastDist < 1.0f) { goto A; }\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.src.temps.size(), std::size_t(1));
    ASSERT_EQ(r.src.temps[0].name, std::string("lastDist"));
    ASSERT_EQ(r.src.temps[0].depth, uint8_t(1));
}

TEST(scope, state_level_temp_not_visible_in_other_state) {
    auto r = fh::compile(
        "State A {\n"
        "    temp int x = 1;\n"
        "    Actions {\n"
        "        x = x + 1;\n"
        "    }\n"
        "    Traversals {\n"
        "        goto B;\n"
        "    }\n"
        "}\n"
        "State B {\n"
        "    Actions {\n"
        "        temp int y = x;\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown variable 'x'"));
}

TEST(scope, temp_shadowing_global_allowed) {
    auto r = fh::compile(
        "var int health;\n"
        "State A {\n"
        "    Actions {\n"
        "        temp int health = 3;\n"
        "        temp int h2 = health;\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    // inside the block the temp wins
    const auto& stmts = r.src.states[0].items[0].stmts;
    ASSERT_EQ(stmts[1].init->var.kind, VarKind::Temp);
}

TEST(scope, temp_cannot_be_declared_at_file_level) {
    auto r = fh::compile("temp int x = 1;\n"
                         "State A {\n    Actions { }\n    Traversals { goto A; }\n}\n"
                         "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
}
