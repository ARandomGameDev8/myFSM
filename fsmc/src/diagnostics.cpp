#include "diagnostics.hpp"

#include <sstream>

namespace fsmc {

void Diagnostics::error(SrcLoc loc, const std::string& message) {
    items_.push_back(Diagnostic{loc, message, false});
    ++errors_;
}

void Diagnostics::warn(SrcLoc loc, const std::string& message) {
    items_.push_back(Diagnostic{loc, message, true});
    ++warnings_;
}

std::string Diagnostics::format(const std::string& fileName, const Diagnostic& d) {
    std::ostringstream os;
    os << fileName << ":" << d.loc.line << ":" << d.loc.col << ": "
       << (d.warning ? "warning" : "error") << ": " << d.message;
    return os.str();
}

void Diagnostics::print(std::ostream& os) const {
    for (const Diagnostic& d : items_) {
        os << format(fileName_, d) << "\n";
    }
    os << (errors_ == 1 ? "1 error" : std::to_string(errors_) + " errors") << ", "
       << (warnings_ == 1 ? "1 warning" : std::to_string(warnings_) + " warnings") << "\n";
}

} // namespace fsmc
