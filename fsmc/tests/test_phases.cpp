// The Start{} / Update{} phase blocks (module format v0.5).
//
// Every Actions{} block must declare both phases, in that order, and all
// action logic lives inside them. `temp` declarations may still sit directly
// in the Actions body: they live for the whole state visit and are visible in
// both phases, while a temp declared inside one phase stays private to it.

#include "test_helpers.hpp"

#include <string>
#include <vector>

#include "disassembler.hpp"
#include "module_reader.hpp"

using namespace fsmc;

namespace {

// One state whose Actions body is exactly `body`; Traversals always self-loops
// through an if, so no dead-code warning is produced.
std::string stateWith(const std::string& body) {
    return "var bool flag;\n"
           "var Object3D o;\n"
           "State A {\n"
           "    Actions {\n" +
           body +
           "    }\n"
           "    Traversals {\n"
           "        if (flag) { goto A; }\n"
           "        goto A;\n"
           "    }\n"
           "}\n"
           "@ENTRY A\n";
}

bool parseAndValidate(const fh::CompileResult& r, ReadModule& mod, std::string& err) {
    return readModule(r.module, mod, err) && validateModule(mod, err);
}

// Index of the first AST token of `type`, or -1.
int firstTokenOfType(const ReadModule& m, fmt::AstTok type) {
    for (std::size_t i = 0; i < m.ast.size(); ++i) {
        if (m.ast[i].type == uint8_t(type)) return int(i);
    }
    return -1;
}

// Index of the AST token at `address`, or -1.
int tokenIndexAt(const ReadModule& m, uint32_t address) {
    uint32_t off = 0;
    if (!m.astIndexByAddress(address, off)) return -1;
    for (std::size_t k = 0; k < m.astEntryOffsets.size(); ++k) {
        if (m.astEntryOffsets[k] == off) return int(k);
    }
    return -1;
}

} // namespace

TEST(phases, both_phase_blocks_are_mandatory) {
    auto r = fh::compile(stateWith(""));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "Actions must contain a Start{} block"));
    ASSERT_TRUE(fh::hasErrorContaining(r, "Actions must contain an Update{} block"));
    // both diagnostics point at the Actions keyword itself
    ASSERT_TRUE(fh::hasErrorContaining(r, "it holds the statements that run once, when the state is entered"));
    ASSERT_TRUE(fh::hasErrorContaining(r, "it holds the statements that run every tick"));
}

TEST(phases, missing_update_block_is_an_error) {
    auto r = fh::compile(stateWith("        Start { }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "Actions must contain an Update{} block"));
    ASSERT_FALSE(fh::hasErrorContaining(r, "Actions must contain a Start{} block"));
}

TEST(phases, missing_start_block_is_an_error) {
    auto r = fh::compile(stateWith("        Update { }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "Actions must contain a Start{} block"));
    ASSERT_FALSE(fh::hasErrorContaining(r, "Actions must contain an Update{} block"));
}

TEST(phases, empty_phase_blocks_are_allowed) {
    auto r = fh::compile(stateWith("        Start { }\n        Update { }\n"));
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.errors, 0);
    ASSERT_EQ(r.warnings, 0);

    ReadModule mod;
    std::string err;
    ASSERT_TRUE(parseAndValidate(r, mod, err));
    // the phases contribute no instructions: only the two Traversals gotos run
    ASSERT_EQ(mod.stateInstrs.size(), std::size_t(1));
    ASSERT_EQ(mod.stateInstrs[0].instrs.size(), std::size_t(2));
}

TEST(phases, calls_are_rejected_directly_in_the_actions_body) {
    auto r = fh::compile(stateWith("        wait(1.0f);\n        Start { }\n        Update { }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(
        r, "action logic must be inside Start{} or Update{} (only 'temp' declarations may appear "
           "directly in the Actions body)"));
}

TEST(phases, assignments_are_rejected_directly_in_the_actions_body) {
    auto r = fh::compile(stateWith("        temp int x = 1;\n"
                                   "        x = 2;\n"
                                   "        Start { }\n"
                                   "        Update { }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "action logic must be inside Start{} or Update{}"));
}

TEST(phases, if_statements_are_rejected_directly_in_the_actions_body) {
    auto r = fh::compile(stateWith("        if (flag) { }\n        Start { }\n        Update { }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "action logic must be inside Start{} or Update{}"));
}

TEST(phases, goto_is_rejected_directly_in_the_actions_body) {
    // gotos are Traversals-only, and that older rule fires before the phase rule
    auto r = fh::compile(stateWith("        goto A;\n        Start { }\n        Update { }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "goto is only allowed inside Traversals"));
}

TEST(phases, temp_declarations_are_allowed_directly_in_the_actions_body) {
    auto r = fh::compile(stateWith("        temp int counter = 0;\n"
                                   "        Start { counter = 1; }\n"
                                   "        Update { counter = counter + 1; }\n"));
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.errors, 0);

    const StateBodyItem& item = r.src.states[0].items[0];
    ASSERT_EQ(item.kind, StateBodyItem::Kind::Actions);
    ASSERT_TRUE(item.hasStart);
    ASSERT_TRUE(item.hasUpdate);
    // the temp is a direct child of the Actions body, the statements are not
    ASSERT_EQ(item.stmts.size(), std::size_t(1));
    ASSERT_EQ(item.stmts[0].kind, Stmt::Kind::TempDecl);
    ASSERT_EQ(item.stmts[0].tempName, std::string("counter"));
    ASSERT_EQ(item.startStmts.size(), std::size_t(1));
    ASSERT_EQ(item.updateStmts.size(), std::size_t(1));
    // source order is preserved for the serializer: temp, Start, Update
    ASSERT_EQ(item.actionsChildren.size(), std::size_t(3));
    ASSERT_EQ(int(item.actionsChildren[0].kind), int(StateBodyItem::Child::Kind::Temp));
    ASSERT_EQ(item.actionsChildren[0].tempIndex, 0);
    ASSERT_EQ(int(item.actionsChildren[1].kind), int(StateBodyItem::Child::Kind::Start));
    ASSERT_EQ(int(item.actionsChildren[2].kind), int(StateBodyItem::Child::Kind::Update));
}

TEST(phases, start_must_come_before_update) {
    auto r = fh::compile(stateWith("        Update { }\n        Start { }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "Start{} must come before Update{} in the Actions body"));
}

TEST(phases, duplicate_start_block_is_rejected) {
    auto r = fh::compile(stateWith("        Start { }\n        Start { }\n        Update { }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "duplicate Start{} block in the Actions body"));
}

TEST(phases, duplicate_update_block_is_rejected) {
    auto r = fh::compile(stateWith("        Start { }\n        Update { }\n        Update { }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "duplicate Update{} block in the Actions body"));
}

TEST(phases, bare_block_in_the_actions_body_is_rejected) {
    auto r = fh::compile(stateWith("        Start { }\n        Update { }\n        { }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unexpected block"));
    // a single, precise diagnostic: no cascade about missing phase blocks
    ASSERT_EQ(r.errors, 1);
}

TEST(phases, start_and_update_are_reserved_words) {
    auto r = fh::compile("var int Start;\n" + stateWith("        Start { }\n        Update { }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "expected a runtime variable name but found 'Start'"));

    auto r2 = fh::compile("var int Update;\n" + stateWith("        Start { }\n        Update { }\n"));
    ASSERT_FALSE(r2.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r2, "expected a runtime variable name but found 'Update'"));
}

TEST(phases, a_start_temp_is_invisible_in_update) {
    auto r = fh::compile(stateWith("        Start { temp int s = 1; }\n"
                                   "        Update { s = 2; }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown variable 's'"));
}

TEST(phases, an_update_temp_is_invisible_in_start) {
    // Start{} is compiled first, so a temp declared later cannot be seen at all
    auto r = fh::compile(stateWith("        Start { u = 1; }\n"
                                   "        Update { temp int u = 0; }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown variable 'u'"));
}

TEST(phases, an_actions_body_temp_is_visible_in_both_phases) {
    auto r = fh::compile(stateWith("        temp float shared = 0.5f;\n"
                                   "        Start { shared = 1.0f; }\n"
                                   "        Update { shared = shared + 0.5f; }\n"));
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.errors, 0);
    ASSERT_EQ(r.src.temps.size(), std::size_t(1));
    ASSERT_EQ(r.src.temps[0].name, std::string("shared"));
}

TEST(phases, phase_blocks_nest_their_own_scope_frames) {
    // the same name may be reused: each phase opens a fresh frame
    auto r = fh::compile(stateWith("        Start { temp int x = 1; }\n"
                                   "        Update { temp int x = 2; }\n"));
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.errors, 0);
    ASSERT_EQ(r.src.temps.size(), std::size_t(2));
    ASSERT_TRUE(r.src.temps[0].scopeId != r.src.temps[1].scopeId);
}

TEST(phases, ast_gains_start_and_update_children_of_actions) {
    auto r = fh::compile(stateWith("        temp int counter = 0;\n"
                                   "        Start { counter = 1; }\n"
                                   "        Update { counter = counter + 1; }\n"));
    ASSERT_TRUE(r.ok);
    ReadModule mod;
    std::string err;
    ASSERT_TRUE(parseAndValidate(r, mod, err));

    const int actions = firstTokenOfType(mod, fmt::AstTok::Actions);
    ASSERT_TRUE(actions >= 0);
    const ReadAstToken& act = mod.ast[static_cast<std::size_t>(actions)];
    // four children: the TEMP_VAR_DECL and its initialising ASSIGN (both from
    // the Actions body), then START, then UPDATE
    ASSERT_EQ(act.children.size(), std::size_t(4));
    const int startIdx = tokenIndexAt(mod, act.children[2]);
    const int updateIdx = tokenIndexAt(mod, act.children[3]);
    ASSERT_TRUE(startIdx >= 0);
    ASSERT_TRUE(updateIdx >= 0);
    ASSERT_EQ(mod.ast[static_cast<std::size_t>(startIdx)].type, uint8_t(fmt::AstTok::Start));
    ASSERT_EQ(mod.ast[static_cast<std::size_t>(updateIdx)].type, uint8_t(fmt::AstTok::Update));
    // the container tokens carry no data of their own
    ASSERT_TRUE(mod.ast[static_cast<std::size_t>(startIdx)].data.empty());
    ASSERT_TRUE(mod.ast[static_cast<std::size_t>(updateIdx)].data.empty());
    // each phase owns its statements
    ASSERT_EQ(mod.ast[static_cast<std::size_t>(startIdx)].children.size(), std::size_t(1));
    ASSERT_EQ(mod.ast[static_cast<std::size_t>(updateIdx)].children.size(), std::size_t(1));
    ASSERT_EQ(uint8_t(fmt::AstTok::Start), uint8_t(0x07));
    ASSERT_EQ(uint8_t(fmt::AstTok::Update), uint8_t(0x08));
}

TEST(phases, an_empty_start_block_has_no_children_in_the_ast) {
    auto r = fh::compile(stateWith("        Start { }\n        Update { wait(1.0f); }\n"));
    ASSERT_TRUE(r.ok);
    ReadModule mod;
    std::string err;
    ASSERT_TRUE(parseAndValidate(r, mod, err));
    const int startIdx = firstTokenOfType(mod, fmt::AstTok::Start);
    const int updateIdx = firstTokenOfType(mod, fmt::AstTok::Update);
    ASSERT_TRUE(startIdx >= 0);
    ASSERT_TRUE(updateIdx >= 0);
    ASSERT_TRUE(mod.ast[static_cast<std::size_t>(startIdx)].children.empty());
    ASSERT_EQ(mod.ast[static_cast<std::size_t>(updateIdx)].children.size(), std::size_t(1));
}

TEST(phases, instructions_run_start_before_update) {
    auto r = fh::compile(stateWith("        Start { wait(1.0f); }\n"
                                   "        Update { wait(2.0f); }\n"));
    ASSERT_TRUE(r.ok);
    ReadModule mod;
    std::string err;
    ASSERT_TRUE(parseAndValidate(r, mod, err));
    ASSERT_EQ(mod.stateInstrs.size(), std::size_t(1));
    // two calls (Start then Update) followed by the two Traversals gotos
    ASSERT_EQ(mod.stateInstrs[0].instrs.size(), std::size_t(4));
    ASSERT_EQ(mod.stateInstrs[0].instrs[0].opcode, fmt::OpCall);
    ASSERT_EQ(mod.stateInstrs[0].instrs[1].opcode, fmt::OpCall);

    const std::string t = disassemble(mod, "phases.fsmb", true);
    const std::size_t first = t.find("wait(1f)");
    const std::size_t second = t.find("wait(2f)");
    ASSERT_TRUE(first != std::string::npos);
    ASSERT_TRUE(second != std::string::npos);
    ASSERT_TRUE(first < second); // source order == execution order
}

TEST(phases, disassembly_labels_the_phase_blocks) {
    auto r = fh::compile(fh::readFixture("two_state.fsm"), "two_state.fsm");
    ASSERT_TRUE(r.ok);
    ReadModule mod;
    std::string err;
    ASSERT_TRUE(parseAndValidate(r, mod, err));
    const std::string t = disassemble(mod, "two_state.fsmb", true);
    ASSERT_TRUE(t.find("START") != std::string::npos);
    ASSERT_TRUE(t.find("(runs once, on state entry)") != std::string::npos);
    ASSERT_TRUE(t.find("UPDATE") != std::string::npos);
    ASSERT_TRUE(t.find("(runs every tick)") != std::string::npos);
}

TEST(phases, error_fixtures_report_their_one_phase_diagnostic) {
    struct Case {
        const char* file;
        const char* message;
    };
    const Case cases[] = {
        {"errors/missing_start_block.fsm", "Actions must contain a Start{} block"},
        {"errors/logic_outside_phases.fsm", "action logic must be inside Start{} or Update{}"},
    };
    for (const Case& c : cases) {
        auto r = fh::compile(fh::readFixture(c.file), c.file);
        ASSERT_FALSE(r.ok);
        ASSERT_EQ(r.errors, 1); // one class per fixture, no cascade
        ASSERT_TRUE(fh::hasErrorContaining(r, c.message));
    }
}

TEST(phases, a_phase_block_may_not_be_nested_in_another_block) {
    // inside the other phase
    auto r = fh::compile(stateWith("        Start { Start { } }\n        Update { }\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_EQ(r.errors, 1); // one precise diagnostic, no stray-block cascade
    ASSERT_TRUE(fh::hasErrorContaining(
        r, "Start{} belongs directly in the Actions body, not inside another block"));

    // inside an if body in a phase
    auto r2 = fh::compile(stateWith("        Start { }\n"
                                    "        Update { if (flag) { Update { } } }\n"));
    ASSERT_FALSE(r2.ok);
    ASSERT_EQ(r2.errors, 1);
    ASSERT_TRUE(fh::hasErrorContaining(
        r2, "Update{} belongs directly in the Actions body, not inside another block"));
}

TEST(phases, a_temp_declared_after_a_phase_block_is_not_visible_in_it) {
    // C scoping is unchanged: visible from the declaration onward, so a temp
    // written after Update{} cannot be used inside it
    auto r = fh::compile(stateWith("        Start { }\n"
                                   "        Update { late = 1; }\n"
                                   "        temp int late = 5;\n"));
    ASSERT_FALSE(r.ok);
    ASSERT_TRUE(fh::hasErrorContaining(r, "unknown variable 'late'"));
}

TEST(phases, actions_body_children_keep_their_source_order) {
    // A temp may be declared between the phase blocks; the AST and the
    // instruction stream both keep source order (DESIGN.md §2.8).
    auto r = fh::compile(stateWith("        Start { wait(1.0f); }\n"
                                   "        temp int late = 5;\n"
                                   "        Update { wait(2.0f); }\n"));
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(r.errors, 0);

    const StateBodyItem& item = r.src.states[0].items[0];
    ASSERT_EQ(item.actionsChildren.size(), std::size_t(3));
    ASSERT_EQ(int(item.actionsChildren[0].kind), int(StateBodyItem::Child::Kind::Start));
    ASSERT_EQ(int(item.actionsChildren[1].kind), int(StateBodyItem::Child::Kind::Temp));
    ASSERT_EQ(int(item.actionsChildren[2].kind), int(StateBodyItem::Child::Kind::Update));

    ReadModule mod;
    std::string err;
    ASSERT_TRUE(parseAndValidate(r, mod, err));
    // CALL wait(1f) | ASSIGN late = 5 | CALL wait(2f) | two traversals gotos
    ASSERT_EQ(mod.stateInstrs[0].instrs.size(), std::size_t(5));
    ASSERT_EQ(mod.stateInstrs[0].instrs[0].opcode, fmt::OpCall);
    ASSERT_EQ(mod.stateInstrs[0].instrs[1].opcode, fmt::OpAssign);
    ASSERT_EQ(mod.stateInstrs[0].instrs[2].opcode, fmt::OpCall);

    // AST children of ACTIONS, in source order: START, TEMP + its ASSIGN, UPDATE
    const int actions = firstTokenOfType(mod, fmt::AstTok::Actions);
    ASSERT_TRUE(actions >= 0);
    const ReadAstToken& act = mod.ast[static_cast<std::size_t>(actions)];
    ASSERT_EQ(act.children.size(), std::size_t(4));
    const int startIdx = tokenIndexAt(mod, act.children[0]);
    const int tempIdx = tokenIndexAt(mod, act.children[1]);
    const int updateIdx = tokenIndexAt(mod, act.children[3]);
    ASSERT_EQ(mod.ast[static_cast<std::size_t>(startIdx)].type, uint8_t(fmt::AstTok::Start));
    ASSERT_EQ(mod.ast[static_cast<std::size_t>(tempIdx)].type, uint8_t(fmt::AstTok::TempVarDecl));
    ASSERT_EQ(mod.ast[static_cast<std::size_t>(updateIdx)].type, uint8_t(fmt::AstTok::Update));
    ASSERT_TRUE(startIdx < tempIdx);
    ASSERT_TRUE(tempIdx < updateIdx);
}

TEST(phases, a_module_that_lost_a_phase_block_fails_validation) {
    auto r = fh::compile(stateWith("        Start { wait(1.0f); }\n"
                                   "        Update { wait(2.0f); }\n"));
    ASSERT_TRUE(r.ok);
    ReadModule mod;
    std::string err;
    ASSERT_TRUE(parseAndValidate(r, mod, err));

    const int startIdx = firstTokenOfType(mod, fmt::AstTok::Start);
    ASSERT_TRUE(startIdx >= 0);
    const std::size_t off =
        mod.sectionOffsets[fmt::SecAst] + mod.astEntryOffsets[static_cast<std::size_t>(startIdx)];
    ASSERT_EQ(r.module[off], uint8_t(fmt::AstTok::Start)); // the type byte is first

    std::vector<uint8_t> bytes = r.module;
    bytes[off] = 0x09; // an unused token type: the START container is gone
    ReadModule broken;
    err.clear();
    ASSERT_TRUE(readModule(bytes, broken, err)); // the bytes still parse
    ASSERT_FALSE(validateModule(broken, err));   // ... but break the phase invariant
    ASSERT_TRUE(err.find("ACTIONS token is missing its START/UPDATE phase blocks") !=
                std::string::npos);
}

TEST(phases, every_fixture_declares_both_phases) {
    for (const char* name : {"minimal.fsm", "two_state.fsm", "strings.fsm"}) {
        auto r = fh::compile(fh::readFixture(name), name);
        ASSERT_TRUE(r.ok);
        ASSERT_EQ(r.errors, 0);
        std::size_t actionsBlocks = 0;
        for (const StateDef& st : r.src.states) {
            for (const StateBodyItem& item : st.items) {
                if (item.kind != StateBodyItem::Kind::Actions) continue;
                ++actionsBlocks;
                ASSERT_TRUE(item.hasStart);
                ASSERT_TRUE(item.hasUpdate);
                // the phase blocks are serialized as AST children, in order
                bool seenStart = false;
                bool ordered = true;
                for (const StateBodyItem::Child& c : item.actionsChildren) {
                    if (c.kind == StateBodyItem::Child::Kind::Start) seenStart = true;
                    if (c.kind == StateBodyItem::Child::Kind::Update && !seenStart) ordered = false;
                }
                ASSERT_TRUE(seenStart);
                ASSERT_TRUE(ordered);
            }
        }
        ASSERT_TRUE(actionsBlocks >= 1);
    }
}
