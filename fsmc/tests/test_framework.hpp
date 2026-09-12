#pragma once

// Minimal hand-rolled test framework (no external dependencies).
// Run: fsmc_tests [suite-name-filter]

#include <cstdio>
#include <functional>
#include <sstream>
#include <string>
#include <vector>

namespace ftest {

struct Case {
    std::string name;
    std::function<void()> fn;
};

inline std::vector<Case>& registry() {
    static std::vector<Case> r;
    return r;
}

struct Registrar {
    Registrar(std::string name, std::function<void()> fn) {
        registry().push_back({std::move(name), std::move(fn)});
    }
};

inline int& failures() {
    static int f = 0;
    return f;
}

inline void fail(const char* file, int line, const std::string& msg) {
    std::printf("  FAIL %s:%d: %s\n", file, line, msg.c_str());
    ++failures();
}

inline int runAll(const std::string& filter) {
    int run = 0;
    for (const Case& c : registry()) {
        if (!filter.empty() && c.name.find(filter) == std::string::npos) continue;
        int before = failures();
        std::printf("[ RUN  ] %s\n", c.name.c_str());
        try {
            c.fn();
        } catch (const std::exception& e) {
            fail("<exception>", 0, e.what());
        } catch (...) {
            fail("<exception>", 0, "unknown exception");
        }
        std::printf(failures() == before ? "[  OK  ] %s\n" : "[ FAIL ] %s\n", c.name.c_str());
        ++run;
    }
    std::printf("%d test(s) run, %d failure(s)\n", run, failures());
    return failures() == 0 ? 0 : 1;
}

} // namespace ftest

#define TEST(suite, name)                                                                                \
    static void fsmc_test_##suite##_##name##__impl();                                                    \
    static ::ftest::Registrar fsmc_test_reg_##suite##_##name(#suite "." #name,                           \
                                                             &fsmc_test_##suite##_##name##__impl);        \
    static void fsmc_test_##suite##_##name##__impl()

#define ASSERT_TRUE(cond)                                                                                \
    do {                                                                                                 \
        if (!(cond)) {                                                                                   \
            ::ftest::fail(__FILE__, __LINE__, std::string("ASSERT_TRUE(") + #cond + ")");                \
            return;                                                                                      \
        }                                                                                                \
    } while (0)

#define ASSERT_FALSE(cond) ASSERT_TRUE(!(cond))

#define ASSERT_EQ(a, b)                                                                                  \
    do {                                                                                                 \
        auto va_ = (a);                                                                                  \
        auto vb_ = (b);                                                                                  \
        if (!(va_ == vb_)) {                                                                             \
            std::ostringstream os_;                                                                      \
            os_ << "ASSERT_EQ(" #a ", " #b ") — got '" << va_ << "' vs '" << vb_ << "'";                \
            ::ftest::fail(__FILE__, __LINE__, os_.str());                                                \
            return;                                                                                      \
        }                                                                                                \
    } while (0)

#define ASSERT_NE(a, b)                                                                                  \
    do {                                                                                                 \
        auto va_ = (a);                                                                                  \
        auto vb_ = (b);                                                                                  \
        if (!(va_ != vb_)) {                                                                             \
            std::ostringstream os_;                                                                      \
            os_ << "ASSERT_NE(" #a ", " #b ") — both are '" << va_ << "'";                              \
            ::ftest::fail(__FILE__, __LINE__, os_.str());                                                \
            return;                                                                                      \
        }                                                                                                \
    } while (0)

#define EXPECT_TRUE(cond)                                                                                \
    do {                                                                                                 \
        if (!(cond)) {                                                                                   \
            ::ftest::fail(__FILE__, __LINE__, std::string("EXPECT_TRUE(") + #cond + ")");                \
        }                                                                                                \
    } while (0)
