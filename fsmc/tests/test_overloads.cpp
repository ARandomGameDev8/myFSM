#include "test_helpers.hpp"

using namespace fsmc;

namespace {
// Finds the id of the first statement-level call named `fn` in the first
// state's Actions of a successful compile.
int findCallId(const fh::CompileResult& r, const std::string& fn) {
    for (const StateBodyItem& item : r.src.states[0].items) {
        if (item.kind != StateBodyItem::Kind::Actions) continue;
        for (const Stmt& s : item.stmts) {
            if (s.kind == Stmt::Kind::Call && s.funcName == fn) return int(s.functionId);
        }
    }
    return -1;
}
} // namespace

TEST(overloads, goTo_nav_agent_vector3_resolves) {
    auto r = fh::compile(
        "var NavMeshAgent navAgent;\n"
        "var Vector3 pos;\n"
        "State A {\n"
        "    Actions {\n"
        "        goTo(navAgent, pos);\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(findCallId(r, "goTo"), int(0x060A)); // (NavMeshAgent, Vector3)
    // Tier 3 with claims: two claims bound to the runtime agent.
    const auto& item = r.src.states[0].items[0];
    ASSERT_EQ(item.stmts[0].claims.size(), std::size_t(2));
    ASSERT_EQ(item.stmts[0].claims[0].fieldIndex, uint8_t(0));
    ASSERT_EQ(item.stmts[0].claims[1].fieldIndex, uint8_t(1));
}

TEST(overloads, goTo_obj2d_obj2d_resolves) {
    auto r = fh::compile(
        "var Object2D obj2d;\n"
        "var Object2D target;\n"
        "State A {\n"
        "    Actions {\n"
        "        goTo(obj2d, target);\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(findCallId(r, "goTo"), int(0x060F)); // (Object2D, Object2D)
}

TEST(overloads, goTo_nav_agent_int_fails_with_candidates) {
    auto r = fh::compile(
        "var NavMeshAgent navAgent;\n"
        "State A {\n"
        "    Actions {\n"
        "        goTo(navAgent, 5);\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "no overload of 'goTo' matches arguments (NavMeshAgent, int)"));
    ASSERT_TRUE(fh::hasErrorContaining(r, "goTo(NavMeshAgent agent, Vector3 dest)"));
    ASSERT_TRUE(fh::hasErrorContaining(r, "goTo(Object2D agent, Object2D dest)"));
}

TEST(overloads, goTo_obj3d_int_fails) {
    auto r = fh::compile(
        "var Object3D obj3d;\n"
        "State A {\n"
        "    Actions {\n"
        "        goTo(obj3d, 5);\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "no overload of 'goTo' matches arguments (Object3D, int)"));
}

TEST(overloads, goTo_object3d_vector3_resolves) {
    auto r = fh::compile(
        "var Object3D obj;\n"
        "var Vector3 pos;\n"
        "State A {\n"
        "    Actions {\n"
        "        goTo(obj, pos);\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(findCallId(r, "goTo"), int(0x060C)); // (Object3D, Vector3)
}

TEST(overloads, setPosition_picks_2d_overload) {
    auto r = fh::compile(
        "var Object2D obj;\n"
        "State A {\n"
        "    Actions {\n"
        "        setPosition(obj, Vector2(1.0f, 2.0f));\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(findCallId(r, "setPosition"), int(0x0106));
}

TEST(overloads, getPosition_camera_vs_object) {
    auto r = fh::compile(
        "var Camera3D cam;\n"
        "var Object3D obj;\n"
        "State A {\n"
        "    Actions {\n"
        "        temp Vector3 a = getPosition(cam);\n"
        "        temp Vector3 b = getPosition(obj);\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    const auto& stmts = r.src.states[0].items[0].stmts;
    ASSERT_EQ(stmts[0].init->functionId, uint16_t(0x0500)); // Camera3D
    ASSERT_EQ(stmts[1].init->functionId, uint16_t(0x0100)); // Object3D
}

TEST(overloads, wrong_arity_fails_via_no_match) {
    auto r = fh::compile(
        "State A {\n"
        "    Actions {\n"
        "        clamp(1.0f);\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "no overload of 'clamp' matches arguments (float)"));
    ASSERT_TRUE(fh::hasErrorContaining(r, "clamp(float x, float lo, float hi)"));
}

TEST(overloads, unknown_function_fails) {
    auto r = fh::compile(
        "State A {\n"
        "    Actions {\n"
        "        foo(1);\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown function 'foo'"));
}

TEST(overloads, tier2_mutation_of_static_constant_fails) {
    auto r = fh::compile(
        "const int eventId = 42;\n"
        "State A {\n"
        "    Actions {\n"
        "        emit(eventId);\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "static constants cannot be the mutating argument of Tier 2 function 'emit'"));
}

TEST(overloads, tier3_first_argument_must_be_a_variable) {
    auto r = fh::compile(
        "var Object3D a;\n"
        "var Object3D b;\n"
        "var Vector3 pos;\n"
        "State A {\n"
        "    Actions {\n"
        "        goTo(getNearestOfTag(pos, 1, 10.0f), pos);\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "its first argument must be a runtime or temporary variable"));
}

TEST(overloads, nested_call_argument_types_propagate) {
    auto r = fh::compile(
        "var Object3D obj;\n"
        "var PhysicsObject3D phys;\n"
        "State A {\n"
        "    Actions {\n"
        "        temp Vector3 v = getVelocity(phys) * 2.0f;\n"
        "        setVelocity(phys, v);\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    const auto& stmts = r.src.states[0].items[0].stmts;
    ASSERT_EQ(stmts[1].functionId, uint16_t(0x0402)); // setVelocity(PhysicsObject3D, Vector3)
}
