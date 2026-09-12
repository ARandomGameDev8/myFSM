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
        if (fn.tier != 3) EXPECT_TRUE(fn.claims.empty());
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

TEST(lib_functions, tier3_claims_match_spec) {
    auto claimsOf = [](const char* name, uint16_t id) {
        const FunctionDefinition* fn = BuiltinFunctions::instance().findById(id);
        (void)name;
        return fn ? fn->claims : std::vector<std::string>();
    };
    // goTo (NavMeshAgent, Vector3) = 0x060A
    {
        std::vector<std::string> c = claimsOf("goTo", 0x060A);
        ASSERT_EQ(c.size(), std::size_t(2));
        ASSERT_EQ(c[0], std::string("agent.position"));
        ASSERT_EQ(c[1], std::string("agent.velocity"));
    }
    // follow (NavMeshAgent, Object3D) = 0x061A: also rotation
    {
        std::vector<std::string> c = claimsOf("follow", 0x061A);
        ASSERT_EQ(c.size(), std::size_t(3));
        ASSERT_EQ(c[2], std::string("agent.rotation"));
    }
    // lookAt (Object3D, Object3D) = 0x0700: src.rotation
    {
        std::vector<std::string> c = claimsOf("lookAt", 0x0700);
        ASSERT_EQ(c.size(), std::size_t(1));
        ASSERT_EQ(c[0], std::string("src.rotation"));
    }
    // wait / waitUntil are tier 3 with no claims
    {
        ASSERT_TRUE(claimsOf("wait", 0x0A00).empty());
        ASSERT_TRUE(claimsOf("waitUntil", 0x0A01).empty());
    }
    // every goTo overload has exactly the two position/velocity claims
    for (const FunctionDefinition* fn : BuiltinFunctions::instance().find("goTo")) {
        ASSERT_EQ(fn->claims.size(), std::size_t(2));
    }
    for (const FunctionDefinition* fn : BuiltinFunctions::instance().find("followTarget")) {
        ASSERT_EQ(fn->claims.size(), std::size_t(3));
    }
}
