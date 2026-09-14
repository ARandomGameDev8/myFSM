#include "test_framework.hpp"

#include <map>
#include <unordered_set>

#include "builtin_functions.hpp"
#include "builtin_types.hpp"

using namespace fsmc;

TEST(lib_functions, total_overload_count_is_179) {
    ASSERT_EQ(BuiltinFunctions::instance().all().size(), std::size_t(179));
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

TEST(lib_functions, ids_are_sequential_in_category_ranges) {
    // Category base IDs (spec 0.5).
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
        for (std::size_t i = 0; i < ids.size(); ++i) {
            ASSERT_EQ(ids[i], base + uint16_t(i));
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
        {"goTo", 6}, {"followTarget", 4}, {"findShortestPathAndMove", 6},
        {"follow", 4}, {"sprintTowards", 6}, {"moveTowards", 6},
        {"stopMovement", 3}, {"hasReachedDestination", 6}, {"getPosition", 4},
        {"raycast", 2}, {"getScale", 2}, {"sin", 1}, {"random", 1},
        {"emit", 1}, {"wait", 1}, {"waitUntil", 1}, {"lookAt", 2},
        {"getNearestOfTag", 2}, {"getFleeDirection", 2},
    };
    for (const auto& [name, n] : counts) {
        ASSERT_EQ(BuiltinFunctions::instance().find(name).size(), n);
    }
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
        "goTo", "followTarget", "findShortestPathAndMove", "follow",
        "sprintTowards", "moveTowards", "stopMovement", "lookAt",
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
    ASSERT_EQ(drivingOverloads, std::size_t(37)); // 6+4+6+4+6+6+3+2

    // wait / waitUntil are Tier 3 but drive no object: a literal is fine there
    ASSERT_TRUE(!drives(0x0A00)); // wait(float seconds)
    ASSERT_TRUE(!drives(0x0A01)); // waitUntil(bool condition)
    ASSERT_EQ(reg.findById(0x0A00)->tier, uint8_t(3));

    // spot-check ids that used to carry claim lists
    ASSERT_TRUE(drives(0x060A)); // goTo(NavMeshAgent, Vector3)     was position+velocity
    ASSERT_TRUE(drives(0x061A)); // follow(NavMeshAgent, Object3D)  was position+velocity+rotation
    ASSERT_TRUE(drives(0x0700)); // lookAt(Object3D, Object3D)      was src.rotation
    ASSERT_TRUE(drives(0x062A)); // stopMovement(NavMeshAgent)

    // Tier 1 / Tier 2 never carry the flag (Tier 2 has its own const rule)
    ASSERT_TRUE(!drives(0x0105)); // setPosition  (Tier 2)
    ASSERT_TRUE(!drives(0x0A02)); // emit         (Tier 2)
    ASSERT_TRUE(!drives(0x0100)); // getPosition  (Tier 1)
}
