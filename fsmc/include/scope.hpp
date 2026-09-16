#pragma once

// C block scoping for temporary variables — a pure scope stack.
//
// Every '{' body pushes one frame, every '}' pops it. A declaration writes to
// the frame that is open at that point (its *direct parent block*), lookup
// walks the open frames top-to-bottom, and popping discards that frame's
// symbols: a temporary lives from its declaration to the closing '}' of the
// block that declared it, and is visible in any block nested inside it (where
// it may be shadowed).
//
// There are NO fixed depth levels: the stack has no cap and no level is
// recorded in the AST or in the binary module. Nesting is expressed purely
// structurally — by which frame a name was declared in, and by the parent/child
// edges of the AST tokens. Frames are identified by an id handed out on push,
// which is what a declaration records as its owning scope.

#include <cstddef>
#include <string>
#include <utility>
#include <vector>

namespace fsmc {

class ScopeStack {
public:
    // Opens a frame for a '{' body and returns its id (unique for the whole
    // parse; never reused, so ids of sibling and nested blocks never collide).
    int push();

    // Closes the innermost frame ('}'), discarding every name declared in it.
    void pop();

    // Walks the open frames top-to-bottom (innermost first). Returns nullptr
    // when the name is not visible at this point.
    const int* find(const std::string& name) const;

    // Declares in the top frame — the direct parent block of the declaration.
    // Returns false when the name is already declared in the TOP frame
    // (redeclaration in the same block) or when no frame is open.
    bool declare(const std::string& name, int varId);

    // Id of the innermost open frame (-1 when the stack is empty).
    int currentFrameId() const;

    // Number of open frames. Informational only: it is the live nesting of the
    // stack, never a recorded property of a variable or of the module.
    std::size_t frameCount() const { return frames_.size(); }

    bool empty() const { return frames_.empty(); }

private:
    struct Frame {
        int id = -1;
        std::vector<std::pair<std::string, int>> decls; // (name -> temp id), in order
    };
    std::vector<Frame> frames_;
    int nextFrameId_ = 0;
};

} // namespace fsmc
