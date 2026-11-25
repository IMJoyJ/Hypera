using System.Collections.Immutable;
using Hypera.Diagnostics;

namespace Hypera.Compiler;

public enum ErbTokenKind : byte
{
    EndOfLine = 0,
    IntegerLiteral = 1,
    StringLiteral = 2,
    FormStringLiteral = 3,
    InlineYenAtForm = 4,
    Identifier = 5,
    Operator = 6,
    OpenParen = 7,      // (
    CloseParen = 8,     // )
    OpenBracket = 9,    // [
    CloseBracket = 10,  // ]
    Colon = 11,         // :
    Comma = 12,         // ,
    At = 13,            // @
    Dot = 14,           // .
}

public enum OperatorKind : byte
{
    None = 0,

    // Arithmetic
    Plus,               // + (unary or binary)
    Minus,              // - (unary or binary)
    Multiply,           // *
    Divide,             // /
    Modulo,             // %

    // Unary / Postfix
    BitNot,             // ~
    LogicalNot,         // !
    Increment,          // ++ (prefix or postfix)
    Decrement,          // -- (prefix or postfix)

    // Shifts
    ShiftLeft,          // <<
    ShiftRight,         // >>

    // Comparisons
    Less,               // <
    LessEqual,          // <=
    Greater,            // >
    GreaterEqual,       // >=
    Equal,              // ==
    NotEqual,           // !=

    // Bitwise
    BitAnd,             // &
    BitXor,             // ^
    BitOr,              // |

    // Logical
    LogicalAnd,         // &&
    LogicalNand,        // !&
    LogicalOr,          // ||
    LogicalNor,         // !|
    LogicalXor,         // ^^

    // Ternary
    TernaryQuestion,    // ?
    TernaryHash,        // #

    // Assignments
    Assign,             // =
    AssignStr,          // '=
    AddAssign,          // +=
    SubAssign,          // -=
    MulAssign,          // *=
    DivAssign,          // /=
    ModAssign,          // %=
    BitAndAssign,       // &=
    BitOrAssign,        // |=
    BitXorAssign,       // ^=
    ShiftLeftAssign,    // <<=
    ShiftRightAssign,   // >>=
}

/// <summary>
/// Raw formatted string segment produced by scanning <c>@"..."</c>, <c>PRINTFORM</c>, or <c>\@ ... \@</c>.
/// </summary>
public enum RawFormPartKind : byte
{
    Literal = 0,
    PercentExpr = 1,      // %strExpr[,width[,LEFT|RIGHT]]%
    CurlyIntExpr = 2,     // {intExpr[,width[,LEFT|RIGHT]]}
    YenAtConditional = 3, // \@ cond ? trueForm # falseForm \@
    TripleSymbol = 4,     // ***, +++, ===, ///, $$$
}

public sealed record RawFormPart(
    RawFormPartKind Kind,
    SourceSpan Span,
    string? LiteralText = null,
    ImmutableArray<ErbToken> ExprTokens = default,
    ImmutableArray<RawFormPart> TrueBranchParts = default,
    ImmutableArray<RawFormPart> FalseBranchParts = default,
    bool HasFalseBranch = false,
    char TripleSymbolChar = '\0');

public readonly record struct ErbToken(
    ErbTokenKind Kind,
    SourceSpan Span,
    long IntValue = 0,
    string? Text = null,
    OperatorKind Operator = OperatorKind.None,
    ImmutableArray<RawFormPart> FormParts = default)
{
    public static ErbToken Eol(SourceSpan span) => new(ErbTokenKind.EndOfLine, span);
}

/// <summary>
/// Represents one active logical source line produced by <see cref="ErbPreprocessor"/>.
/// </summary>
public sealed record PreprocessedLine(
    int FileId,
    string FilePath,
    int LineNo,
    int EndLineNo,
    int StartColumn,
    string Text,
    string RawLineText)
{
    public SourceSpan CreateSpan(int colOffset = 0, int length = 1)
    {
        int startCol = Math.Max(1, StartColumn + colOffset);
        int endCol = Math.Max(startCol, startCol + Math.Max(1, length) - 1);
        return new SourceSpan(FileId, LineNo, startCol, LineNo, endCol);
    }
}

/// <summary>
/// Token stream cursor with bounds-safe lookahead over a <see cref="ErbToken"/> sequence.
/// </summary>
public sealed class TokenStream
{
    private readonly ImmutableArray<ErbToken> _tokens;
    private readonly SourceSpan _eolSpan;
    private int _index;

    public TokenStream(ImmutableArray<ErbToken> tokens, SourceSpan eolSpan)
    {
        _tokens = tokens;
        _eolSpan = eolSpan;
    }

    public int Position
    {
        get => _index;
        set => _index = Math.Clamp(value, 0, _tokens.Length);
    }

    public int Count => _tokens.Length;

    public bool IsAtEnd => _index >= _tokens.Length;

    public ErbToken Current => _index < _tokens.Length ? _tokens[_index] : ErbToken.Eol(_eolSpan);

    public ErbToken Peek(int offset = 1)
    {
        int target = _index + offset;
        return (uint)target < (uint)_tokens.Length ? _tokens[target] : ErbToken.Eol(_eolSpan);
    }

    public ErbToken Advance()
    {
        ErbToken cur = Current;
        if (_index < _tokens.Length)
        {
            _index++;
        }

        return cur;
    }

    public bool Match(ErbTokenKind kind)
    {
        if (Current.Kind == kind)
        {
            _index++;
            return true;
        }

        return false;
    }

    public bool MatchOperator(OperatorKind op)
    {
        if (Current.Kind == ErbTokenKind.Operator && Current.Operator == op)
        {
            _index++;
            return true;
        }

        return false;
    }
}
