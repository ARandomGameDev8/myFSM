#include "lexer.hpp"

#include <cctype>

namespace fsmc::lex {

std::ostream& operator<<(std::ostream& os, Tok t) { return os << tokenName(t); }

const char* tokenName(Tok t) {
    switch (t) {
        case Tok::End: return "end of file";
        case Tok::KwState: return "State";
        case Tok::KwActions: return "Actions";
        case Tok::KwStart: return "Start";
        case Tok::KwUpdate: return "Update";
        case Tok::KwTraversals: return "Traversals";
        case Tok::KwIf: return "if";
        case Tok::KwElse: return "else";
        case Tok::KwGoto: return "goto";
        case Tok::KwTemp: return "temp";
        case Tok::KwConst: return "const";
        case Tok::KwVar: return "var";
        case Tok::KwReturn: return "return";
        case Tok::KwTrue: return "true";
        case Tok::KwFalse: return "false";
        case Tok::Entry: return "@ENTRY";
        case Tok::Ident: return "identifier";
        case Tok::IntLit: return "integer literal";
        case Tok::FloatLit: return "float literal";
        case Tok::DoubleLit: return "double literal";
        case Tok::StringLit: return "string literal";
        case Tok::Plus: return "'+'";
        case Tok::Minus: return "'-'";
        case Tok::Star: return "'*'";
        case Tok::StarStar: return "'**'";
        case Tok::Slash: return "'/'";
        case Tok::SlashSlash: return "'//'";
        case Tok::Assign: return "'='";
        case Tok::AmpAmp: return "'&&'";
        case Tok::PipePipe: return "'||'";
        case Tok::Bang: return "'!'";
        case Tok::EqEq: return "'=='";
        case Tok::NotEq: return "'!='";
        case Tok::Lt: return "'<'";
        case Tok::Gt: return "'>'";
        case Tok::Le: return "'<='";
        case Tok::Ge: return "'>='";
        case Tok::LParen: return "'('";
        case Tok::RParen: return "')'";
        case Tok::LBrace: return "'{'";
        case Tok::RBrace: return "'}'";
        case Tok::Comma: return "','";
        case Tok::Semicolon: return "';'";
    }
    return "?";
}

namespace {

struct Lexer {
    const std::string& src;
    const std::string& fileName;
    Diagnostics& diag;
    std::vector<Token> out;
    std::size_t p = 0;
    int line = 1;
    int col = 1;
    // The last character that was part of a "value" token (letter/digit/) /]"),
    // used to disambiguate '//' (floor division) from '//' (line comment).
    char lastValueChar = '\n';

    explicit Lexer(const std::string& s, const std::string& fn, Diagnostics& d)
        : src(s), fileName(fn), diag(d) {}

    [[nodiscard]] char peek(std::size_t off = 0) const {
        return p + off < src.size() ? src[p + off] : '\0';
    }
    bool eof() const { return p >= src.size(); }

    char advance() {
        char c = peek();
        if (c == '\n') { line++; col = 1; lastValueChar = ' '; }
        else { col++; }
        ++p;
        return c;
    }

    void noteValueChar(char c) {
        if (std::isalnum(static_cast<unsigned char>(c)) || c == ')' || c == ']' || c == '"') {
            lastValueChar = c;
        }
    }

    SrcLoc loc() const { return {line, col}; }

    void error(int l, int c, const std::string& msg) {
        diag.error({l, c}, msg);
    }
    void errorAtStart(int l, int c, const std::string& msg) { error(l, c, msg); }

    bool isIdentStart(char c) const {
        return std::isalpha(static_cast<unsigned char>(c)) || c == '_';
    }
    bool isIdentChar(char c) const {
        return std::isalnum(static_cast<unsigned char>(c)) || c == '_';
    }
    bool isDigit(char c) const { return std::isdigit(static_cast<unsigned char>(c)); }

    // ------------------------------------------------------------------
    void skipWhitespaceAndComments() {
        while (!eof()) {
            char c = peek();
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') {
                advance();
                continue;
            }
            if (c == '/' && peek(1) == '/') {
                // '//' is floor division when it directly follows a value
                // token character (no intervening whitespace); otherwise it
                // starts a line comment. (Same rule C-like languages need to
                // coexist with a floor-division operator.)
                if (std::isalnum(static_cast<unsigned char>(lastValueChar)) ||
                    lastValueChar == ')' || lastValueChar == ']' || lastValueChar == '"') {
                    break; // the operator path in run() will consume it
                }
                while (!eof() && peek() != '\n') advance();
                lastValueChar = ' '; // a comment breaks the value run
                continue;
            }
            if (c == '/' && peek(1) == '*') {
                int sl = line, sc = col;
                advance(); advance();
                bool closed = false;
                while (!eof()) {
                    if (peek() == '*' && peek(1) == '/') { advance(); advance(); closed = true; break; }
                    advance();
                }
                if (!closed) error(sl, sc, "unterminated block comment");
                lastValueChar = ' '; // a comment breaks the value run
                continue;
            }
            break;
        }
    }

    void pushSimple(Tok kind, const std::string& text, SrcLoc l, SrcLoc /*c*/, bool value) {
        Token t;
        t.kind = kind;
        t.text = text;
        t.line = l.line;
        t.col = l.col;
        out.push_back(std::move(t));
        if (value) {
            noteValueChar(text.back());
        } else {
            lastValueChar = ' '; // a non-value token breaks the value run
        }
    }

    void lexIdentOrKeyword() {
        SrcLoc start = loc();
        std::string s;
        while (!eof() && isIdentChar(peek())) s.push_back(advance());
        Token t;
        t.line = start.line;
        t.col = start.col;
        t.text = s;
        if (s == "State") t.kind = Tok::KwState;
        else if (s == "Actions") t.kind = Tok::KwActions;
        else if (s == "Start") t.kind = Tok::KwStart;
        else if (s == "Update") t.kind = Tok::KwUpdate;
        else if (s == "Traversals") t.kind = Tok::KwTraversals;
        else if (s == "if") t.kind = Tok::KwIf;
        else if (s == "else") t.kind = Tok::KwElse;
        else if (s == "goto") t.kind = Tok::KwGoto;
        else if (s == "temp") t.kind = Tok::KwTemp;
        else if (s == "const") t.kind = Tok::KwConst;
        else if (s == "var") t.kind = Tok::KwVar;
        else if (s == "return") t.kind = Tok::KwReturn;
        else if (s == "true") t.kind = Tok::KwTrue;
        else if (s == "false") t.kind = Tok::KwFalse;
        else t.kind = Tok::Ident;
        out.push_back(std::move(t));
        noteValueChar(s.back());
    }

    void lexNumber() {
        SrcLoc start = loc();
        std::string s;
        while (!eof() && isDigit(peek())) s.push_back(advance());
        bool hasFraction = false;
        if (peek() == '.' && isDigit(peek(1))) {
            hasFraction = true;
            s.push_back(advance()); // '.'
            while (!eof() && isDigit(peek())) s.push_back(advance());
        }
        if (peek() == 'e' || peek() == 'E') {
            // Tentatively consume an exponent; roll back if it is not one.
            std::size_t saveP = p;
            int saveLine = line, saveCol = col;
            s.push_back(advance()); // 'e'
            if (peek() == '+' || peek() == '-') s.push_back(advance());
            if (isDigit(peek())) {
                hasFraction = true;
                while (!eof() && isDigit(peek())) s.push_back(advance());
            } else {
                p = saveP; line = saveLine; col = saveCol;
                s.pop_back(); // not an exponent after all
            }
        }
        bool floatSuffix = false;
        if (peek() == 'f' || peek() == 'F') {
            advance();
            floatSuffix = true;
        }

        Token t;
        t.line = start.line;
        t.col = start.col;
        t.text = s;
        if (!hasFraction && !floatSuffix) {
            t.kind = Tok::IntLit;
            try {
                t.intValue = std::stoll(s);
            } catch (const std::exception&) {
                t.intValue = 0;
                t.kind = Tok::DoubleLit; // out of int64 range; parser reports
            }
        } else {
            t.realValue = std::stod(s);
            t.kind = floatSuffix ? Tok::FloatLit : Tok::DoubleLit;
        }
        out.push_back(std::move(t));
        noteValueChar(s.empty() ? '0' : s.back());
    }

    void lexString() {
        SrcLoc start = loc();
        advance(); // opening quote
        std::string s;
        bool closed = false;
        while (!eof()) {
            char c = peek();
            if (c == '"') { advance(); closed = true; break; }
            if (c == '\n') break; // unterminated at end of line
            if (c == '\\') {
                advance();
                char e = peek();
                if (e == '\0') break;
                switch (e) {
                    case 'n': s.push_back('\n'); advance(); break;
                    case 't': s.push_back('\t'); advance(); break;
                    case 'r': s.push_back('\r'); advance(); break;
                    case '"': s.push_back('"'); advance(); break;
                    case '\\': s.push_back('\\'); advance(); break;
                    default:
                        error(start.line, start.col,
                              "unknown escape sequence '\\'" + std::string(1, e) + " in string literal");
                        s.push_back(e);
                        advance();
                        break;
                }
                continue;
            }
            s.push_back(advance());
        }
        if (!closed) {
            error(start.line, start.col, "unterminated string literal");
            return; // the string is dropped; parser sees the next token
        }
        Token t;
        t.kind = Tok::StringLit;
        t.text = s;
        t.strValue = s;
        t.line = start.line;
        t.col = start.col;
        out.push_back(std::move(t));
        noteValueChar('"');
    }

    void lexAt() {
        SrcLoc start = loc();
        advance(); // '@'
        if (isIdentStart(peek())) {
            std::string name;
            while (!eof() && isIdentChar(peek())) name.push_back(advance());
            if (name == "ENTRY") {
                Token t;
                t.kind = Tok::Entry;
                t.text = "@ENTRY";
                t.line = start.line;
                t.col = start.col;
                out.push_back(std::move(t));
                return;
            }
            error(start.line, start.col, "unknown directive '@" + name + "' (expected @ENTRY)");
        } else {
            error(start.line, start.col, "expected identifier after '@' (did you mean @ENTRY?)");
        }
    }

    void run() {
        while (true) {
            skipWhitespaceAndComments();
            if (eof()) break;
            SrcLoc start = loc();
            char c = peek();

            if (isIdentStart(c)) { lexIdentOrKeyword(); continue; }
            if (isDigit(c)) { lexNumber(); continue; }
            if (c == '"') { lexString(); continue; }
            if (c == '@') { lexAt(); continue; }

            auto two = [&](Tok k, const char* s) {
                if (peek(1) == s[1]) {
                    advance(); advance();
                    Token t; t.kind = k; t.text = s; t.line = start.line; t.col = start.col;
                    out.push_back(std::move(t));
                    return true;
                }
                return false;
            };

            if (c == '+') { advance(); pushSimple(Tok::Plus, "+", start, loc(), false); continue; }
            if (c == '-') { advance(); pushSimple(Tok::Minus, "-", start, loc(), false); continue; }
            if (c == '(') { advance(); pushSimple(Tok::LParen, "(", start, loc(), false); continue; }
            if (c == ')') { advance(); pushSimple(Tok::RParen, ")", start, loc(), true); continue; }
            if (c == '{') { advance(); pushSimple(Tok::LBrace, "{", start, loc(), false); continue; }
            if (c == '}') { advance(); pushSimple(Tok::RBrace, "}", start, loc(), false); continue; }
            if (c == ',') { advance(); pushSimple(Tok::Comma, ",", start, loc(), false); continue; }
            if (c == ';') { advance(); pushSimple(Tok::Semicolon, ";", start, loc(), false); continue; }
            if (c == '<') {
                if (two(Tok::Le, "<=")) continue;
                advance(); pushSimple(Tok::Lt, "<", start, loc(), false); continue;
            }
            if (c == '>') {
                if (two(Tok::Ge, ">=")) continue;
                advance(); pushSimple(Tok::Gt, ">", start, loc(), false); continue;
            }
            if (c == '!') {
                if (two(Tok::NotEq, "!=")) continue;
                advance(); pushSimple(Tok::Bang, "!", start, loc(), false); continue;
            }
            if (c == '&') {
                if (two(Tok::AmpAmp, "&&")) continue;
                errorAtStart(start.line, start.col, "unexpected character '&' (did you mean '&&'?)");
                advance();
                continue;
            }
            if (c == '|') {
                if (two(Tok::PipePipe, "||")) continue;
                errorAtStart(start.line, start.col, "unexpected character '|' (did you mean '||'?)");
                advance();
                continue;
            }
            if (c == '=') {
                if (two(Tok::EqEq, "==")) continue;
                advance(); pushSimple(Tok::Assign, "=", start, loc(), false); continue;
            }
            if (c == '*') {
                if (two(Tok::StarStar, "**")) continue;
                advance(); pushSimple(Tok::Star, "*", start, loc(), false); continue;
            }
            if (c == '/') {
                // Floor division directly after a value character; a comment
                // was already handled in skipWhitespaceAndComments.
                if (two(Tok::SlashSlash, "//")) continue;
                advance(); pushSimple(Tok::Slash, "/", start, loc(), false); continue;
            }

            errorAtStart(start.line, start.col, std::string("unexpected character '") + c + "'");
            advance();
        }
        Token t;
        t.kind = Tok::End;
        t.line = line;
        t.col = col;
        out.push_back(std::move(t));
    }
};

} // namespace

std::vector<Token> tokenize(const std::string& source, const std::string& fileName, Diagnostics& diag) {
    Lexer lx(source, fileName, diag);
    lx.run();
    return std::move(lx.out);
}

} // namespace fsmc::lex
