using System.Collections.Immutable;
using Hypera.Bytecode;
using Hypera.Diagnostics;

namespace Hypera.Compiler;

/// <summary>
/// Base node for all statically type-checked ERH/ERB expressions.
/// </summary>
public abstract record ExprNode(VariableValueKind ValueKind, SourceSpan Span);

public sealed record IntLiteralExpr(long Value, SourceSpan Span)
    : ExprNode(VariableValueKind.Int, Span);

public sealed record StrLiteralExpr(string Value, SourceSpan Span)
    : ExprNode(VariableValueKind.Str, Span);

public sealed record UnaryExpr(
    OperatorKind Operator,
    ExprNode Operand,
    VariableValueKind ValueKind,
    SourceSpan Span)
    : ExprNode(ValueKind, Span);

public sealed record IncDecExpr(
    OperatorKind Operator,
    VariableAccessExpr Target,
    bool IsPostfix,
    SourceSpan Span)
    : ExprNode(VariableValueKind.Int, Span);

public sealed record BinaryExpr(
    OperatorKind Operator,
    ExprNode Left,
    ExprNode Right,
    VariableValueKind ValueKind,
    SourceSpan Span)
    : ExprNode(ValueKind, Span);

public sealed record TernaryExpr(
    ExprNode Condition,
    ExprNode TrueExpr,
    ExprNode FalseExpr,
    VariableValueKind ValueKind,
    SourceSpan Span)
    : ExprNode(ValueKind, Span);

public sealed record KeywordSubscriptExpr(
    string VariableName,
    int DimensionIndex,
    ExprNode KeyStringExpr,
    SourceSpan Span)
    : ExprNode(VariableValueKind.Int, Span);

public sealed record VariableAccessExpr(
    ResolvedVariable Variable,
    ImmutableArray<ExprNode> Subscripts,
    bool IsBareArrayReference,
    SourceSpan Span)
    : ExprNode(Variable.ValueKind, Span);

public abstract record CompiledFormSegment(SourceSpan Span);

public sealed record LiteralFormSegment(string Text, SourceSpan Span)
    : CompiledFormSegment(Span);

public sealed record ValueFormSegment(
    ExprNode Value,
    ExprNode? Width,
    bool LeftAlign,
    SourceSpan Span)
    : CompiledFormSegment(Span);

public sealed record ConditionalFormSegment(
    ExprNode Condition,
    StrFormExpr TrueBranch,
    StrFormExpr? FalseBranch,
    SourceSpan Span)
    : CompiledFormSegment(Span);

public sealed record StrFormExpr(
    ImmutableArray<CompiledFormSegment> Segments,
    SourceSpan Span)
    : ExprNode(VariableValueKind.Str, Span);

public sealed record BuiltinMethodCallExpr(
    BuiltinMethodSpec Method,
    ImmutableArray<ExprNode?> Arguments,
    SourceSpan Span)
    : ExprNode(Method.ReturnKind, Span);

public sealed record UserMethodCallExpr(
    string FunctionName,
    int TargetFunctionId,
    VariableValueKind ReturnKind,
    ImmutableArray<ExprNode?> Arguments,
    SourceSpan Span)
    : ExprNode(ReturnKind, Span);

public enum CaseMatchKind : byte
{
    Exact = 0,
    RangeTo = 1,
    IsRelational = 2,
}

public sealed record CaseClauseCondition(
    CaseMatchKind Kind,
    ExprNode PrimaryExpr,
    ExprNode? SecondaryExpr = null,
    OperatorKind RelationalOperator = OperatorKind.None);

/// <summary>
/// Represents a statically resolved variable symbol in <see cref="SymbolTable"/>.
/// </summary>
public sealed class ResolvedVariable
{
    public required string Name { get; init; }
    public required int SlotId { get; set; }
    public required VariableValueKind ValueKind { get; init; }
    public required VariableScopeKind Scope { get; init; }
    public required int ArrayRank { get; init; }
    public required ImmutableArray<int> Dimensions { get; set; }
    public bool IsCharaData { get; init; }
    public bool IsSavedata { get; init; }
    public bool IsGlobal { get; init; }
    public bool IsConst { get; init; }
    public bool IsCalc { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsByRef { get; init; }
    public string? OwnerFunctionName { get; init; }
    public string? AssociatedCsvNameTable { get; init; }

    /// <summary>
    /// True if this dynamic scalar local is promoted directly to a <see cref="Runtime.CallFrame"/> register.
    /// </summary>
    public bool IsRegisterPromoted { get; set; }

    public ushort PromotedRegisterIndex { get; set; }

    /// <summary>
    /// Compile-time initializers for <c>#DIM</c> / <c>#DIMS</c> (and <c>CONST</c> folding values).
    /// </summary>
    public ImmutableArray<long> DefaultIntValues { get; init; } = [];
    public ImmutableArray<string> DefaultStrValues { get; init; } = [];

    public bool IsScalarZeroDim => !IsCharaData && ArrayRank == 0;

    public VariableSlotDescriptor ToDescriptor() =>
        new(
            SlotId,
            OwnerFunctionName is null ? Name : $"{Name}@{OwnerFunctionName}",
            ValueKind,
            Scope,
            Dimensions,
            IsCharaData,
            IsSavedata,
            IsGlobal,
            IsConst);
}

/// <summary>
/// Represents a <c>#DEFINE</c> token-replacement macro declared in an <c>.ERH</c> header.
/// </summary>
public sealed record DefineMacroSymbol(
    string Name,
    ImmutableArray<ErbToken> ReplacementTokens,
    SourceSpan DeclarationSpan);

/// <summary>
/// Represents a <c>#FUNCTION</c> / <c>#FUNCTIONS</c> function reference variable declared in <c>.ERH</c>.
/// </summary>
public sealed record FunctionReferenceSymbol(
    string Name,
    VariableValueKind ReturnKind,
    SourceSpan DeclarationSpan);
