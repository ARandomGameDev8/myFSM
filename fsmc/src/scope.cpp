#include "scope.hpp"

namespace fsmc {

int ScopeStack::push() {
    Frame f;
    f.id = nextFrameId_++;
    frames_.push_back(f);
    return f.id;
}

void ScopeStack::pop() {
    if (!frames_.empty()) frames_.pop_back();
}

const int* ScopeStack::find(const std::string& name) const {
    for (auto it = frames_.rbegin(); it != frames_.rend(); ++it) {
        const Frame& frame = *it;
        for (auto rit = frame.decls.rbegin(); rit != frame.decls.rend(); ++rit) {
            if (rit->first == name) return &rit->second;
        }
    }
    return nullptr;
}

bool ScopeStack::declare(const std::string& name, int varId) {
    if (frames_.empty()) return false;
    Frame& top = frames_.back();
    for (const auto& d : top.decls) {
        if (d.first == name) return false; // redeclaration in this block
    }
    top.decls.emplace_back(name, varId);
    return true;
}

int ScopeStack::currentFrameId() const {
    return frames_.empty() ? -1 : frames_.back().id;
}

} // namespace fsmc
