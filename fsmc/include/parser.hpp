#pragma once

// Recursive-descent parser. Produces the ordered AST forest (ParsedSource)
// with all parser-level validations from spec section 1 / section 4 applied
// before Pass 2 runs:
//
//   * C block scoping for temps (scope stack, push on '{', pop on '}')
//   * state-level temps legal (depth 1), temps only inside {} bodies
//   * no nested conditionals (an if/else-if/else chain is one conditional)
//   * no bare blocks
//   * Traversals structure (>=1 goto, only ifs, exactly one goto per if body)
//   * full-tuple overload resolution via BuiltinFunctions
//   * all type references resolved via BuiltinTypes
//   * operator type rules (type_rules.hpp)
//   * static-constant / Tier-2 / Tier-3 claim rules

#include "ast.hpp"
#include "builtin_functions.hpp"
#include "diagnostics.hpp"
#include "lexer.hpp"
#include "scope.hpp"

namespace fsmc {

class Parser {
public:
    Parser(const std::vector<lex::Token>& tokens, Diagnostics& diag);
    ParsedSource parse();

private:
    // token stream helpers
    const lex::Token& cur() const;
    const lex::Token& peekAt(std::size_t n) const;
    bool at(lex::Tok k) const;
    const lex::Token& next();
    const lex::Token* expect(lex::Tok k, const char* what);
    void errorAt(const lex::Token& t, const std::string& msg);
    void warnAt(const lex::Token& t, const std::string& msg);
    void skipToStatementBoundary();
    void skipMatchingBraces(); // error recovery for stray blocks

    // top level
    void parseTopLevel();
    void parseGlobalConst();
    void parseGlobalVar();
    void parseEntry();
    void parseState();
    void parseStateBody(StateDef& st, int stateIndex);
    void parseActionsBlock(StateBodyItem& item);
    void parseTraversalsBlock(StateBodyItem& item);

    // statements
    void parseActionStmts(std::vector<Stmt>& out, uint8_t depth);
    Stmt parseTempDecl(uint8_t depth, int stateIndex);
    Stmt parseAssignStmt(uint8_t depth, const lex::Token& nameTok);
    Stmt parseCallStmt(uint8_t depth, const lex::Token& nameTok);
    void parseIfChain(std::vector<Stmt>& out, uint8_t depth);
    Stmt parseIfHead(Stmt::Kind kind);
    void parseTraversalsStmts(std::vector<Stmt>& out);

    // expressions (C precedence)
    Expr* parseExpr();
    Expr* parseOr();
    Expr* parseAnd();
    Expr* parseEquality();
    Expr* parseRelational();
    Expr* parseAdditive();
    Expr* parseMultiplicative();
    Expr* parsePower();
    Expr* parseUnary();
    Expr* parsePrimary();
    Expr* makeBinary(uint8_t op, Expr* l, Expr* r, const lex::Token& opTok);
    Expr* makeUnary(uint8_t op, Expr* operand, const lex::Token& opTok);
    Expr* makePoison(SrcLoc loc);
    Expr* makeIntLiteral(const lex::Token& t);
    Expr* makeRealLiteral(const lex::Token& t, const TypeDefinition* type);
    Expr* parseVectorLiteral(const lex::Token& nameTok, const TypeDefinition* vecType);
    Expr* parseCallExpr(const lex::Token& nameTok);
    Expr* parseVarRefExpr(const lex::Token& nameTok);

    // call resolution (full-tuple overload matching, spec 0.4)
    const FunctionDefinition* resolveCall(const std::string& name,
                                          const std::vector<Expr*>& args,
                                          const lex::Token& nameTok);
    static std::string fullSignature(const FunctionDefinition* fn);
    void applyStatementCallSemantics(Stmt& st, const FunctionDefinition* fn,
                                     const lex::Token& nameTok);

    // variable resolution: temp scope stack -> runtime vars -> static consts
    bool resolveVariable(const lex::Token& nameTok, VarInfo& out);

    ScopeStack scope_;
    const std::vector<lex::Token>& toks_;
    std::size_t pos_ = 0;
    Diagnostics& diag_;
    ParsedSource src_;

    // parse context
    int stateIndex_ = -1;
    bool inConditionalBody_ = false; // inside an if/else-if/else body
    bool seenTravIf_ = false;        // traversals: an if has already appeared
    std::vector<std::pair<std::string, int>> constNames_; // for fold ordering checks
};

} // namespace fsmc
