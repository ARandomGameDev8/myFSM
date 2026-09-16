// myFSM Unity Runtime — built-in function catalog (179 overloads).
//
// Mirrors fsmc/lib/builtin_functions (IDs, tiers, signatures) exactly.
// IDs are stable forever: the runtime dispatches on them, so this table is
// part of the module ABI. Never renumber, never reuse retired IDs.
//
// Each row: id, name, category, tier, return type, requiresVariableTarget,
// then "type name" parameter pairs.

using System;
using System.Collections.Generic;

namespace MyFSM.Core
{
    public sealed class FunctionOverload
    {
        public readonly ushort Id;
        public readonly string Name;
        public readonly string Category;
        public readonly byte Tier;
        public readonly string ReturnType;
        public readonly bool RequiresVariableTarget;
        public readonly string[] ParamTypes;
        public readonly string[] ParamNames;

        public FunctionOverload(ushort id, string name, string category, byte tier,
                                string returnType, bool requiresVariableTarget,
                                string[] paramTypes, string[] paramNames)
        {
            Id = id;
            Name = name;
            Category = category;
            Tier = tier;
            ReturnType = returnType;
            RequiresVariableTarget = requiresVariableTarget;
            ParamTypes = paramTypes;
            ParamNames = paramNames;
        }

        public int Arity { get { return ParamTypes.Length; } }

        public override string ToString()
        {
            String s = Name + "(";
            for (int i = 0; i < ParamTypes.Length; i++)
            {
                if (i > 0) s += ", ";
                s += ParamTypes[i] + " " + ParamNames[i];
            }
            return s + ") -> " + ReturnType + " [0x" + Id.ToString("X4") + " T" + Tier + "]";
        }
    }

    public static class FunctionCatalog
    {
        public const int ExpectedCount = 179;
        public const ushort WaitId = 0x0A00;
        public const ushort WaitUntilId = 0x0A01;
        public const ushort EmitId = 0x0A02;

        public static readonly FunctionOverload[] All;
        private static readonly Dictionary<ushort, FunctionOverload> ById =
            new Dictionary<ushort, FunctionOverload>();

        private static FunctionOverload O(ushort id, string name, string category,
                                          byte tier, string ret, bool reqVar,
                                          params string[] ps)
        {
            string[] types = new string[ps.Length];
            string[] names = new string[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                int sp = ps[i].IndexOf(' ');
                types[i] = ps[i].Substring(0, sp);
                names[i] = ps[i].Substring(sp + 1);
            }
            return new FunctionOverload(id, name, category, tier, ret, reqVar, types, names);
        }

        static FunctionCatalog()
        {
            All = new FunctionOverload[]
            {
                // Math (0x0000-0x0018, 25)
                O(0x0000, "sin", "Math", 1, "float", false, "float x"),
                O(0x0001, "cos", "Math", 1, "float", false, "float x"),
                O(0x0002, "tan", "Math", 1, "float", false, "float x"),
                O(0x0003, "asin", "Math", 1, "float", false, "float x"),
                O(0x0004, "acos", "Math", 1, "float", false, "float x"),
                O(0x0005, "atan", "Math", 1, "float", false, "float x"),
                O(0x0006, "atan2", "Math", 1, "float", false, "float y", "float x"),
                O(0x0007, "sqrt", "Math", 1, "float", false, "float x"),
                O(0x0008, "pow", "Math", 1, "float", false, "float b", "float exp"),
                O(0x0009, "abs", "Math", 1, "float", false, "float x"),
                O(0x000A, "sign", "Math", 1, "float", false, "float x"),
                O(0x000B, "clamp", "Math", 1, "float", false, "float x", "float lo", "float hi"),
                O(0x000C, "lerp", "Math", 1, "float", false, "float a", "float b", "float t"),
                O(0x000D, "min", "Math", 1, "float", false, "float a", "float b"),
                O(0x000E, "max", "Math", 1, "float", false, "float a", "float b"),
                O(0x000F, "floor", "Math", 1, "float", false, "float x"),
                O(0x0010, "ceil", "Math", 1, "float", false, "float x"),
                O(0x0011, "round", "Math", 1, "float", false, "float x"),
                O(0x0012, "normalize", "Math", 1, "Vector3", false, "Vector3 v"),
                O(0x0013, "dot", "Math", 1, "float", false, "Vector3 a", "Vector3 b"),
                O(0x0014, "cross", "Math", 1, "Vector3", false, "Vector3 a", "Vector3 b"),
                O(0x0015, "distance", "Math", 1, "float", false, "Vector3 a", "Vector3 b"),
                O(0x0016, "magnitude", "Math", 1, "float", false, "Vector3 v"),
                O(0x0017, "random", "Math", 1, "float", false),
                O(0x0018, "randomRange", "Math", 1, "float", false, "float lo", "float hi"),
                // Object (0x0100-0x0115, 22)
                O(0x0100, "getPosition", "Object", 1, "Vector3", false, "Object3D obj"),
                O(0x0101, "getPosition", "Object", 1, "Vector2", false, "Object2D obj"),
                O(0x0102, "getRotation", "Object", 1, "Quaternion", false, "Object3D obj"),
                O(0x0103, "getScale", "Object", 1, "Vector3", false, "Object3D obj"),
                O(0x0104, "getScale", "Object", 1, "Vector2", false, "Object2D obj"),
                O(0x0105, "setPosition", "Object", 2, "void", false, "Object3D obj", "Vector3 pos"),
                O(0x0106, "setPosition", "Object", 2, "void", false, "Object2D obj", "Vector2 pos"),
                O(0x0107, "setRotation", "Object", 2, "void", false, "Object3D obj", "Quaternion rot"),
                O(0x0108, "setScale", "Object", 2, "void", false, "Object3D obj", "Vector3 scale"),
                O(0x0109, "setScale", "Object", 2, "void", false, "Object2D obj", "Vector2 scale"),
                O(0x010A, "distanceTo", "Object", 1, "float", false, "Object3D a", "Object3D b"),
                O(0x010B, "distanceTo", "Object", 1, "float", false, "Object2D a", "Object2D b"),
                O(0x010C, "directionTo", "Object", 1, "Vector3", false, "Object3D a", "Object3D b"),
                O(0x010D, "directionTo", "Object", 1, "Vector2", false, "Object2D a", "Object2D b"),
                O(0x010E, "isActive", "Object", 1, "bool", false, "Object3D obj"),
                O(0x010F, "isActive", "Object", 1, "bool", false, "Object2D obj"),
                O(0x0110, "setActive", "Object", 2, "void", false, "Object3D obj", "bool active"),
                O(0x0111, "setActive", "Object", 2, "void", false, "Object2D obj", "bool active"),
                O(0x0112, "getTag", "Object", 1, "int", false, "Object3D obj"),
                O(0x0113, "getTag", "Object", 1, "int", false, "Object2D obj"),
                O(0x0114, "getLayer", "Object", 1, "int", false, "Object3D obj"),
                O(0x0115, "getLayer", "Object", 1, "int", false, "Object2D obj"),
                // Sprite (0x0200-0x020B, 12)
                O(0x0200, "getColor", "Sprite", 1, "Vector3", false, "Sprite3D spr"),
                O(0x0201, "getColor", "Sprite", 1, "Vector3", false, "Sprite2D spr"),
                O(0x0202, "setColor", "Sprite", 2, "void", false, "Sprite3D spr", "Vector3 rgb"),
                O(0x0203, "setColor", "Sprite", 2, "void", false, "Sprite2D spr", "Vector3 rgb"),
                O(0x0204, "isVisible", "Sprite", 1, "bool", false, "Sprite3D spr"),
                O(0x0205, "isVisible", "Sprite", 1, "bool", false, "Sprite2D spr"),
                O(0x0206, "setVisible", "Sprite", 2, "void", false, "Sprite3D spr", "bool visible"),
                O(0x0207, "setVisible", "Sprite", 2, "void", false, "Sprite2D spr", "bool visible"),
                O(0x0208, "getBounds", "Sprite", 1, "Vector3", false, "Sprite3D spr"),
                O(0x0209, "getBounds", "Sprite", 1, "Vector2", false, "Sprite2D spr"),
                O(0x020A, "getSize", "Sprite", 1, "Vector2", false, "Sprite3D spr"),
                O(0x020B, "getSize", "Sprite", 1, "Vector2", false, "Sprite2D spr"),
                // Animation (0x0300-0x0311, 18)
                O(0x0300, "play", "Animation", 2, "void", false, "AnimationController3D ctrl"),
                O(0x0301, "play", "Animation", 2, "void", false, "AnimationController2D ctrl"),
                O(0x0302, "stop", "Animation", 2, "void", false, "AnimationController3D ctrl"),
                O(0x0303, "stop", "Animation", 2, "void", false, "AnimationController2D ctrl"),
                O(0x0304, "pause", "Animation", 2, "void", false, "AnimationController3D ctrl"),
                O(0x0305, "pause", "Animation", 2, "void", false, "AnimationController2D ctrl"),
                O(0x0306, "resume", "Animation", 2, "void", false, "AnimationController3D ctrl"),
                O(0x0307, "resume", "Animation", 2, "void", false, "AnimationController2D ctrl"),
                O(0x0308, "isPlaying", "Animation", 1, "bool", false, "AnimationController3D ctrl"),
                O(0x0309, "isPlaying", "Animation", 1, "bool", false, "AnimationController2D ctrl"),
                O(0x030A, "getCurrentClip", "Animation", 1, "int", false, "AnimationController3D ctrl"),
                O(0x030B, "getCurrentClip", "Animation", 1, "int", false, "AnimationController2D ctrl"),
                O(0x030C, "setSpeed", "Animation", 2, "void", false, "AnimationController3D ctrl", "float speed"),
                O(0x030D, "setSpeed", "Animation", 2, "void", false, "AnimationController2D ctrl", "float speed"),
                O(0x030E, "getProgress", "Animation", 1, "float", false, "AnimationController3D ctrl"),
                O(0x030F, "getProgress", "Animation", 1, "float", false, "AnimationController2D ctrl"),
                O(0x0310, "setAnimation", "Animation", 2, "void", false, "AnimationController3D ctrl", "int clip"),
                O(0x0311, "setAnimation", "Animation", 2, "void", false, "AnimationController2D ctrl", "int clip"),
                // Physics (0x0400-0x0411, 18)
                O(0x0400, "getVelocity", "Physics", 1, "Vector3", false, "PhysicsObject3D phys"),
                O(0x0401, "getVelocity", "Physics", 1, "Vector2", false, "PhysicsObject2D phys"),
                O(0x0402, "setVelocity", "Physics", 2, "void", false, "PhysicsObject3D phys", "Vector3 v"),
                O(0x0403, "setVelocity", "Physics", 2, "void", false, "PhysicsObject2D phys", "Vector2 v"),
                O(0x0404, "getMass", "Physics", 1, "float", false, "PhysicsObject3D phys"),
                O(0x0405, "getMass", "Physics", 1, "float", false, "PhysicsObject2D phys"),
                O(0x0406, "applyForce", "Physics", 2, "void", false, "PhysicsObject3D phys", "Vector3 force"),
                O(0x0407, "applyForce", "Physics", 2, "void", false, "PhysicsObject2D phys", "Vector2 force"),
                O(0x0408, "applyImpulse", "Physics", 2, "void", false, "PhysicsObject3D phys", "Vector3 impulse"),
                O(0x0409, "applyImpulse", "Physics", 2, "void", false, "PhysicsObject2D phys", "Vector2 impulse"),
                O(0x040A, "isGrounded", "Physics", 1, "bool", false, "PhysicsObject3D phys"),
                O(0x040B, "isGrounded", "Physics", 1, "bool", false, "PhysicsObject2D phys"),
                O(0x040C, "isColliding", "Physics", 1, "bool", false, "PhysicsObject3D phys"),
                O(0x040D, "isColliding", "Physics", 1, "bool", false, "PhysicsObject2D phys"),
                O(0x040E, "getCollisionNormal", "Physics", 1, "Vector3", false, "PhysicsObject3D phys"),
                O(0x040F, "getCollisionNormal", "Physics", 1, "Vector2", false, "PhysicsObject2D phys"),
                O(0x0410, "raycast", "Physics", 1, "bool", false, "Vector3 origin", "Vector3 dir", "float dist"),
                O(0x0411, "raycast", "Physics", 1, "bool", false, "Vector2 origin", "Vector2 dir", "float dist"),
                // Camera (0x0500-0x0509, 10)
                O(0x0500, "getPosition", "Camera", 1, "Vector3", false, "Camera3D cam"),
                O(0x0501, "getPosition", "Camera", 1, "Vector2", false, "Camera2D cam"),
                O(0x0502, "isInView", "Camera", 1, "bool", false, "Camera3D cam", "Object3D obj"),
                O(0x0503, "isInView", "Camera", 1, "bool", false, "Camera2D cam", "Object2D obj"),
                O(0x0504, "screenToWorld", "Camera", 1, "Vector3", false, "Camera3D cam", "Vector2 screen"),
                O(0x0505, "screenToWorld", "Camera", 1, "Vector2", false, "Camera2D cam", "Vector2 screen"),
                O(0x0506, "worldToScreen", "Camera", 1, "Vector2", false, "Camera3D cam", "Vector3 world"),
                O(0x0507, "worldToScreen", "Camera", 1, "Vector2", false, "Camera2D cam", "Vector3 world"),
                O(0x0508, "getViewport", "Camera", 1, "Vector2", false, "Camera3D cam"),
                O(0x0509, "getViewport", "Camera", 1, "Vector2", false, "Camera2D cam"),
                // Navigation (0x0600-0x062C, 45)
                O(0x0600, "findPath", "Navigation", 1, "int", false, "Vector3 from", "Vector3 to"),
                O(0x0601, "findPath", "Navigation", 1, "int", false, "Vector2 from", "Vector2 to"),
                O(0x0602, "getNextWaypoint", "Navigation", 1, "Vector3", false, "int path"),
                O(0x0603, "getPathLength", "Navigation", 1, "float", false, "int path"),
                O(0x0604, "hasReachedDestination", "Navigation", 1, "bool", false, "NavMeshAgent agent", "Vector3 tgt"),
                O(0x0605, "hasReachedDestination", "Navigation", 1, "bool", false, "NavMeshAgent agent", "Object3D tgt"),
                O(0x0606, "hasReachedDestination", "Navigation", 1, "bool", false, "Object3D agent", "Vector3 tgt"),
                O(0x0607, "hasReachedDestination", "Navigation", 1, "bool", false, "Object3D agent", "Object3D tgt"),
                O(0x0608, "hasReachedDestination", "Navigation", 1, "bool", false, "Object2D agent", "Vector2 tgt"),
                O(0x0609, "hasReachedDestination", "Navigation", 1, "bool", false, "Object2D agent", "Object2D tgt"),
                O(0x060A, "goTo", "Navigation", 3, "void", true, "NavMeshAgent agent", "Vector3 dest"),
                O(0x060B, "goTo", "Navigation", 3, "void", true, "NavMeshAgent agent", "Object3D dest"),
                O(0x060C, "goTo", "Navigation", 3, "void", true, "Object3D agent", "Vector3 dest"),
                O(0x060D, "goTo", "Navigation", 3, "void", true, "Object3D agent", "Object3D dest"),
                O(0x060E, "goTo", "Navigation", 3, "void", true, "Object2D agent", "Vector2 dest"),
                O(0x060F, "goTo", "Navigation", 3, "void", true, "Object2D agent", "Object2D dest"),
                O(0x0610, "followTarget", "Navigation", 3, "void", true, "NavMeshAgent agent", "Object3D tgt"),
                O(0x0611, "followTarget", "Navigation", 3, "void", true, "NavMeshAgent agent", "Object2D tgt"),
                O(0x0612, "followTarget", "Navigation", 3, "void", true, "Object3D agent", "Object3D tgt"),
                O(0x0613, "followTarget", "Navigation", 3, "void", true, "Object2D agent", "Object2D tgt"),
                O(0x0614, "findShortestPathAndMove", "Navigation", 3, "void", true, "NavMeshAgent agent", "Vector3 tgt"),
                O(0x0615, "findShortestPathAndMove", "Navigation", 3, "void", true, "NavMeshAgent agent", "Object3D tgt"),
                O(0x0616, "findShortestPathAndMove", "Navigation", 3, "void", true, "Object3D agent", "Vector3 tgt"),
                O(0x0617, "findShortestPathAndMove", "Navigation", 3, "void", true, "Object3D agent", "Object3D tgt"),
                O(0x0618, "findShortestPathAndMove", "Navigation", 3, "void", true, "Object2D agent", "Vector2 tgt"),
                O(0x0619, "findShortestPathAndMove", "Navigation", 3, "void", true, "Object2D agent", "Object2D tgt"),
                O(0x061A, "follow", "Navigation", 3, "void", true, "NavMeshAgent agent", "Object3D tgt"),
                O(0x061B, "follow", "Navigation", 3, "void", true, "NavMeshAgent agent", "Object2D tgt"),
                O(0x061C, "follow", "Navigation", 3, "void", true, "Object3D agent", "Object3D tgt"),
                O(0x061D, "follow", "Navigation", 3, "void", true, "Object2D agent", "Object2D tgt"),
                O(0x061E, "sprintTowards", "Navigation", 3, "void", true, "NavMeshAgent agent", "Vector3 dest", "float speedMult"),
                O(0x061F, "sprintTowards", "Navigation", 3, "void", true, "NavMeshAgent agent", "Object3D dest", "float speedMult"),
                O(0x0620, "sprintTowards", "Navigation", 3, "void", true, "Object3D agent", "Vector3 dest", "float speedMult"),
                O(0x0621, "sprintTowards", "Navigation", 3, "void", true, "Object3D agent", "Object3D dest", "float speedMult"),
                O(0x0622, "sprintTowards", "Navigation", 3, "void", true, "Object2D agent", "Vector2 dest", "float speedMult"),
                O(0x0623, "sprintTowards", "Navigation", 3, "void", true, "Object2D agent", "Object2D dest", "float speedMult"),
                O(0x0624, "moveTowards", "Navigation", 3, "void", true, "NavMeshAgent agent", "Vector3 dest", "float speed"),
                O(0x0625, "moveTowards", "Navigation", 3, "void", true, "NavMeshAgent agent", "Object3D dest", "float speed"),
                O(0x0626, "moveTowards", "Navigation", 3, "void", true, "Object3D agent", "Vector3 dest", "float speed"),
                O(0x0627, "moveTowards", "Navigation", 3, "void", true, "Object3D agent", "Object3D dest", "float speed"),
                O(0x0628, "moveTowards", "Navigation", 3, "void", true, "Object2D agent", "Vector2 dest", "float speed"),
                O(0x0629, "moveTowards", "Navigation", 3, "void", true, "Object2D agent", "Object2D dest", "float speed"),
                O(0x062A, "stopMovement", "Navigation", 3, "void", true, "NavMeshAgent agent"),
                O(0x062B, "stopMovement", "Navigation", 3, "void", true, "Object3D agent"),
                O(0x062C, "stopMovement", "Navigation", 3, "void", true, "Object2D agent"),
                // Perception (0x0700-0x070D, 14)
                O(0x0700, "lookAt", "Perception", 3, "void", true, "Object3D src", "Object3D tgt"),
                O(0x0701, "lookAt", "Perception", 3, "void", true, "Object2D src", "Object2D tgt"),
                O(0x0702, "isInLineOfSight", "Perception", 1, "bool", false, "Object3D src", "Object3D tgt"),
                O(0x0703, "isInLineOfSight", "Perception", 1, "bool", false, "Object2D src", "Object2D tgt"),
                O(0x0704, "isInRange", "Perception", 1, "bool", false, "Object3D src", "Object3D tgt", "float radius"),
                O(0x0705, "isInRange", "Perception", 1, "bool", false, "Object2D src", "Object2D tgt", "float radius"),
                O(0x0706, "getAngleTo", "Perception", 1, "float", false, "Object3D src", "Object3D tgt"),
                O(0x0707, "getAngleTo", "Perception", 1, "float", false, "Object2D src", "Object2D tgt"),
                O(0x0708, "getDistanceTo", "Perception", 1, "float", false, "Object3D src", "Object3D tgt"),
                O(0x0709, "getDistanceTo", "Perception", 1, "float", false, "Object2D src", "Object2D tgt"),
                O(0x070A, "getNearestOfTag", "Perception", 1, "Object3D", false, "Vector3 pos", "int tag", "float radius"),
                O(0x070B, "getNearestOfTag", "Perception", 1, "Object2D", false, "Vector2 pos", "int tag", "float radius"),
                O(0x070C, "getAllInRadius", "Perception", 1, "int", false, "Vector3 pos", "float radius"),
                O(0x070D, "getAllInRadius", "Perception", 1, "int", false, "Vector2 pos", "float radius"),
                // Steering (0x0800-0x0809, 10)
                O(0x0800, "getFleeDirection", "Steering", 1, "Vector3", false, "Vector3 from", "Vector3 threat"),
                O(0x0801, "getFleeDirection", "Steering", 1, "Vector2", false, "Vector2 from", "Vector2 threat"),
                O(0x0802, "getPursuitPosition", "Steering", 1, "Vector3", false, "Object3D tgt", "float speed"),
                O(0x0803, "getPursuitPosition", "Steering", 1, "Vector2", false, "Object2D tgt", "float speed"),
                O(0x0804, "getSeparationVector", "Steering", 1, "Vector3", false, "Object3D agent", "int neighbors"),
                O(0x0805, "getSeparationVector", "Steering", 1, "Vector2", false, "Object2D agent", "int neighbors"),
                O(0x0806, "getArrivalVector", "Steering", 1, "Vector3", false, "Vector3 pos", "Vector3 dest", "float slowRadius"),
                O(0x0807, "getArrivalVector", "Steering", 1, "Vector2", false, "Vector2 pos", "Vector2 dest", "float slowRadius"),
                O(0x0808, "getWanderVector", "Steering", 1, "Vector3", false, "Vector3 pos", "float radius"),
                O(0x0809, "getWanderVector", "Steering", 1, "Vector2", false, "Vector2 pos", "float radius"),
                // Sensing (0x0900-0x0901, 2)
                O(0x0900, "getRaycastHit", "Sensing", 1, "Object3D", false, "Vector3 origin", "Vector3 dir", "float dist"),
                O(0x0901, "getRaycastHit", "Sensing", 1, "Object2D", false, "Vector2 origin", "Vector2 dir", "float dist"),
                // Control (0x0A00-0x0A02, 3)
                O(0x0A00, "wait", "Control", 3, "void", false, "float seconds"),
                O(0x0A01, "waitUntil", "Control", 3, "void", false, "bool condition"),
                O(0x0A02, "emit", "Control", 2, "void", false, "int eventId"),
            };
            for (int i = 0; i < All.Length; i++)
            {
                ById[All[i].Id] = All[i];
            }
        }

        public static FunctionOverload FindById(ushort id)
        {
            FunctionOverload o;
            return ById.TryGetValue(id, out o) ? o : null;
        }

        public static List<FunctionOverload> FindByName(string name)
        {
            List<FunctionOverload> result = new List<FunctionOverload>();
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Name == name) result.Add(All[i]);
            }
            return result;
        }
    }
}
