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
        // Runtime entry: [4] self-addr [1] type tag [4] binding slot
        // [2] name len [·] name — no owner byte, no dirty byte (v0.3)
        uint32_t walk = rm.sectionOffsets[fmt::SecRuntime];
        for (const auto& g : rm.runtime) walk += 4 + 1 + 4 + 2 + g.name.size();
        ASSERT_EQ(walk, rm.sectionOffsets[fmt::SecTemp]);
    }
    {
        // Temporary entry: [4] self-addr [1] type tag [2] name len [·] name
        // (no scope-depth byte: block ownership is structural in the AST)
        uint32_t walk = rm.sectionOffsets[fmt::SecTemp];
        for (const auto& g : rm.temps) walk += 4 + 1 + 2 + g.name.size();
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
        // AST entry: [1] type [2] child-count [4]x child + type-specific data
        uint32_t walk = rm.sectionOffsets[fmt::SecAst];
        for (const auto& t : rm.ast) walk += 1 + 2 + 4 * t.children.size() + t.data.size();
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

// The module format is v0.4: `string` (tag 0x05) brought the first
// variable-width value, so an older module — whose literals and global values
// are all fixed-width — must not be parsed with the v0.4 layout.
TEST(golden_bytes, module_version_is_0_4_and_other_versions_are_rejected) {
    auto r = fh::compile(fh::readFixture("minimal.fsm"), "minimal.fsm");
    ASSERT_TRUE(r.ok);
    ASSERT_EQ(fh::le16(r.module, 4), uint16_t(fmt::kVersionMajor));
    ASSERT_EQ(fh::le16(r.module, 6), uint16_t(4));

    ReadModule rm;
    std::string err;
    // every older minor version is rejected by name
    for (uint8_t old = 1; old <= 3; ++old) {
        std::vector<uint8_t> bytes = r.module;
        bytes[6] = old; // v0.1 depth bytes, v0.2 owner/dirty, v0.3 no strings
        err.clear();
        ASSERT_FALSE(readModule(bytes, rm, err));
        ASSERT_TRUE(err.find("unsupported module version 0." + std::to_string(old)) !=
                    std::string::npos);
    }
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
    ASSERT_EQ(rm.temps[0].name, std::string("tick"));      // State body frame
    ASSERT_EQ(rm.temps[1].name, std::string("waitTime"));  // Actions body frame
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
    // 'agent' is driven by three Tier 3 calls, but nothing in the module says
    // so: ownership is not encoded any more. Only type + slot remain.
    int agentSlot = -1;
    for (std::size_t i = 0; i < rm.runtime.size(); ++i) {
        if (rm.runtime[i].name == "agent") agentSlot = int(i);
    }
    ASSERT_TRUE(agentSlot >= 0);
    ASSERT_EQ(rm.runtime[static_cast<std::size_t>(agentSlot)].bindingSlot, uint32_t(2));
    ASSERT_EQ(rm.runtime[static_cast<std::size_t>(agentSlot)].tag, uint8_t(0x70)); // NavMeshAgent
}

// Pinned golden bytes for tests/fixtures/minimal.fsm — module format v0.4,
// 378 bytes: identical to v0.2/v0.3 except for the version field, because this
// fixture has no runtime variables, no claim-bearing call (it only calls
// `wait`) and no strings. Was 401 bytes in v0.1, before the scope-depth bytes
// were removed (2 temp entries x 1 byte + 21 AST tokens x 1 byte).
//
// Independently verified by hand (separate walk of the file): the seven
// section offsets tile it exactly (0x24, 0x38, 0x38, 0x52, 0x60, 0x96, 0x168
// -> EOF at 0x17a); the global entry is [4]0x20000000 [1]int [4]10 "tickLimit";
// both temp entries are [4] self-addr [1] tag [2] len + name with no depth
// byte ("tick" @0x60000000, "waitTime" @0x6000000b); the state entry's root
// address resolves to AST token #0, a STATE token with entry flag 1; the AST
// is 21 tokens of [1] type [2] child-count [4]x child + data, and block
// ownership is structural — token #1 (TEMP tick) and token #5 (TEMP waitTime)
// are children of #0 (STATE) and #4 (ACTIONS) respectively; the instruction
// frame holds 6 instructions and the FSM entry lists both self-loop gotos. If this test fails, either the compiler
// output changed (review the diff before updating) or the fixture did.
TEST(golden_bytes, minimal_module_exact_bytes) {
    auto r = fh::compile(fh::readFixture("minimal.fsm"), "minimal.fsm");
    ASSERT_TRUE(r.ok);
    static const uint8_t kBytes[] = {
        0x44, 0x4d, 0x53, 0x46, 0x00, 0x00, 0x04, 0x00, 0x24, 0x00, 0x00, 0x00,
        0x38, 0x00, 0x00, 0x00, 0x38, 0x00, 0x00, 0x00, 0x52, 0x00, 0x00, 0x00,
        0x60, 0x00, 0x00, 0x00, 0x96, 0x00, 0x00, 0x00, 0x68, 0x01, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x20, 0x01, 0x0a, 0x00, 0x00, 0x00, 0x09, 0x00, 0x74,
        0x69, 0x63, 0x6b, 0x4c, 0x69, 0x6d, 0x69, 0x74, 0x00, 0x00, 0x00, 0x60,
        0x01, 0x04, 0x00, 0x74, 0x69, 0x63, 0x6b, 0x0b, 0x00, 0x00, 0x60, 0x02,
        0x08, 0x00, 0x77, 0x61, 0x69, 0x74, 0x54, 0x69, 0x6d, 0x65, 0x01, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0xc0, 0x04, 0x00, 0x49, 0x64, 0x6c, 0x65,
        0x04, 0x00, 0x00, 0x80, 0x06, 0x00, 0x02, 0x02, 0x00, 0x00, 0x00, 0x60,
        0x27, 0x00, 0x00, 0xc0, 0x02, 0x02, 0x0b, 0x00, 0x00, 0x60, 0x55, 0x00,
        0x00, 0xc0, 0x02, 0x02, 0x00, 0x00, 0x00, 0x60, 0x68, 0x00, 0x00, 0xc0,
        0x01, 0x01, 0x83, 0x00, 0x00, 0xc0, 0x03, 0x01, 0x04, 0x00, 0x00, 0x80,
        0x03, 0x01, 0x04, 0x00, 0x00, 0x80, 0x01, 0x04, 0x00, 0x14, 0x00, 0x00,
        0xc0, 0x1c, 0x00, 0x00, 0xc0, 0x2f, 0x00, 0x00, 0xc0, 0x94, 0x00, 0x00,
        0xc0, 0x01, 0x12, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x60, 0x13, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x60, 0x27, 0x00, 0x00, 0xc0, 0x22, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x00, 0x02, 0x04, 0x00, 0x42, 0x00, 0x00, 0xc0,
        0x4a, 0x00, 0x00, 0xc0, 0x5d, 0x00, 0x00, 0xc0, 0x83, 0x00, 0x00, 0xc0,
        0x12, 0x00, 0x00, 0x02, 0x0b, 0x00, 0x00, 0x60, 0x13, 0x00, 0x00, 0x0b,
        0x00, 0x00, 0x60, 0x55, 0x00, 0x00, 0xc0, 0x22, 0x00, 0x00, 0x02, 0xcd,
        0xcc, 0xcc, 0x3d, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x60, 0x68, 0x00,
        0x00, 0xc0, 0x20, 0x00, 0x00, 0x01, 0x74, 0x00, 0x00, 0xc0, 0x7b, 0x00,
        0x00, 0xc0, 0x23, 0x00, 0x00, 0x00, 0x00, 0x00, 0x60, 0x22, 0x00, 0x00,
        0x01, 0x01, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x0a, 0x01, 0x8d,
        0x00, 0x00, 0xc0, 0x23, 0x00, 0x00, 0x0b, 0x00, 0x00, 0x60, 0x03, 0x02,
        0x00, 0x9f, 0x00, 0x00, 0xc0, 0xcb, 0x00, 0x00, 0xc0, 0x04, 0x01, 0x00,
        0xc4, 0x00, 0x00, 0xc0, 0xaa, 0x00, 0x00, 0xc0, 0x20, 0x00, 0x00, 0x0f,
        0xb6, 0x00, 0x00, 0xc0, 0xbd, 0x00, 0x00, 0xc0, 0x23, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x60, 0x23, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x11, 0x00,
        0x00, 0x04, 0x00, 0x00, 0x80, 0x11, 0x00, 0x00, 0x04, 0x00, 0x00, 0x80,
        0x01, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x80, 0x02, 0x00, 0x04, 0x00,
        0x00, 0x80, 0x04, 0x00, 0x00, 0x80,
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
