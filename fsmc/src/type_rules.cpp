#include "type_rules.hpp"

#include "binary_format.hpp"
#include "builtin_types.hpp"

namespace fsmc {

namespace {

const TypeDefinition* byName(const char* n) { return BuiltinTypes::instance().find(n); }

bool isScalarNumeric(const TypeDefinition* t) {
    return t && t->isNumeric && t->category == "primitive" && t->name != "bool";
}
bool isVector(const TypeDefinition* t) {
    return t && t->isNumeric && t->category == "vector";
}

} // namespace

const TypeDefinition* promoteScalars(const TypeDefinition* a, const TypeDefinition* b) {
    if (!a || !b) return nullptr;
    if (a->typeTag == b->typeTag) return a;
    auto rank = [](uint8_t tag) -> int {
        switch (tag) {
            case 0x01: return 1; // int
            case 0x02: return 2; // float
            case 0x03: return 3; // double
            default: return -1;
        }
    };
    int ra = rank(a->typeTag), rb = rank(b->typeTag);
    if (ra < 0 || rb < 0) return nullptr;
    return ra >= rb ? a : b;
}

const TypeDefinition* binaryOpType(uint8_t opId, const TypeDefinition* a, const TypeDefinition* b,
                                   std::string& errMsg, const char* opSymbol) {
    if (!a || !b) {
        errMsg = "incomplete type information";
        return nullptr;
    }
    auto name = [](const TypeDefinition* t) { return t ? t->name : "?"; };

    // --- logical operators: bool only ---
    if (opId == fmt::OpLogicalAnd || opId == fmt::OpLogicalOr) {
        if (a->name == "bool" && b->name == "bool") return byName("bool");
        errMsg = std::string("logical operator '") + opSymbol +
                 "' requires bool operands, got " + name(a) + " and " + name(b);
        return nullptr;
    }

    // --- equality: exact same type, numeric or bool ---
    if (opId == fmt::OpEqEq || opId == fmt::OpNotEq) {
        if (a->typeTag != b->typeTag) {
            errMsg = "comparison operands must have the same type, got " + name(a) + " and " + name(b);
            return nullptr;
        }
        if (a->isHandle) {
            errMsg = "handles cannot be compared with '" + std::string(opSymbol) +
                     "' (use a dedicated equals function)";
            return nullptr;
        }
        if (!a->isNumeric && a->name != "bool") {
            errMsg = "comparison operands must be numeric or bool, got " + name(a);
            return nullptr;
        }
        return byName("bool");
    }

    // --- relational: numeric, same type for vectors, scalar promotion ---
    if (opId == fmt::OpLt || opId == fmt::OpGt || opId == fmt::OpLe || opId == fmt::OpGe) {
        if (a->isHandle || b->isHandle) {
            errMsg = "handles cannot be used with '" + std::string(opSymbol) + "'";
            return nullptr;
        }
        if (!a->isNumeric || !b->isNumeric) {
            errMsg = std::string("relational operator '") + opSymbol + "' requires numeric operands, got " +
                     name(a) + " and " + name(b);
            return nullptr;
        }
        if (isVector(a) || isVector(b)) {
            std::string msg = "relational operators are not defined for vectors";
            if (!(isVector(a) && isVector(b) && a->typeTag == b->typeTag)) {
                msg += " (mixed or non-identical vector types)";
            }
            errMsg = msg;
            return nullptr;
        }
        (void)promoteScalars(a, b);
        return byName("bool");
    }

    // --- integer floor division: int // int only ---
    if (opId == fmt::OpFloorDiv) {
        if (a->name == "int" && b->name == "int") return byName("int");
        errMsg = "'//' requires two int operands, got " + name(a) + " and " + name(b);
        return nullptr;
    }

    // --- power: scalar only, result = same type (promoted when mixed) ---
    if (opId == fmt::OpPow) {
        if (!isScalarNumeric(a) || !isScalarNumeric(b)) {
            errMsg = "'**' requires scalar numeric operands, got " + name(a) + " and " + name(b);
            return nullptr;
        }
        return a->typeTag == b->typeTag ? a : promoteScalars(a, b);
    }

    // --- true division: int/int -> float, else promoted ---
    if (opId == fmt::OpSlash) {
        if (a->isHandle || b->isHandle) {
            errMsg = "arithmetic on handles is not allowed";
            return nullptr;
        }
        if (!a->isNumeric || !b->isNumeric) {
            errMsg = std::string("'/' requires numeric operands, got ") + name(a) + " and " + name(b);
            return nullptr;
        }
        if (isVector(a) || isVector(b)) {
            errMsg = "'/' is not defined for vectors";
            return nullptr;
        }
        if (a->name == "bool" || b->name == "bool") {
            errMsg = "'/' is not defined for bool";
            return nullptr;
        }
        if (a->name == "int" && b->name == "int") return byName("float");
        return promoteScalars(a, b);
    }

    // --- add / subtract / multiply ---
    if (opId == fmt::OpPlus || opId == fmt::OpMinus || opId == fmt::OpStar) {
        if (a->isHandle || b->isHandle) {
            errMsg = "arithmetic on handles is not allowed";
            return nullptr;
        }
        if (!a->isNumeric || !b->isNumeric) {
            errMsg = std::string("'") + opSymbol + "' requires numeric operands, got " + name(a) +
                     " and " + name(b);
            return nullptr;
        }
        bool av = isVector(a), bv = isVector(b);
        if (av && bv) {
            if (a->typeTag != b->typeTag) {
                errMsg = "mixed vector types: " + name(a) + " and " + name(b);
                return nullptr;
            }
            if (opId == fmt::OpStar) {
                errMsg = "vector * vector is not supported (use dot() for a dot product)";
                return nullptr;
            }
            return a; // component-wise
        }
        if (av != bv) {
            if (opId == fmt::OpStar) return av ? a : b; // scalar * vector / vector * scalar
            errMsg = std::string("vector and scalar cannot be combined with '") + opSymbol +
                     "' (only '*' supports scalar-vector)";
            return nullptr;
        }
        // scalar + scalar
        if (a->name == "bool" || b->name == "bool") {
            errMsg = std::string("'") + opSymbol + "' is not defined for bool";
            return nullptr;
        }
        return promoteScalars(a, b);
    }

    errMsg = "unknown operator";
    return nullptr;
}

const TypeDefinition* unaryOpType(uint8_t opId, const TypeDefinition* a, std::string& errMsg,
                                  const char* opSymbol) {
    if (!a) {
        errMsg = "incomplete type information";
        return nullptr;
    }
    if (opId == fmt::OpNot) {
        if (a->name == "bool") return byName("bool");
        errMsg = "unary '!' requires a bool operand, got " + a->name;
        return nullptr;
    }
    if (opId == fmt::OpMinus) {
        if (a->isHandle) {
            errMsg = "arithmetic on handles is not allowed";
            return nullptr;
        }
        if (a->isNumeric) return a; // scalars and vectors (component-wise)
        errMsg = std::string("unary '") + opSymbol + "' requires a numeric operand, got " + a->name;
        return nullptr;
    }
    errMsg = "unknown operator";
    return nullptr;
}

} // namespace fsmc
