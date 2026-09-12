#pragma once

#include <cstdint>
#include <ostream>
#include <string>
#include <vector>

#include "diagnostics.hpp"

namespace fsmc::lex {

enum class Tok {
    End,
    // keywords (case-sensitive)
    KwState, KwActions, KwTraversals, KwIf, KwElse, KwGoto, KwTemp, KwConst,
    KwVar, KwReturn, KwTrue, KwFalse,
    Entry, // @ENTRY
    // literals / identifiers
    Ident, IntLit, FloatLit, DoubleLit, StringLit,
    // operators / punctuation
    Plus, Minus, Star, StarStar, Slash, SlashSlash,
    Assign, // single '=' (assignment)
    AmpAmp, PipePipe, Bang,
    EqEq, NotEq, Lt, Gt, Le, Ge,
    LParen, RParen, LBrace, RBrace, Comma, Semicolon,
};

struct Token {
    Tok kind = Tok::End;
    std::string text;
    int line = 0;
    int col = 0;
    int64_t intValue = 0;   // IntLit
    double realValue = 0.0; // FloatLit / DoubleLit
    std::string strValue;   // StringLit
};

const char* tokenName(Tok t);

std::ostream& operator<<(std::ostream& os, Tok t);

// Tokenizes the whole source. On lex errors, diagnostics are appended and the
// lexer skips the offending construct so the parser can continue (the compile
// still fails — the parser/CLI exits non-zero).
std::vector<Token> tokenize(const std::string& source, const std::string& fileName,
                            Diagnostics& diag);

} // namespace fsmc::lex
