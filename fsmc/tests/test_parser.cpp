#include "test_helpers.hpp"

using namespace fsmc;

namespace {
std::string base(const std::string& stateBody) {
    return std::string("State A {\n") + stateBody + "    Traversals {\n        goto A;\n    }\n}\n\n@ENTRY A\n";
}
} // namespace

TEST(parser, minimal_one_state_ast_shape) {
    auto r = fh::compile(
        "State A {\n"
        "    Actions {\n"
        "        temp int x = 1;\n"
        "        wait(0.5f);\n"
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
    const auto& actions = r.src.states[0].items[0].stmts;
    ASSERT_EQ(actions.size(), std::size_t(2));
    ASSERT_EQ(actions[0].kind, Stmt::Kind::TempDecl);
    ASSERT_EQ(actions[0].tempName, std::string("x"));
    ASSERT_EQ(actions[0].depth, uint8_t(2));
    ASSERT_TRUE(actions[0].init != nullptr);
    ASSERT_EQ(actions[0].init->kind, Expr::Kind::Literal);
    ASSERT_EQ(actions[0].init->type->name, std::string("int"));
    ASSERT_EQ(actions[1].kind, Stmt::Kind::Call);
    ASSERT_EQ(actions[1].funcName, std::string("wait"));
    ASSERT_EQ(actions[1].functionId, uint16_t(0x0A00));
    ASSERT_EQ(actions[1].tier, uint8_t(3));
    ASSERT_EQ(r.src.temps.size(), std::size_t(1));
    ASSERT_EQ(r.src.temps[0].depth, uint8_t(2));
}

TEST(parser, unknown_type_in_declaration_fails) {
    auto r = fh::compile(base("Actions {\n        temp String s;\n    }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown type 'String'"));
}

TEST(parser, void_cannot_be_a_variable_type) {
    auto r = fh::compile(base("Actions {\n        temp void v;\n    }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "void cannot be used as a variable type"));
}

TEST(parser, unknown_variable_fails) {
    auto r = fh::compile(base("Actions {\n        temp int x = noSuchVar + 1;\n    }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown variable 'noSuchVar'"));
}

TEST(parser, type_mismatch_on_temp_init) {
    auto r = fh::compile(base("Actions {\n        temp int x = 1.0f;\n    }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "type mismatch"));
}

TEST(parser, return_type_mismatch_on_temp_init) {
    auto r = fh::compile(
        "var Object3D obj;\n" +
        base("Actions {\n        temp int x = getPosition(obj);\n    }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "type mismatch"));
    ASSERT_TRUE(fh::hasErrorContaining(r, "Vector3"));
}

TEST(parser, const_folds_arithmetic) {
    auto r = fh::compile("const int a = 2 + 3 * 4;\n" + base("Actions { }\n"));
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.src.globals.size(), std::size_t(1));
    ASSERT_EQ(r.src.globals[0].constBytes.size(), std::size_t(4));
    ASSERT_EQ(fh::le32(r.src.globals[0].constBytes, 0), uint32_t(14));
}

TEST(parser, const_int_division_is_float) {
    auto r = fh::compile("const float f = 1 / 2;\n" + base("Actions { }\n"));
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.src.globals[0].constBytes.size(), std::size_t(4));
    ASSERT_TRUE(fh::leFloat(r.src.globals[0].constBytes, 0) == 0.5f);
}

TEST(parser, const_floordiv_rounds_toward_negative_infinity) {
    auto r = fh::compile("const int a = 7 // 2;\nconst int b = -7 // 2;\n" + base("Actions { }\n"));
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(fh::le32(r.src.globals[0].constBytes, 0), uint32_t(3));
    ASSERT_EQ(fh::le32(r.src.globals[1].constBytes, 0), uint32_t(int32_t(-4)));
}

TEST(parser, const_reference_must_be_declared_earlier) {
    auto r = fh::compile("const int a = b;\nconst int b = 1;\n" + base("Actions { }\n"));
    ASSERT_FALSE(r.ok);
    // 'b' is declared later, so at the point of use it is not a known variable.
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown variable 'b'"));
}

TEST(parser, const_runtime_reference_is_not_constant) {
    auto r = fh::compile("var int health;\nconst int a = health + 1;\n" + base("Actions { }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "not a compile-time constant"));
}

TEST(parser, vector_literal_ok) {
    auto r = fh::compile(base("Actions {\n        temp Vector3 v = Vector3(1.0f, 2.0f, 3.0f);\n    }\n"));
    ASSERT_TRUE(r.ok);
    const auto& decl = r.src.states[0].items[0].stmts[0];
    ASSERT_EQ(decl.init->kind, Expr::Kind::Literal);
    ASSERT_EQ(decl.init->litVecCount, 3);
    ASSERT_TRUE(decl.init->litVec[1] == 2.0f);
}

TEST(parser, vector_literal_wrong_arity_fails) {
    auto r = fh::compile(base("Actions {\n        temp Vector3 v = Vector3(1.0f, 2.0f);\n    }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "expects 3 components"));
}

TEST(parser, vector_literal_nonconst_component_fails) {
    auto r = fh::compile(base("Actions {\n        temp float f = 1.0f;\n        temp Vector3 v = Vector3(f, 0.0f, 0.0f);\n    }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "constant float expressions"));
}

TEST(parser, vector_literal_from_earlier_const_ok) {
    auto r = fh::compile("const float k = 2.5f;\n" +
                         base("Actions {\n        temp Vector2 v = Vector2(k, -k);\n    }\n"));
    ASSERT_TRUE(r.ok);
    const auto& decl = r.src.states[0].items[0].stmts[0];
    ASSERT_TRUE(decl.init->litVec[0] == 2.5f);
    ASSERT_TRUE(decl.init->litVec[1] == -2.5f);
}

TEST(parser, string_literal_in_expression_fails) {
    auto r = fh::compile("var AnimationController3D ctrl;\n" +
                         base("Actions {\n        setAnimation(ctrl, \"run\");\n    }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "string literals are not supported"));
}

TEST(parser, operator_type_rejections) {
    // handle arithmetic
    auto r1 = fh::compile("var Object3D a;\n" + base("Actions {\n        temp Object3D x = a;\n        temp Object3D y = x;\n        temp int z = 1;\n        temp Object3D bad = y;\n    }\n"));
    (void)r1;
    auto r2 = fh::compile(base("Actions {\n        temp Vector2 v2 = Vector2(1.0f, 1.0f);\n        temp Vector3 v3 = Vector3(1.0f, 1.0f, 1.0f);\n        temp Vector2 bad = v2 + v3;\n    }\n"));
    ASSERT_FALSE(r2.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r2, "mixed vector types"));
    auto r3 = fh::compile(base("Actions {\n        temp Vector3 a = Vector3(1.0f, 1.0f, 1.0f);\n        temp Vector3 b = Vector3(1.0f, 1.0f, 1.0f);\n        temp Vector3 bad = a * b;\n    }\n"));
    ASSERT_FALSE(r3.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r3, "vector * vector is not supported"));
    auto r4 = fh::compile(base("Actions {\n        temp bool b = true;\n        temp bool bad = b < b;\n    }\n"));
    ASSERT_FALSE(r4.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r4, "requires numeric operands"));
}

TEST(parser, scalar_vector_multiply_allowed) {
    auto r = fh::compile(base("Actions {\n        temp Vector3 a = Vector3(1.0f, 1.0f, 1.0f);\n        temp Vector3 b = a * 2.0f;\n        temp Vector3 c = 2.0f * a;\n        temp Vector3 d = b + c - a;\n    }\n"));
    ASSERT_TRUE(r.ok);
}

TEST(parser, relational_on_vectors_rejected) {
    auto r = fh::compile(base("Actions {\n        temp Vector3 a = Vector3(1.0f, 0.0f, 0.0f);\n        temp Vector3 b = Vector3(0.0f, 1.0f, 0.0f);\n        temp bool bad = a < b;\n    }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "not defined for vectors"));
}

TEST(parser, equality_on_same_vector_type_ok) {
    auto r = fh::compile(base("Actions {\n        temp Vector3 a = Vector3(1.0f, 0.0f, 0.0f);\n        temp Vector3 b = Vector3(1.0f, 0.0f, 0.0f);\n        temp bool eq = a == b;\n    }\n"));
    ASSERT_TRUE(r.ok);
}

TEST(parser, nonbool_condition_fails) {
    auto r = fh::compile(base("Actions {\n        if (5) { }\n    }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "if condition must be a bool expression"));
}

TEST(parser, precedence_and_associativity) {
    auto r = fh::compile("const int a = 2 + 3 * 4;      // 14\n"
                         "const int b = (2 + 3) * 4;   // 20\n"
                         "const int c = 2 - 3 - 4;      // -5\n"
                         "const int d = 2 ** 3 ** 2;    // 512 (right assoc)\n"
                         "const float e = 7.0f / 2.0f;  // 3.5\n" +
                         base("Actions { }\n"));
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(fh::le32(r.src.globals[0].constBytes, 0), uint32_t(14));
    ASSERT_EQ(fh::le32(r.src.globals[1].constBytes, 0), uint32_t(20));
    ASSERT_EQ(fh::le32(r.src.globals[2].constBytes, 0), uint32_t(int32_t(-5)));
    ASSERT_EQ(fh::le32(r.src.globals[3].constBytes, 0), uint32_t(512));
    ASSERT_TRUE(fh::leFloat(r.src.globals[4].constBytes, 0) == 3.5f);
}

TEST(parser, runtime_var_cannot_have_initializer) {
    auto r = fh::compile("var int health = 100;\n" + base("Actions { }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "runtime variables cannot have initializers"));
}
