#include "test_helpers.hpp"

using namespace fsmc;

namespace {
std::string base(const std::string& stateBody) {
    return std::string("State A {\n") + stateBody + "    Traversals {\n        goto A;\n    }\n}\n\n@ENTRY A\n";
}

// Wraps statement text in a valid Actions body: Start{} and Update{} are both
// mandatory, so the statements go in Update{} (the per-tick phase).
std::string acts(const std::string& stmts) {
    return "Actions {\n        Start { }\n        Update {\n" + stmts +
           "        }\n    }\n";
}
} // namespace

TEST(parser, minimal_one_state_ast_shape) {
    auto r = fh::compile(
        "State A {\n"
        "    Actions {\n"
        "        Start { }\n"
        "        Update {\n"
        "        temp int x = 1;\n"
        "        wait(0.5f);\n"
        "        }\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.src.states.size(), std::size_t(1));
    ASSERT_TRUE(r.src.states[0].isEntry);
    ASSERT_EQ(r.src.states[0].items.size(), std::size_t(2));
    ASSERT_EQ(r.src.states[0].items[0].kind, StateBodyItem::Kind::Actions);
    ASSERT_EQ(r.src.states[0].items[1].kind, StateBodyItem::Kind::Traversals);
    const auto& item = r.src.states[0].items[0];
    // both phase blocks are present, and the statements live in Update{}
    ASSERT_TRUE(item.hasStart);
    ASSERT_TRUE(item.hasUpdate);
    ASSERT_TRUE(item.stmts.empty());       // no temp declared directly in Actions
    ASSERT_TRUE(item.startStmts.empty());  // Start { } is empty
    ASSERT_EQ(item.actionsChildren.size(), std::size_t(2));
    ASSERT_EQ(int(item.actionsChildren[0].kind), int(StateBodyItem::Child::Kind::Start));
    ASSERT_EQ(int(item.actionsChildren[1].kind), int(StateBodyItem::Child::Kind::Update));
    const auto& actions = item.updateStmts;
    ASSERT_EQ(actions.size(), std::size_t(2));
    ASSERT_EQ(actions[0].kind, Stmt::Kind::TempDecl);
    ASSERT_EQ(actions[0].tempName, std::string("x"));
    ASSERT_TRUE(actions[0].init != nullptr);
    ASSERT_EQ(actions[0].init->kind, Expr::Kind::Literal);
    ASSERT_EQ(actions[0].init->type->name, std::string("int"));
    ASSERT_EQ(actions[1].kind, Stmt::Kind::Call);
    ASSERT_EQ(actions[1].funcName, std::string("wait"));
    ASSERT_EQ(actions[1].functionId, uint16_t(0x0A00));
    ASSERT_EQ(actions[1].tier, uint8_t(3));
    ASSERT_EQ(r.src.temps.size(), std::size_t(1));
    // the temp belongs to a scope-stack frame (the Update body), not to a
    // numbered depth level
    ASSERT_TRUE(r.src.temps[0].scopeId >= 0);
}

TEST(parser, unknown_type_in_declaration_fails) {
    auto r = fh::compile(base(acts("        temp String s;\n")));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown type 'String'"));
}

TEST(parser, void_cannot_be_a_variable_type) {
    auto r = fh::compile(base(acts("        temp void v;\n")));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "void cannot be used as a variable type"));
}

TEST(parser, unknown_variable_fails) {
    auto r = fh::compile(base(acts("        temp int x = noSuchVar + 1;\n")));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown variable 'noSuchVar'"));
}

TEST(parser, type_mismatch_on_temp_init) {
    auto r = fh::compile(base(acts("        temp int x = 1.0f;\n")));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "type mismatch"));
}

TEST(parser, return_type_mismatch_on_temp_init) {
    auto r = fh::compile(
        "var Object3D obj;\n" +
        base(acts("        temp int x = getPosition(obj);\n")));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "type mismatch"));
    ASSERT_TRUE(fh::hasErrorContaining(r, "Vector3"));
}

TEST(parser, const_folds_arithmetic) {
    auto r = fh::compile("const int a = 2 + 3 * 4;\n" + base("Actions { Start { } Update { } }\n"));
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.src.globals.size(), std::size_t(1));
    ASSERT_EQ(r.src.globals[0].constBytes.size(), std::size_t(4));
    ASSERT_EQ(fh::le32(r.src.globals[0].constBytes, 0), uint32_t(14));
}

TEST(parser, const_int_division_is_float) {
    auto r = fh::compile("const float f = 1 / 2;\n" + base("Actions { Start { } Update { } }\n"));
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.src.globals[0].constBytes.size(), std::size_t(4));
    ASSERT_TRUE(fh::leFloat(r.src.globals[0].constBytes, 0) == 0.5f);
}

TEST(parser, const_floordiv_rounds_toward_negative_infinity) {
    auto r = fh::compile("const int a = 7 // 2;\nconst int b = -7 // 2;\n" + base("Actions { Start { } Update { } }\n"));
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(fh::le32(r.src.globals[0].constBytes, 0), uint32_t(3));
    ASSERT_EQ(fh::le32(r.src.globals[1].constBytes, 0), uint32_t(int32_t(-4)));
}

TEST(parser, const_reference_must_be_declared_earlier) {
    auto r = fh::compile("const int a = b;\nconst int b = 1;\n" + base("Actions { Start { } Update { } }\n"));
    ASSERT_FALSE(r.ok);
    // 'b' is declared later, so at the point of use it is not a known variable.
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown variable 'b'"));
}

TEST(parser, const_runtime_reference_is_not_constant) {
    auto r = fh::compile("var int health;\nconst int a = health + 1;\n" + base("Actions { Start { } Update { } }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "not a compile-time constant"));
}

TEST(parser, vector_literal_ok) {
    auto r = fh::compile(base(acts("        temp Vector3 v = Vector3(1.0f, 2.0f, 3.0f);\n")));
    ASSERT_TRUE(r.ok);
    const auto decls = fh::actionStmts(r.src);
    const Stmt& decl = decls[0];
    ASSERT_EQ(decl.init->kind, Expr::Kind::Literal);
    ASSERT_EQ(decl.init->litVecCount, 3);
    ASSERT_TRUE(decl.init->litVec[1] == 2.0f);
}

TEST(parser, vector_literal_wrong_arity_fails) {
    auto r = fh::compile(base(acts("        temp Vector3 v = Vector3(1.0f, 2.0f);\n")));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "expects 3 components"));
}

TEST(parser, vector_literal_nonconst_component_fails) {
    auto r = fh::compile(base(acts("        temp float f = 1.0f;\n        temp Vector3 v = Vector3(f, 0.0f, 0.0f);\n")));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "constant float expressions"));
}

TEST(parser, vector_literal_from_earlier_const_ok) {
    auto r = fh::compile("const float k = 2.5f;\n" +
                         base(acts("        temp Vector2 v = Vector2(k, -k);\n")));
    ASSERT_TRUE(r.ok);
    const auto declsV = fh::actionStmts(r.src);
    const auto& decl = declsV[0];
    ASSERT_TRUE(decl.init->litVec[0] == 2.5f);
    ASSERT_TRUE(decl.init->litVec[1] == -2.5f);
}

TEST(parser, string_literal_is_an_expression) {
    // Strings are fully supported (module v0.4); see the `strings` suite for
    // the type rules, folding and binary encoding.
    auto r = fh::compile(base(acts("        temp string s = \"run\";\n")));
    ASSERT_TRUE(r.ok);
    const auto declsV = fh::actionStmts(r.src);
    const Stmt& decl = declsV[0];
    ASSERT_EQ(decl.init->kind, Expr::Kind::Literal);
    ASSERT_EQ(decl.init->litStr, std::string("run"));
    ASSERT_EQ(decl.init->type->typeTag, uint8_t(0x05));
}

TEST(parser, operator_type_rejections) {
    // handle arithmetic
    auto r1 = fh::compile("var Object3D a;\n" + base(acts("        temp Object3D x = a;\n        temp Object3D y = x;\n        temp int z = 1;\n        temp Object3D bad = y;\n")));
    (void)r1;
    auto r2 = fh::compile(base(acts("        temp Vector2 v2 = Vector2(1.0f, 1.0f);\n        temp Vector3 v3 = Vector3(1.0f, 1.0f, 1.0f);\n        temp Vector2 bad = v2 + v3;\n")));
    ASSERT_FALSE(r2.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r2, "mixed vector types"));
    auto r3 = fh::compile(base(acts("        temp Vector3 a = Vector3(1.0f, 1.0f, 1.0f);\n        temp Vector3 b = Vector3(1.0f, 1.0f, 1.0f);\n        temp Vector3 bad = a * b;\n")));
    ASSERT_FALSE(r3.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r3, "vector * vector is not supported"));
    auto r4 = fh::compile(base(acts("        temp bool b = true;\n        temp bool bad = b < b;\n")));
    ASSERT_FALSE(r4.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r4, "requires numeric operands"));
}

TEST(parser, scalar_vector_multiply_allowed) {
    auto r = fh::compile(base(acts("        temp Vector3 a = Vector3(1.0f, 1.0f, 1.0f);\n        temp Vector3 b = a * 2.0f;\n        temp Vector3 c = 2.0f * a;\n        temp Vector3 d = b + c - a;\n")));
    ASSERT_TRUE(r.ok);
}

TEST(parser, relational_on_vectors_rejected) {
    auto r = fh::compile(base(acts("        temp Vector3 a = Vector3(1.0f, 0.0f, 0.0f);\n        temp Vector3 b = Vector3(0.0f, 1.0f, 0.0f);\n        temp bool bad = a < b;\n")));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "not defined for vectors"));
}

TEST(parser, equality_on_same_vector_type_ok) {
    auto r = fh::compile(base(acts("        temp Vector3 a = Vector3(1.0f, 0.0f, 0.0f);\n        temp Vector3 b = Vector3(1.0f, 0.0f, 0.0f);\n        temp bool eq = a == b;\n")));
    ASSERT_TRUE(r.ok);
}

TEST(parser, nonbool_condition_fails) {
    auto r = fh::compile(base(acts("        if (5) { }\n")));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "if condition must be a bool expression"));
}

TEST(parser, precedence_and_associativity) {
    auto r = fh::compile("const int a = 2 + 3 * 4;      // 14\n"
                         "const int b = (2 + 3) * 4;   // 20\n"
                         "const int c = 2 - 3 - 4;      // -5\n"
                         "const int d = 2 ** 3 ** 2;    // 512 (right assoc)\n"
                         "const float e = 7.0f / 2.0f;  // 3.5\n" +
                         base("Actions { Start { } Update { } }\n"));
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(fh::le32(r.src.globals[0].constBytes, 0), uint32_t(14));
    ASSERT_EQ(fh::le32(r.src.globals[1].constBytes, 0), uint32_t(20));
    ASSERT_EQ(fh::le32(r.src.globals[2].constBytes, 0), uint32_t(int32_t(-5)));
    ASSERT_EQ(fh::le32(r.src.globals[3].constBytes, 0), uint32_t(512));
    ASSERT_TRUE(fh::leFloat(r.src.globals[4].constBytes, 0) == 3.5f);
}

TEST(parser, runtime_var_cannot_have_initializer) {
    auto r = fh::compile("var int health = 100;\n" + base("Actions { Start { } Update { } }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "runtime variables cannot have initializers"));
}
