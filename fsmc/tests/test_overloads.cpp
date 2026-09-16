#include "test_helpers.hpp"

#include "module_reader.hpp"

using namespace fsmc;

namespace {
// Finds the id of the first statement-level call named `fn` in the first
// state's Actions of a successful compile.
int findCallId(const fh::CompileResult& r, const std::string& fn) {
    for (const StateBodyItem& item : r.src.states[0].items) {
        if (item.kind != StateBodyItem::Kind::Actions) continue;
        // calls live inside the Start{} / Update{} phase blocks; temps (and
        // nothing else) may sit directly in the Actions body
        for (const std::vector<Stmt>* list : {&item.stmts, &item.startStmts, &item.updateStmts}) {
            for (const Stmt& s : *list) {
                if (s.kind == Stmt::Kind::Call && s.funcName == fn) return int(s.functionId);
            }
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
        "        Start { }\n"
        "        Update {\n"
        "        goTo(navAgent, pos);\n"
        "        }\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(findCallId(r, "goTo"), int(0x060A)); // (NavMeshAgent, Vector3)
    // A Tier 3 driving call carries nothing but the resolved id, tier and
    // arguments: claim bindings are gone from the language and the module.
    const auto item = fh::actionStmts(r.src);
    ASSERT_EQ(item[0].tier, uint8_t(3));
    ASSERT_EQ(item[0].args.size(), std::size_t(2));
    ASSERT_EQ(item[0].args[0]->var.kind, VarKind::Runtime);
}

TEST(overloads, goTo_obj2d_obj2d_resolves) {
    auto r = fh::compile(
        "var Object2D obj2d;\n"
        "var Object2D target;\n"
        "State A {\n"
        "    Actions {\n"
        "        Start { }\n"
        "        Update {\n"
        "        goTo(obj2d, target);\n"
        "        }\n"
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
        "        Start { }\n"
        "        Update {\n"
        "        goTo(navAgent, 5);\n"
        "        }\n"
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
        "        Start { }\n"
        "        Update {\n"
        "        goTo(obj3d, 5);\n"
        "        }\n"
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
        "        Start { }\n"
        "        Update {\n"
        "        goTo(obj, pos);\n"
        "        }\n"
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
        "        Start { }\n"
        "        Update {\n"
        "        setPosition(obj, Vector2(1.0f, 2.0f));\n"
        "        }\n"
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
        "        Start { }\n"
        "        Update {\n"
        "        temp Vector3 a = getPosition(cam);\n"
        "        temp Vector3 b = getPosition(obj);\n"
        "        }\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    const auto stmts = fh::actionStmts(r.src);
    ASSERT_EQ(stmts[0].init->functionId, uint16_t(0x0500)); // Camera3D
    ASSERT_EQ(stmts[1].init->functionId, uint16_t(0x0100)); // Object3D
}

TEST(overloads, wrong_arity_fails_via_no_match) {
    auto r = fh::compile(
        "State A {\n"
        "    Actions {\n"
        "        Start { }\n"
        "        Update {\n"
        "        clamp(1.0f);\n"
        "        }\n"
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
        "        Start { }\n"
        "        Update {\n"
        "        foo(1);\n"
        "        }\n"
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
        "        Start { }\n"
        "        Update {\n"
        "        emit(eventId);\n"
        "        }\n"
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
        "        Start { }\n"
        "        Update {\n"
        "        goTo(getNearestOfTag(pos, 1, 10.0f), pos);\n"
        "        }\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(
        r, "Tier 3 function 'goTo' drives its first argument; that argument must be a "
           "runtime or temporary variable"));
}

TEST(overloads, tier3_wait_takes_a_literal_because_it_drives_no_object) {
    auto r = fh::compile(
        "State A {\n"
        "    Actions {\n"
        "        Start { }\n"
        "        Update {\n"
        "        wait(0.5f);\n"
        "        waitUntil(true);\n"
        "        }\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
}

TEST(overloads, tier3_temp_target_is_accepted) {
    // A temporary works just as well as a runtime var: the rule is "a
    // variable", not "a runtime variable" — and nothing is recorded about the
    // choice either way.
    auto r = fh::compile(
        "var Object3D target;\n"
        "var Object3D other;\n"
        "State A {\n"
        "    Actions {\n"
        "        Start { }\n"
        "        Update {\n"
        "        temp Object3D mover = other;\n"
        "        goTo(mover, target);\n"
        "        }\n"
        "    }\n"
        "    Traversals {\n"
        "        if (true) { goto A; }\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    // one CALL, resolved to the (Object3D, Object3D) overload
    ASSERT_EQ(findCallId(r, "goTo"), int(0x060D));
    ReadModule rm;
    std::string err;
    ASSERT_TRUE(readModule(r.module, rm, err));
    long calls = 0;
    for (const ReadStateInstrs& ss : rm.stateInstrs) {
        for (const ReadInstr& in : ss.instrs) {
            if (in.opcode == fmt::OpCall) ++calls;
            // the retired opcodes may never appear again
            ASSERT_TRUE(in.opcode != 0x14 && in.opcode != 0x15);
        }
    }
    ASSERT_EQ(calls, 1);
}

TEST(overloads, nested_call_argument_types_propagate) {
    auto r = fh::compile(
        "var Object3D obj;\n"
        "var PhysicsObject3D phys;\n"
        "State A {\n"
        "    Actions {\n"
        "        Start { }\n"
        "        Update {\n"
        "        temp Vector3 v = getVelocity(phys) * 2.0f;\n"
        "        setVelocity(phys, v);\n"
        "        }\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_TRUE(r.ok);
    const auto stmts = fh::actionStmts(r.src);
    ASSERT_EQ(stmts[1].functionId, uint16_t(0x0402)); // setVelocity(PhysicsObject3D, Vector3)
}
