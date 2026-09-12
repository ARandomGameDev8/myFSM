#pragma once

namespace fsmc {

// 1-based source position for diagnostics.
struct SrcLoc {
    int line = 0;
    int col = 0;
};

} // namespace fsmc
