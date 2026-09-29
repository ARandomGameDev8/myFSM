#include "test_framework.hpp"

#include <map>
#include <unordered_set>

#include "builtin_functions.hpp"
#include "builtin_types.hpp"

using namespace fsmc;

TEST(lib_functions, total_overload_count_is_159) {
    // 179 overloads before the v0.5 purge; goTo/followTarget/follow/
    // sprintTowards (20 overloads) were removed as redundant with moveTowards.
    ASSERT_EQ(BuiltinFunctions::instance().all().size(), std::size_t(159));
}

TEST(lib_functions, every_overload_reachable_by_name) {
    const auto& all = BuiltinFunctions::instance().all();
    for (const FunctionDefinition& fn : all) {
        auto overloads = BuiltinFunctions::instance().find(fn.name);
        bool found = false;
        for (const FunctionDefinition* o : overloads) {
            if (o->functionId == fn.functionId) { found = true; break; }
        }
        ASSERT_TRUE(found);
    }
}

TEST(lib_functions, no_duplicate_function_ids) {
    std::unordered_set<uint16_t> seen;
    for (const FunctionDefinition& fn : BuiltinFunctions::instance().all()) {
        ASSERT_TRUE(seen.insert(fn.functionId).second);
    }
}

TEST(lib_functions, every_id_resolves) {
    for (const FunctionDefinition& fn : BuiltinFunctions::instance().all()) {
        const FunctionDefinition* got = BuiltinFunctions::instance().findById(fn.functionId);
        ASSERT_TRUE(got != nullptr);
        ASSERT_EQ(got->functionId, fn.functionId);
        ASSERT_EQ(got->name, fn.name);
    }
    EXPECT_TRUE(BuiltinFunctions::instance().findById(0x7FFF) == nullptr);
    EXPECT_TRUE(BuiltinFunctions::instance().findById(0xFFFF) == nullptr);
}

TEST(lib_functions, unknown_name_returns_empty) {
    ASSERT_TRUE(BuiltinFunctions::instance().find("nonexistent").empty());
}

TEST(lib_functions, ids_stay_in_category_ranges) {
    // Category base IDs (spec 0.5). Gaps inside a category are legitimate:
    // goTo/followTarget/follow/sprintTowards (0x060A-0x0623) were retired, so
    // Navigation is no longer gapless — the ids must merely start at the base,
    // never leave the range, and strictly increase.
    static const std::pair<const char*, uint16_t> bases[] = {
        {"Math", 0x0000}, {"Object", 0x0100}, {"Sprite", 0x0200},
        {"Animation", 0x0300}, {"Physics", 0x0400}, {"Camera", 0x0500},
        {"Navigation", 0x0600}, {"Perception", 0x0700}, {"Steering", 0x0800},
        {"Sensing", 0x0900}, {"Control", 0x0A00},
    };
    std::map<std::string, std::vector<uint16_t>> byCategory;
    for (const FunctionDefinition& fn : BuiltinFunctions::instance().all()) {
        byCategory[fn.category].push_back(fn.functionId);
    }
    ASSERT_EQ(byCategory.size(), std::size_t(11));
    for (const auto& [category, base] : bases) {
        auto it = byCategory.find(category);
        ASSERT_TRUE(it != byCategory.end());
        const std::vector<uint16_t>& ids = it->second;
        ASSERT_TRUE(!ids.empty());
        ASSERT_EQ(ids.front(), base);
        for (std::size_t i = 1; i < ids.size(); ++i) {
            ASSERT_TRUE(ids[i] > ids[i - 1]);
        }
        ASSERT_TRUE(ids.back() <= base + 0xFF);
    }
}

TEST(lib_functions, every_param_and_return_type_resolves) {
    const auto& types = BuiltinTypes::instance();
    for (const FunctionDefinition& fn : BuiltinFunctions::instance().all()) {
        ASSERT_TRUE(types.find(fn.returnType) != nullptr);
        for (const FunctionParam& p : fn.params) {
            ASSERT_TRUE(types.find(p.typeName) != nullptr);
            ASSERT_TRUE(!p.name.empty());
        }
    }
}

TEST(lib_functions, tiers_are_valid) {
    for (const FunctionDefinition& fn : BuiltinFunctions::instance().all()) {
        ASSERT_TRUE(fn.tier >= 1 && fn.tier <= 3);
        // only a Tier 3 call can drive its first argument
        if (fn.tier != 3) EXPECT_TRUE(!fn.requiresVariableTarget);
    }
}

TEST(lib_functions, overload_counts_match_spec) {
    static const std::pair<const char*, std::size_t> counts[] = {
        {"findShortestPathAndMove", 6}, {"moveTowards", 6},
        {"stopMovement", 3}, {"hasReachedDestination", 6}, {"getPosition", 4},
        {"raycast", 2}, {"getScale", 2}, {"sin", 1}, {"random", 1},
        {"emit", 1}, {"wait", 1}, {"waitUntil", 1}, {"lookAt", 2},
        {"getNearestOfTag", 2}, {"getFleeDirection", 2},
    };
    for (const auto& [name, n] : counts) {
        ASSERT_EQ(BuiltinFunctions::instance().find(name).size(), n);
    }
    // the purged names are gone entirely
    ASSERT_TRUE(BuiltinFunctions::instance().find("goTo").empty());
    ASSERT_TRUE(BuiltinFunctions::instance().find("followTarget").empty());
    ASSERT_TRUE(BuiltinFunctions::instance().find("follow").empty());
    ASSERT_TRUE(BuiltinFunctions::instance().find("sprintTowards").empty());
}

// The claim tables are gone (module v0.3): what remains of them is the
// source-level rule "a Tier 3 call that drives an object needs a variable as
// its first argument", flagged per overload by `requiresVariableTarget`.
TEST(lib_functions, tier3_target_driving_flags_match_spec) {
    const auto& reg = BuiltinFunctions::instance();
    auto drives = [&](uint16_t id) {
        const FunctionDefinition* fn = reg.findById(id);
        return fn ? fn->requiresVariableTarget : false;
    };

    // every overload of the object-driving Tier 3 functions requires a variable
    static const char* kDriving[] = {
        "findShortestPathAndMove", "moveTowards", "stopMovement", "lookAt",
    };
    std::size_t drivingOverloads = 0;
    for (const char* name : kDriving) {
        const auto overloads = reg.find(name);
        ASSERT_TRUE(!overloads.empty());
        for (const FunctionDefinition* fn : overloads) {
            ASSERT_TRUE(fn->requiresVariableTarget);
            ASSERT_EQ(fn->tier, uint8_t(3));
            ++drivingOverloads;
        }
    }
    ASSERT_EQ(drivingOverloads, std::size_t(17)); // 6+6+3+2

    // the purged slots are retired: no overload answers on them any more
    static const uint16_t kRetired[] = {
        0x060A, 0x060B, 0x060C, 0x060D, 0x060E, 0x060F, // goTo
        0x0610, 0x0611, 0x0612, 0x0613,                 // followTarget
        0x061A, 0x061B, 0x061C, 0x061D,                 // follow
        0x061E, 0x061F, 0x0620, 0x0621, 0x0622, 0x0623, // sprintTowards
    };
    for (uint16_t id : kRetired) {
        EXPECT_TRUE(reg.findById(id) == nullptr);
    }

    // wait / waitUntil are Tier 3 but drive no object: a literal is fine there
    ASSERT_TRUE(!drives(0x0A00)); // wait(float seconds)
    ASSERT_TRUE(!drives(0x0A01)); // waitUntil(bool condition)
    ASSERT_EQ(reg.findById(0x0A00)->tier, uint8_t(3));

    // spot-check ids that used to carry claim lists
    ASSERT_TRUE(drives(0x0624)); // moveTowards(NavMeshAgent, Vector3, float)
    ASSERT_TRUE(drives(0x0614)); // findShortestPathAndMove(NavMeshAgent, Vector3)
    ASSERT_TRUE(drives(0x0700)); // lookAt(Object3D, Object3D)      was src.rotation
    ASSERT_TRUE(drives(0x062A)); // stopMovement(NavMeshAgent)

    // Tier 1 / Tier 2 never carry the flag (Tier 2 has its own const rule)
    ASSERT_TRUE(!drives(0x0105)); // setPosition  (Tier 2)
    ASSERT_TRUE(!drives(0x0A02)); // emit         (Tier 2)
    ASSERT_TRUE(!drives(0x0100)); // getPosition  (Tier 1)
}
