#include "const_fold.hpp"

#include <cmath>

#include "binary_format.hpp"
#include "builtin_types.hpp"
#include "type_rules.hpp"

namespace fsmc {

void ConstPool::set(const std::string& name, const GlobalVar& var) {
    if (index_.count(name)) return;
    index_.emplace(name, items_.size());
    items_.emplace_back(name, &var);
}

const GlobalVar* ConstPool::find(const std::string& name) const {
    auto it = index_.find(name);
    if (it == index_.end()) return nullptr;
    return items_[it->second].second;
}

namespace {

bool isInt(const TypeDefinition* t) { return t && t->name == "int"; }
bool isFloat(const TypeDefinition* t) { return t && t->name == "float"; }
bool isDouble(const TypeDefinition* t) { return t && t->name == "double"; }
bool isBool(const TypeDefinition* t) { return t && t->name == "bool"; }
bool isVector(const TypeDefinition* t) { return t && t->isNumeric && t->category == "vector"; }

bool intMul(int64_t a, int64_t b, int64_t& out) {
    if (a == 0 || b == 0) { out = 0; return true; }
    if (a == INT64_MAX / b || b == INT64_MAX / a) {
        out = a * b;
        return out >= INT64_MIN && out <= INT64_MAX;
    }
    if (a == INT64_MIN / b || b == INT64_MIN / a) {
        out = a * b;
        return out >= INT64_MIN && out <= INT64_MAX;
    }
    // Sign-aware range check
    bool neg = (a < 0) != (b < 0);
    uint64_t aa = a < 0 ? 0 - static_cast<uint64_t>(a) : static_cast<uint64_t>(a);
    uint64_t bb = b < 0 ? 0 - static_cast<uint64_t>(b) : static_cast<uint64_t>(b);
    const uint64_t limit = neg ? 0 - static_cast<uint64_t>(INT64_MIN) : static_cast<uint64_t>(INT64_MAX);
    if (aa > limit / bb) return false;
    out = a * b;
    return true;
}

} // namespace

bool foldExpr(const Expr* e, ConstPool& pool, ConstValue& out, std::string& err) {
    if (!e) { err = "null expression"; return false; }
    const auto& types = BuiltinTypes::instance();

    switch (e->kind) {
        case Expr::Kind::Literal: {
            if (!e->type) { err = "untyped literal"; return false; }
            out.type = e->type;
            switch (e->type->typeTag) {
                case 0x01: out.i = e->litInt; break;
                case 0x02: out.f = static_cast<float>(e->litReal); break;
                case 0x03: out.d = e->litReal; break;
                case 0x04: out.b = e->litBool; break;
                default:
                    if (isVector(e->type)) {
                        out.v = e->litVec;
                        out.vcount = e->litVecCount;
                    } else {
                        err = "literal of non-constant type";
                        return false;
                    }
            }
            return true;
        }

        case Expr::Kind::VarRef: {
            if (e->var.kind != VarKind::GlobalConst) {
                err = "references non-constant variable '" + e->var.name + "'";
                return false;
            }
            const GlobalVar* g = pool.find(e->var.name);
            if (!g) {
                err = "constant '" + e->var.name + "' is not declared yet";
                return false;
            }
            // Reconstruct the value from the stored little-endian bytes.
            const std::vector<uint8_t>& raw = g->constBytes;
            if (raw.size() != g->type->sizeBytes) {
                err = "internal: malformed constant '" + g->name + "'";
                return false;
            }
            out.type = g->type;
            std::size_t p = 0;
            auto rd8 = [&]() { uint8_t v = raw[p++]; return v; };
            auto rd32 = [&]() {
                uint32_t v = uint32_t(rd8()) | (uint32_t(rd8()) << 8) |
                             (uint32_t(rd8()) << 16) | (uint32_t(rd8()) << 24);
                return v;
            };
            auto rd64 = [&]() {
                uint64_t lo = rd32(), hi = rd32();
                return lo | (hi << 32);
            };
            switch (g->type->typeTag) {
                case 0x01: out.i = static_cast<int32_t>(rd32()); break;
                case 0x02: out.f = fmt::bitsToFloat(rd32()); break;
                case 0x03: out.d = fmt::bitsToDouble(rd64()); break;
                case 0x04: out.b = rd8() != 0; break;
                case 0x10: out.v[0] = fmt::bitsToFloat(rd32()); out.v[1] = fmt::bitsToFloat(rd32()); out.vcount = 2; break;
                case 0x11:
                    for (int k = 0; k < 3; ++k) out.v[k] = fmt::bitsToFloat(rd32());
                    out.vcount = 3;
                    break;
                case 0x12:
                    for (int k = 0; k < 4; ++k) out.v[k] = fmt::bitsToFloat(rd32());
                    out.vcount = 4;
                    break;
                default:
                    err = "constant '" + g->name + "' of type " + g->type->name +
                          " has no literal value";
                    return false;
            }
            return true;
        }

        case Expr::Kind::Call:
            err = "calls are not constant expressions";
            return false;
        case Expr::Kind::Binary:
        case Expr::Kind::Unary:
            break;
    }

    if (e->kind == Expr::Kind::Unary) {
        ConstValue a;
        if (!foldExpr(e->left, pool, a, err)) return false;
        if (e->op == fmt::OpNot) {
            if (!isBool(a.type)) { err = "'!' applied to non-bool constant"; return false; }
            out.type = a.type;
            out.b = !a.b;
            return true;
        }
        if (e->op == fmt::OpMinus) {
            if (!a.type->isNumeric) { err = "'-' applied to non-numeric constant"; return false; }
            out.type = a.type;
            if (isInt(a.type)) {
                if (a.i == INT32_MIN) { err = "negating INT_MIN overflows"; return false; }
                out.i = -a.i;
            } else if (isFloat(a.type)) {
                out.f = -a.f;
            } else if (isDouble(a.type)) {
                out.d = -a.d;
            } else if (isVector(a.type)) {
                out.v = a.v;
                out.vcount = a.vcount;
                for (int k = 0; k < a.vcount; ++k) out.v[k] = -out.v[k];
            }
            return true;
        }
        err = "unknown unary operator";
        return false;
    }

    // Binary
    ConstValue l, r;
    if (!foldExpr(e->left, pool, l, err)) return false;
    if (!foldExpr(e->right, pool, r, err)) return false;

    const TypeDefinition* lt = l.type, * rt = r.type;

    // --- bool logic ---
    if (e->op == fmt::OpLogicalAnd || e->op == fmt::OpLogicalOr) {
        if (!isBool(lt) || !isBool(rt)) { err = "logical op on non-bool constants"; return false; }
        out.type = lt;
        out.b = e->op == fmt::OpLogicalAnd ? (l.b && r.b) : (l.b || r.b);
        return true;
    }

    // --- comparisons ---
    if (e->op == fmt::OpEqEq || e->op == fmt::OpNotEq || e->op == fmt::OpLt ||
        e->op == fmt::OpGt || e->op == fmt::OpLe || e->op == fmt::OpGe) {
        bool eq = false;
        if (isBool(lt) && isBool(rt)) {
            eq = l.b == r.b;
        } else if (isInt(lt) && isInt(rt)) {
            eq = l.i == r.i;
        } else if (isFloat(lt) && isFloat(rt)) {
            eq = l.f == r.f;
        } else if (isDouble(lt) && isDouble(rt)) {
            eq = l.d == r.d;
        } else if (isVector(lt) && isVector(rt) && lt == rt) {
            if (l.vcount != r.vcount) { err = "vector constants of different rank"; return false; }
            eq = true;
            for (int k = 0; k < l.vcount; ++k) if (l.v[k] != r.v[k]) { eq = false; break; }
        } else {
            err = "comparison of incompatible constant types";
            return false;
        }
        if (e->op == fmt::OpEqEq) { out.type = types.find("bool"); out.b = eq; return true; }
        if (e->op == fmt::OpNotEq) { out.type = types.find("bool"); out.b = !eq; return true; }
        // relational: scalars only
        int cmp = 0;
        if (isInt(lt) && isInt(rt)) cmp = (l.i < r.i) - (l.i > r.i);
        else if (isFloat(lt) && isFloat(rt)) cmp = (l.f < r.f) - (l.f > r.f);
        else if (isDouble(lt) && isDouble(rt)) cmp = (l.d < r.d) - (l.d > r.d);
        else { err = "relational op on non-scalar constants"; return false; }
        out.type = types.find("bool");
        switch (e->op) {
            case fmt::OpLt: out.b = cmp < 0; break;
            case fmt::OpGt: out.b = cmp > 0; break;
            case fmt::OpLe: out.b = cmp <= 0; break;
            case fmt::OpGe: out.b = cmp >= 0; break;
            default: break;
        }
        return true;
    }

    // --- vector arithmetic / scalar-mixed ---
    if (isVector(lt) || isVector(rt)) {
        if (lt == rt && isVector(lt)) {
            out.type = lt;
            out.vcount = l.vcount;
            if (e->op == fmt::OpPlus) {
                for (int k = 0; k < l.vcount; ++k) out.v[k] = l.v[k] + r.v[k];
                return true;
            }
            if (e->op == fmt::OpMinus) {
                for (int k = 0; k < l.vcount; ++k) out.v[k] = l.v[k] - r.v[k];
                return true;
            }
            err = "unsupported vector operation in constant expression";
            return false;
        }
        // scalar * vector
        if (e->op == fmt::OpStar) {
            const ConstValue* vec = isVector(lt) ? &l : &r;
            const ConstValue* s = isVector(lt) ? &r : &l;
            out.type = vec->type;
            out.vcount = vec->vcount;
            for (int k = 0; k < vec->vcount; ++k) {
                if (isInt(s->type)) out.v[k] = vec->v[k] * static_cast<float>(s->i);
                else if (isFloat(s->type)) out.v[k] = vec->v[k] * s->f;
                else out.v[k] = vec->v[k] * static_cast<float>(s->d);
            }
            return true;
        }
        err = "vector and scalar cannot be combined (only '*' supports scalar-vector)";
        return false;
    }

    // --- scalar arithmetic ---
    const TypeDefinition* result = binaryOpType(e->op, lt, rt, err, "?");
    if (!result) return false;
    out.type = result;

    auto asD = [](const ConstValue& v) {
        if (isInt(v.type)) return static_cast<double>(v.i);
        if (isFloat(v.type)) return static_cast<double>(v.f);
        return v.d;
    };
    auto store = [&](double d) {
        if (isInt(out.type)) {
            if (d < static_cast<double>(INT32_MIN) || d > static_cast<double>(INT32_MAX)) {
                err = "integer constant overflow";
                return;
            }
            out.i = static_cast<int32_t>(d);
        } else if (isFloat(out.type)) {
            out.f = static_cast<float>(d);
        } else {
            out.d = d;
        }
    };

    double a = asD(l), b = asD(r);
    switch (e->op) {
        case fmt::OpPlus: store(a + b); return true;
        case fmt::OpMinus: store(a - b); return true;
        case fmt::OpStar: {
            if (isInt(lt) && isInt(rt)) {
                int64_t prod;
                if (!intMul(l.i, r.i, prod)) { err = "integer constant overflow"; return false; }
                out.i = static_cast<int32_t>(prod);
                return true;
            }
            store(a * b);
            return true;
        }
        case fmt::OpSlash:
            if (b == 0.0) { err = "division by zero in constant expression"; return false; }
            store(a / b);
            return true;
        case fmt::OpFloorDiv: {
            if (!isInt(lt) || !isInt(rt)) { err = "'//' requires int constants"; return false; }
            if (r.i == 0) { err = "division by zero in constant expression"; return false; }
            int64_t q = l.i / r.i;
            int64_t rem = l.i % r.i;
            if (rem != 0 && ((l.i < 0) != (r.i < 0))) --q;
            out.i = static_cast<int32_t>(q);
            return true;
        }
        case fmt::OpPow: {
            if (isInt(lt) && isInt(rt)) {
                if (r.i < 0) { err = "negative exponent for integer constant power"; return false; }
                int64_t acc = 1;
                for (int64_t k = 0; k < r.i; ++k) {
                    int64_t next;
                    if (!intMul(acc, l.i, next)) { err = "integer constant overflow"; return false; }
                    acc = next;
                }
                out.i = static_cast<int32_t>(acc);
                return true;
            }
            store(std::pow(a, b));
            return true;
        }
        default:
            err = "unsupported operator in constant expression";
            return false;
    }
}

} // namespace fsmc
