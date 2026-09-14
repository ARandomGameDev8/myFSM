#include "test_helpers.hpp"

#include <string>

#include "scope.hpp"

using namespace fsmc;

// ---------------------------------------------------------------------------
// Temporary variables: C block scoping through a scope stack.
//
// A temp belongs to the '{' body it is declared in (its direct parent block),
// is visible from its declaration to that block's closing '}' — including in
// blocks nested inside it, where it may be shadowed — and dies with the block.
// There are no fixed depth levels: nothing in the AST or in the binary records
// a depth, only the scope frame a declaration was made in.
// ---------------------------------------------------------------------------

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
    // x belongs to the Actions frame, y to the nested if-body frame: two
    // different frames of the same stack (no depth level is recorded).
    ASSERT_EQ(r.src.temps.size(), std::size_t(2));
    ASSERT_EQ(r.src.temps[0].name, std::string("x"));
    ASSERT_EQ(r.src.temps[1].name, std::string("y"));
    ASSERT_NE(r.src.temps[0].scopeId, r.src.temps[1].scopeId);
    ASSERT_TRUE(r.src.temps[0].scopeId >= 0);
    ASSERT_TRUE(r.src.temps[1].scopeId >= 0);
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
    // declared in the State body frame — the parent of both Actions and
    // Traversals, which is why both can see it
    ASSERT_TRUE(r.src.temps[0].scopeId >= 0);
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

TEST(scope, temps_in_the_same_block_share_one_scope_frame) {
    auto r = fh::compile(
        "State A {\n"
        "    Actions {\n"
        "        temp int a = 1;\n"
        "        temp int b = a + 1;\n"
        "        temp int c = a + b;\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.src.temps.size(), std::size_t(3));
    ASSERT_EQ(r.src.temps[0].scopeId, r.src.temps[1].scopeId);
    ASSERT_EQ(r.src.temps[1].scopeId, r.src.temps[2].scopeId);
}

TEST(scope, state_body_frame_is_distinct_from_the_actions_frame) {
    auto r = fh::compile(
        "State A {\n"
        "    temp int s = 1;\n"
        "    Actions {\n"
        "        temp int x = s;\n"
        "    }\n"
        "    Traversals {\n"
        "        if (s > 0) { goto A; }\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.src.temps.size(), std::size_t(2));
    ASSERT_NE(r.src.temps[0].scopeId, r.src.temps[1].scopeId);
}

TEST(scope, every_branch_of_an_if_chain_gets_its_own_frame) {
    auto r = fh::compile(
        "var int n;\n"
        "State A {\n"
        "    Actions {\n"
        "        if (n > 2) {\n"
        "            temp int a = 1;\n"
        "        } else if (n > 1) {\n"
        "            temp int b = 2;\n"
        "        } else {\n"
        "            temp int c = 3;\n"
        "        }\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.src.temps.size(), std::size_t(3));
    ASSERT_NE(r.src.temps[0].scopeId, r.src.temps[1].scopeId);
    ASSERT_NE(r.src.temps[1].scopeId, r.src.temps[2].scopeId);
    ASSERT_NE(r.src.temps[0].scopeId, r.src.temps[2].scopeId);
}

TEST(scope, sibling_branch_temps_are_invisible_to_each_other) {
    auto r = fh::compile(
        "var int n;\n"
        "State A {\n"
        "    Actions {\n"
        "        if (n > 2) {\n"
        "            temp int a = 1;\n"
        "        } else {\n"
        "            temp int b = a;\n"     // a died with the previous branch
        "        }\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown variable 'a'"));
}

TEST(scope, state_level_temp_must_be_declared_before_the_block_that_uses_it) {
    // Same C rule one level up: the State body is a block too, so a temp
    // written after Actions is not visible inside Actions.
    auto r = fh::compile(
        "State A {\n"
        "    Actions {\n"
        "        x = 1;\n"
        "    }\n"
        "    temp int x = 0;\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown variable 'x'"));
}

TEST(scope, temp_is_not_visible_before_its_declaration_in_the_same_block) {
    // C lifetime: a name is in scope from its declaration to the end of the
    // block, not for the whole block.
    auto r = fh::compile(
        "State A {\n"
        "    Actions {\n"
        "        x = 1;\n"
        "        temp int x = 2;\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown variable 'x'"));
}

// ---------------------------------------------------------------------------
// The scope stack itself: unbounded nesting, innermost-first lookup, frames
// discarded on pop, no fixed depth level anywhere.
// ---------------------------------------------------------------------------

TEST(scope, stack_nests_without_any_depth_limit) {
    ScopeStack st;
    const int kFrames = 64; // arbitrary: nothing caps the stack
    for (int i = 0; i < kFrames; ++i) {
        st.push();
        ASSERT_TRUE(st.declare("v" + std::to_string(i), i));
        ASSERT_EQ(st.frameCount(), std::size_t(i + 1));
    }
    // every level is still reachable from the innermost one
    for (int i = kFrames - 1; i >= 0; --i) {
        const int* id = st.find("v" + std::to_string(i));
        ASSERT_TRUE(id != nullptr);
        ASSERT_EQ(*id, i);
    }
    // popping discards exactly that frame's declarations
    for (int i = kFrames - 1; i >= 0; --i) {
        st.pop();
        ASSERT_TRUE(st.find("v" + std::to_string(i)) == nullptr);
        if (i > 0) ASSERT_TRUE(st.find("v" + std::to_string(i - 1)) != nullptr);
    }
    ASSERT_TRUE(st.empty());
    ASSERT_EQ(st.frameCount(), std::size_t(0));
}

TEST(scope, stack_lookup_resolves_the_innermost_declaration_first) {
    ScopeStack st;
    st.push();
    ASSERT_TRUE(st.declare("x", 1));
    st.push();
    ASSERT_TRUE(st.declare("x", 2)); // shadows the outer x
    st.push();
    ASSERT_TRUE(st.declare("x", 3)); // shadows again
    ASSERT_EQ(*st.find("x"), 3);
    st.pop();
    ASSERT_EQ(*st.find("x"), 2);
    st.pop();
    ASSERT_EQ(*st.find("x"), 1);
    st.pop();
    ASSERT_TRUE(st.find("x") == nullptr);
}

TEST(scope, stack_frame_ids_identify_blocks_and_are_never_reused) {
    ScopeStack st;
    const int outer = st.push();
    const int inner = st.push();
    st.pop();
    const int sibling = st.push(); // a fresh frame, not a recycled id
    ASSERT_NE(outer, inner);
    ASSERT_NE(inner, sibling);
    ASSERT_NE(outer, sibling);
    ASSERT_EQ(st.currentFrameId(), sibling);
    st.pop();
    ASSERT_EQ(st.currentFrameId(), outer);
    st.pop();
    ASSERT_EQ(st.currentFrameId(), -1);
}

TEST(scope, stack_declaration_needs_an_open_frame_and_rejects_redeclaration) {
    ScopeStack st;
    ASSERT_FALSE(st.declare("x", 0)); // no frame open (file level)
    ASSERT_TRUE(st.empty());
    st.push();
    ASSERT_TRUE(st.declare("x", 0));
    ASSERT_FALSE(st.declare("x", 1)); // same block: redeclaration
    st.push();
    ASSERT_TRUE(st.declare("x", 2)); // nested block: shadowing is fine
}
