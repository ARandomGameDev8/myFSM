#include "builtin_functions.hpp"

#include <utility>

namespace fsmc {

namespace {

// Parameters are written as (name, type) pairs, in signature order.
using Param = std::pair<const char*, const char*>;

// The trailing `drivesTarget` flag marks the Tier 3 overloads whose first
// argument is the object being driven (see FunctionDefinition). It replaces the
// old per-field claim lists, which are gone from both the language and the
// binary format; only the "first argument must be a variable" rule remains.
FunctionDefinition mk(const char* category, const char* name, uint8_t tier,
                      uint16_t functionId, const char* returnType,
                      std::initializer_list<Param> params,
                      bool drivesTarget = false) {
    FunctionDefinition f;
    f.category = category;
    f.name = name;
    f.tier = tier;
    f.functionId = functionId;
    f.returnType = returnType;
    for (const auto& p : params) f.params.push_back({p.first, p.second});
    f.requiresVariableTarget = drivesTarget;
    return f;
}

// Tier 3 overloads that drive their first argument.
constexpr bool kDrivesTarget = true;

} // namespace

BuiltinFunctions::BuiltinFunctions() {
    using D = FunctionDefinition;
    std::vector<D> f;

    // ------------------------------------------------------------------
    // Math — Tier 1 — IDs 0x0000..0x00FF
    // ------------------------------------------------------------------
    f.push_back(mk("Math", "sin",        1, 0x0000, "float",   {{"x", "float"}}));
    f.push_back(mk("Math", "cos",        1, 0x0001, "float",   {{"x", "float"}}));
    f.push_back(mk("Math", "tan",        1, 0x0002, "float",   {{"x", "float"}}));
    f.push_back(mk("Math", "asin",       1, 0x0003, "float",   {{"x", "float"}}));
    f.push_back(mk("Math", "acos",       1, 0x0004, "float",   {{"x", "float"}}));
    f.push_back(mk("Math", "atan",       1, 0x0005, "float",   {{"x", "float"}}));
    f.push_back(mk("Math", "atan2",      1, 0x0006, "float",   {{"y", "float"}, {"x", "float"}}));
    f.push_back(mk("Math", "sqrt",       1, 0x0007, "float",   {{"x", "float"}}));
    f.push_back(mk("Math", "pow",        1, 0x0008, "float",   {{"base", "float"}, {"exp", "float"}}));
    f.push_back(mk("Math", "abs",        1, 0x0009, "float",   {{"x", "float"}}));
    f.push_back(mk("Math", "sign",       1, 0x000A, "float",   {{"x", "float"}}));
    f.push_back(mk("Math", "clamp",      1, 0x000B, "float",   {{"x", "float"}, {"lo", "float"}, {"hi", "float"}}));
    f.push_back(mk("Math", "lerp",       1, 0x000C, "float",   {{"a", "float"}, {"b", "float"}, {"t", "float"}}));
    f.push_back(mk("Math", "min",        1, 0x000D, "float",   {{"a", "float"}, {"b", "float"}}));
    f.push_back(mk("Math", "max",        1, 0x000E, "float",   {{"a", "float"}, {"b", "float"}}));
    f.push_back(mk("Math", "floor",      1, 0x000F, "float",   {{"x", "float"}}));
    f.push_back(mk("Math", "ceil",       1, 0x0010, "float",   {{"x", "float"}}));
    f.push_back(mk("Math", "round",      1, 0x0011, "float",   {{"x", "float"}}));
    f.push_back(mk("Math", "normalize",  1, 0x0012, "Vector3", {{"v", "Vector3"}}));
    f.push_back(mk("Math", "dot",        1, 0x0013, "float",   {{"a", "Vector3"}, {"b", "Vector3"}}));
    f.push_back(mk("Math", "cross",      1, 0x0014, "Vector3", {{"a", "Vector3"}, {"b", "Vector3"}}));
    f.push_back(mk("Math", "distance",   1, 0x0015, "float",   {{"a", "Vector3"}, {"b", "Vector3"}}));
    f.push_back(mk("Math", "magnitude",  1, 0x0016, "float",   {{"v", "Vector3"}}));
    f.push_back(mk("Math", "random",     1, 0x0017, "float",   {}));
    f.push_back(mk("Math", "randomRange",1, 0x0018, "float",   {{"lo", "float"}, {"hi", "float"}}));

    // ------------------------------------------------------------------
    // Object — IDs 0x0100..0x01FF
    // ------------------------------------------------------------------
    f.push_back(mk("Object", "getPosition", 1, 0x0100, "Vector3",    {{"obj", "Object3D"}}));
    f.push_back(mk("Object", "getPosition", 1, 0x0101, "Vector2",    {{"obj", "Object2D"}}));
    f.push_back(mk("Object", "getRotation", 1, 0x0102, "Quaternion", {{"obj", "Object3D"}}));
    f.push_back(mk("Object", "getScale",    1, 0x0103, "Vector3",    {{"obj", "Object3D"}}));
    f.push_back(mk("Object", "getScale",    1, 0x0104, "Vector2",    {{"obj", "Object2D"}}));
    f.push_back(mk("Object", "setPosition", 2, 0x0105, "void",       {{"obj", "Object3D"}, {"pos", "Vector3"}}));
    f.push_back(mk("Object", "setPosition", 2, 0x0106, "void",       {{"obj", "Object2D"}, {"pos", "Vector2"}}));
    f.push_back(mk("Object", "setRotation", 2, 0x0107, "void",       {{"obj", "Object3D"}, {"rot", "Quaternion"}}));
    f.push_back(mk("Object", "setScale",    2, 0x0108, "void",       {{"obj", "Object3D"}, {"scale", "Vector3"}}));
    f.push_back(mk("Object", "setScale",    2, 0x0109, "void",       {{"obj", "Object2D"}, {"scale", "Vector2"}}));
    f.push_back(mk("Object", "distanceTo",  1, 0x010A, "float",      {{"a", "Object3D"}, {"b", "Object3D"}}));
    f.push_back(mk("Object", "distanceTo",  1, 0x010B, "float",      {{"a", "Object2D"}, {"b", "Object2D"}}));
    f.push_back(mk("Object", "directionTo", 1, 0x010C, "Vector3",    {{"a", "Object3D"}, {"b", "Object3D"}}));
    f.push_back(mk("Object", "directionTo", 1, 0x010D, "Vector2",    {{"a", "Object2D"}, {"b", "Object2D"}}));
    f.push_back(mk("Object", "isActive",    1, 0x010E, "bool",       {{"obj", "Object3D"}}));
    f.push_back(mk("Object", "isActive",    1, 0x010F, "bool",       {{"obj", "Object2D"}}));
    f.push_back(mk("Object", "setActive",   2, 0x0110, "void",       {{"obj", "Object3D"}, {"active", "bool"}}));
    f.push_back(mk("Object", "setActive",   2, 0x0111, "void",       {{"obj", "Object2D"}, {"active", "bool"}}));
    f.push_back(mk("Object", "getTag",      1, 0x0112, "int",        {{"obj", "Object3D"}}));
    f.push_back(mk("Object", "getTag",      1, 0x0113, "int",        {{"obj", "Object2D"}}));
    f.push_back(mk("Object", "getLayer",    1, 0x0114, "int",        {{"obj", "Object3D"}}));
    f.push_back(mk("Object", "getLayer",    1, 0x0115, "int",        {{"obj", "Object2D"}}));

    // ------------------------------------------------------------------
    // Sprite — IDs 0x0200..0x02FF
    // ------------------------------------------------------------------
    f.push_back(mk("Sprite", "getColor",  1, 0x0200, "Vector3", {{"spr", "Sprite3D"}}));
    f.push_back(mk("Sprite", "getColor",  1, 0x0201, "Vector3", {{"spr", "Sprite2D"}}));
    f.push_back(mk("Sprite", "setColor",  2, 0x0202, "void",    {{"spr", "Sprite3D"}, {"rgb", "Vector3"}}));
    f.push_back(mk("Sprite", "setColor",  2, 0x0203, "void",    {{"spr", "Sprite2D"}, {"rgb", "Vector3"}}));
    f.push_back(mk("Sprite", "isVisible", 1, 0x0204, "bool",    {{"spr", "Sprite3D"}}));
    f.push_back(mk("Sprite", "isVisible", 1, 0x0205, "bool",    {{"spr", "Sprite2D"}}));
    f.push_back(mk("Sprite", "setVisible",2, 0x0206, "void",    {{"spr", "Sprite3D"}, {"visible", "bool"}}));
    f.push_back(mk("Sprite", "setVisible",2, 0x0207, "void",    {{"spr", "Sprite2D"}, {"visible", "bool"}}));
    f.push_back(mk("Sprite", "getBounds", 1, 0x0208, "Vector3", {{"spr", "Sprite3D"}}));
    f.push_back(mk("Sprite", "getBounds", 1, 0x0209, "Vector2", {{"spr", "Sprite2D"}}));
    f.push_back(mk("Sprite", "getSize",   1, 0x020A, "Vector2", {{"spr", "Sprite3D"}}));
    f.push_back(mk("Sprite", "getSize",   1, 0x020B, "Vector2", {{"spr", "Sprite2D"}}));

    // ------------------------------------------------------------------
    // Animation — IDs 0x0300..0x03FF
    // ------------------------------------------------------------------
    f.push_back(mk("Animation", "play",           2, 0x0300, "void", {{"ctrl", "AnimationController3D"}}));
    f.push_back(mk("Animation", "play",           2, 0x0301, "void", {{"ctrl", "AnimationController2D"}}));
    f.push_back(mk("Animation", "stop",           2, 0x0302, "void", {{"ctrl", "AnimationController3D"}}));
    f.push_back(mk("Animation", "stop",           2, 0x0303, "void", {{"ctrl", "AnimationController2D"}}));
    f.push_back(mk("Animation", "pause",          2, 0x0304, "void", {{"ctrl", "AnimationController3D"}}));
    f.push_back(mk("Animation", "pause",          2, 0x0305, "void", {{"ctrl", "AnimationController2D"}}));
    f.push_back(mk("Animation", "resume",         2, 0x0306, "void", {{"ctrl", "AnimationController3D"}}));
    f.push_back(mk("Animation", "resume",         2, 0x0307, "void", {{"ctrl", "AnimationController2D"}}));
    f.push_back(mk("Animation", "isPlaying",      1, 0x0308, "bool", {{"ctrl", "AnimationController3D"}}));
    f.push_back(mk("Animation", "isPlaying",      1, 0x0309, "bool", {{"ctrl", "AnimationController2D"}}));
    f.push_back(mk("Animation", "getCurrentClip", 1, 0x030A, "int",  {{"ctrl", "AnimationController3D"}}));
    f.push_back(mk("Animation", "getCurrentClip", 1, 0x030B, "int",  {{"ctrl", "AnimationController2D"}}));
    f.push_back(mk("Animation", "setSpeed",       2, 0x030C, "void", {{"ctrl", "AnimationController3D"}, {"speed", "float"}}));
    f.push_back(mk("Animation", "setSpeed",       2, 0x030D, "void", {{"ctrl", "AnimationController2D"}, {"speed", "float"}}));
    f.push_back(mk("Animation", "getProgress",    1, 0x030E, "float",{{"ctrl", "AnimationController3D"}}));
    f.push_back(mk("Animation", "getProgress",    1, 0x030F, "float",{{"ctrl", "AnimationController2D"}}));
    f.push_back(mk("Animation", "setAnimation",   2, 0x0310, "void", {{"ctrl", "AnimationController3D"}, {"clip", "int"}}));
    f.push_back(mk("Animation", "setAnimation",   2, 0x0311, "void", {{"ctrl", "AnimationController2D"}, {"clip", "int"}}));

    // ------------------------------------------------------------------
    // Physics — IDs 0x0400..0x04FF
    // ------------------------------------------------------------------
    f.push_back(mk("Physics", "getVelocity",        1, 0x0400, "Vector3", {{"phys", "PhysicsObject3D"}}));
    f.push_back(mk("Physics", "getVelocity",        1, 0x0401, "Vector2", {{"phys", "PhysicsObject2D"}}));
    f.push_back(mk("Physics", "setVelocity",        2, 0x0402, "void",    {{"phys", "PhysicsObject3D"}, {"v", "Vector3"}}));
    f.push_back(mk("Physics", "setVelocity",        2, 0x0403, "void",    {{"phys", "PhysicsObject2D"}, {"v", "Vector2"}}));
    f.push_back(mk("Physics", "getMass",            1, 0x0404, "float",   {{"phys", "PhysicsObject3D"}}));
    f.push_back(mk("Physics", "getMass",            1, 0x0405, "float",   {{"phys", "PhysicsObject2D"}}));
    f.push_back(mk("Physics", "applyForce",         2, 0x0406, "void",    {{"phys", "PhysicsObject3D"}, {"force", "Vector3"}}));
    f.push_back(mk("Physics", "applyForce",         2, 0x0407, "void",    {{"phys", "PhysicsObject2D"}, {"force", "Vector2"}}));
    f.push_back(mk("Physics", "applyImpulse",       2, 0x0408, "void",    {{"phys", "PhysicsObject3D"}, {"impulse", "Vector3"}}));
    f.push_back(mk("Physics", "applyImpulse",       2, 0x0409, "void",    {{"phys", "PhysicsObject2D"}, {"impulse", "Vector2"}}));
    f.push_back(mk("Physics", "isGrounded",         1, 0x040A, "bool",    {{"phys", "PhysicsObject3D"}}));
    f.push_back(mk("Physics", "isGrounded",         1, 0x040B, "bool",    {{"phys", "PhysicsObject2D"}}));
    f.push_back(mk("Physics", "isColliding",        1, 0x040C, "bool",    {{"phys", "PhysicsObject3D"}}));
    f.push_back(mk("Physics", "isColliding",        1, 0x040D, "bool",    {{"phys", "PhysicsObject2D"}}));
    f.push_back(mk("Physics", "getCollisionNormal", 1, 0x040E, "Vector3", {{"phys", "PhysicsObject3D"}}));
    f.push_back(mk("Physics", "getCollisionNormal", 1, 0x040F, "Vector2", {{"phys", "PhysicsObject2D"}}));
    f.push_back(mk("Physics", "raycast",            1, 0x0410, "bool",    {{"origin", "Vector3"}, {"dir", "Vector3"}, {"dist", "float"}}));
    f.push_back(mk("Physics", "raycast",            1, 0x0411, "bool",    {{"origin", "Vector2"}, {"dir", "Vector2"}, {"dist", "float"}}));

    // ------------------------------------------------------------------
    // Camera — IDs 0x0500..0x05FF
    // ------------------------------------------------------------------
    f.push_back(mk("Camera", "getPosition",   1, 0x0500, "Vector3", {{"cam", "Camera3D"}}));
    f.push_back(mk("Camera", "getPosition",   1, 0x0501, "Vector2", {{"cam", "Camera2D"}}));
    f.push_back(mk("Camera", "isInView",      1, 0x0502, "bool",    {{"cam", "Camera3D"}, {"obj", "Object3D"}}));
    f.push_back(mk("Camera", "isInView",      1, 0x0503, "bool",    {{"cam", "Camera2D"}, {"obj", "Object2D"}}));
    f.push_back(mk("Camera", "screenToWorld", 1, 0x0504, "Vector3", {{"cam", "Camera3D"}, {"screen", "Vector2"}}));
    f.push_back(mk("Camera", "screenToWorld", 1, 0x0505, "Vector2", {{"cam", "Camera2D"}, {"screen", "Vector2"}}));
    f.push_back(mk("Camera", "worldToScreen", 1, 0x0506, "Vector2", {{"cam", "Camera3D"}, {"world", "Vector3"}}));
    f.push_back(mk("Camera", "worldToScreen", 1, 0x0507, "Vector2", {{"cam", "Camera2D"}, {"world", "Vector3"}}));
    f.push_back(mk("Camera", "getViewport",   1, 0x0508, "Vector2", {{"cam", "Camera3D"}}));
    f.push_back(mk("Camera", "getViewport",   1, 0x0509, "Vector2", {{"cam", "Camera2D"}}));

    // ------------------------------------------------------------------
    // Navigation — IDs 0x0600..0x06FF
    // ------------------------------------------------------------------
    f.push_back(mk("Navigation", "findPath", 1, 0x0600, "int",     {{"from", "Vector3"}, {"to", "Vector3"}}));
    f.push_back(mk("Navigation", "findPath", 1, 0x0601, "int",     {{"from", "Vector2"}, {"to", "Vector2"}}));
    f.push_back(mk("Navigation", "getNextWaypoint", 1, 0x0602, "Vector3", {{"path", "int"}}));
    f.push_back(mk("Navigation", "getPathLength",   1, 0x0603, "float",   {{"path", "int"}}));
    f.push_back(mk("Navigation", "hasReachedDestination", 1, 0x0604, "bool", {{"agent", "NavMeshAgent"}, {"tgt", "Vector3"}}));
    f.push_back(mk("Navigation", "hasReachedDestination", 1, 0x0605, "bool", {{"agent", "NavMeshAgent"}, {"tgt", "Object3D"}}));
    f.push_back(mk("Navigation", "hasReachedDestination", 1, 0x0606, "bool", {{"agent", "Object3D"}, {"tgt", "Vector3"}}));
    f.push_back(mk("Navigation", "hasReachedDestination", 1, 0x0607, "bool", {{"agent", "Object3D"}, {"tgt", "Object3D"}}));
    f.push_back(mk("Navigation", "hasReachedDestination", 1, 0x0608, "bool", {{"agent", "Object2D"}, {"tgt", "Vector2"}}));
    f.push_back(mk("Navigation", "hasReachedDestination", 1, 0x0609, "bool", {{"agent", "Object2D"}, {"tgt", "Object2D"}}));
    f.push_back(mk("Navigation", "goTo", 3, 0x060A, "void", {{"agent", "NavMeshAgent"}, {"dest", "Vector3"}},  kDrivesTarget));
    f.push_back(mk("Navigation", "goTo", 3, 0x060B, "void", {{"agent", "NavMeshAgent"}, {"dest", "Object3D"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "goTo", 3, 0x060C, "void", {{"agent", "Object3D"},   {"dest", "Vector3"}},  kDrivesTarget));
    f.push_back(mk("Navigation", "goTo", 3, 0x060D, "void", {{"agent", "Object3D"},   {"dest", "Object3D"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "goTo", 3, 0x060E, "void", {{"agent", "Object2D"},   {"dest", "Vector2"}},  kDrivesTarget));
    f.push_back(mk("Navigation", "goTo", 3, 0x060F, "void", {{"agent", "Object2D"},   {"dest", "Object2D"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "followTarget", 3, 0x0610, "void", {{"agent", "NavMeshAgent"}, {"tgt", "Object3D"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "followTarget", 3, 0x0611, "void", {{"agent", "NavMeshAgent"}, {"tgt", "Object2D"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "followTarget", 3, 0x0612, "void", {{"agent", "Object3D"},   {"tgt", "Object3D"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "followTarget", 3, 0x0613, "void", {{"agent", "Object2D"},   {"tgt", "Object2D"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "findShortestPathAndMove", 3, 0x0614, "void", {{"agent", "NavMeshAgent"}, {"tgt", "Vector3"}},  kDrivesTarget));
    f.push_back(mk("Navigation", "findShortestPathAndMove", 3, 0x0615, "void", {{"agent", "NavMeshAgent"}, {"tgt", "Object3D"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "findShortestPathAndMove", 3, 0x0616, "void", {{"agent", "Object3D"},   {"tgt", "Vector3"}},  kDrivesTarget));
    f.push_back(mk("Navigation", "findShortestPathAndMove", 3, 0x0617, "void", {{"agent", "Object3D"},   {"tgt", "Object3D"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "findShortestPathAndMove", 3, 0x0618, "void", {{"agent", "Object2D"},   {"tgt", "Vector2"}},  kDrivesTarget));
    f.push_back(mk("Navigation", "findShortestPathAndMove", 3, 0x0619, "void", {{"agent", "Object2D"},   {"tgt", "Object2D"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "follow", 3, 0x061A, "void", {{"agent", "NavMeshAgent"}, {"tgt", "Object3D"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "follow", 3, 0x061B, "void", {{"agent", "NavMeshAgent"}, {"tgt", "Object2D"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "follow", 3, 0x061C, "void", {{"agent", "Object3D"},   {"tgt", "Object3D"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "follow", 3, 0x061D, "void", {{"agent", "Object2D"},   {"tgt", "Object2D"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "sprintTowards", 3, 0x061E, "void", {{"agent", "NavMeshAgent"}, {"dest", "Vector3"},  {"speedMult", "float"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "sprintTowards", 3, 0x061F, "void", {{"agent", "NavMeshAgent"}, {"dest", "Object3D"}, {"speedMult", "float"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "sprintTowards", 3, 0x0620, "void", {{"agent", "Object3D"},   {"dest", "Vector3"},  {"speedMult", "float"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "sprintTowards", 3, 0x0621, "void", {{"agent", "Object3D"},   {"dest", "Object3D"}, {"speedMult", "float"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "sprintTowards", 3, 0x0622, "void", {{"agent", "Object2D"},   {"dest", "Vector2"},  {"speedMult", "float"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "sprintTowards", 3, 0x0623, "void", {{"agent", "Object2D"},   {"dest", "Object2D"}, {"speedMult", "float"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "moveTowards", 3, 0x0624, "void", {{"agent", "NavMeshAgent"}, {"dest", "Vector3"},  {"speed", "float"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "moveTowards", 3, 0x0625, "void", {{"agent", "NavMeshAgent"}, {"dest", "Object3D"}, {"speed", "float"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "moveTowards", 3, 0x0626, "void", {{"agent", "Object3D"},   {"dest", "Vector3"},  {"speed", "float"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "moveTowards", 3, 0x0627, "void", {{"agent", "Object3D"},   {"dest", "Object3D"}, {"speed", "float"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "moveTowards", 3, 0x0628, "void", {{"agent", "Object2D"},   {"dest", "Vector2"},  {"speed", "float"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "moveTowards", 3, 0x0629, "void", {{"agent", "Object2D"},   {"dest", "Object2D"}, {"speed", "float"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "stopMovement", 3, 0x062A, "void", {{"agent", "NavMeshAgent"}}, kDrivesTarget));
    f.push_back(mk("Navigation", "stopMovement", 3, 0x062B, "void", {{"agent", "Object3D"}},   kDrivesTarget));
    f.push_back(mk("Navigation", "stopMovement", 3, 0x062C, "void", {{"agent", "Object2D"}},   kDrivesTarget));

    // ------------------------------------------------------------------
    // Perception — IDs 0x0700..0x07FF
    // ------------------------------------------------------------------
    f.push_back(mk("Perception", "lookAt", 3, 0x0700, "void", {{"src", "Object3D"}, {"tgt", "Object3D"}}, kDrivesTarget));
    f.push_back(mk("Perception", "lookAt", 3, 0x0701, "void", {{"src", "Object2D"}, {"tgt", "Object2D"}}, kDrivesTarget));
    f.push_back(mk("Perception", "isInLineOfSight", 1, 0x0702, "bool", {{"src", "Object3D"}, {"tgt", "Object3D"}}));
    f.push_back(mk("Perception", "isInLineOfSight", 1, 0x0703, "bool", {{"src", "Object2D"}, {"tgt", "Object2D"}}));
    f.push_back(mk("Perception", "isInRange", 1, 0x0704, "bool", {{"src", "Object3D"}, {"tgt", "Object3D"}, {"radius", "float"}}));
    f.push_back(mk("Perception", "isInRange", 1, 0x0705, "bool", {{"src", "Object2D"}, {"tgt", "Object2D"}, {"radius", "float"}}));
    f.push_back(mk("Perception", "getAngleTo", 1, 0x0706, "float", {{"src", "Object3D"}, {"tgt", "Object3D"}}));
    f.push_back(mk("Perception", "getAngleTo", 1, 0x0707, "float", {{"src", "Object2D"}, {"tgt", "Object2D"}}));
    f.push_back(mk("Perception", "getDistanceTo", 1, 0x0708, "float", {{"src", "Object3D"}, {"tgt", "Object3D"}}));
    f.push_back(mk("Perception", "getDistanceTo", 1, 0x0709, "float", {{"src", "Object2D"}, {"tgt", "Object2D"}}));
    f.push_back(mk("Perception", "getNearestOfTag", 1, 0x070A, "Object3D", {{"pos", "Vector3"}, {"tag", "int"}, {"radius", "float"}}));
    f.push_back(mk("Perception", "getNearestOfTag", 1, 0x070B, "Object2D", {{"pos", "Vector2"}, {"tag", "int"}, {"radius", "float"}}));
    f.push_back(mk("Perception", "getAllInRadius", 1, 0x070C, "int", {{"pos", "Vector3"}, {"radius", "float"}}));
    f.push_back(mk("Perception", "getAllInRadius", 1, 0x070D, "int", {{"pos", "Vector2"}, {"radius", "float"}}));

    // ------------------------------------------------------------------
    // Steering — IDs 0x0800..0x08FF
    // ------------------------------------------------------------------
    f.push_back(mk("Steering", "getFleeDirection",    1, 0x0800, "Vector3", {{"from", "Vector3"}, {"threat", "Vector3"}}));
    f.push_back(mk("Steering", "getFleeDirection",    1, 0x0801, "Vector2", {{"from", "Vector2"}, {"threat", "Vector2"}}));
    f.push_back(mk("Steering", "getPursuitPosition",  1, 0x0802, "Vector3", {{"tgt", "Object3D"}, {"speed", "float"}}));
    f.push_back(mk("Steering", "getPursuitPosition",  1, 0x0803, "Vector2", {{"tgt", "Object2D"}, {"speed", "float"}}));
    f.push_back(mk("Steering", "getSeparationVector", 1, 0x0804, "Vector3", {{"agent", "Object3D"}, {"neighbors", "int"}}));
    f.push_back(mk("Steering", "getSeparationVector", 1, 0x0805, "Vector2", {{"agent", "Object2D"}, {"neighbors", "int"}}));
    f.push_back(mk("Steering", "getArrivalVector",    1, 0x0806, "Vector3", {{"pos", "Vector3"}, {"dest", "Vector3"}, {"slowRadius", "float"}}));
    f.push_back(mk("Steering", "getArrivalVector",    1, 0x0807, "Vector2", {{"pos", "Vector2"}, {"dest", "Vector2"}, {"slowRadius", "float"}}));
    f.push_back(mk("Steering", "getWanderVector",     1, 0x0808, "Vector3", {{"pos", "Vector3"}, {"radius", "float"}}));
    f.push_back(mk("Steering", "getWanderVector",     1, 0x0809, "Vector2", {{"pos", "Vector2"}, {"radius", "float"}}));

    // ------------------------------------------------------------------
    // Sensing — IDs 0x0900..0x09FF
    // ------------------------------------------------------------------
    f.push_back(mk("Sensing", "getRaycastHit", 1, 0x0900, "Object3D", {{"origin", "Vector3"}, {"dir", "Vector3"}, {"dist", "float"}}));
    f.push_back(mk("Sensing", "getRaycastHit", 1, 0x0901, "Object2D", {{"origin", "Vector2"}, {"dir", "Vector2"}, {"dist", "float"}}));

    // ------------------------------------------------------------------
    // Control — IDs 0x0A00..0x0AFF
    // ------------------------------------------------------------------
    f.push_back(mk("Control", "wait",     3, 0x0A00, "void", {{"seconds", "float"}}));
    f.push_back(mk("Control", "waitUntil",3, 0x0A01, "void", {{"condition", "bool"}}));
    f.push_back(mk("Control", "emit",     2, 0x0A02, "void", {{"eventId", "int"}}));

    for (auto& fn : f) functions_.push_back(std::move(fn));
}

const BuiltinFunctions& BuiltinFunctions::instance() {
    static const BuiltinFunctions s_instance;
    return s_instance;
}

const std::vector<const FunctionDefinition*> BuiltinFunctions::find(const std::string& name) const {
    std::vector<const FunctionDefinition*> result;
    for (const FunctionDefinition& fn : functions_) {
        if (fn.name == name) result.push_back(&fn);
    }
    return result;
}

const FunctionDefinition* BuiltinFunctions::findById(uint16_t id) const {
    for (const FunctionDefinition& fn : functions_) {
        if (fn.functionId == id) return &fn;
    }
    return nullptr;
}

} // namespace fsmc
