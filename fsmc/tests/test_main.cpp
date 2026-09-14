#include "test_framework.hpp"

int main(int argc, char** argv) {
    std::string filter = argc > 1 ? argv[1] : "";
    return ftest::runAll(filter);
}
