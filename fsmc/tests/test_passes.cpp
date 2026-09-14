#include "test_helpers.hpp"

#include "builtin_functions.hpp"
#include "module_reader.hpp"

using namespace fsmc;

namespace {
const char* kTwoState =
    "const float chaseRange = 10.0f;\n"
    "var Object3D player;\n"
    "var Object3D enemy;\n"
    "var NavMeshAgent agent;\n"
    "var bool enemyVisible;\n"
    "\n"
    "State Chase {\n"
    "    temp float lastDist = 99.0f;\n"
    "    Actions {\n"
    "        temp float dist = getDistanceTo(player, enemy);\n"
    "        lastDist = dist;\n"
    "        if (enemyVisible) {\n"
    "            temp Vector3 dir = normalize(directionTo(player, enemy));\n"
    "            moveTowards(agent, dir, 5.0f);\n"
    "        } else if (lastDist < 3.0f) {\n"
    "            followTarget(agent, enemy);\n"
    "        } else {\n"
    "            stopMovement(agent);\n"
    "        }\n"
    "    }\n"
    "    Traversals {\n"
    "        if (lastDist < 1.0f) { goto Flee; }\n"
    "        if (!enemyVisible) { goto Flee; }\n"
    "        goto Chase;\n"
    "    }\n"
    "}\n"
    "\n"
    "State Flee {\n"
    "    Actions {\n"
    "        temp Vector3 away = getFleeDirection(getPosition(player), getPosition(enemy));\n"
    "        moveTowards(agent, away, 8.0f);\n"
    "    }\n"
    "    Traversals {\n"
    "        if (!enemyVisible) { goto Chase; }\n"
    "        goto Flee;\n"
    "    }\n"
    "}\n"
    "@ENTRY Chase\n";

// Index of the AST token whose children contain token #i (-1 for a root).
std::vector<int> astParents(const ReadModule& m) {
    std::vector<int> parent(m.ast.size(), -1);
    for (std::size_t i = 0; i < m.ast.size(); ++i) {
        for (uint32_t c : m.ast[i].children) {
            uint32_t off = 0;
            if (!m.astIndexByAddress(c, off)) continue;
            for (std::size_t k = 0; k < m.astEntryOffsets.size(); ++k) {
                if (m.astEntryOffsets[k] == off) { parent[k] = int(i); break; }
            }
        }
    }
    return parent;
}

// Index of the TEMP_VAR_DECL token that declares temporary #tempIndex.
int tempDeclToken(const ReadModule& m, std::size_t tempIndex) {
    const uint32_t want = m.tempEntryOffsets[tempIndex];
    for (std::size_t t = 0; t < m.ast.size(); ++t) {
        const ReadAstToken& tok = m.ast[t];
        if (tok.type != uint8_t(fmt::AstTok::TempVarDecl) || tok.data.size() != 5) continue;
        const uint32_t va = fh::le32(tok.data, 1);
        if (fmt::addressSection(va) == fmt::SecTemp && fmt::addressOffset(va) == want)
            return int(t);
    }
    return -1;
}
} // namespace

TEST(passes, two_state_fixture_compiles) {
    auto r = fh::compile(kTwoState);
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.src.states.size(), std::size_t(2));
    ASSERT_TRUE(r.src.states[0].isEntry);
    ASSERT_FALSE(r.src.states[1].isEntry);
}

TEST(passes, pass3_collects_gotos_in_order) {
    auto r = fh::compile(kTwoState);
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.gotos.targets.size(), std::size_t(2));
    ASSERT_EQ(r.gotos.targets[0].size(), std::size_t(3));
    ASSERT_EQ(r.gotos.targets[0][0], std::string("Flee"));
    ASSERT_EQ(r.gotos.targets[0][1], std::string("Flee"));
    ASSERT_EQ(r.gotos.targets[0][2], std::string("Chase"));
    ASSERT_EQ(r.gotos.targets[1].size(), std::size_t(2));
    ASSERT_EQ(r.gotos.targets[1][0], std::string("Chase"));
    ASSERT_EQ(r.gotos.targets[1][1], std::string("Flee"));
}

TEST(passes, pass4_builds_adjacency) {
    auto r = fh::compile(kTwoState);
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.fsm.adjacency[0].size(), std::size_t(3));
    ASSERT_EQ(r.fsm.adjacency[0][0], 1);
    ASSERT_EQ(r.fsm.adjacency[0][1], 1);
    ASSERT_EQ(r.fsm.adjacency[0][2], 0);
    ASSERT_EQ(r.fsm.adjacency[1].size(), std::size_t(2));
    ASSERT_EQ(r.fsm.adjacency[1][0], 0);
    ASSERT_EQ(r.fsm.adjacency[1][1], 1);
}

TEST(passes, unknown_goto_target_fails) {
    auto r = fh::compile(
        "State A {\n"
        "    Actions {\n"
        "    }\n"
        "    Traversals {\n"
        "        goto Nope;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown state 'Nope' in goto"));
}

TEST(passes, exactly_one_entry_enforced) {
    auto two = fh::compile(
        "State A {\n    Actions { }\n    Traversals { goto A; }\n}\n"
        "@ENTRY A\n@ENTRY A\n");
    ASSERT_FALSE(two.ok);
    ASSERT_TRUE(fh::hasErrorContaining(two, "duplicate @ENTRY"));

    auto zero = fh::compile(
        "State A {\n    Actions { }\n    Traversals { goto A; }\n}\n");
    ASSERT_FALSE(zero.ok);
    ASSERT_TRUE(fh::hasErrorContaining(zero, "missing @ENTRY"));
}

TEST(passes, entry_must_name_declared_state) {
    auto r = fh::compile(
        "State A {\n    Actions { }\n    Traversals { goto A; }\n}\n"
        "@ENTRY B\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "@ENTRY must name a declared state: 'B'"));
}

TEST(passes, entry_may_appear_before_states) {
    auto r = fh::compile(
        "@ENTRY A\n"
        "State A {\n    Actions { }\n    Traversals { goto A; }\n}\n");
    ASSERT_TRUE(r.ok);
    ASSERT_TRUE(r.src.states[0].isEntry);
}

TEST(passes, duplicate_state_fails) {
    auto r = fh::compile(
        "State A {\n    Actions { }\n    Traversals { goto A; }\n}\n"
        "State A {\n    Actions { }\n    Traversals { goto A; }\n}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "duplicate state 'A'"));
}

TEST(passes, duplicate_global_fails) {
    auto r = fh::compile(
        "var int health;\nvar int health;\n"
        "State A {\n    Actions { }\n    Traversals { goto A; }\n}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "duplicate global variable 'health'"));
}

TEST(passes, state_needs_actions_or_traversals) {
    auto r = fh::compile(
        "State A {\n    temp int x = 1;\n}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "must contain an Actions or Traversals block"));
}

TEST(passes, bare_block_fails) {
    auto r = fh::compile(
        "State A {\n"
        "    Actions {\n"
        "        temp int x = 1;\n"
        "        {\n"
        "        }\n"
        "    }\n"
        "    Traversals {\n"
        "        goto A;\n"
        "    }\n"
        "}\n"
        "@ENTRY A\n");
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unexpected block"));
}

TEST(passes, nested_conditional_fails_else_if_chain_ok) {
    auto nested = fh::compile(
        "var bool a;\nvar bool b;\n"
        "State S {\n"
        "    Actions {\n"
        "        if (a) {\n"
        "            if (b) {\n"
        "            }\n"
        "        }\n"
        "    }\n"
        "    Traversals {\n"
        "        goto S;\n"
        "    }\n"
        "}\n"
        "@ENTRY S\n");
    ASSERT_FALSE(nested.ok);
    ASSERT_TRUE(fh::hasErrorContaining(nested, "nested conditionals are not allowed inside conditionals"));

    auto chain = fh::compile(
        "var bool a;\nvar bool b;\nvar bool c;\n"
        "State S {\n"
        "    Actions {\n"
        "        if (a) {\n"
        "        } else if (b) {\n"
        "        } else if (c) {\n"
        "        } else {\n"
        "        }\n"
        "    }\n"
        "    Traversals {\n"
        "        goto S;\n"
        "    }\n"
        "}\n"
        "@ENTRY S\n");
    ASSERT_TRUE(chain.ok);
    ASSERT_EQ(chain.src.states[0].items[0].stmts.size(), std::size_t(4));
    ASSERT_EQ(chain.src.states[0].items[0].stmts[0].kind, Stmt::Kind::If);
    ASSERT_EQ(chain.src.states[0].items[0].stmts[1].kind, Stmt::Kind::ElseIf);
    ASSERT_EQ(chain.src.states[0].items[0].stmts[2].kind, Stmt::Kind::ElseIf);
    ASSERT_EQ(chain.src.states[0].items[0].stmts[3].kind, Stmt::Kind::Else);
}

TEST(passes, sibling_if_chains_are_ok) {
    auto r = fh::compile(
        "var bool a;\nvar bool b;\n"
        "State S {\n"
        "    Actions {\n"
        "        if (a) {\n"
        "        }\n"
        "        if (b) {\n"
        "        }\n"
        "    }\n"
        "    Traversals {\n"
        "        goto S;\n"
        "    }\n"
        "}\n"
        "@ENTRY S\n");
    ASSERT_TRUE(r.ok);
}

TEST(passes, tier3_claim_promotes_runtime_owner_to_dsl) {
    auto r = fh::compile(kTwoState);
    ASSERT_TRUE(r.ok);
    // 'agent' is a runtime var claimed by moveTowards/followTarget/stopMovement
    int runtimeIdx = -1, slot = 0;
    for (std::size_t i = 0; i < r.src.globals.size(); ++i) {
        if (!r.src.globals[i].isConst) {
            if (r.src.globals[i].name == "agent") runtimeIdx = slot;
            ++slot;
        }
    }
    ASSERT_TRUE(runtimeIdx >= 0);
    ASSERT_EQ(r.forest.runtimeOwner[static_cast<std::size_t>(runtimeIdx)],
              uint8_t(fmt::OwnerDsl));
    // 'player' is never claimed -> stays EXTERNAL
    runtimeIdx = -1; slot = 0;
    for (std::size_t i = 0; i < r.src.globals.size(); ++i) {
        if (!r.src.globals[i].isConst) {
            if (r.src.globals[i].name == "player") runtimeIdx = slot;
            ++slot;
        }
    }
    ASSERT_EQ(r.forest.runtimeOwner[static_cast<std::size_t>(runtimeIdx)],
              uint8_t(fmt::OwnerExternal));
}

TEST(passes, round_trip_read_back_and_compare) {
    auto r = fh::compile(kTwoState);
    ASSERT_TRUE(r.ok);
    ReadModule m;
    std::string err;
    ASSERT_TRUE(readModule(r.module, m, err));
    ASSERT_TRUE(validateModule(m, err));

    // states
    ASSERT_EQ(m.states.size(), std::size_t(2));
    ASSERT_EQ(m.states[0].name, std::string("Chase"));
    ASSERT_EQ(m.states[1].name, std::string("Flee"));
    // globals
    ASSERT_EQ(m.globals.size(), std::size_t(1));
    ASSERT_EQ(m.globals[0].name, std::string("chaseRange"));
    ASSERT_EQ(m.globals[0].tag, uint8_t(0x02)); // float
    // runtime
    ASSERT_EQ(m.runtime.size(), std::size_t(4));
    ASSERT_EQ(m.runtime[0].name, std::string("player"));
    ASSERT_EQ(m.runtime[0].bindingSlot, uint32_t(0));
    ASSERT_EQ(m.runtime[3].name, std::string("enemyVisible"));
    ASSERT_EQ(m.runtime[3].bindingSlot, uint32_t(3));
    // temps in canonical order: Chase has lastDist, dist, dir; Flee has away.
    // No depth field is stored: which block owns a temp is structural (see
    // temp_owning_block_is_structural_in_the_ast below).
    ASSERT_EQ(m.temps.size(), std::size_t(4));
    ASSERT_EQ(m.temps[0].name, std::string("lastDist"));
    ASSERT_EQ(m.temps[1].name, std::string("dist"));
    ASSERT_EQ(m.temps[2].name, std::string("dir"));
    ASSERT_EQ(m.temps[3].name, std::string("away"));
    // fsm
    ASSERT_EQ(m.fsm.size(), std::size_t(2));
    ASSERT_EQ(m.fsm[0].targets.size(), std::size_t(3));
    // instruction frames exist for both states
    ASSERT_EQ(m.stateInstrs.size(), std::size_t(2));
    ASSERT_TRUE(m.stateInstrs[0].instrs.size() >= 6); // claims+call+release x3 + assign
    // every AST root is a STATE token and the entry flag matches
    for (std::size_t i = 0; i < m.states.size(); ++i) {
        uint32_t off = 0;
        ASSERT_TRUE(m.astIndexByAddress(m.states[i].addr, off));
        std::size_t idx = 0;
        for (std::size_t k = 0; k < m.astEntryOffsets.size(); ++k) {
            if (m.astEntryOffsets[k] == off) { idx = k; break; }
        }
        ASSERT_EQ(m.ast[idx].type, uint8_t(fmt::AstTok::State));
        ASSERT_EQ(m.ast[idx].data[0], uint8_t(i == 0 ? 1 : 0));
    }
}

// Temporary-variable ownership after the removal of the fixed depth levels:
// a temp belongs to the block whose AST token is the *parent* of its
// TEMP_VAR_DECL token. lastDist -> State body, dist -> Actions body,
// dir -> the if body, away -> Flee's Actions body.
TEST(passes, temp_owning_block_is_structural_in_the_ast) {
    auto r = fh::compile(kTwoState);
    ASSERT_TRUE(r.ok);
    ReadModule m;
    std::string err;
    ASSERT_TRUE(readModule(r.module, m, err));
    ASSERT_TRUE(validateModule(m, err));

    const std::vector<int> parents = astParents(m);
    const uint8_t expectParent[4] = {
        uint8_t(fmt::AstTok::State),   // lastDist: State Chase { ... }
        uint8_t(fmt::AstTok::Actions), // dist:     Actions { ... }
        uint8_t(fmt::AstTok::If),      // dir:      if (enemyVisible) { ... }
        uint8_t(fmt::AstTok::Actions), // away:     State Flee / Actions { ... }
    };
    ASSERT_EQ(m.temps.size(), std::size_t(4));
    for (std::size_t i = 0; i < m.temps.size(); ++i) {
        const int decl = tempDeclToken(m, i);
        ASSERT_TRUE(decl >= 0);
        const int par = parents[static_cast<std::size_t>(decl)];
        ASSERT_TRUE(par >= 0);
        ASSERT_EQ(m.ast[static_cast<std::size_t>(par)].type, expectParent[i]);
    }
}

TEST(passes, pass6_rejects_corrupted_module) {
    auto r = fh::compile(kTwoState);
    ASSERT_TRUE(r.ok);
    auto broken = r.module;
    // corrupt a GOTO target address: flip a byte in the AST section
    uint32_t astOff = broken[36 + 4 + 4 + 4 + 4 + 4]; // header: global, runtime, temp, state, token, ast...
    (void)astOff;
    // simpler: corrupt the magic
    broken[0] ^= 0xFF;
    ReadModule m;
    std::string err;
    ASSERT_FALSE(readModule(broken, m, err));
}

TEST(passes, module_is_deterministic) {
    auto a = fh::compile(kTwoState);
    auto b = fh::compile(kTwoState);
    ASSERT_TRUE(a.ok && b.ok);
    ASSERT_TRUE(a.module == b.module);
}
