#include "test_helpers.hpp"

#include <iomanip>
#include <sstream>
#include <string>

#include "binary_format.hpp"
#include "module_reader.hpp"

using namespace fsmc;

TEST(golden_bytes, minimal_fixture_compiles) {
    auto src = fh::readFixture("minimal.fsm");
    ASSERT_TRUE(src.find("<missing fixture") == std::string::npos);
    auto r = fh::compile(src, "minimal.fsm");
    ASSERT_TRUE(r.ok);
    ASSERT_TRUE(!r.module.empty());
}

TEST(golden_bytes, header_is_36_bytes_with_magic_version_and_offsets) {
    auto r = fh::compile(fh::readFixture("minimal.fsm"), "minimal.fsm");
    ASSERT_TRUE(r.ok);
    const auto& m = r.module;

    ASSERT_TRUE(m.size() >= fmt::kHeaderSize);
    // magic 0x46534D44 ("FSMD"), little-endian on disk
    ASSERT_EQ(m[0], uint8_t(0x44));
    ASSERT_EQ(m[1], uint8_t(0x4D));
    ASSERT_EQ(m[2], uint8_t(0x53));
    ASSERT_EQ(m[3], uint8_t(0x46));
    ASSERT_EQ(fh::le32(m, 0), uint32_t(fmt::kMagic));
    // version
    ASSERT_EQ(fh::le16(m, 4), uint16_t(fmt::kVersionMajor));
    ASSERT_EQ(fh::le16(m, 6), uint16_t(fmt::kVersionMinor));

    uint32_t offsets[8] = {};
    for (int i = 1; i <= 7; ++i) offsets[i] = fh::le32(m, 8 + 4 * (i - 1));

    // Every offset points at or after the header end and is monotone.
    for (int i = 1; i <= 7; ++i) {
        ASSERT_TRUE(offsets[i] >= fmt::kHeaderSize);
        ASSERT_TRUE(offsets[i] <= m.size());
        ASSERT_TRUE(offsets[i] >= offsets[i - 1]);
    }

    // Independent walk: recompute each section size from the entries themselves
    // and check it exactly fills the gap to the next section.
    ReadModule rm;
    std::string err;
    ASSERT_TRUE(readModule(m, rm, err));
    ASSERT_TRUE(validateModule(rm, err));
    ASSERT_EQ(rm.sectionOffsets[1], offsets[1]);
    ASSERT_EQ(rm.sectionOffsets[7], offsets[7]);

    uint32_t expect = fmt::kHeaderSize;
    ASSERT_EQ(rm.sectionOffsets[fmt::SecGlobal], expect);
    expect += 0; // globals: walk entries
    {
        uint32_t walk = rm.sectionOffsets[fmt::SecGlobal];
        for (const auto& g : rm.globals) walk += 4 + 1 + g.value.size() + 2 + g.name.size();
        ASSERT_EQ(walk, rm.sectionOffsets[fmt::SecRuntime]);
    }
    {
        uint32_t walk = rm.sectionOffsets[fmt::SecRuntime];
        for (const auto& g : rm.runtime) walk += 4 + 1 + 1 + 1 + 4 + 2 + g.name.size();
        ASSERT_EQ(walk, rm.sectionOffsets[fmt::SecTemp]);
    }
    {
        uint32_t walk = rm.sectionOffsets[fmt::SecTemp];
        for (const auto& g : rm.temps) walk += 4 + 1 + 1 + 2 + g.name.size();
        ASSERT_EQ(walk, rm.sectionOffsets[fmt::SecState]);
    }
    {
        uint32_t walk = rm.sectionOffsets[fmt::SecState] + 4;
        for (const auto& s : rm.states) walk += 4 + 2 + s.name.size();
        ASSERT_EQ(walk, rm.sectionOffsets[fmt::SecToken]);
    }
    {
        uint32_t walk = rm.sectionOffsets[fmt::SecToken];
        for (const auto& s : rm.stateInstrs) {
            walk += 4 + 2;
            for (const auto& in : s.instrs) walk += 1 + 1 + 4 * in.operands.size();
        }
        ASSERT_EQ(walk, rm.sectionOffsets[fmt::SecAst]);
    }
    {
        uint32_t walk = rm.sectionOffsets[fmt::SecAst];
        for (const auto& t : rm.ast) walk += 1 + 1 + 2 + 4 * t.children.size() + t.data.size();
        ASSERT_EQ(walk, rm.sectionOffsets[fmt::SecFsm]);
    }
    {
        uint32_t walk = rm.sectionOffsets[fmt::SecFsm] + 4;
        for (const auto& e : rm.fsm) walk += 4 + 2 + 4 * e.targets.size();
        ASSERT_EQ(walk, m.size());
    }

    // The eight header offsets agree with the reader's section offsets.
    ASSERT_EQ(offsets[fmt::SecGlobal], rm.sectionOffsets[fmt::SecGlobal]);
    ASSERT_EQ(offsets[fmt::SecRuntime], rm.sectionOffsets[fmt::SecRuntime]);
    ASSERT_EQ(offsets[fmt::SecTemp], rm.sectionOffsets[fmt::SecTemp]);
    ASSERT_EQ(offsets[fmt::SecState], rm.sectionOffsets[fmt::SecState]);
    ASSERT_EQ(offsets[fmt::SecToken], rm.sectionOffsets[fmt::SecToken]);
    ASSERT_EQ(offsets[fmt::SecAst], rm.sectionOffsets[fmt::SecAst]);
    ASSERT_EQ(offsets[fmt::SecFsm], rm.sectionOffsets[fmt::SecFsm]);
}

TEST(golden_bytes, minimal_module_contents) {
    auto r = fh::compile(fh::readFixture("minimal.fsm"), "minimal.fsm");
    ASSERT_TRUE(r.ok);
    ReadModule rm;
    std::string err;
    ASSERT_TRUE(readModule(r.module, rm, err));
    ASSERT_TRUE(validateModule(rm, err));

    ASSERT_EQ(rm.states.size(), std::size_t(1));
    ASSERT_EQ(rm.states[0].name, std::string("Idle"));
    ASSERT_EQ(rm.globals.size(), std::size_t(1));
    ASSERT_EQ(rm.globals[0].name, std::string("tickLimit"));
    ASSERT_EQ(rm.globals[0].tag, uint8_t(0x01)); // int
    ASSERT_EQ(rm.globals[0].value.size(), std::size_t(4));
    ASSERT_EQ(rm.runtime.size(), std::size_t(0));
    ASSERT_EQ(rm.temps.size(), std::size_t(2));
    ASSERT_EQ(rm.temps[0].name, std::string("tick"));
    ASSERT_EQ(rm.temps[0].depth, uint8_t(1)); // state-level temp
    ASSERT_EQ(rm.temps[1].name, std::string("waitTime"));
    ASSERT_EQ(rm.temps[1].depth, uint8_t(2)); // Actions-body temp
    ASSERT_EQ(rm.fsm.size(), std::size_t(1));
    ASSERT_EQ(rm.fsm[0].targets.size(), std::size_t(2));

    // Entry flag on the state token
    uint32_t off = 0;
    ASSERT_TRUE(rm.astIndexByAddress(rm.states[0].addr, off));
    std::size_t idx = 0;
    for (std::size_t k = 0; k < rm.astEntryOffsets.size(); ++k) {
        if (rm.astEntryOffsets[k] == off) { idx = k; break; }
    }
    ASSERT_EQ(rm.ast[idx].type, uint8_t(fmt::AstTok::State));
    ASSERT_EQ(rm.ast[idx].data[0], uint8_t(1)); // @ENTRY Idle
}

TEST(golden_bytes, two_state_module_contents) {
    auto r = fh::compile(fh::readFixture("two_state.fsm"), "two_state.fsm");
    ASSERT_TRUE(r.ok);
    ReadModule rm;
    std::string err;
    ASSERT_TRUE(readModule(r.module, rm, err));
    ASSERT_TRUE(validateModule(rm, err));
    ASSERT_EQ(rm.states.size(), std::size_t(2));
    ASSERT_EQ(rm.states[0].name, std::string("Chase"));
    ASSERT_EQ(rm.states[1].name, std::string("Flee"));
    // owner flags: agent (claimed by tier 3) = DSL, others EXTERNAL
    int agentSlot = -1;
    for (std::size_t i = 0; i < rm.runtime.size(); ++i) {
        if (rm.runtime[i].name == "agent") agentSlot = int(i);
    }
    ASSERT_TRUE(agentSlot >= 0);
    ASSERT_EQ(rm.runtime[static_cast<std::size_t>(agentSlot)].owner, uint8_t(fmt::OwnerDsl));
    ASSERT_EQ(rm.runtime[0].owner, uint8_t(fmt::OwnerExternal));
}

// Pinned golden bytes for tests/fixtures/minimal.fsm.
//
// Independently verified by hand: header offsets tile the file exactly;
// every self-address, AST root, variable, expression and state reference
// resolves; the entry flag, temp depths, instruction stream and FSM
// adjacency match the source. If this test fails, either the compiler
// output changed (review the diff before updating) or the fixture did.
TEST(golden_bytes, minimal_module_exact_bytes) {
    auto r = fh::compile(fh::readFixture("minimal.fsm"), "minimal.fsm");
    ASSERT_TRUE(r.ok);
    static const uint8_t kBytes[] = {
        0x44, 0x4d, 0x53, 0x46, 0x00, 0x00, 0x01, 0x00, 0x24, 0x00, 0x00, 0x00,
        0x38, 0x00, 0x00, 0x00, 0x38, 0x00, 0x00, 0x00, 0x54, 0x00, 0x00, 0x00,
        0x62, 0x00, 0x00, 0x00, 0x98, 0x00, 0x00, 0x00, 0x7f, 0x01, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x20, 0x01, 0x0a, 0x00, 0x00, 0x00, 0x09, 0x00, 0x74,
        0x69, 0x63, 0x6b, 0x4c, 0x69, 0x6d, 0x69, 0x74, 0x00, 0x00, 0x00, 0x60,
        0x01, 0x01, 0x04, 0x00, 0x74, 0x69, 0x63, 0x6b, 0x0c, 0x00, 0x00, 0x60,
        0x02, 0x02, 0x08, 0x00, 0x77, 0x61, 0x69, 0x74, 0x54, 0x69, 0x6d, 0x65,
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xc0, 0x04, 0x00, 0x49, 0x64,
        0x6c, 0x65, 0x04, 0x00, 0x00, 0x80, 0x06, 0x00, 0x02, 0x02, 0x00, 0x00,
        0x00, 0x60, 0x2a, 0x00, 0x00, 0xc0, 0x02, 0x02, 0x0c, 0x00, 0x00, 0x60,
        0x5c, 0x00, 0x00, 0xc0, 0x02, 0x02, 0x00, 0x00, 0x00, 0x60, 0x71, 0x00,
        0x00, 0xc0, 0x01, 0x01, 0x8f, 0x00, 0x00, 0xc0, 0x03, 0x01, 0x04, 0x00,
        0x00, 0x80, 0x03, 0x01, 0x04, 0x00, 0x00, 0x80, 0x01, 0x01, 0x04, 0x00,
        0x15, 0x00, 0x00, 0xc0, 0x1e, 0x00, 0x00, 0xc0, 0x33, 0x00, 0x00, 0xc0,
        0xa2, 0x00, 0x00, 0xc0, 0x01, 0x12, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00,
        0x00, 0x60, 0x13, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x60, 0x2a, 0x00,
        0x00, 0xc0, 0x22, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x02,
        0x02, 0x04, 0x00, 0x47, 0x00, 0x00, 0xc0, 0x50, 0x00, 0x00, 0xc0, 0x65,
        0x00, 0x00, 0xc0, 0x8f, 0x00, 0x00, 0xc0, 0x12, 0x02, 0x00, 0x00, 0x02,
        0x0c, 0x00, 0x00, 0x60, 0x13, 0x02, 0x00, 0x00, 0x0c, 0x00, 0x00, 0x60,
        0x5c, 0x00, 0x00, 0xc0, 0x22, 0x02, 0x00, 0x00, 0x02, 0xcd, 0xcc, 0xcc,
        0x3d, 0x13, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x60, 0x71, 0x00, 0x00,
        0xc0, 0x20, 0x02, 0x00, 0x00, 0x01, 0x7e, 0x00, 0x00, 0xc0, 0x86, 0x00,
        0x00, 0xc0, 0x23, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x60, 0x22, 0x02,
        0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x10, 0x02, 0x00, 0x00, 0x00,
        0x0a, 0x01, 0x9a, 0x00, 0x00, 0xc0, 0x23, 0x02, 0x00, 0x00, 0x0c, 0x00,
        0x00, 0x60, 0x03, 0x02, 0x02, 0x00, 0xae, 0x00, 0x00, 0xc0, 0xdf, 0x00,
        0x00, 0xc0, 0x04, 0x03, 0x01, 0x00, 0xd7, 0x00, 0x00, 0xc0, 0xba, 0x00,
        0x00, 0xc0, 0x20, 0x03, 0x00, 0x00, 0x0f, 0xc7, 0x00, 0x00, 0xc0, 0xcf,
        0x00, 0x00, 0xc0, 0x23, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x60, 0x23,
        0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x11, 0x03, 0x00, 0x00, 0x04,
        0x00, 0x00, 0x80, 0x11, 0x02, 0x00, 0x00, 0x04, 0x00, 0x00, 0x80, 0x01,
        0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x80, 0x02, 0x00, 0x04, 0x00, 0x00,
        0x80, 0x04, 0x00, 0x00, 0x80,
    };
    ASSERT_EQ(r.module.size(), std::size_t(sizeof(kBytes)));
    for (std::size_t i = 0; i < sizeof(kBytes); ++i) {
        if (r.module[i] != kBytes[i]) {
            std::ostringstream os;
            os << "byte mismatch at offset " << i << ": got "
               << std::hex << "0x" << std::setw(2) << std::setfill('0')
               << static_cast<int>(r.module[i]) << " expected "
               << "0x" << std::setw(2) << std::setfill('0')
               << static_cast<int>(kBytes[i]);
            ::ftest::fail(__FILE__, __LINE__, os.str());
            return;
        }
    }
}

TEST(golden_bytes, byte_identical_across_runs) {
    auto a = fh::compile(fh::readFixture("two_state.fsm"), "two_state.fsm");
    auto b = fh::compile(fh::readFixture("two_state.fsm"), "two_state.fsm");
    ASSERT_TRUE(a.ok && b.ok);
    ASSERT_TRUE(a.module == b.module);
}
