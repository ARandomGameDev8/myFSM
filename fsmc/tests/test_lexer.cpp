#include "test_framework.hpp"

#include <string>

#include "diagnostics.hpp"
#include "lexer.hpp"

using namespace fsmc;
using namespace fsmc::lex;

namespace {

std::vector<Token> toks(const std::string& s, Diagnostics& d) {
    return tokenize(s, "lex.fsm", d);
}

} // namespace

TEST(lexer, tokenizes_keywords_and_punctuation) {
    Diagnostics d;
    auto t = toks("State A { Actions { } Traversals { if (x) { goto B; } else goto C; } }", d);
    ASSERT_FALSE(d.hasErrors());
    std::vector<Tok> got;
    for (const Token& x : t) got.push_back(x.kind);
    std::vector<Tok> want = {Tok::KwState, Tok::Ident, Tok::LBrace, Tok::KwActions, Tok::LBrace,
                             Tok::RBrace, Tok::KwTraversals, Tok::LBrace, Tok::KwIf, Tok::LParen,
                             Tok::Ident, Tok::RParen, Tok::LBrace, Tok::KwGoto, Tok::Ident,
                             Tok::Semicolon, Tok::RBrace, Tok::KwElse, Tok::KwGoto, Tok::Ident,
                             Tok::Semicolon, Tok::RBrace, Tok::RBrace, Tok::End};
    ASSERT_EQ(got.size(), want.size());
    for (std::size_t i = 0; i < want.size(); ++i) ASSERT_EQ(static_cast<int>(got[i]), static_cast<int>(want[i]));
}

TEST(lexer, keywords_are_case_sensitive) {
    Diagnostics d;
    auto t = toks("state state Idle", d);
    ASSERT_FALSE(d.hasErrors());
    ASSERT_EQ(t[0].kind, Tok::Ident); // lowercase is an identifier
    ASSERT_EQ(t[1].kind, Tok::Ident);
    ASSERT_EQ(t[2].kind, Tok::Ident);
}

TEST(lexer, number_literal_kinds) {
    Diagnostics d;
    auto t = toks("5 5.0 5.0f 1e3 2.5e-2f 0", d);
    ASSERT_FALSE(d.hasErrors());
    ASSERT_EQ(t[0].kind, Tok::IntLit);
    ASSERT_EQ(t[0].intValue, int64_t(5));
    ASSERT_EQ(t[1].kind, Tok::DoubleLit);
    ASSERT_EQ(t[1].realValue, 5.0);
    ASSERT_EQ(t[2].kind, Tok::FloatLit);
    ASSERT_EQ(t[2].realValue, 5.0);
    ASSERT_EQ(t[3].kind, Tok::DoubleLit);
    ASSERT_EQ(t[3].realValue, 1000.0);
    ASSERT_EQ(t[4].kind, Tok::FloatLit);
    ASSERT_EQ(t[4].realValue, 0.025);
}

TEST(lexer, operators_and_precedence_of_lexer) {
    Diagnostics d;
    auto t = toks("a ** b // c * d / e == f != g <= h >= i && j || k !l = m", d);
    ASSERT_FALSE(d.hasErrors());
    ASSERT_EQ(t[1].kind, Tok::StarStar);
    ASSERT_EQ(t[3].kind, Tok::SlashSlash);
    ASSERT_EQ(t[5].kind, Tok::Star);
    ASSERT_EQ(t[7].kind, Tok::Slash);
    ASSERT_EQ(t[9].kind, Tok::EqEq);
    ASSERT_EQ(t[11].kind, Tok::NotEq);
    ASSERT_EQ(t[13].kind, Tok::Le);
    ASSERT_EQ(t[15].kind, Tok::Ge);
    ASSERT_EQ(t[17].kind, Tok::AmpAmp);
    ASSERT_EQ(t[19].kind, Tok::PipePipe);
    ASSERT_EQ(t[21].kind, Tok::Bang);
    ASSERT_EQ(t[23].kind, Tok::Assign);
}

TEST(lexer, floor_division_after_value_is_not_a_comment) {
    Diagnostics d;
    auto t = toks("7 // 2", d);
    ASSERT_FALSE(d.hasErrors());
    ASSERT_EQ(t.size(), std::size_t(4)); // 7, //, 2, Tok::End
    ASSERT_EQ(t[1].kind, Tok::SlashSlash);
}

TEST(lexer, comment_after_statement_is_a_comment) {
    Diagnostics d;
    auto t = toks("x; // floor divide\n5 // 2", d);
    ASSERT_FALSE(d.hasErrors());
    // x ; Tok::End-of-comment 5 // 2  => tokens: Tok::Ident, Semicolon, Tok::IntLit, Tok::SlashSlash, Tok::IntLit, Tok::End
    ASSERT_EQ(t.size(), std::size_t(6));
    ASSERT_EQ(t[2].kind, Tok::IntLit);
    ASSERT_EQ(t[3].kind, Tok::SlashSlash);
    ASSERT_EQ(t[4].kind, Tok::IntLit);
}

TEST(lexer, block_comments) {
    Diagnostics d;
    auto t = toks("a /* one */ b /* multi\nline */ c", d);
    ASSERT_FALSE(d.hasErrors());
    ASSERT_EQ(t[0].kind, Tok::Ident);
    ASSERT_EQ(t[1].kind, Tok::Ident);
    ASSERT_EQ(t[2].kind, Tok::Ident);
    Diagnostics d2;
    auto t2 = toks("a /* never closed", d2);
    ASSERT_TRUE(d2.hasErrors());
}

TEST(lexer, strings_with_escapes) {
    Diagnostics d;
    auto t = toks("\"hi\\n\" \"a\\\\b\"", d);
    ASSERT_FALSE(d.hasErrors());
    ASSERT_EQ(t[0].kind, Tok::StringLit);
    ASSERT_EQ(t[0].strValue, std::string("hi\n"));
    ASSERT_EQ(t[1].strValue, std::string("a\\b"));
}

TEST(lexer, unterminated_string_is_an_error) {
    Diagnostics d;
    toks("\"oops", d);
    ASSERT_TRUE(d.hasErrors());
    EXPECT_TRUE(d.all().front().message.find("unterminated string") != std::string::npos);
}

TEST(lexer, entry_directive) {
    Diagnostics d;
    auto t = toks("@ENTRY Idle", d);
    ASSERT_FALSE(d.hasErrors());
    ASSERT_EQ(t[0].kind, Tok::Entry);
    ASSERT_EQ(t[1].kind, Tok::Ident);
}

TEST(lexer, unknown_directive_is_an_error) {
    Diagnostics d;
    toks("@FOO x", d);
    ASSERT_TRUE(d.hasErrors());
}

TEST(lexer, line_and_column_tracking) {
    Diagnostics d;
    auto t = toks("State A {\n    temp int x = 5; // c\n}", d);
    ASSERT_FALSE(d.hasErrors());
    ASSERT_EQ(t[0].line, 1);
    ASSERT_EQ(t[0].col, 1);
    ASSERT_EQ(t[2].line, 1); // LBrace (end of line 1: "State A {")
    ASSERT_EQ(t[2].col, 9);
    ASSERT_EQ(t[3].line, 2); // temp
    ASSERT_EQ(t[3].col, 5);
    ASSERT_EQ(t[7].intValue, int64_t(5)); // literal 5
    ASSERT_EQ(t[7].line, 2);
    ASSERT_EQ(t[7].col, 18);
}
