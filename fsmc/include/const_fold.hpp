#pragma once

// Compile-time constant evaluation for static-constant initializers and
// vector-literal components.

#include <cstddef>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include "ast.hpp"

namespace fsmc {

// Declared static constants available to the folder. Lookup is by name; only
// previously declared constants may be referenced (enforced by construction).
class ConstPool {
public:
    void set(const std::string& name, const GlobalVar& var);
    const GlobalVar* find(const std::string& name) const;
    std::size_t size() const { return items_.size(); }

private:
    std::vector<std::pair<std::string, const GlobalVar*>> items_;
    std::unordered_map<std::string, std::size_t> index_;
};

// Folds `e` into a compile-time constant. Returns false with err set when the
// expression is not a constant expression (e.g. it references a runtime
// variable or a function call, or divides by zero).
bool foldExpr(const Expr* e, ConstPool& pool, ConstValue& out, std::string& err);

} // namespace fsmc
