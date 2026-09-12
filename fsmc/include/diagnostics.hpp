#pragma once

#include <ostream>
#include <string>
#include <vector>

#include "source.hpp"

namespace fsmc {

struct Diagnostic {
    SrcLoc loc{1, 1};
    std::string message;
    bool warning = false;
};

// Collects errors and warnings with file:line:col positions.
// Diagnostics are stored in generation order (deterministic).
class Diagnostics {
public:
    explicit Diagnostics(std::string fileName = "<source>")
        : fileName_(std::move(fileName)) {}

    void error(SrcLoc loc, const std::string& message);
    void warn(SrcLoc loc, const std::string& message);

    bool hasErrors() const { return errors_ > 0; }
    int errorCount() const { return errors_; }
    int warningCount() const { return warnings_; }

    const std::vector<Diagnostic>& all() const { return items_; }
    const std::string& fileName() const { return fileName_; }

    // Formats one diagnostic as "file:line:col: error: message".
    static std::string format(const std::string& fileName, const Diagnostic& d);

    // Prints all diagnostics (in order) to the stream.
    void print(std::ostream& os) const;

private:
    std::string fileName_;
    std::vector<Diagnostic> items_;
    int errors_ = 0;
    int warnings_ = 0;
};

} // namespace fsmc
