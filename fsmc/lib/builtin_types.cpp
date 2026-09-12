#include "builtin_types.hpp"

#include <utility>

namespace fsmc {

namespace {

TypeDefinition mk(const char* name, uint8_t tag, uint32_t size, bool handle,
                  bool numeric, bool value, const char* category) {
    TypeDefinition t;
    t.name = name;
    t.typeTag = tag;
    t.sizeBytes = size;
    t.isHandle = handle;
    t.isNumeric = numeric;
    t.isValueType = value;
    t.category = category;
    return t;
}

} // namespace

BuiltinTypes::BuiltinTypes() {
    // Order is canonical: ascending tag.
    types_.push_back(mk("void",                 0x00,  0, false, false, false, "primitive"));
    types_.push_back(mk("int",                  0x01,  4, false, true,  true,  "primitive"));
    types_.push_back(mk("float",                0x02,  4, false, true,  true,  "primitive"));
    types_.push_back(mk("double",               0x03,  8, false, true,  true,  "primitive"));
    types_.push_back(mk("bool",                 0x04,  1, false, false, true,  "primitive"));
    types_.push_back(mk("Vector2",              0x10,  8, false, true,  true,  "vector"));
    types_.push_back(mk("Vector3",              0x11, 12, false, true,  true,  "vector"));
    types_.push_back(mk("Quaternion",           0x12, 16, false, true,  true,  "vector"));
    types_.push_back(mk("Object2D",             0x20,  4, true,  false, false, "object"));
    types_.push_back(mk("Object3D",             0x21,  4, true,  false, false, "object"));
    types_.push_back(mk("Transform2D",          0x22,  4, true,  false, false, "object"));
    types_.push_back(mk("Transform3D",          0x23,  4, true,  false, false, "object"));
    types_.push_back(mk("Camera2D",             0x30,  4, true,  false, false, "camera"));
    types_.push_back(mk("Camera3D",             0x31,  4, true,  false, false, "camera"));
    types_.push_back(mk("Sprite2D",             0x40,  4, true,  false, false, "sprite"));
    types_.push_back(mk("Sprite3D",             0x41,  4, true,  false, false, "sprite"));
    types_.push_back(mk("AnimationController2D", 0x50, 4, true, false, false, "animation"));
    types_.push_back(mk("AnimationController3D", 0x51, 4, true, false, false, "animation"));
    types_.push_back(mk("PhysicsObject2D",      0x60,  4, true,  false, false, "physics"));
    types_.push_back(mk("PhysicsObject3D",      0x61,  4, true,  false, false, "physics"));
    types_.push_back(mk("NavMeshAgent",         0x70,  4, true,  false, false, "navigation"));
}

const BuiltinTypes& BuiltinTypes::instance() {
    static const BuiltinTypes s_instance;
    return s_instance;
}

const TypeDefinition* BuiltinTypes::find(const std::string& name) const {
    for (const TypeDefinition& t : types_) {
        if (t.name == name) return &t;
    }
    return nullptr;
}

} // namespace fsmc
