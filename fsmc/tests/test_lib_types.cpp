#include "test_framework.hpp"

#include <unordered_map>
#include <unordered_set>

#include "builtin_functions.hpp"
#include "builtin_types.hpp"

using namespace fsmc;

TEST(lib_types, all_count_is_22) {
    ASSERT_EQ(BuiltinTypes::instance().all().size(), std::size_t(22));
}

TEST(lib_types, every_type_reachable_by_name) {
    const auto& all = BuiltinTypes::instance().all();
    for (const TypeDefinition& t : all) {
        const TypeDefinition* found = BuiltinTypes::instance().find(t.name);
        ASSERT_TRUE(found != nullptr);
        ASSERT_EQ(found->typeTag, t.typeTag);
        ASSERT_EQ(found->name, t.name);
    }
}

TEST(lib_types, tags_are_unique) {
    std::unordered_set<uint8_t> seen;
    for (const TypeDefinition& t : BuiltinTypes::instance().all()) {
        ASSERT_TRUE(seen.insert(t.typeTag).second);
    }
}

TEST(lib_types, table_matches_spec) {
    struct Row {
        const char* name;
        uint8_t tag;
        uint32_t size;
        bool handle, numeric, value;
        const char* category;
    };
    static const Row rows[] = {
        {"void", 0x00, 0, false, false, false, "primitive"},
        {"int", 0x01, 4, false, true, true, "primitive"},
        {"float", 0x02, 4, false, true, true, "primitive"},
        {"double", 0x03, 8, false, true, true, "primitive"},
        {"bool", 0x04, 1, false, false, true, "primitive"},
        {"string", 0x05, 0, false, false, true, "primitive"},
        {"Vector2", 0x10, 8, false, true, true, "vector"},
        {"Vector3", 0x11, 12, false, true, true, "vector"},
        {"Quaternion", 0x12, 16, false, true, true, "vector"},
        {"Object2D", 0x20, 4, true, false, false, "object"},
        {"Object3D", 0x21, 4, true, false, false, "object"},
        {"Transform2D", 0x22, 4, true, false, false, "object"},
        {"Transform3D", 0x23, 4, true, false, false, "object"},
        {"Camera2D", 0x30, 4, true, false, false, "camera"},
        {"Camera3D", 0x31, 4, true, false, false, "camera"},
        {"Sprite2D", 0x40, 4, true, false, false, "sprite"},
        {"Sprite3D", 0x41, 4, true, false, false, "sprite"},
        {"AnimationController2D", 0x50, 4, true, false, false, "animation"},
        {"AnimationController3D", 0x51, 4, true, false, false, "animation"},
        {"PhysicsObject2D", 0x60, 4, true, false, false, "physics"},
        {"PhysicsObject3D", 0x61, 4, true, false, false, "physics"},
        {"NavMeshAgent", 0x70, 4, true, false, false, "navigation"},
    };
    for (const Row& r : rows) {
        const TypeDefinition* t = BuiltinTypes::instance().find(r.name);
        ASSERT_TRUE(t != nullptr);
        ASSERT_EQ(t->typeTag, r.tag);
        ASSERT_EQ(t->sizeBytes, r.size);
        ASSERT_EQ(t->isHandle, r.handle);
        ASSERT_EQ(t->isNumeric, r.numeric);
        ASSERT_EQ(t->isValueType, r.value);
        ASSERT_EQ(t->category, std::string(r.category));
    }
}

TEST(lib_types, unknown_name_is_null) {
    // `string` (lowercase) is a type since v0.4; `String` never was
    EXPECT_TRUE(BuiltinTypes::instance().find("string") != nullptr);
    EXPECT_TRUE(BuiltinTypes::instance().find("String") == nullptr);
    EXPECT_TRUE(BuiltinTypes::instance().find("vector3") == nullptr);
    EXPECT_TRUE(BuiltinTypes::instance().find("") == nullptr);
}
