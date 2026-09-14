#pragma once

// C block scoping for temporary variables.
//
// Standard scope stack: push a frame on '{', pop on '}', declarations write to
// the top frame, lookup walks frames top-to-bottom, popping discards the
// frame's symbols. Inner frames may shadow outer names.

#include <cstddef>
#include <string>
#include <vector>

namespace fsmc {

class ScopeStack {
public:
    void push();
    void pop();

    // Walks frames top-to-bottom. Returns nullptr when not visible.
    const int* find(const std::string& name) const;

    // Declares in the top frame. Returns false when the name is already
    // declared in the TOP frame (redeclaration) or no frame is open.
    bool declare(const std::string& name, int varId);

    // Number of open frames (0 at file level).
    std::size_t depth() const { return frames_.size(); }

    bool empty() const { return frames_.empty(); }

private:
    struct Frame {
        std::vector<std::pair<std::string, int>> decls; // (name -> temp id), in order
    };
    std::vector<Frame> frames_;
};

} // namespace fsmc
