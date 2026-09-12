#include "parser.hpp"

#include <cctype>
#include <cmath>

#include "builtin_functions.hpp"
#include "const_fold.hpp"
#include "type_rules.hpp"

namespace fsmc {

// RAII scope-frame guard: exactly one pop per block, even on error paths.
namespace {
struct ScopeGuard {
    ScopeStack& stack;
    explicit ScopeGuard(ScopeStack& s) : stack(s) { stack.push(); }
    ~ScopeGuard() { stack.pop(); }
};
} // namespace

Parser::Parser(const std::vector<lex::Token>& tokens, Diagnostics& diag)
    : toks_(tokens), diag_(diag) {}

// ---------------------------------------------------------------------------
// token stream helpers
// ---------------------------------------------------------------------------

const lex::Token& Parser::cur() const { return toks_[pos_]; }
const lex::Token& Parser::peekAt(std::size_t n) const {
    std::size_t i = pos_ + n;
    return i < toks_.size() ? toks_[i] : toks_.back();
}
bool Parser::at(lex::Tok k) const { return cur().kind == k; }

const lex::Token& Parser::next() {
    const lex::Token& t = cur();
    if (pos_ + 1 < toks_.size()) ++pos_;
    return t;
}

const lex::Token* Parser::expect(lex::Tok k, const char* what) {
    if (at(k)) return &next();
    errorAt(cur(), std::string("expected ") + what + " but found '" + cur().text + "'");
    return nullptr;
}

void Parser::errorAt(const lex::Token& t, const std::string& msg) {
    diag_.error({t.line, t.col}, msg);
}
void Parser::warnAt(const lex::Token& t, const std::string& msg) {
    diag_.warn({t.line, t.col}, msg);
}

void Parser::skipToStatementBoundary() {
    while (!at(lex::Tok::End) && !at(lex::Tok::Semicolon) && !at(lex::Tok::RBrace)) next();
    if (at(lex::Tok::Semicolon)) next();
}

void Parser::skipMatchingBraces() {
    // Consumes a block body (including nested braces) for error recovery.
    if (!at(lex::Tok::LBrace)) {
        skipToStatementBoundary();
        return;
    }
    next(); // '{'
    int depth = 1;
    while (!at(lex::Tok::End) && depth > 0) {
        if (at(lex::Tok::LBrace)) ++depth;
        if (at(lex::Tok::RBrace)) --depth;
        next();
    }
}

ParsedSource Parser::parse() {
    parseTopLevel();
    if (src_.entryCount == 0) {
        diag_.error({1, 1}, "missing @ENTRY: exactly one @ENTRY is required per file");
    }
    if (src_.states.empty()) {
        diag_.error({1, 1}, "file must define at least one State");
    }
    return std::move(src_);
}

// ---------------------------------------------------------------------------
// top level
// ---------------------------------------------------------------------------

void Parser::parseTopLevel() {
    while (!at(lex::Tok::End)) {
        switch (cur().kind) {
            case lex::Tok::KwConst: parseGlobalConst(); break;
            case lex::Tok::KwVar: parseGlobalVar(); break;
            case lex::Tok::KwState: parseState(); break;
            case lex::Tok::Entry: parseEntry(); break;
            default:
                errorAt(cur(), "unexpected token '" + cur().text +
                                 "' at top level (expected const, var, State, or @ENTRY)");
                next();
                break;
        }
    }
}

void Parser::parseGlobalConst() {
    const lex::Token kw = next(); // 'const'
    const lex::Token* typeTok = expect(lex::Tok::Ident, "a type name after 'const'");
    if (!typeTok) return;
    const TypeDefinition* type = BuiltinTypes::instance().find(typeTok->text);
    if (!type) {
        errorAt(*typeTok, "unknown type '" + typeTok->text + "' in declaration");
        skipToStatementBoundary();
        return;
    }
    if (type->name == "void") {
        errorAt(*typeTok, "void cannot be used as a variable type");
    }
    const lex::Token* nameTok = expect(lex::Tok::Ident, "a constant name");
    if (!nameTok) { skipToStatementBoundary(); return; }
    for (const auto& g : src_.globals) {
        if (g.name == nameTok->text) {
            errorAt(*nameTok, "duplicate global variable '" + nameTok->text + "'");
            break;
        }
    }
    if (!expect(lex::Tok::Assign, "'=' after the constant name")) {
        skipToStatementBoundary();
        return;
    }
    Expr* init = parseExpr();
    if (expect(lex::Tok::Semicolon, "';' at the end of the constant declaration")) {
        if (init && init->type && init->type != type) {
            errorAt(*nameTok, "type mismatch: cannot initialize constant '" + nameTok->text +
                                   "' of type '" + type->name + "' with expression of type '" +
                                   init->type->name + "'");
        }
        ConstValue folded;
        std::string foldErr;
        ConstPool pool;
        for (const auto& [n, i] : constNames_) {
            const GlobalVar& g = src_.globals[i];
            pool.set(n, g);
        }
        if (!foldExpr(init, pool, folded, foldErr)) {
            errorAt(*nameTok, "static constant '" + nameTok->text +
                                   "' initializer is not a compile-time constant: " + foldErr);
        }
        // Commit the constant (order matters: earlier consts are visible to later ones).
        GlobalVar gv;
        gv.name = nameTok->text;
        gv.loc = {kw.line, kw.col};
        gv.type = type;
        gv.isConst = true;
        gv.index = static_cast<int>(src_.globals.size());
        gv.bindingSlot = -1;
        if (folded.type == type) gv.constBytes = folded.toBytes();
        constNames_.emplace_back(gv.name, gv.index);
        src_.globals.push_back(std::move(gv));
    } else {
        skipToStatementBoundary();
    }
}

void Parser::parseGlobalVar() {
    const lex::Token kw = next(); // 'var'
    const lex::Token* typeTok = expect(lex::Tok::Ident, "a type name after 'var'");
    if (!typeTok) return;
    const TypeDefinition* type = BuiltinTypes::instance().find(typeTok->text);
    if (!type) {
        errorAt(*typeTok, "unknown type '" + typeTok->text + "' in declaration");
        skipToStatementBoundary();
        return;
    }
    if (type->name == "void") {
        errorAt(*typeTok, "void cannot be used as a variable type");
    }
    const lex::Token* nameTok = expect(lex::Tok::Ident, "a runtime variable name");
    if (!nameTok) { skipToStatementBoundary(); return; }
    for (const auto& g : src_.globals) {
        if (g.name == nameTok->text) {
            errorAt(*nameTok, "duplicate global variable '" + nameTok->text + "'");
            break;
        }
    }
    if (at(lex::Tok::Assign)) {
        const lex::Token eq = next();
        errorAt(eq, "runtime variables cannot have initializers (they are externally driven; "
                    "values arrive through their binding slot)");
        Expr* dummy = parseExpr(); (void)dummy;
    }
    if (!expect(lex::Tok::Semicolon, "';' at the end of the runtime variable declaration")) {
        skipToStatementBoundary();
        return;
    }
    GlobalVar gv;
    gv.name = nameTok->text;
    gv.loc = {kw.line, kw.col};
    gv.type = type;
    gv.isConst = false;
    gv.index = static_cast<int>(src_.globals.size());
    int runtimeCount = 0;
    for (const auto& g : src_.globals) if (!g.isConst) ++runtimeCount;
    gv.bindingSlot = runtimeCount;
    gv.owner = fmt::OwnerExternal;
    src_.globals.push_back(std::move(gv));
}

void Parser::parseEntry() {
    const lex::Token kw = next(); // '@ENTRY'
    const lex::Token* nameTok = expect(lex::Tok::Ident, "a state name after @ENTRY");
    if (!nameTok) return;
    if (src_.entryCount > 0) {
        errorAt(kw, "duplicate @ENTRY (exactly one is required per file)");
        return;
    }
    src_.entryName = nameTok->text;
    src_.entryLoc = {nameTok->line, nameTok->col};
    src_.entryCount = 1;
}

// ---------------------------------------------------------------------------
// states
// ---------------------------------------------------------------------------

void Parser::parseState() {
    const lex::Token kw = next(); // 'State'
    const lex::Token* nameTok = expect(lex::Tok::Ident, "a state name after 'State'");
    if (!nameTok) {
        skipMatchingBraces();
        return;
    }
    for (const auto& s : src_.states) {
        if (s.name == nameTok->text) {
            errorAt(*nameTok, "duplicate state '" + nameTok->text + "'");
            break;
        }
    }
    StateDef st;
    st.name = nameTok->text;
    st.loc = {kw.line, kw.col};
    int stateIndex = static_cast<int>(src_.states.size());
    src_.states.push_back(std::move(st));
    stateIndex_ = stateIndex;
    parseStateBody(src_.states.back(), stateIndex);
    stateIndex_ = -1;
}

void Parser::parseStateBody(StateDef& st, int stateIndex) {
    if (!expect(lex::Tok::LBrace, "'{' after the state name")) return;
    ScopeGuard guard(scope_); // depth-1 frame: state-level temps
    const lex::Token* closer = nullptr;
    bool hasActions = false, hasTraversals = false;
    while (!at(lex::Tok::RBrace) && !at(lex::Tok::End)) {
        switch (cur().kind) {
            case lex::Tok::KwTemp: {
                Stmt d = parseTempDecl(1, stateIndex);
                StateBodyItem item;
                item.kind = StateBodyItem::Kind::TempDecl;
                item.loc = d.loc;
                item.decl = std::move(d);
                st.items.push_back(std::move(item));
                break;
            }
            case lex::Tok::KwActions: {
                if (hasActions) {
                    errorAt(cur(), "duplicate Actions block in state '" + st.name + "'");
                    skipMatchingBraces();
                    break;
                }
                hasActions = true;
                StateBodyItem item;
                item.kind = StateBodyItem::Kind::Actions;
                item.loc = {cur().line, cur().col};
                parseActionsBlock(item);
                st.items.push_back(std::move(item));
                break;
            }
            case lex::Tok::KwTraversals: {
                if (hasTraversals) {
                    errorAt(cur(), "duplicate Traversals block in state '" + st.name + "'");
                    skipMatchingBraces();
                    break;
                }
                hasTraversals = true;
                StateBodyItem item;
                item.kind = StateBodyItem::Kind::Traversals;
                item.loc = {cur().line, cur().col};
                parseTraversalsBlock(item);
                st.items.push_back(std::move(item));
                break;
            }
            case lex::Tok::KwGoto: {
                const lex::Token t = next();
                errorAt(t, "goto is only allowed inside Traversals");
                skipToStatementBoundary();
                break;
            }
            case lex::Tok::KwConst:
            case lex::Tok::KwVar: {
                const lex::Token t = next();
                errorAt(t, "global 'const'/'var' declarations are not allowed inside a State body");
                skipToStatementBoundary();
                break;
            }
            case lex::Tok::LBrace: {
                errorAt(cur(), "unexpected block; { must follow a State, Actions, Traversals, if, "
                               "else if, or else");
                skipMatchingBraces();
                break;
            }
            default: {
                errorAt(cur(), "unexpected token '" + cur().text +
                                   "' in State body (expected temp, Actions, Traversals, or })");
                next();
                break;
            }
        }
    }
    closer = expect(lex::Tok::RBrace, "'}' closing the State body");
    (void)closer;
    if (!hasActions && !hasTraversals) {
        errorAt(cur(), std::string("state '") + st.name +
                           "' must contain an Actions or Traversals block");
    }
}

void Parser::parseActionsBlock(StateBodyItem& item) {
    next(); // 'Actions'
    if (!expect(lex::Tok::LBrace, "'{' after 'Actions'")) return;
    ScopeGuard guard(scope_); // depth-2 frame
    parseActionStmts(item.stmts, 2);
    expect(lex::Tok::RBrace, "'}' closing the Actions body");
}

void Parser::parseTraversalsBlock(StateBodyItem& item) {
    next(); // 'Traversals'
    if (!expect(lex::Tok::LBrace, "'{' after 'Traversals'")) return;
    ScopeGuard guard(scope_); // depth-2 frame (traversals cannot declare temps)
    seenTravIf_ = false;
    parseTraversalsStmts(item.stmts);
    expect(lex::Tok::RBrace, "'}' closing the Traversals body");
    bool anyGoto = false;
    for (const auto& s : item.stmts) {
        if (s.kind == Stmt::Kind::Goto) { anyGoto = true; break; }
        if (s.kind == Stmt::Kind::If && !s.body.empty()) { anyGoto = true; break; }
    }
    if (!anyGoto) {
        diag_.error({item.loc.line, item.loc.col},
                    "Traversals body must contain at least one goto statement");
    }
}

// ---------------------------------------------------------------------------
// statements (Actions context: depth 2 or 3)
// ---------------------------------------------------------------------------

void Parser::parseActionStmts(std::vector<Stmt>& out, uint8_t depth) {
    while (!at(lex::Tok::RBrace) && !at(lex::Tok::End)) {
        switch (cur().kind) {
            case lex::Tok::KwTemp: {
                out.push_back(parseTempDecl(depth, stateIndex_));
                break;
            }
            case lex::Tok::KwIf: {
                if (inConditionalBody_) {
                    const lex::Token t = cur();
                    errorAt(t, "nested conditionals are not allowed inside conditionals");
                    skipMatchingBraces();
                    break;
                }
                parseIfChain(out, depth);
                break;
            }
            case lex::Tok::KwGoto: {
                const lex::Token t = next();
                errorAt(t, "goto is only allowed inside Traversals");
                skipToStatementBoundary();
                break;
            }
            case lex::Tok::KwReturn: {
                const lex::Token t = next();
                errorAt(t, "return statements are not supported by this language");
                skipToStatementBoundary();
                break;
            }
            case lex::Tok::Semicolon:
                next();
                break;
            case lex::Tok::LBrace: {
                errorAt(cur(), "unexpected block; { must follow a State, Actions, Traversals, if, "
                               "else if, or else");
                skipMatchingBraces();
                break;
            }
            case lex::Tok::RBrace:
                return; // caller's loop terminates
            case lex::Tok::End:
                errorAt(cur(), "unexpected end of file (missing '}'?)");
                return;
            case lex::Tok::Ident: {
                if (peekAt(1).kind == lex::Tok::Assign) {
                    const lex::Token name = next();
                    out.push_back(parseAssignStmt(depth, name));
                } else if (peekAt(1).kind == lex::Tok::LParen) {
                    const lex::Token name = next();
                    out.push_back(parseCallStmt(depth, name));
                } else {
                    errorAt(cur(), "unexpected token '" + cur().text +
                                       "' (expected a temp declaration, assignment, or function call)");
                    skipToStatementBoundary();
                }
                break;
            }
            default: {
                errorAt(cur(), "unexpected token '" + cur().text + "' in statement position");
                next();
                break;
            }
        }
    }
}

Stmt Parser::parseTempDecl(uint8_t depth, int stateIndex) {
    const lex::Token kw = next(); // 'temp'
    Stmt s;
    s.kind = Stmt::Kind::TempDecl;
    s.loc = {kw.line, kw.col};
    s.depth = depth;

    const lex::Token* typeTok = expect(lex::Tok::Ident, "a type name after 'temp'");
    if (!typeTok) return s;
    const TypeDefinition* type = BuiltinTypes::instance().find(typeTok->text);
    if (!type) {
        errorAt(*typeTok, "unknown type '" + typeTok->text + "' in declaration");
        skipToStatementBoundary();
        return s;
    }
    if (type->name == "void") {
        errorAt(*typeTok, "void cannot be used as a variable type");
        skipToStatementBoundary();
        return s;
    }
    s.type = type;

    const lex::Token* nameTok = expect(lex::Tok::Ident, "a temporary variable name");
    if (!nameTok) { skipToStatementBoundary(); return s; }
    s.tempName = nameTok->text;

    int tempId = static_cast<int>(src_.temps.size());
    TempVar tv;
    tv.name = s.tempName;
    tv.loc = {nameTok->line, nameTok->col};
    tv.type = type;
    tv.depth = depth;
    tv.stateIndex = stateIndex;
    tv.index = tempId;
    src_.temps.push_back(std::move(tv));
    if (!scope_.declare(s.tempName, tempId)) {
        errorAt(*nameTok, "redeclaration of '" + s.tempName + "' in this scope");
    }
    s.tempId = tempId;

    if (at(lex::Tok::Assign)) {
        next();
        s.init = parseExpr();
        if (s.init && s.init->type && s.init->type != type) {
            errorAt(*nameTok, "type mismatch: cannot initialize '" + s.tempName +
                                   "' of type '" + type->name + "' with expression of type '" +
                                   s.init->type->name + "'");
        }
    }
    expect(lex::Tok::Semicolon, "';' at the end of the temp declaration");
    return s;
}

Stmt Parser::parseAssignStmt(uint8_t depth, const lex::Token& nameTok) {
    Stmt s;
    s.kind = Stmt::Kind::Assign;
    s.loc = {nameTok.line, nameTok.col};
    s.depth = depth;

    VarInfo target;
    if (!resolveVariable(nameTok, target)) {
        skipToStatementBoundary();
        return s;
    }
    if (target.kind == VarKind::GlobalConst) {
        errorAt(nameTok, "static constants cannot be assigned: '" + target.name + "'");
        skipToStatementBoundary();
        return s;
    }
    s.target = target;

    if (!expect(lex::Tok::Assign, "'=' for the assignment")) {
        skipToStatementBoundary();
        return s;
    }
    s.value = parseExpr();
    if (s.value && s.value->type && target.type && s.value->type != target.type) {
        errorAt(nameTok, "type mismatch: cannot assign expression of type '" +
                             s.value->type->name + "' to '" + target.name + "' of type '" +
                             target.type->name + "'");
    }
    expect(lex::Tok::Semicolon, "';' at the end of the assignment");
    return s;
}

Stmt Parser::parseCallStmt(uint8_t depth, const lex::Token& nameTok) {
    Stmt s;
    s.kind = Stmt::Kind::Call;
    s.loc = {nameTok.line, nameTok.col};
    s.depth = depth;
    s.funcName = nameTok.text;

    if (!expect(lex::Tok::LParen, "'(' after the function name")) {
        skipToStatementBoundary();
        return s;
    }
    std::vector<Expr*> args;
    if (!at(lex::Tok::RParen)) {
        do {
            Expr* a = parseExpr();
            if (a) args.push_back(a);
            else args.push_back(makePoison({cur().line, cur().col}));
        } while (at(lex::Tok::Comma) ? (next(), true) : false);
    }
    const lex::Token* closeTok = expect(lex::Tok::RParen, "')' closing the argument list");

    const FunctionDefinition* fn = resolveCall(nameTok.text, args, nameTok);
    if (!closeTok || !fn) {
        expect(lex::Tok::Semicolon, "';' at the end of the call statement");
        return s;
    }
    s.functionId = fn->functionId;
    s.tier = fn->tier;
    s.args = std::move(args);
    applyStatementCallSemantics(s, fn, nameTok);
    expect(lex::Tok::Semicolon, "';' at the end of the call statement");
    return s;
}

// ---------------------------------------------------------------------------
// conditionals
// ---------------------------------------------------------------------------

void Parser::parseIfChain(std::vector<Stmt>& out, uint8_t depth) {
    Stmt first = parseIfHead(Stmt::Kind::If);
    out.push_back(std::move(first));

    while (at(lex::Tok::KwElse)) {
        next(); // 'else'
        if (at(lex::Tok::KwIf)) {
            out.push_back(parseIfHead(Stmt::Kind::ElseIf));
        } else {
            out.push_back(parseIfHead(Stmt::Kind::Else));
            break; // an if/else-if/else chain ends at 'else'
        }
    }
    (void)depth;
}

Stmt Parser::parseIfHead(Stmt::Kind kind) {
    Stmt s;
    s.kind = kind;
    if (kind == Stmt::Kind::If || kind == Stmt::Kind::ElseIf) {
        const lex::Token kw = next(); // 'if' or the 'if' of 'else if'
        s.loc = {kw.line, kw.col};
        s.depth = 3; // an if/else-if/else body is always block depth 3
        if (!expect(lex::Tok::LParen, "'(' after 'if'")) {
            skipMatchingBraces();
            return s;
        }
        s.cond = parseExpr();
        if (s.cond && s.cond->type) {
            if (s.cond->type->name != "bool") {
                errorAt(kw, "if condition must be a bool expression, got '" +
                                s.cond->type->name + "'");
            }
        }
        expect(lex::Tok::RParen, "')' closing the if condition");
    } else {
        const lex::Token kw = cur(); // 'else'
        s.loc = {kw.line, kw.col};
        s.depth = 3;
    }
    if (!expect(lex::Tok::LBrace, "'{' after the if condition")) return s;

    ScopeGuard guard(scope_); // depth-3 frame
    bool saveInCond = inConditionalBody_;
    inConditionalBody_ = true;
    parseActionStmts(s.body, 3);
    inConditionalBody_ = saveInCond;

    expect(lex::Tok::RBrace, "'}' closing the if body");
    return s;
}

void Parser::parseTraversalsStmts(std::vector<Stmt>& out) {
    while (!at(lex::Tok::RBrace) && !at(lex::Tok::End)) {
        switch (cur().kind) {
            case lex::Tok::KwIf: {
                Stmt s;
                s.kind = Stmt::Kind::If;
                const lex::Token kw = next(); // 'if'
                s.loc = {kw.line, kw.col};
                s.depth = 3;
                if (!expect(lex::Tok::LParen, "'(' after 'if'")) {
                    skipMatchingBraces();
                    out.push_back(std::move(s));
                    seenTravIf_ = true;
                    continue;
                }
                s.cond = parseExpr();
                if (s.cond && s.cond->type) {
                    if (s.cond->type->name != "bool") {
                        errorAt(kw, "if condition must be a bool expression, got '" +
                                        s.cond->type->name + "'");
                    }
                }
                expect(lex::Tok::RParen, "')' closing the if condition");
                if (!expect(lex::Tok::LBrace, "'{' after the if condition")) {
                    skipMatchingBraces();
                    out.push_back(std::move(s));
                    seenTravIf_ = true;
                    continue;
                }
                if (!at(lex::Tok::KwGoto)) {
                    std::string msg = "Traversals if body must contain exactly one goto statement";
                    if (at(lex::Tok::RBrace)) msg += " (found an empty body)";
                    errorAt(cur(), msg);
                    skipToStatementBoundary();
                } else {
                    Stmt g;
                    g.kind = Stmt::Kind::Goto;
                    g.loc = {cur().line, cur().col};
                    g.depth = 3;
                    next(); // 'goto'
                    const lex::Token* target = expect(lex::Tok::Ident, "a state name after 'goto'");
                    if (target) g.targetName = target->text;
                    expect(lex::Tok::Semicolon, "';' after the goto target");
                    s.body.push_back(std::move(g));
                    if (!at(lex::Tok::RBrace)) {
                        errorAt(cur(), "Traversals if body must contain exactly one goto statement "
                                       "(found an extra statement)");
                        skipToStatementBoundary();
                    }
                }
                expect(lex::Tok::RBrace, "'}' closing the if body");
                if (at(lex::Tok::KwElse)) {
                    const lex::Token elseTok = next();
                    errorAt(elseTok, "else is not allowed in Traversals");
                    if (at(lex::Tok::KwIf)) next(); // consume 'else if' pair
                    skipMatchingBraces();
                }
                out.push_back(std::move(s));
                seenTravIf_ = true;
                break;
            }
            case lex::Tok::KwGoto: {
                const lex::Token t = next(); // 'goto'
                if (!seenTravIf_) {
                    warnAt(t, "bare goto before any if: subsequent statements are dead code "
                              "(this transition always fires)");
                }
                Stmt g;
                g.kind = Stmt::Kind::Goto;
                g.loc = {t.line, t.col};
                g.depth = 2;
                const lex::Token* target = expect(lex::Tok::Ident, "a state name after 'goto'");
                if (target) g.targetName = target->text;
                expect(lex::Tok::Semicolon, "';' after the goto target");
                out.push_back(std::move(g));
                break;
            }
            case lex::Tok::KwElse: {
                const lex::Token t = next();
                errorAt(t, "else is not allowed in Traversals");
                if (at(lex::Tok::KwIf)) next();
                skipMatchingBraces();
                break;
            }
            case lex::Tok::Semicolon:
                next();
                break;
            case lex::Tok::End:
                errorAt(cur(), "unexpected end of file (missing '}'?)");
                return;
            default: {
                errorAt(cur(), "only 'if' and 'goto' statements are allowed in Traversals");
                skipToStatementBoundary();
                break;
            }
        }
    }
}

// ---------------------------------------------------------------------------
// expressions — C precedence, right-associative **
// ---------------------------------------------------------------------------

Expr* Parser::parseExpr() { return parseOr(); }
Expr* Parser::parseOr() {
    Expr* l = parseAnd();
    while (at(lex::Tok::PipePipe)) {
        const lex::Token op = next();
        Expr* r = parseAnd();
        l = makeBinary(fmt::OpLogicalOr, l, r, op);
    }
    return l;
}
Expr* Parser::parseAnd() {
    Expr* l = parseEquality();
    while (at(lex::Tok::AmpAmp)) {
        const lex::Token op = next();
        Expr* r = parseEquality();
        l = makeBinary(fmt::OpLogicalAnd, l, r, op);
    }
    return l;
}
Expr* Parser::parseEquality() {
    Expr* l = parseRelational();
    while (at(lex::Tok::EqEq) || at(lex::Tok::NotEq)) {
        const lex::Token op = next();
        Expr* r = parseRelational();
        l = makeBinary(op.kind == lex::Tok::EqEq ? fmt::OpEqEq : fmt::OpNotEq, l, r, op);
    }
    return l;
}
Expr* Parser::parseRelational() {
    Expr* l = parseAdditive();
    while (at(lex::Tok::Lt) || at(lex::Tok::Gt) || at(lex::Tok::Le) || at(lex::Tok::Ge)) {
        const lex::Token op = next();
        Expr* r = parseAdditive();
        uint8_t oid = fmt::OpLt;
        switch (op.kind) {
            case lex::Tok::Gt: oid = fmt::OpGt; break;
            case lex::Tok::Le: oid = fmt::OpLe; break;
            case lex::Tok::Ge: oid = fmt::OpGe; break;
            default: break;
        }
        l = makeBinary(oid, l, r, op);
    }
    return l;
}
Expr* Parser::parseAdditive() {
    Expr* l = parseMultiplicative();
    while (at(lex::Tok::Plus) || at(lex::Tok::Minus)) {
        const lex::Token op = next();
        Expr* r = parseMultiplicative();
        l = makeBinary(op.kind == lex::Tok::Plus ? fmt::OpPlus : fmt::OpMinus, l, r, op);
    }
    return l;
}
Expr* Parser::parseMultiplicative() {
    Expr* l = parsePower();
    while (at(lex::Tok::Star) || at(lex::Tok::Slash) || at(lex::Tok::SlashSlash)) {
        const lex::Token op = next();
        Expr* r = parsePower();
        uint8_t oid = fmt::OpStar;
        switch (op.kind) {
            case lex::Tok::Slash: oid = fmt::OpSlash; break;
            case lex::Tok::SlashSlash: oid = fmt::OpFloorDiv; break;
            default: break;
        }
        l = makeBinary(oid, l, r, op);
    }
    return l;
}
Expr* Parser::parsePower() {
    Expr* l = parseUnary();
    if (at(lex::Tok::StarStar)) {
        const lex::Token op = next();
        Expr* r = parsePower(); // right-associative
        l = makeBinary(fmt::OpPow, l, r, op);
    }
    return l;
}
Expr* Parser::parseUnary() {
    if (at(lex::Tok::Bang) || at(lex::Tok::Minus)) {
        const lex::Token op = next();
        Expr* operand = parseUnary();
        return makeUnary(op.kind == lex::Tok::Bang ? uint8_t(fmt::OpNot) : uint8_t(fmt::OpMinus),
                         operand, op);
    }
    return parsePrimary();
}

Expr* Parser::makePoison(SrcLoc loc) {
    auto e = std::make_unique<Expr>();
    e->kind = Expr::Kind::Literal;
    e->loc = loc;
    e->type = nullptr; // suppresses cascading diagnostics
    Expr* raw = e.get();
    src_.exprPool.push_back(std::move(e));
    return raw;
}

Expr* Parser::makeBinary(uint8_t op, Expr* l, Expr* r, const lex::Token& opTok) {
    if (!l || !r) return makePoison({opTok.line, opTok.col});
    if (!l->type || !r->type) return makePoison({opTok.line, opTok.col});
    std::string errMsg;
    const char* sym = "+";
    switch (op) {
        case fmt::OpPlus: sym = "+"; break;
        case fmt::OpMinus: sym = "-"; break;
        case fmt::OpStar: sym = "*"; break;
        case fmt::OpPow: sym = "**"; break;
        case fmt::OpSlash: sym = "/"; break;
        case fmt::OpFloorDiv: sym = "//"; break;
        case fmt::OpLogicalAnd: sym = "&&"; break;
        case fmt::OpLogicalOr: sym = "||"; break;
        case fmt::OpEqEq: sym = "=="; break;
        case fmt::OpNotEq: sym = "!="; break;
        case fmt::OpLt: sym = "<"; break;
        case fmt::OpGt: sym = ">"; break;
        case fmt::OpLe: sym = "<="; break;
        case fmt::OpGe: sym = ">="; break;
        default: break;
    }
    const TypeDefinition* res = binaryOpType(op, l->type, r->type, errMsg, sym);
    if (!res) {
        errorAt(opTok, errMsg);
        return makePoison({opTok.line, opTok.col});
    }
    auto e = std::make_unique<Expr>();
    e->kind = Expr::Kind::Binary;
    e->loc = {opTok.line, opTok.col};
    e->type = res;
    e->op = op;
    e->left = l;
    e->right = r;
    Expr* raw = e.get();
    src_.exprPool.push_back(std::move(e));
    return raw;
}

Expr* Parser::makeUnary(uint8_t op, Expr* operand, const lex::Token& opTok) {
    if (!operand || !operand->type) return makePoison({opTok.line, opTok.col});
    std::string errMsg;
    const char* sym = op == fmt::OpNot ? "!" : "-";
    const TypeDefinition* res = unaryOpType(op, operand->type, errMsg, sym);
    if (!res) {
        errorAt(opTok, errMsg);
        return makePoison({opTok.line, opTok.col});
    }
    auto e = std::make_unique<Expr>();
    e->kind = Expr::Kind::Unary;
    e->loc = {opTok.line, opTok.col};
    e->type = res;
    e->op = op;
    e->left = operand;
    Expr* raw = e.get();
    src_.exprPool.push_back(std::move(e));
    return raw;
}

Expr* Parser::makeIntLiteral(const lex::Token& t) {
    if (t.intValue < INT32_MIN || t.intValue > INT32_MAX) {
        errorAt(t, "integer literal out of range for int (32-bit)");
        return makePoison({t.line, t.col});
    }
    auto e = std::make_unique<Expr>();
    e->kind = Expr::Kind::Literal;
    e->loc = {t.line, t.col};
    e->type = BuiltinTypes::instance().find("int");
    e->litInt = static_cast<int32_t>(t.intValue);
    Expr* raw = e.get();
    src_.exprPool.push_back(std::move(e));
    return raw;
}

Expr* Parser::makeRealLiteral(const lex::Token& t, const TypeDefinition* type) {
    auto e = std::make_unique<Expr>();
    e->kind = Expr::Kind::Literal;
    e->loc = {t.line, t.col};
    e->type = type;
    e->litReal = t.realValue;
    Expr* raw = e.get();
    src_.exprPool.push_back(std::move(e));
    return raw;
}

Expr* Parser::parseVectorLiteral(const lex::Token& nameTok, const TypeDefinition* vecType) {
    const int componentCount = static_cast<int>(vecType->sizeBytes) / 4;
    auto e = std::make_unique<Expr>();
    e->kind = Expr::Kind::Literal;
    e->loc = {nameTok.line, nameTok.col};
    e->type = vecType;
    Expr* raw = e.get();
    src_.exprPool.push_back(std::move(e));

    if (!expect(lex::Tok::LParen, "'(' after the vector type name")) return raw;
    std::vector<Expr*> comps;
    if (!at(lex::Tok::RParen)) {
        do {
            Expr* c = parseExpr();
            if (c) comps.push_back(c);
        } while (at(lex::Tok::Comma) ? (next(), true) : false);
    }
    expect(lex::Tok::RParen, "')' closing the vector literal");

    if (static_cast<int>(comps.size()) != componentCount) {
        errorAt(nameTok, "vector literal '" + vecType->name + "' expects " +
                             std::to_string(componentCount) + " components, got " +
                             std::to_string(comps.size()));
        return raw;
    }
    ConstPool pool;
    for (const auto& [n, i] : constNames_) pool.set(n, src_.globals[i]);
    for (const Expr* c : comps) {
        ConstValue cv;
        std::string err;
        if (!foldExpr(c, pool, cv, err)) {
            errorAt(nameTok, "vector literal '" + vecType->name +
                                   "' components must be constant float expressions (" + err + ")");
            return raw;
        }
        if (cv.type != BuiltinTypes::instance().find("float")) {
            errorAt(nameTok, "vector literal '" + vecType->name +
                                   "' components must be float, got " + cv.type->name);
            return raw;
        }
    }
    for (int k = 0; k < componentCount; ++k) {
        ConstValue cv;
        std::string err;
        (void)foldExpr(comps[k], pool, cv, err);
        raw->litVec[k] = cv.f;
    }
    raw->litVecCount = componentCount;
    return raw;
}

Expr* Parser::parseCallExpr(const lex::Token& nameTok) {
    auto e = std::make_unique<Expr>();
    e->kind = Expr::Kind::Call;
    e->loc = {nameTok.line, nameTok.col};
    e->funcName = nameTok.text;
    Expr* raw = e.get();
    src_.exprPool.push_back(std::move(e));

    if (!expect(lex::Tok::LParen, "'(' after the function name")) return raw;
    if (!at(lex::Tok::RParen)) {
        do {
            Expr* a = parseExpr();
            if (a) raw->args.push_back(a);
            else raw->args.push_back(makePoison({cur().line, cur().col}));
        } while (at(lex::Tok::Comma) ? (next(), true) : false);
    }
    expect(lex::Tok::RParen, "')' closing the argument list");

    const FunctionDefinition* fn = resolveCall(nameTok.text, raw->args, nameTok);
    if (fn) {
        raw->functionId = fn->functionId;
        raw->tier = fn->tier;
        raw->type = BuiltinTypes::instance().find(fn->returnType);
    } else {
        raw->type = nullptr;
    }
    return raw;
}

Expr* Parser::parseVarRefExpr(const lex::Token& nameTok) {
    VarInfo vi;
    if (!resolveVariable(nameTok, vi)) {
        return makePoison({nameTok.line, nameTok.col});
    }
    auto e = std::make_unique<Expr>();
    e->kind = Expr::Kind::VarRef;
    e->loc = {nameTok.line, nameTok.col};
    e->type = vi.type;
    e->var = vi;
    Expr* raw = e.get();
    src_.exprPool.push_back(std::move(e));
    return raw;
}

Expr* Parser::parsePrimary() {
    const lex::Token& t = cur();
    switch (t.kind) {
        case lex::Tok::IntLit: {
            next();
            return makeIntLiteral(t);
        }
        case lex::Tok::FloatLit: {
            next();
            return makeRealLiteral(t, BuiltinTypes::instance().find("float"));
        }
        case lex::Tok::DoubleLit: {
            next();
            return makeRealLiteral(t, BuiltinTypes::instance().find("double"));
        }
        case lex::Tok::KwTrue: {
            next();
            auto e = std::make_unique<Expr>();
            e->kind = Expr::Kind::Literal;
            e->loc = {t.line, t.col};
            e->type = BuiltinTypes::instance().find("bool");
            e->litBool = true;
            Expr* raw = e.get();
            src_.exprPool.push_back(std::move(e));
            return raw;
        }
        case lex::Tok::KwFalse: {
            next();
            auto e = std::make_unique<Expr>();
            e->kind = Expr::Kind::Literal;
            e->loc = {t.line, t.col};
            e->type = BuiltinTypes::instance().find("bool");
            e->litBool = false;
            Expr* raw = e.get();
            src_.exprPool.push_back(std::move(e));
            return raw;
        }
        case lex::Tok::StringLit: {
            errorAt(t, "string literals are not supported (string type is reserved for future use)");
            next();
            return makePoison({t.line, t.col});
        }
        case lex::Tok::Ident: {
            next();
            if (at(lex::Tok::LParen)) {
                const TypeDefinition* vecType = BuiltinTypes::instance().find(t.text);
                const bool isVectorType = vecType && vecType->isNumeric &&
                                          vecType->category == "vector";
                const bool isFunctionName =
                    !BuiltinFunctions::instance().find(t.text).empty();
                if (isVectorType && !isFunctionName) {
                    return parseVectorLiteral(t, vecType);
                }
                return parseCallExpr(t);
            }
            return parseVarRefExpr(t);
        }
        case lex::Tok::LParen: {
            next();
            Expr* e = parseExpr();
            expect(lex::Tok::RParen, "')' closing the parenthesized expression");
            return e;
        }
        default: {
            errorAt(t, std::string("unexpected token '") + t.text + "' in expression");
            next();
            return makePoison({t.line, t.col});
        }
    }
}

// ---------------------------------------------------------------------------
// overload resolution (spec 0.4) and statement-call semantics
// ---------------------------------------------------------------------------

const FunctionDefinition* Parser::resolveCall(const std::string& name,
                                              const std::vector<Expr*>& args,
                                              const lex::Token& nameTok) {
    const auto& registry = BuiltinFunctions::instance();
    std::vector<const FunctionDefinition*> overloads = registry.find(name);
    if (overloads.empty()) {
        errorAt(nameTok, "unknown function '" + name + "'");
        return nullptr;
    }

    // Guard: if any argument subtree already failed, skip the (noisy) match.
    for (const Expr* a : args) {
        if (!a || !a->type) {
            errorAt(nameTok, "call to '" + name + "' has an invalid argument expression");
            return nullptr;
        }
    }

    // 1. filter by arity, 2. filter by exact type match on every argument.
    std::vector<const FunctionDefinition*> survivors;
    for (const FunctionDefinition* fn : overloads) {
        if (fn->params.size() != args.size()) continue;
        bool ok = true;
        for (std::size_t i = 0; i < fn->params.size(); ++i) {
            if (args[i]->type->name != fn->params[i].typeName) { ok = false; break; }
        }
        if (ok) survivors.push_back(fn);
    }

    auto argTypes = [&]() {
        std::string s;
        for (std::size_t i = 0; i < args.size(); ++i) {
            if (i) s += ", ";
            s += args[i]->type->name;
        }
        return s;
    };
    auto candidates = [&]() {
        std::string s;
        for (const FunctionDefinition* fn : overloads) {
            if (!s.empty()) s += "; ";
            s += fullSignature(fn);
        }
        return s;
    };

    if (survivors.empty()) {
        errorAt(nameTok, "no overload of '" + name + "' matches arguments (" + argTypes() +
                             "). Candidates: " + candidates());
        return nullptr;
    }
    if (survivors.size() > 1) {
        errorAt(nameTok, "call to '" + name + "' with arguments (" + argTypes() +
                             ") is ambiguous. Candidates: " + candidates());
        return nullptr;
    }
    return survivors[0];
}

std::string Parser::fullSignature(const FunctionDefinition* fn) {
    std::string s = fn->name + "(";
    for (std::size_t i = 0; i < fn->params.size(); ++i) {
        if (i) s += ", ";
        s += fn->params[i].typeName + " " + fn->params[i].name;
    }
    s += ")";
    return s;
}

void Parser::applyStatementCallSemantics(Stmt& st, const FunctionDefinition* fn,
                                         const lex::Token& nameTok) {
    // Tier 2: the mutating argument (first parameter) cannot be a static
    // constant.
    if (fn->tier == 2 && !fn->params.empty() && !st.args.empty()) {
        const Expr* a0 = st.args[0];
        if (a0 && a0->kind == Expr::Kind::VarRef &&
            a0->var.kind == VarKind::GlobalConst) {
            errorAt(nameTok, "static constants cannot be the mutating argument of Tier 2 "
                              "function '" + fn->name + "'");
        }
    }
    // Tier 3: every declared claim binds to the call's first argument, which
    // must be a variable (runtime or temp) so the ownership handoff can be
    // encoded at compile time.
    if (fn->tier == 3 && !fn->claims.empty()) {
        if (st.args.empty()) return;
        const Expr* a0 = st.args[0];
        if (!a0 || a0->kind != Expr::Kind::VarRef ||
            a0->var.kind == VarKind::GlobalConst) {
            errorAt(nameTok, "Tier 3 function '" + fn->name + "' claims '" +
                            fn->claims.front() + "'; its first argument must be a runtime or "
                            "temporary variable so the claim can be bound");
            return;
        }
        for (const std::string& claim : fn->claims) {
            uint8_t field = 0;
            if (!fmt::claimFieldIndex(claim, field)) {
                errorAt(nameTok, "internal: unknown claim field in '" + claim + "'");
                continue;
            }
            ClaimBinding cb;
            cb.var = a0->var;
            cb.fieldIndex = field;
            cb.claim = claim;
            st.claims.push_back(std::move(cb));
        }
    }
}

// ---------------------------------------------------------------------------
// variable resolution
// ---------------------------------------------------------------------------

bool Parser::resolveVariable(const lex::Token& nameTok, VarInfo& out) {
    // 1. temporaries: scope stack, top frame first
    if (const int* id = scope_.find(nameTok.text)) {
        const TempVar& tv = src_.temps[*id];
        out = VarInfo{VarKind::Temp, tv.index, tv.name, tv.type};
        return true;
    }
    // 2. global section: runtime vars and static constants
    for (const auto& g : src_.globals) {
        if (g.name == nameTok.text) {
            out = VarInfo{g.isConst ? VarKind::GlobalConst : VarKind::Runtime,
                          g.index, g.name, g.type};
            return true;
        }
    }
    errorAt(nameTok, "unknown variable '" + nameTok.text + "'");
    return false;
}

} // namespace fsmc
