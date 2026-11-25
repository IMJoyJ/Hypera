using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Hypera.Bytecode;
using Hypera.Diagnostics;

namespace Hypera.Compiler;

/// <summary>
/// Full Emuera expression parser with exact operator precedence, constant folding,
/// variable subscript &amp; keyword resolution, <c>AllowFunctionOverloading</c> method binding,
/// <c>ref</c> array argument validation, and <c>SELECTCASE</c> clause parsing.
/// </summary>
public sealed class ExpressionParser
{
    private readonly SymbolTable _symbols;
    private readonly StrFormParser _strFormParser;

    public ExpressionParser(SymbolTable symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        _symbols = symbols;
        _strFormParser = new StrFormParser(symbols, this);
    }

    public StrFormParser StrFormParser => _strFormParser;

    public ExprNode ParseIntExpression(
        TokenStream stream,
        UserFunctionSymbol? activeFunction,
        string? filePath,
        DiagnosticBag diagnostics,
        Action<UserFunctionSymbol>? onUserFunctionReferenced = null)
    {
        ExprNode expr = ParseExpression(stream, activeFunction, filePath, diagnostics, onUserFunctionReferenced: onUserFunctionReferenced);
        if (expr.ValueKind != VariableValueKind.Int)
        {
            diagnostics.ReportError("ERA1401", "Expected an integer expression, but got a string expression.", expr.Span, filePath);
            return new IntLiteralExpr(0, expr.Span);
        }

        return expr;
    }

    public ExprNode ParseStrExpression(
        TokenStream stream,
        UserFunctionSymbol? activeFunction,
        string? filePath,
        DiagnosticBag diagnostics,
        Action<UserFunctionSymbol>? onUserFunctionReferenced = null)
    {
        ExprNode expr = ParseExpression(stream, activeFunction, filePath, diagnostics, onUserFunctionReferenced: onUserFunctionReferenced);
        if (expr.ValueKind != VariableValueKind.Str)
        {
            diagnostics.ReportError("ERA1402", "Expected a string expression, but got an integer expression.", expr.Span, filePath);
            return new StrLiteralExpr(string.Empty, expr.Span);
        }

        return expr;
    }

    public ExprNode ParseExpression(
        TokenStream stream,
        UserFunctionSymbol? activeFunction,
        string? filePath,
        DiagnosticBag diagnostics,
        bool stopAtComma = false,
        bool stopAtAssign = false,
        bool allowToKeyword = false,
        bool stopAtPxSuffix = false,
        Action<UserFunctionSymbol>? onUserFunctionReferenced = null)
    {
        var ctx = new ParseContext(
            stream,
            activeFunction,
            filePath,
            diagnostics,
            stopAtComma,
            stopAtAssign,
            allowToKeyword,
            stopAtPxSuffix,
            InSubscript: false,
            SubscriptVar: null,
            SubscriptDimension: 0,
            onUserFunctionReferenced);
        return ParseTernary(ref ctx);
    }

    public ImmutableArray<ExprNode?> ParseArgumentList(
        TokenStream stream,
        ErbTokenKind? closingDelimiter,
        UserFunctionSymbol? activeFunction,
        string? filePath,
        DiagnosticBag diagnostics,
        Action<UserFunctionSymbol>? onUserFunctionReferenced = null)
    {
        var args = new List<ExprNode?>();

        if (closingDelimiter is { } close && stream.Match(close))
        {
            return [];
        }

        if (closingDelimiter is null && stream.IsAtEnd)
        {
            return [];
        }

        while (true)
        {
            if (stream.IsAtEnd)
            {
                if (closingDelimiter is not null)
                {
                    diagnostics.ReportError("ERA1403", $"Missing closing '{closingDelimiter}' in argument list.", stream.Current.Span, filePath);
                }
                else
                {
                    args.Add(null);
                }

                break;
            }

            if (closingDelimiter is { } c && stream.Current.Kind == c)
            {
                args.Add(null);
                stream.Advance();
                break;
            }

            if (stream.Current.Kind == ErbTokenKind.Comma)
            {
                args.Add(null);
                stream.Advance();
                continue;
            }

            ExprNode arg = ParseExpression(
                stream,
                activeFunction,
                filePath,
                diagnostics,
                stopAtComma: true,
                onUserFunctionReferenced: onUserFunctionReferenced);
            args.Add(arg);

            if (stream.Match(ErbTokenKind.Comma))
            {
                continue;
            }

            if (closingDelimiter is { } closeKind)
            {
                if (!stream.Match(closeKind))
                {
                    diagnostics.ReportError("ERA1404", $"Expected ',' or '{closeKind}' in argument list.", stream.Current.Span, filePath);
                }

                break;
            }

            if (!stream.IsAtEnd)
            {
                diagnostics.ReportError("ERA1405", $"Unexpected token '{stream.Current.Text ?? stream.Current.Kind.ToString()}' after argument.", stream.Current.Span, filePath);
                break;
            }

            break;
        }

        return [.. args];
    }

    public ImmutableArray<CaseClauseCondition> ParseCaseClauseConditions(
        TokenStream stream,
        VariableValueKind selectorKind,
        UserFunctionSymbol? activeFunction,
        string? filePath,
        DiagnosticBag diagnostics,
        Action<UserFunctionSymbol>? onUserFunctionReferenced = null)
    {
        var list = new List<CaseClauseCondition>();
        StringComparison cmp = _symbols.Options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        while (!stream.IsAtEnd)
        {
            ErbToken cur = stream.Current;
            if (cur.Kind == ErbTokenKind.Identifier && cur.Text is not null && cur.Text.Equals("IS", cmp))
            {
                stream.Advance();
                if (stream.Current.Kind != ErbTokenKind.Operator || !IsBinaryCaseOperator(stream.Current.Operator))
                {
                    diagnostics.ReportError("ERA1406", "Expected a binary relational/comparison operator after 'CASE IS'.", stream.Current.Span, filePath);
                    break;
                }

                OperatorKind relOp = stream.Advance().Operator;
                ExprNode rhs = ParseExpression(
                    stream, activeFunction, filePath, diagnostics, stopAtComma: true, onUserFunctionReferenced: onUserFunctionReferenced);
                ValidateCaseOperandType(rhs, selectorKind, filePath, diagnostics);
                list.Add(new CaseClauseCondition(CaseMatchKind.IsRelational, rhs, RelationalOperator: relOp));
            }
            else
            {
                ExprNode first = ParseExpression(
                    stream, activeFunction, filePath, diagnostics, stopAtComma: true, allowToKeyword: true, onUserFunctionReferenced: onUserFunctionReferenced);
                ValidateCaseOperandType(first, selectorKind, filePath, diagnostics);

                if (stream.Current.Kind == ErbTokenKind.Identifier && stream.Current.Text is not null && stream.Current.Text.Equals("TO", cmp))
                {
                    stream.Advance();
                    ExprNode second = ParseExpression(
                        stream, activeFunction, filePath, diagnostics, stopAtComma: true, allowToKeyword: true, onUserFunctionReferenced: onUserFunctionReferenced);
                    ValidateCaseOperandType(second, selectorKind, filePath, diagnostics);

                    if (stream.Current.Kind == ErbTokenKind.Identifier && stream.Current.Text is not null && stream.Current.Text.Equals("TO", cmp))
                    {
                        diagnostics.ReportError("ERA1407", "Duplicate 'TO' in CASE range expression.", stream.Current.Span, filePath);
                    }

                    list.Add(new CaseClauseCondition(CaseMatchKind.RangeTo, first, second));
                }
                else
                {
                    list.Add(new CaseClauseCondition(CaseMatchKind.Exact, first));
                }
            }

            if (!stream.Match(ErbTokenKind.Comma))
            {
                if (!stream.IsAtEnd)
                {
                    diagnostics.ReportError("ERA1408", "Expected ',' between CASE conditions.", stream.Current.Span, filePath);
                }

                break;
            }
        }

        if (list.Count == 0)
        {
            diagnostics.ReportError("ERA1409", "CASE statement requires at least one condition.", stream.Current.Span, filePath);
        }

        return [.. list];
    }

    public VariableAccessExpr? ParseVariableLValue(
        TokenStream stream,
        UserFunctionSymbol? activeFunction,
        string? filePath,
        DiagnosticBag diagnostics,
        bool requireWritable = true,
        Action<UserFunctionSymbol>? onUserFunctionReferenced = null)
    {
        ExprNode expr = ParseExpression(
            stream, activeFunction, filePath, diagnostics, stopAtComma: true, stopAtAssign: true, onUserFunctionReferenced: onUserFunctionReferenced);
        if (expr is not VariableAccessExpr varExpr)
        {
            diagnostics.ReportError("ERA1410", "Expected a variable reference.", expr.Span, filePath);
            return null;
        }

        if (requireWritable && (varExpr.Variable.IsConst || varExpr.Variable.IsReadOnly))
        {
            diagnostics.ReportError("ERA1411", $"Variable '{varExpr.Variable.Name}' is read-only/constant and cannot be modified.", varExpr.Span, filePath);
            return null;
        }

        return varExpr;
    }

    private ExprNode ParseTernary(ref ParseContext ctx)
    {
        if (ctx.InSubscript)
        {
            return ParseUnary(ref ctx);
        }

        ExprNode cond = ParseLogical(ref ctx);
        if (!ShouldStop(ref ctx) && ctx.Stream.MatchOperator(OperatorKind.TernaryQuestion))
        {
            if (cond.ValueKind != VariableValueKind.Int)
            {
                ctx.Diagnostics.ReportError("ERA1412", "Ternary '?' condition must be an integer expression.", cond.Span, ctx.FilePath);
            }

            ExprNode trueBranch = ParseTernary(ref ctx);
            if (!ctx.Stream.MatchOperator(OperatorKind.TernaryHash))
            {
                ctx.Diagnostics.ReportError("ERA1413", "Missing '#' separator in ternary 'cond ? trueExpr # falseExpr' expression.", ctx.Stream.Current.Span, ctx.FilePath);
                return trueBranch;
            }

            ExprNode falseBranch = ParseTernary(ref ctx);
            EnsureValueExpression(trueBranch, ref ctx);
            EnsureValueExpression(falseBranch, ref ctx);

            if (trueBranch.ValueKind != falseBranch.ValueKind)
            {
                ctx.Diagnostics.ReportError(
                    "ERA1414",
                    $"Ternary branches have mismatched types ({trueBranch.ValueKind} vs {falseBranch.ValueKind}).",
                    falseBranch.Span,
                    ctx.FilePath);
            }

            if (cond is IntLiteralExpr constCond)
            {
                return constCond.Value != 0 ? trueBranch : falseBranch;
            }

            return new TernaryExpr(cond, trueBranch, falseBranch, trueBranch.ValueKind, MergeSpan(cond.Span, falseBranch.Span));
        }

        return cond;
    }

    private ExprNode ParseLogical(ref ParseContext ctx)
    {
        ExprNode left = ParseBitwiseOr(ref ctx);
        while (!ShouldStop(ref ctx) && ctx.Stream.Current.Kind == ErbTokenKind.Operator
               && ctx.Stream.Current.Operator is OperatorKind.LogicalAnd or OperatorKind.LogicalNand
                   or OperatorKind.LogicalOr or OperatorKind.LogicalNor or OperatorKind.LogicalXor)
        {
            OperatorKind op = ctx.Stream.Advance().Operator;
            ExprNode right = ParseBitwiseOr(ref ctx);
            left = BuildBinary(op, left, right, ref ctx);
        }

        return left;
    }

    private ExprNode ParseBitwiseOr(ref ParseContext ctx)
    {
        ExprNode left = ParseBitwiseXor(ref ctx);
        while (!ShouldStop(ref ctx) && ctx.Stream.MatchOperator(OperatorKind.BitOr))
        {
            ExprNode right = ParseBitwiseXor(ref ctx);
            left = BuildBinary(OperatorKind.BitOr, left, right, ref ctx);
        }

        return left;
    }

    private ExprNode ParseBitwiseXor(ref ParseContext ctx)
    {
        ExprNode left = ParseBitwiseAnd(ref ctx);
        while (!ShouldStop(ref ctx) && ctx.Stream.MatchOperator(OperatorKind.BitXor))
        {
            ExprNode right = ParseBitwiseAnd(ref ctx);
            left = BuildBinary(OperatorKind.BitXor, left, right, ref ctx);
        }

        return left;
    }

    private ExprNode ParseBitwiseAnd(ref ParseContext ctx)
    {
        ExprNode left = ParseEquality(ref ctx);
        while (!ShouldStop(ref ctx) && ctx.Stream.MatchOperator(OperatorKind.BitAnd))
        {
            ExprNode right = ParseEquality(ref ctx);
            left = BuildBinary(OperatorKind.BitAnd, left, right, ref ctx);
        }

        return left;
    }

    private ExprNode ParseEquality(ref ParseContext ctx)
    {
        ExprNode left = ParseRelational(ref ctx);
        while (!ShouldStop(ref ctx) && ctx.Stream.Current.Kind == ErbTokenKind.Operator
               && ctx.Stream.Current.Operator is OperatorKind.Equal or OperatorKind.NotEqual)
        {
            OperatorKind op = ctx.Stream.Advance().Operator;
            ExprNode right = ParseRelational(ref ctx);
            left = BuildBinary(op, left, right, ref ctx);
        }

        return left;
    }

    private ExprNode ParseRelational(ref ParseContext ctx)
    {
        ExprNode left = ParseShift(ref ctx);
        while (!ShouldStop(ref ctx) && ctx.Stream.Current.Kind == ErbTokenKind.Operator
               && ctx.Stream.Current.Operator is OperatorKind.Less or OperatorKind.LessEqual or OperatorKind.Greater or OperatorKind.GreaterEqual)
        {
            OperatorKind op = ctx.Stream.Advance().Operator;
            ExprNode right = ParseShift(ref ctx);
            left = BuildBinary(op, left, right, ref ctx);
        }

        return left;
    }

    private ExprNode ParseShift(ref ParseContext ctx)
    {
        ExprNode left = ParseAdditive(ref ctx);
        while (!ShouldStop(ref ctx) && ctx.Stream.Current.Kind == ErbTokenKind.Operator
               && ctx.Stream.Current.Operator is OperatorKind.ShiftLeft or OperatorKind.ShiftRight)
        {
            OperatorKind op = ctx.Stream.Advance().Operator;
            ExprNode right = ParseAdditive(ref ctx);
            left = BuildBinary(op, left, right, ref ctx);
        }

        return left;
    }

    private ExprNode ParseAdditive(ref ParseContext ctx)
    {
        ExprNode left = ParseMultiplicative(ref ctx);
        while (!ShouldStop(ref ctx) && ctx.Stream.Current.Kind == ErbTokenKind.Operator
               && ctx.Stream.Current.Operator is OperatorKind.Plus or OperatorKind.Minus)
        {
            OperatorKind op = ctx.Stream.Advance().Operator;
            ExprNode right = ParseMultiplicative(ref ctx);
            left = BuildBinary(op, left, right, ref ctx);
        }

        return left;
    }

    private ExprNode ParseMultiplicative(ref ParseContext ctx)
    {
        ExprNode left = ParseUnary(ref ctx);
        while (!ShouldStop(ref ctx) && ctx.Stream.Current.Kind == ErbTokenKind.Operator
               && ctx.Stream.Current.Operator is OperatorKind.Multiply or OperatorKind.Divide or OperatorKind.Modulo)
        {
            OperatorKind op = ctx.Stream.Advance().Operator;
            ExprNode right = ParseUnary(ref ctx);
            left = BuildBinary(op, left, right, ref ctx);
        }

        return left;
    }

    private ExprNode ParseUnary(ref ParseContext ctx)
    {
        if (!ctx.InSubscript && ctx.Stream.Current.Kind == ErbTokenKind.Operator
            && ctx.Stream.Current.Operator is OperatorKind.Plus or OperatorKind.Minus
                or OperatorKind.BitNot or OperatorKind.LogicalNot
                or OperatorKind.Increment or OperatorKind.Decrement)
        {
            ErbToken opTok = ctx.Stream.Advance();
            OperatorKind op = opTok.Operator;

            if (op is OperatorKind.Increment or OperatorKind.Decrement)
            {
                ExprNode target = ParsePostfix(ref ctx, allowPostfixIncDec: false);
                return BuildIncDec(op, target, isPostfix: false, opTok.Span, ref ctx);
            }

            ExprNode operand = ParseUnary(ref ctx);
            EnsureValueExpression(operand, ref ctx);

            if (operand.ValueKind != VariableValueKind.Int)
            {
                ctx.Diagnostics.ReportError("ERA1415", $"Unary operator '{op}' requires an integer operand.", opTok.Span, ctx.FilePath);
                return new IntLiteralExpr(0, opTok.Span);
            }

            if (operand is IntLiteralExpr lit)
            {
                long folded = op switch
                {
                    OperatorKind.Plus => lit.Value,
                    OperatorKind.Minus => unchecked(-lit.Value),
                    OperatorKind.BitNot => ~lit.Value,
                    OperatorKind.LogicalNot => lit.Value == 0 ? 1L : 0L,
                    _ => lit.Value,
                };
                return new IntLiteralExpr(folded, MergeSpan(opTok.Span, operand.Span));
            }

            if (op == OperatorKind.Plus)
            {
                return operand;
            }

            return new UnaryExpr(op, operand, VariableValueKind.Int, MergeSpan(opTok.Span, operand.Span));
        }

        // Handle signed literal inside array subscript (e.g., ARR:-1 is an error at runtime/compile, or A:(-1))
        if (ctx.InSubscript && ctx.Stream.Current.Kind == ErbTokenKind.Operator
            && ctx.Stream.Current.Operator is OperatorKind.Plus or OperatorKind.Minus)
        {
            ErbToken opTok = ctx.Stream.Advance();
            ExprNode prim = ParsePrimary(ref ctx);
            if (prim.ValueKind != VariableValueKind.Int)
            {
                ctx.Diagnostics.ReportError("ERA1415", $"Unary '{opTok.Operator}' requires an integer.", opTok.Span, ctx.FilePath);
                return new IntLiteralExpr(0, opTok.Span);
            }

            if (prim is IntLiteralExpr lit)
            {
                return new IntLiteralExpr(opTok.Operator == OperatorKind.Minus ? -lit.Value : lit.Value, MergeSpan(opTok.Span, prim.Span));
            }

            return opTok.Operator == OperatorKind.Plus
                ? prim
                : new UnaryExpr(OperatorKind.Minus, prim, VariableValueKind.Int, MergeSpan(opTok.Span, prim.Span));
        }

        return ParsePostfix(ref ctx, allowPostfixIncDec: !ctx.InSubscript);
    }

    private ExprNode ParsePostfix(ref ParseContext ctx, bool allowPostfixIncDec)
    {
        ExprNode expr = ParsePrimary(ref ctx);

        if (allowPostfixIncDec && !ShouldStop(ref ctx) && ctx.Stream.Current.Kind == ErbTokenKind.Operator
            && ctx.Stream.Current.Operator is OperatorKind.Increment or OperatorKind.Decrement)
        {
            ErbToken opTok = ctx.Stream.Advance();
            if (ctx.Stream.Current.Kind == ErbTokenKind.Operator
                && ctx.Stream.Current.Operator is OperatorKind.Increment or OperatorKind.Decrement)
            {
                ctx.Diagnostics.ReportError("ERA1416", "Chained '++'/'--' operators on the same expression are invalid.", ctx.Stream.Current.Span, ctx.FilePath);
                ctx.Stream.Advance();
            }

            return BuildIncDec(opTok.Operator, expr, isPostfix: true, MergeSpan(expr.Span, opTok.Span), ref ctx);
        }

        return expr;
    }

    private ExprNode ParsePrimary(ref ParseContext ctx)
    {
        ErbToken cur = ctx.Stream.Current;

        switch (cur.Kind)
        {
            case ErbTokenKind.IntegerLiteral:
                ctx.Stream.Advance();
                return new IntLiteralExpr(cur.IntValue, cur.Span);

            case ErbTokenKind.StringLiteral:
                ctx.Stream.Advance();
                return new StrLiteralExpr(cur.Text ?? string.Empty, cur.Span);

            case ErbTokenKind.FormStringLiteral:
            case ErbTokenKind.InlineYenAtForm:
                ctx.Stream.Advance();
                return _strFormParser.ParsePartsToExpression(
                    cur.FormParts,
                    cur.Span,
                    ctx.ActiveFunction,
                    ctx.FilePath,
                    ctx.Diagnostics,
                    ctx.OnUserFunctionReferenced);

            case ErbTokenKind.OpenParen:
                {
                    ErbToken open = ctx.Stream.Advance();
                    var innerCtx = ctx with
                    {
                        StopAtComma = false,
                        StopAtAssign = false,
                        AllowToKeyword = false,
                        StopAtPxSuffix = false,
                        InSubscript = false,
                        SubscriptVar = null,
                    };
                    if (innerCtx.Stream.Match(ErbTokenKind.CloseParen))
                    {
                        ctx.Diagnostics.ReportError("ERA1417", "Empty parentheses '()' are not a valid expression.", open.Span, ctx.FilePath);
                        return new IntLiteralExpr(0, open.Span);
                    }

                    ExprNode inner = ParseTernary(ref innerCtx);
                    if (!ctx.Stream.Match(ErbTokenKind.CloseParen))
                    {
                        ctx.Diagnostics.ReportError("ERA1418", "Missing closing parenthesis ')'.", ctx.Stream.Current.Span, ctx.FilePath);
                    }

                    return inner;
                }

            case ErbTokenKind.Identifier:
                return ParseIdentifierPrimary(ref ctx);

            default:
                ctx.Diagnostics.ReportError(
                    "ERA1419",
                    $"Expected an expression, but found '{cur.Text ?? cur.Kind.ToString()}'.",
                    cur.Span,
                    ctx.FilePath);
                if (!ctx.Stream.IsAtEnd && !ShouldStop(ref ctx))
                {
                    ctx.Stream.Advance();
                }

                return new IntLiteralExpr(0, cur.Span);
        }
    }

    private ExprNode ParseIdentifierPrimary(ref ParseContext ctx)
    {
        ErbToken idTok = ctx.Stream.Advance();
        string name = idTok.Text ?? string.Empty;
        StringComparison cmp = _symbols.Options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (name.Equals("IS", cmp))
        {
            ctx.Diagnostics.ReportError("ERA1420", "Reserved keyword 'IS' is only valid immediately after 'CASE'.", idTok.Span, ctx.FilePath);
            return new IntLiteralExpr(0, idTok.Span);
        }

        if (name.Equals("TO", cmp))
        {
            ctx.Diagnostics.ReportError("ERA1421", "Unexpected reserved keyword 'TO' in expression.", idTok.Span, ctx.FilePath);
            return new IntLiteralExpr(0, idTok.Span);
        }

        if (ctx.Stream.Match(ErbTokenKind.Dot))
        {
            ctx.Diagnostics.ReportError("ERA1422", "Namespace member access '.' is not supported.", idTok.Span, ctx.FilePath);
            return new IntLiteralExpr(0, idTok.Span);
        }

        if (ctx.Stream.Match(ErbTokenKind.OpenBracket))
        {
            ctx.Diagnostics.ReportError("ERA1423", "Bracketed function call 'FUNC[...]' is not supported in expressions.", idTok.Span, ctx.FilePath);
            return new IntLiteralExpr(0, idTok.Span);
        }

        // Method or #FUNCTION/#FUNCTIONS invocation: IDENT(...)
        if (ctx.Stream.Match(ErbTokenKind.OpenParen))
        {
            if (ctx.InSubscript)
            {
                // Function calls without parentheses cannot appear in subscripts, EXCEPT IDENT(...) IS valid in Emuera!
                // Note: In upstream ReduceIdentifier, '(' comes BEFORE varCode check, so ABL:MAX(0, 1) IS valid!
            }

            ImmutableArray<ExprNode?> rawArgs = ParseArgumentList(
                ctx.Stream,
                ErbTokenKind.CloseParen,
                ctx.ActiveFunction,
                ctx.FilePath,
                ctx.Diagnostics,
                ctx.OnUserFunctionReferenced);
            SourceSpan callSpan = MergeSpan(idTok.Span, ctx.Stream.Position > 0 ? ctx.Stream.Peek(-1).Span : idTok.Span);
            return BindFunctionOrMethodCall(name, rawArgs, callSpan, ref ctx);
        }

        // Check for scoped variable qualifier: LOCAL@OTHER_FUNC
        string? explicitSubFunc = null;
        SourceSpan endSpan = idTok.Span;
        if (ctx.Stream.Match(ErbTokenKind.At))
        {
            if (ctx.Stream.Current.Kind != ErbTokenKind.Identifier || string.IsNullOrEmpty(ctx.Stream.Current.Text))
            {
                ctx.Diagnostics.ReportError("ERA1424", $"Expected function name after '{name}@'.", ctx.Stream.Current.Span, ctx.FilePath);
                return new IntLiteralExpr(0, idTok.Span);
            }

            ErbToken subTok = ctx.Stream.Advance();
            explicitSubFunc = subTok.Text;
            endSpan = subTok.Span;
        }

        if (_symbols.TryResolveVariable(name, explicitSubFunc, ctx.ActiveFunction, out ResolvedVariable? variable)
            && variable is not null)
        {
            return ParseVariableSubscriptsAndFold(variable, idTok.Span, endSpan, ref ctx);
        }

        if (explicitSubFunc is not null)
        {
            ctx.Diagnostics.ReportError(
                "ERA1425",
                $"Cannot resolve scoped variable '{name}@{explicitSubFunc}' (target function or static variable not found).",
                MergeSpan(idTok.Span, endSpan),
                ctx.FilePath);
            return new IntLiteralExpr(0, idTok.Span);
        }

        // Inside an array subscript (e.g. ABL:TARGET:技巧 or MY_ARR:HP), check CSV / ERD named index!
        if (ctx.InSubscript && ctx.SubscriptVar is not null && _symbols.External.KeywordResolver is not null)
        {
            if (_symbols.External.KeywordResolver(ctx.SubscriptVar.Name, ctx.SubscriptDimension, name, out long resolvedIdx))
            {
                return new IntLiteralExpr(resolvedIdx, idTok.Span);
            }
        }

        ctx.Diagnostics.ReportError("ERA1426", $"Unknown variable or identifier '{name}'.", idTok.Span, ctx.FilePath);
        return new IntLiteralExpr(0, idTok.Span);
    }

    private ExprNode ParseVariableSubscriptsAndFold(
        ResolvedVariable variable,
        SourceSpan startSpan,
        SourceSpan currentEndSpan,
        ref ParseContext ctx)
    {
        var rawSubs = new List<ExprNode>(3);

        // In Emuera, a bare variable inside another variable's ':subscript' (without parentheses) does NOT consume following ':'!
        if (!ctx.InSubscript)
        {
            int dim = 0;
            while (ctx.Stream.Match(ErbTokenKind.Colon))
            {
                if (dim >= 3)
                {
                    ctx.Diagnostics.ReportError(
                        "ERA1427",
                        $"Too many subscripts on variable '{variable.Name}' (maximum 3).",
                        ctx.Stream.Current.Span,
                        ctx.FilePath);
                    break;
                }

                var subCtx = ctx with
                {
                    InSubscript = true,
                    SubscriptVar = variable,
                    SubscriptDimension = dim,
                };
                ExprNode subExpr = ParseUnary(ref subCtx);
                currentEndSpan = subExpr.Span;
                rawSubs.Add(subExpr);
                dim++;
            }
        }

        SourceSpan fullSpan = MergeSpan(startSpan, currentEndSpan);
        bool hasNoExplicitSubs = rawSubs.Count == 0;
        var finalSubs = new List<ExprNode>(3);

        if (variable.IsCharaData)
        {
            if (variable.ArrayRank == 2)
            {
                if (hasNoExplicitSubs)
                {
                    return new VariableAccessExpr(variable, [], IsBareArrayReference: true, fullSpan);
                }

                if (rawSubs.Count != 3)
                {
                    ctx.Diagnostics.ReportError("ERA1428", $"2D character variable '{variable.Name}' requires all 3 subscripts (chara:dim1:dim2).", fullSpan, ctx.FilePath);
                    while (rawSubs.Count < 3) rawSubs.Add(new IntLiteralExpr(0, fullSpan));
                }

                for (int i = 0; i < 3; i++)
                {
                    finalSubs.Add(ConvertSubscriptExpr(variable, i, rawSubs[i], ref ctx));
                }
            }
            else if (variable.ArrayRank == 1)
            {
                if (rawSubs.Count > 2)
                {
                    ctx.Diagnostics.ReportError("ERA1428", $"1D character variable '{variable.Name}' accepts at most 2 subscripts (chara:index).", fullSpan, ctx.FilePath);
                }

                if (hasNoExplicitSubs && _symbols.Options.SystemNoTarget)
                {
                    return new VariableAccessExpr(variable, [], IsBareArrayReference: true, fullSpan);
                }

                if (rawSubs.Count <= 1)
                {
                    if (_symbols.Options.SystemNoTarget)
                    {
                        ctx.Diagnostics.ReportError("ERA1429", $"Character variable '{variable.Name}' requires an explicit character index when SystemNoTarget is enabled.", fullSpan, ctx.FilePath);
                    }

                    ExprNode targetZero = BuildImplicitTargetExpr(fullSpan);
                    ExprNode elemSub = rawSubs.Count == 1
                        ? ConvertSubscriptExpr(variable, 1, rawSubs[0], ref ctx)
                        : new IntLiteralExpr(0, fullSpan);
                    finalSubs.Add(targetZero);
                    finalSubs.Add(elemSub);
                }
                else
                {
                    finalSubs.Add(ConvertSubscriptExpr(variable, 0, rawSubs[0], ref ctx));
                    finalSubs.Add(ConvertSubscriptExpr(variable, 1, rawSubs[1], ref ctx));
                }
            }
            else // Chara 0D (NAME, CALLNAME, NO, ISASSI)
            {
                if (rawSubs.Count > 1)
                {
                    ctx.Diagnostics.ReportError("ERA1428", $"Character variable '{variable.Name}' accepts at most 1 subscript (charaIndex).", fullSpan, ctx.FilePath);
                }

                if (hasNoExplicitSubs && _symbols.Options.SystemNoTarget)
                {
                    return new VariableAccessExpr(variable, [], IsBareArrayReference: true, fullSpan);
                }

                ExprNode charaSub = rawSubs.Count == 1
                    ? ConvertSubscriptExpr(variable, 0, rawSubs[0], ref ctx)
                    : BuildImplicitTargetExpr(fullSpan);
                finalSubs.Add(charaSub);
            }
        }
        else if (variable.ArrayRank == 3)
        {
            if (hasNoExplicitSubs)
            {
                return new VariableAccessExpr(variable, [], IsBareArrayReference: true, fullSpan);
            }

            if (rawSubs.Count != 3)
            {
                ctx.Diagnostics.ReportError("ERA1430", $"3D array variable '{variable.Name}' requires 3 subscripts.", fullSpan, ctx.FilePath);
                while (rawSubs.Count < 3) rawSubs.Add(new IntLiteralExpr(0, fullSpan));
            }

            for (int i = 0; i < 3; i++) finalSubs.Add(ConvertSubscriptExpr(variable, i, rawSubs[i], ref ctx));
        }
        else if (variable.ArrayRank == 2)
        {
            if (hasNoExplicitSubs)
            {
                return new VariableAccessExpr(variable, [], IsBareArrayReference: true, fullSpan);
            }

            if (rawSubs.Count != 2)
            {
                ctx.Diagnostics.ReportError("ERA1430", $"2D array variable '{variable.Name}' requires 2 subscripts.", fullSpan, ctx.FilePath);
                while (rawSubs.Count < 2) rawSubs.Add(new IntLiteralExpr(0, fullSpan));
            }

            for (int i = 0; i < 2; i++) finalSubs.Add(ConvertSubscriptExpr(variable, i, rawSubs[i], ref ctx));
        }
        else if (variable.ArrayRank == 1)
        {
            if (rawSubs.Count > 1)
            {
                ctx.Diagnostics.ReportError("ERA1430", $"1D variable '{variable.Name}' accepts at most 1 subscript.", fullSpan, ctx.FilePath);
            }

            bool isRand = variable.IsCalc && variable.Name.Equals("RAND", StringComparison.OrdinalIgnoreCase);
            if (hasNoExplicitSubs)
            {
                if (isRand && !_symbols.Options.CompatiRAND)
                {
                    ctx.Diagnostics.ReportError("ERA1431", "RAND requires a positive upper-bound argument (RAND:X).", fullSpan, ctx.FilePath);
                }

                finalSubs.Add(new IntLiteralExpr(0, fullSpan));
            }
            else
            {
                ExprNode s0 = ConvertSubscriptExpr(variable, 0, rawSubs[0], ref ctx);
                if (isRand && !_symbols.Options.CompatiRAND && s0 is IntLiteralExpr { Value: <= 0 })
                {
                    ctx.Diagnostics.ReportError("ERA1432", "RAND argument must be greater than 0 when CompatiRAND is disabled.", s0.Span, ctx.FilePath);
                }

                finalSubs.Add(s0);
            }
        }
        else // Scalar 0D
        {
            if (rawSubs.Count > 0)
            {
                ctx.Diagnostics.ReportError("ERA1433", $"0D variable '{variable.Name}' does not accept subscripts.", fullSpan, ctx.FilePath);
            }
        }

        // Compile-time constant folding for #DIM CONST / __INT_MAX__ / __INT_MIN__ when NOT bare-array ref
        if (variable.IsCalc && finalSubs.Count == 0)
        {
            if (variable.Name.Equals("__INT_MAX__", StringComparison.OrdinalIgnoreCase))
            {
                return new IntLiteralExpr(long.MaxValue, fullSpan);
            }

            if (variable.Name.Equals("__INT_MIN__", StringComparison.OrdinalIgnoreCase))
            {
                return new IntLiteralExpr(long.MinValue, fullSpan);
            }
        }

        if (variable.Scope == VariableScopeKind.Constant && variable.IsConst && !variable.IsCalc)
        {
            // Keep VariableAccessExpr when bare 1D array > 1 so FINDELEMENT(CONST_ARR, ...) works,
            // EXCEPT scalar #DIM CONST X = 42 (Dimensions[0] == 1) folds immediately unless hasNoExplicitSubs && Dimensions[0] > 1!
            int constLen = variable.Dimensions.Length > 0 ? variable.Dimensions[0] : 1;
            if (constLen == 1 && (!hasNoExplicitSubs || finalSubs.Count <= 1))
            {
                if (variable.ValueKind == VariableValueKind.Int && variable.DefaultIntValues.Length == 1)
                {
                    return new IntLiteralExpr(variable.DefaultIntValues[0], fullSpan);
                }

                if (variable.ValueKind == VariableValueKind.Str && variable.DefaultStrValues.Length == 1)
                {
                    return new StrLiteralExpr(variable.DefaultStrValues[0], fullSpan);
                }
            }
            else if (!hasNoExplicitSubs && finalSubs is [IntLiteralExpr constIdx])
            {
                if (variable.ValueKind == VariableValueKind.Int && (ulong)constIdx.Value < (ulong)variable.DefaultIntValues.Length)
                {
                    return new IntLiteralExpr(variable.DefaultIntValues[(int)constIdx.Value], fullSpan);
                }

                if (variable.ValueKind == VariableValueKind.Str && (ulong)constIdx.Value < (ulong)variable.DefaultStrValues.Length)
                {
                    return new StrLiteralExpr(variable.DefaultStrValues[(int)constIdx.Value], fullSpan);
                }
            }
        }

        return new VariableAccessExpr(variable, [.. finalSubs], IsBareArrayReference: hasNoExplicitSubs, fullSpan);
    }

    private ExprNode ConvertSubscriptExpr(ResolvedVariable variable, int dimIndex, ExprNode sub, ref ParseContext ctx)
    {
        EnsureValueExpression(sub, ref ctx);
        if (sub.ValueKind == VariableValueKind.Int)
        {
            return sub;
        }

        // String subscript: resolve at compile time via KeywordResolver if constant and known, else runtime KeywordSubscriptExpr
        if (sub is StrLiteralExpr constKey && _symbols.External.KeywordResolver is not null)
        {
            if (_symbols.External.KeywordResolver(variable.Name, dimIndex, constKey.Value, out long idx))
            {
                return new IntLiteralExpr(idx, sub.Span);
            }

            ctx.Diagnostics.ReportError(
                "ERA1434",
                $"Keyword '{constKey.Value}' is not defined for variable '{variable.Name}' dimension {dimIndex}.",
                sub.Span,
                ctx.FilePath);
            return new IntLiteralExpr(0, sub.Span);
        }

        return new KeywordSubscriptExpr(variable.Name, dimIndex, sub, sub.Span);
    }

    private ExprNode BindFunctionOrMethodCall(
        string name,
        ImmutableArray<ExprNode?> rawArgs,
        SourceSpan callSpan,
        ref ParseContext ctx)
    {
        bool ignoreFuncCase = _symbols.Options.IgnoreCase && !_symbols.Options.CompatiFunctionNoIgnoreCase;

        UserFunctionSymbol? userFn = _symbols.TryResolveCallableFunction(name, allowEventIfCompati: false);
        BuiltinMethodCatalog.TryGet(name, ignoreFuncCase, out BuiltinMethodSpec? builtinSpec);

        // Priority: when AllowFunctionOverloading is true, user #FUNCTION/#FUNCTIONS overrides built-in method!
        if (_symbols.Options.AllowFunctionOverloading && userFn is { IsMethod: true })
        {
            return BindUserMethodCall(userFn, rawArgs, callSpan, ref ctx);
        }

        if (builtinSpec is not null)
        {
            return BindBuiltinMethodCall(builtinSpec, rawArgs, callSpan, ref ctx);
        }

        if (userFn is not null)
        {
            if (!userFn.IsMethod)
            {
                ctx.Diagnostics.ReportError(
                    "ERA1435",
                    $"Function '@{userFn.Name}' is not declared with #FUNCTION or #FUNCTIONS and cannot be called inside an expression.",
                    callSpan,
                    ctx.FilePath);
                return new IntLiteralExpr(0, callSpan);
            }

            return BindUserMethodCall(userFn, rawArgs, callSpan, ref ctx);
        }

        ctx.Diagnostics.ReportError("ERA1436", $"Unknown expression function or method '{name}'.", callSpan, ctx.FilePath);
        return new IntLiteralExpr(0, callSpan);
    }

    private ExprNode BindUserMethodCall(
        UserFunctionSymbol userFn,
        ImmutableArray<ExprNode?> rawArgs,
        SourceSpan callSpan,
        ref ParseContext ctx)
    {
        ctx.OnUserFunctionReferenced?.Invoke(userFn);

        ImmutableArray<ExprNode?> converted = ValidateAndConvertUserCallArguments(
            userFn,
            rawArgs,
            callSpan,
            ctx.FilePath,
            ctx.Diagnostics);

        return new UserMethodCallExpr(
            userFn.Name,
            userFn.FunctionId,
            userFn.MethodReturnKind ?? VariableValueKind.Int,
            converted,
            callSpan);
    }

    public ImmutableArray<ExprNode?> ValidateAndConvertUserCallArguments(
        UserFunctionSymbol targetFn,
        ImmutableArray<ExprNode?> rawArgs,
        SourceSpan callSpan,
        string? filePath,
        DiagnosticBag diagnostics)
    {
        // Trim trailing single omitted null when caller wrote FOO() with 0 args
        int effectiveCount = rawArgs.Length;
        if (effectiveCount > targetFn.Parameters.Count)
        {
            diagnostics.ReportError(
                "ERA1437",
                $"Too many arguments in call to '@{targetFn.Name}': expected at most {targetFn.Parameters.Count}, got {effectiveCount}.",
                callSpan,
                filePath);
            effectiveCount = targetFn.Parameters.Count;
        }

        var converted = new ExprNode?[targetFn.Parameters.Count];
        for (int i = 0; i < targetFn.Parameters.Count; i++)
        {
            ParsedFunctionParameter param = targetFn.Parameters[i];
            ExprNode? arg = i < effectiveCount ? rawArgs[i] : null;
            ResolvedVariable destVar = param.TargetVariable.Variable;

            if (destVar.IsByRef)
            {
                if (arg is null)
                {
                    diagnostics.ReportError("ERA1438", $"By-reference parameter {i + 1} of '@{targetFn.Name}' cannot be omitted.", callSpan, filePath);
                    continue;
                }

                if (arg is not VariableAccessExpr refArg || refArg.Variable.ArrayRank == 0)
                {
                    diagnostics.ReportError("ERA1439", $"By-reference parameter {i + 1} of '@{targetFn.Name}' requires an array variable.", arg.Span, filePath);
                    continue;
                }

                if (refArg.Variable.ValueKind != destVar.ValueKind || refArg.Variable.ArrayRank != destVar.ArrayRank)
                {
                    diagnostics.ReportError(
                        "ERA1440",
                        $"By-reference argument {i + 1} type/dimension ({refArg.Variable.ValueKind} {refArg.Variable.ArrayRank}D) does not match parameter '{destVar.Name}' ({destVar.ValueKind} {destVar.ArrayRank}D).",
                        arg.Span,
                        filePath);
                    continue;
                }

                if (refArg.Variable.Scope == VariableScopeKind.FunctionDynamic)
                {
                    refArg.Variable.IsRegisterPromoted = false;
                }

                converted[i] = refArg;
                continue;
            }

            if (arg is null)
            {
                if (!param.HasExplicitDefault && !_symbols.Options.CompatiFuncArgOptional)
                {
                    diagnostics.ReportError(
                        "ERA1441",
                        $"Argument {i + 1} of '@{targetFn.Name}' cannot be omitted unless a default value is declared or CompatiFuncArgOptional is enabled.",
                        callSpan,
                        filePath);
                    continue;
                }

                converted[i] = param.HasExplicitDefault
                    ? (destVar.ValueKind == VariableValueKind.Str
                        ? new StrLiteralExpr(param.DefaultStrValue, callSpan)
                        : new IntLiteralExpr(param.DefaultIntValue, callSpan))
                    : null;
                continue;
            }

            if (arg is VariableAccessExpr { IsBareArrayReference: true } bare && bare.Variable.ArrayRank >= 2)
            {
                diagnostics.ReportError("ERA1442", $"Multi-dimensional variable '{bare.Variable.Name}' requires subscripts when passed by value.", bare.Span, filePath);
                continue;
            }

            if (arg.ValueKind != destVar.ValueKind)
            {
                if (arg.ValueKind == VariableValueKind.Int && destVar.ValueKind == VariableValueKind.Str && _symbols.Options.CompatiFuncArgAutoConvert)
                {
                    BuiltinMethodCatalog.TryGet("TOSTR", ignoreCase: true, out BuiltinMethodSpec? toStrSpec);
                    converted[i] = arg is IntLiteralExpr constI
                        ? new StrLiteralExpr(constI.Value.ToString(CultureInfo.InvariantCulture), arg.Span)
                        : new BuiltinMethodCallExpr(toStrSpec!, [arg], arg.Span);
                    continue;
                }

                diagnostics.ReportError(
                    "ERA1443",
                    $"Argument {i + 1} of '@{targetFn.Name}' expects {destVar.ValueKind}, but got {arg.ValueKind}.",
                    arg.Span,
                    filePath);
                continue;
            }

            converted[i] = arg;
        }

        return [.. converted];
    }

    public ExprNode BindBuiltinMethodCall(
        BuiltinMethodSpec spec,
        ImmutableArray<ExprNode?> rawArgs,
        SourceSpan callSpan,
        string? filePath,
        DiagnosticBag diagnostics)
    {
        var dummyCtx = new ParseContext(
            null!, null, filePath, diagnostics, false, false, false, false, false, null, 0, null);
        return BindBuiltinMethodCall(spec, rawArgs, callSpan, ref dummyCtx);
    }

    private ExprNode BindBuiltinMethodCall(
        BuiltinMethodSpec spec,
        ImmutableArray<ExprNode?> rawArgs,
        SourceSpan callSpan,
        ref ParseContext ctx)
    {
        string? lastMismatchReason = null;
        foreach (MethodOverloadSpec ov in spec.Overloads)
        {
            if (TryMatchOverload(spec, ov, rawArgs, out string? mismatch))
            {
                // Mark any local dynamic array passed by ref so it is not scalar-register-promoted!
                for (int i = 0; i < rawArgs.Length; i++)
                {
                    MethodParamKind pk = GetParamKindAt(ov, i);
                    if (IsRefParamKind(pk) && rawArgs[i] is VariableAccessExpr va && va.Variable.Scope == VariableScopeKind.FunctionDynamic)
                    {
                        va.Variable.IsRegisterPromoted = false;
                    }
                }

                if (spec.CanConstantFold && TryFoldBuiltinMethod(spec.Kind, rawArgs, callSpan, out ExprNode? folded))
                {
                    return folded!;
                }

                return new BuiltinMethodCallExpr(spec, rawArgs, callSpan);
            }

            lastMismatchReason = mismatch;
        }

        ctx.Diagnostics.ReportError(
            "ERA1444",
            $"Invalid arguments to built-in method '{spec.Name}': {lastMismatchReason ?? "no matching overload"}.",
            callSpan,
            ctx.FilePath);
        return spec.ReturnKind == VariableValueKind.Str
            ? new StrLiteralExpr(string.Empty, callSpan)
            : new IntLiteralExpr(0, callSpan);
    }

    private static MethodParamKind GetParamKindAt(MethodOverloadSpec ov, int index)
    {
        if (index < ov.ParameterKinds.Length)
        {
            return ov.ParameterKinds[index];
        }

        if (ov.VariadicPairsKeyAndValue && ov.ParameterKinds.Length >= 2)
        {
            int pairOffset = (index - (ov.ParameterKinds.Length - 2)) % 2;
            return ov.ParameterKinds[ov.ParameterKinds.Length - 2 + pairOffset];
        }

        return ov.ParameterKinds[^1];
    }

    private static bool IsRefParamKind(MethodParamKind kind) =>
        kind is MethodParamKind.CharaVar
            or MethodParamKind.RefInt1D or MethodParamKind.RefInt2D or MethodParamKind.RefInt3D or MethodParamKind.RefIntAny
            or MethodParamKind.RefStr or MethodParamKind.RefStr1D
            or MethodParamKind.RefAny1D or MethodParamKind.RefAnyArray
            or MethodParamKind.RefIntScalar;

    private static bool TryMatchOverload(
        BuiltinMethodSpec spec,
        MethodOverloadSpec ov,
        ImmutableArray<ExprNode?> args,
        out string? error)
    {
        error = null;
        if (args.Length < ov.MinArgs)
        {
            error = $"expected at least {ov.MinArgs} argument(s), got {args.Length}";
            return false;
        }

        if (!ov.IsVariadic && args.Length > ov.ParameterKinds.Length)
        {
            error = $"expected at most {ov.ParameterKinds.Length} argument(s), got {args.Length}";
            return false;
        }

        if (ov.VariadicPairsKeyAndValue && args.Length > ov.MinArgs)
        {
            int pairStart = ov.ParameterKinds.Length - 2;
            if (((args.Length - pairStart) & 1) != 0)
            {
                error = $"'{spec.Name}' requires (columnName, value) argument pairs";
                return false;
            }
        }

        VariableValueKind? firstKind = null;
        for (int i = 0; i < args.Length; i++)
        {
            ExprNode? arg = args[i];
            MethodParamKind pk = GetParamKindAt(ov, i);

            if (arg is null)
            {
                if (i < ov.OptionalFromIndex)
                {
                    error = $"argument {i + 1} cannot be omitted";
                    return false;
                }

                continue;
            }

            if (i == 0)
            {
                firstKind = arg.ValueKind;
            }

            switch (pk)
            {
                case MethodParamKind.Int:
                    if (arg.ValueKind != VariableValueKind.Int || IsDisallowedBareMultiDim(arg))
                    {
                        error = $"argument {i + 1} must be Int";
                        return false;
                    }
                    break;

                case MethodParamKind.Str:
                    if (arg.ValueKind != VariableValueKind.Str || IsDisallowedBareMultiDim(arg))
                    {
                        error = $"argument {i + 1} must be Str";
                        return false;
                    }
                    break;

                case MethodParamKind.Any:
                    if (IsDisallowedBareMultiDim(arg))
                    {
                        error = $"argument {i + 1} requires subscripts";
                        return false;
                    }
                    break;

                case MethodParamKind.SameAsFirst:
                    if (firstKind is { } fk && arg.ValueKind != fk)
                    {
                        error = $"argument {i + 1} must match first argument type ({fk})";
                        return false;
                    }
                    break;

                case MethodParamKind.CharaVar:
                    if (arg is not VariableAccessExpr { Variable.IsCharaData: true })
                    {
                        error = $"argument {i + 1} must be a character variable";
                        return false;
                    }
                    break;

                case MethodParamKind.RefIntScalar:
                    if (arg is not VariableAccessExpr { ValueKind: VariableValueKind.Int } ris
                        || (!ov.AllowConstRef && (ris.Variable.IsConst || ris.Variable.IsReadOnly)))
                    {
                        error = $"argument {i + 1} must be a writable integer variable";
                        return false;
                    }
                    break;

                case MethodParamKind.RefStr:
                    if (arg is not VariableAccessExpr { ValueKind: VariableValueKind.Str } rs
                        || (!ov.AllowConstRef && (rs.Variable.IsConst || rs.Variable.IsReadOnly)))
                    {
                        error = $"argument {i + 1} must be a writable string variable";
                        return false;
                    }
                    break;

                case MethodParamKind.RefInt1D:
                case MethodParamKind.RefInt2D:
                case MethodParamKind.RefInt3D:
                case MethodParamKind.RefIntAny:
                case MethodParamKind.RefStr1D:
                case MethodParamKind.RefAny1D:
                case MethodParamKind.RefAnyArray:
                    if (!ValidateRefArrayArg(arg, pk, ov.AllowConstRef, i + 1, out error))
                    {
                        return false;
                    }
                    break;
            }
        }

        return true;
    }

    private static bool ValidateRefArrayArg(ExprNode arg, MethodParamKind pk, bool allowConst, int pos1Based, out string? error)
    {
        error = null;
        if (arg is not VariableAccessExpr va)
        {
            error = $"argument {pos1Based} must be an array variable";
            return false;
        }

        ResolvedVariable v = va.Variable;
        if (!allowConst && (v.IsConst || v.IsReadOnly))
        {
            error = $"argument {pos1Based} ('{v.Name}') cannot be constant/read-only";
            return false;
        }

        bool needInt = pk is MethodParamKind.RefInt1D or MethodParamKind.RefInt2D or MethodParamKind.RefInt3D or MethodParamKind.RefIntAny;
        bool needStr = pk is MethodParamKind.RefStr1D;
        if (needInt && v.ValueKind != VariableValueKind.Int)
        {
            error = $"argument {pos1Based} must be an integer array";
            return false;
        }

        if (needStr && v.ValueKind != VariableValueKind.Str)
        {
            error = $"argument {pos1Based} must be a string array";
            return false;
        }

        int effectiveRank = v.ArrayRank;
        // Note: Chara 0D (NO, NAME, CALLNAME) acts as 1D across characters in CMATCH / SUMCARRAY / GETNUM!
        if (v.IsCharaData && effectiveRank == 0)
        {
            effectiveRank = 1;
        }

        bool rankOk = pk switch
        {
            MethodParamKind.RefInt1D or MethodParamKind.RefStr1D or MethodParamKind.RefAny1D => effectiveRank == 1,
            MethodParamKind.RefInt2D => effectiveRank == 2,
            MethodParamKind.RefInt3D => effectiveRank == 3,
            MethodParamKind.RefIntAny or MethodParamKind.RefAnyArray => effectiveRank >= 1 || v.IsCharaData,
            _ => true,
        };

        if (!rankOk)
        {
            error = $"argument {pos1Based} ('{v.Name}') has incompatible array dimension ({v.ArrayRank}D)";
            return false;
        }

        return true;
    }

    private ExprNode BuildIncDec(OperatorKind op, ExprNode operand, bool isPostfix, SourceSpan span, ref ParseContext ctx)
    {
        if (operand is not VariableAccessExpr varExpr)
        {
            ctx.Diagnostics.ReportError("ERA1445", $"Operator '{op}' can only be applied to a variable.", span, ctx.FilePath);
            return new IntLiteralExpr(0, span);
        }

        if (varExpr.ValueKind != VariableValueKind.Int)
        {
            ctx.Diagnostics.ReportError("ERA1446", $"Operator '{op}' cannot be applied to string variable '{varExpr.Variable.Name}'.", span, ctx.FilePath);
            return new IntLiteralExpr(0, span);
        }

        if (varExpr.Variable.IsConst || varExpr.Variable.IsReadOnly)
        {
            ctx.Diagnostics.ReportError("ERA1447", $"Cannot modify constant/read-only variable '{varExpr.Variable.Name}'.", span, ctx.FilePath);
            return new IntLiteralExpr(0, span);
        }

        EnsureValueExpression(varExpr, ref ctx);
        return new IncDecExpr(op, varExpr, isPostfix, span);
    }

    private ExprNode BuildBinary(OperatorKind op, ExprNode left, ExprNode right, ref ParseContext ctx)
    {
        EnsureValueExpression(left, ref ctx);
        EnsureValueExpression(right, ref ctx);
        SourceSpan span = MergeSpan(left.Span, right.Span);

        // String concatenation: Str + Str -> Str
        if (op == OperatorKind.Plus && left.ValueKind == VariableValueKind.Str && right.ValueKind == VariableValueKind.Str)
        {
            if (left is StrLiteralExpr ls && right is StrLiteralExpr rs)
            {
                return new StrLiteralExpr(ls.Value + rs.Value, span);
            }

            return new BinaryExpr(op, left, right, VariableValueKind.Str, span);
        }

        // String repetition: Str * Int -> Str
        if (op == OperatorKind.Multiply && left.ValueKind == VariableValueKind.Str && right.ValueKind == VariableValueKind.Int)
        {
            if (left is StrLiteralExpr ls && right is IntLiteralExpr rc)
            {
                if (rc.Value <= 0 || ls.Value.Length == 0)
                {
                    return new StrLiteralExpr(string.Empty, span);
                }

                if (rc.Value <= 4096)
                {
                    var sb = new StringBuilder(ls.Value.Length * (int)rc.Value);
                    for (int i = 0; i < rc.Value; i++) sb.Append(ls.Value);
                    return new StrLiteralExpr(sb.ToString(), span);
                }
            }

            return new BinaryExpr(op, left, right, VariableValueKind.Str, span);
        }

        // String relational/equality comparisons (Ordinal in Emuera!): Str op Str -> Int
        if (op is OperatorKind.Equal or OperatorKind.NotEqual
                or OperatorKind.Less or OperatorKind.LessEqual
                or OperatorKind.Greater or OperatorKind.GreaterEqual
            && left.ValueKind == VariableValueKind.Str && right.ValueKind == VariableValueKind.Str)
        {
            if (left is StrLiteralExpr ls && right is StrLiteralExpr rs)
            {
                int cmp = string.CompareOrdinal(ls.Value, rs.Value);
                bool res = op switch
                {
                    OperatorKind.Equal => cmp == 0,
                    OperatorKind.NotEqual => cmp != 0,
                    OperatorKind.Less => cmp < 0,
                    OperatorKind.LessEqual => cmp <= 0,
                    OperatorKind.Greater => cmp > 0,
                    OperatorKind.GreaterEqual => cmp >= 0,
                    _ => false,
                };
                return new IntLiteralExpr(res ? 1L : 0L, span);
            }

            return new BinaryExpr(op, left, right, VariableValueKind.Int, span);
        }

        // All remaining binary operators require Int operands -> Int
        if (left.ValueKind != VariableValueKind.Int || right.ValueKind != VariableValueKind.Int)
        {
            ctx.Diagnostics.ReportError(
                "ERA1448",
                $"Operator '{op}' cannot be applied to operands of type '{left.ValueKind}' and '{right.ValueKind}'.",
                span,
                ctx.FilePath);
            return new IntLiteralExpr(0, span);
        }

        if (left is IntLiteralExpr li && right is IntLiteralExpr ri)
        {
            if (op is OperatorKind.Divide or OperatorKind.Modulo && ri.Value == 0)
            {
                ctx.Diagnostics.ReportError("ERA1449", "Division or modulo by constant zero.", right.Span, ctx.FilePath);
                return new IntLiteralExpr(0, span);
            }

            long folded = FoldIntBinary(op, li.Value, ri.Value);
            return new IntLiteralExpr(folded, span);
        }

        return new BinaryExpr(op, left, right, VariableValueKind.Int, span);
    }

    private static long FoldIntBinary(OperatorKind op, long a, long b)
    {
        unchecked
        {
            return op switch
            {
                OperatorKind.Plus => a + b,
                OperatorKind.Minus => a - b,
                OperatorKind.Multiply => a * b,
                OperatorKind.Divide => b == -1 && a == long.MinValue ? long.MinValue : a / b,
                OperatorKind.Modulo => b == -1 && a == long.MinValue ? 0L : a % b,
                OperatorKind.ShiftLeft => (b < 0 || b >= 64) ? 0L : (a << (int)b),
                OperatorKind.ShiftRight => (b < 0 || b >= 64) ? (a < 0 ? -1L : 0L) : (a >> (int)b),
                OperatorKind.Less => a < b ? 1L : 0L,
                OperatorKind.LessEqual => a <= b ? 1L : 0L,
                OperatorKind.Greater => a > b ? 1L : 0L,
                OperatorKind.GreaterEqual => a >= b ? 1L : 0L,
                OperatorKind.Equal => a == b ? 1L : 0L,
                OperatorKind.NotEqual => a != b ? 1L : 0L,
                OperatorKind.BitAnd => a & b,
                OperatorKind.BitXor => a ^ b,
                OperatorKind.BitOr => a | b,
                OperatorKind.LogicalAnd => (a != 0 && b != 0) ? 1L : 0L,
                OperatorKind.LogicalNand => !(a != 0 && b != 0) ? 1L : 0L,
                OperatorKind.LogicalOr => (a != 0 || b != 0) ? 1L : 0L,
                OperatorKind.LogicalNor => !(a != 0 || b != 0) ? 1L : 0L,
                OperatorKind.LogicalXor => ((a != 0) != (b != 0)) ? 1L : 0L,
                _ => 0L,
            };
        }
    }

    private static bool TryFoldBuiltinMethod(
        BuiltinMethodKind kind,
        ImmutableArray<ExprNode?> args,
        SourceSpan span,
        out ExprNode? folded)
    {
        folded = null;
        if (args.Any(static a => a is not (null or IntLiteralExpr or StrLiteralExpr)))
        {
            return false;
        }

        switch (kind)
        {
            case BuiltinMethodKind.ABS when args is [IntLiteralExpr x]:
                folded = new IntLiteralExpr(x.Value == long.MinValue ? long.MinValue : Math.Abs(x.Value), span);
                return true;

            case BuiltinMethodKind.SIGN when args is [IntLiteralExpr x]:
                folded = new IntLiteralExpr(Math.Sign(x.Value), span);
                return true;

            case BuiltinMethodKind.LIMIT when args is [IntLiteralExpr v, IntLiteralExpr lo, IntLiteralExpr hi] && lo.Value <= hi.Value:
                folded = new IntLiteralExpr(Math.Clamp(v.Value, lo.Value, hi.Value), span);
                return true;

            case BuiltinMethodKind.INRANGE when args is [IntLiteralExpr v, IntLiteralExpr lo, IntLiteralExpr hi]:
                folded = new IntLiteralExpr(v.Value >= lo.Value && v.Value <= hi.Value ? 1L : 0L, span);
                return true;

            case BuiltinMethodKind.GETBIT when args is [IntLiteralExpr v, IntLiteralExpr b] && b.Value is >= 0 and < 64:
                folded = new IntLiteralExpr((v.Value >> (int)b.Value) & 1L, span);
                return true;

            case BuiltinMethodKind.POWER when args is [IntLiteralExpr b, IntLiteralExpr e] && e.Value is >= 0 and <= 62:
                {
                    double p = Math.Pow(b.Value, e.Value);
                    if (!double.IsNaN(p) && !double.IsInfinity(p) && p >= long.MinValue && p <= long.MaxValue)
                    {
                        folded = new IntLiteralExpr((long)p, span);
                        return true;
                    }

                    return false;
                }

            case BuiltinMethodKind.TOUPPER when args is [StrLiteralExpr s]:
                folded = new StrLiteralExpr(s.Value.ToUpperInvariant(), span);
                return true;

            case BuiltinMethodKind.TOLOWER when args is [StrLiteralExpr s]:
                folded = new StrLiteralExpr(s.Value.ToLowerInvariant(), span);
                return true;

            case BuiltinMethodKind.STRLENSU when args is [StrLiteralExpr s]:
                folded = new IntLiteralExpr(new StringInfo(s.Value).LengthInTextElements, span);
                return true;

            case BuiltinMethodKind.TOSTR when args is [IntLiteralExpr n]:
                folded = new StrLiteralExpr(n.Value.ToString(CultureInfo.InvariantCulture), span);
                return true;
        }

        return false;
    }

    private ExprNode BuildImplicitTargetExpr(SourceSpan span)
    {
        _symbols.TryResolveVariable("TARGET", null, null, out ResolvedVariable? targetSym);
        return new VariableAccessExpr(targetSym!, [new IntLiteralExpr(0, span)], IsBareArrayReference: false, span);
    }

    private static bool IsDisallowedBareMultiDim(ExprNode expr) =>
        expr is VariableAccessExpr { IsBareArrayReference: true } va
        && (va.Variable.ArrayRank >= 2 || va.Variable.IsCharaData);

    private static void EnsureValueExpression(ExprNode expr, ref ParseContext ctx)
    {
        if (IsDisallowedBareMultiDim(expr))
        {
            var va = (VariableAccessExpr)expr;
            ctx.Diagnostics.ReportError(
                "ERA1450",
                $"Variable '{va.Variable.Name}' requires explicit subscripts when evaluated as a scalar value.",
                va.Span,
                ctx.FilePath);
        }
    }

    private static void ValidateCaseOperandType(ExprNode expr, VariableValueKind expected, string? filePath, DiagnosticBag diagnostics)
    {
        if (expr.ValueKind != expected)
        {
            diagnostics.ReportError(
                "ERA1451",
                $"CASE condition expression type ({expr.ValueKind}) does not match SELECTCASE expression type ({expected}).",
                expr.Span,
                filePath);
        }
    }

    private static bool IsBinaryCaseOperator(OperatorKind op) =>
        op is OperatorKind.Equal or OperatorKind.NotEqual
            or OperatorKind.Less or OperatorKind.LessEqual
            or OperatorKind.Greater or OperatorKind.GreaterEqual
            or OperatorKind.Plus or OperatorKind.Minus or OperatorKind.Multiply or OperatorKind.Divide or OperatorKind.Modulo
            or OperatorKind.BitAnd or OperatorKind.BitOr or OperatorKind.BitXor
            or OperatorKind.ShiftLeft or OperatorKind.ShiftRight
            or OperatorKind.LogicalAnd or OperatorKind.LogicalOr or OperatorKind.LogicalXor or OperatorKind.LogicalNand or OperatorKind.LogicalNor;

    private bool ShouldStop(ref ParseContext ctx)
    {
        ErbToken cur = ctx.Stream.Current;
        if (cur.Kind == ErbTokenKind.EndOfLine)
        {
            return true;
        }

        if (cur.Kind is ErbTokenKind.CloseParen or ErbTokenKind.CloseBracket)
        {
            return true;
        }

        if (ctx.StopAtComma && cur.Kind == ErbTokenKind.Comma)
        {
            return true;
        }

        if (ctx.StopAtAssign && cur.Kind == ErbTokenKind.Operator && IsAssignmentStopOperator(cur.Operator))
        {
            return true;
        }

        if (ctx.AllowToKeyword && cur.Kind == ErbTokenKind.Identifier && cur.Text is not null)
        {
            StringComparison cmp = _symbols.Options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (cur.Text.Equals("TO", cmp))
            {
                return true;
            }
        }

        if (ctx.StopAtPxSuffix && cur.Kind == ErbTokenKind.Identifier && cur.Text is not null
            && cur.Text.Equals("px", StringComparison.OrdinalIgnoreCase)
            && ctx.Stream.Peek(1).Kind is ErbTokenKind.Comma or ErbTokenKind.EndOfLine)
        {
            return true;
        }

        return false;
    }

    private static bool IsAssignmentStopOperator(OperatorKind op) =>
        op is OperatorKind.Assign
            or OperatorKind.AssignStr
            or OperatorKind.AddAssign
            or OperatorKind.SubAssign
            or OperatorKind.MulAssign
            or OperatorKind.DivAssign
            or OperatorKind.ModAssign
            or OperatorKind.BitAndAssign
            or OperatorKind.BitOrAssign
            or OperatorKind.BitXorAssign
            or OperatorKind.ShiftLeftAssign
            or OperatorKind.ShiftRightAssign
            or OperatorKind.Equal;

    private static SourceSpan MergeSpan(SourceSpan a, SourceSpan b) =>
        new(a.FileId, a.StartLine, a.StartColumn, b.EndLine, Math.Max(a.EndColumn, b.EndColumn));

    private readonly record struct ParseContext(
        TokenStream Stream,
        UserFunctionSymbol? ActiveFunction,
        string? FilePath,
        DiagnosticBag Diagnostics,
        bool StopAtComma,
        bool StopAtAssign,
        bool AllowToKeyword,
        bool StopAtPxSuffix,
        bool InSubscript,
        ResolvedVariable? SubscriptVar,
        int SubscriptDimension,
        Action<UserFunctionSymbol>? OnUserFunctionReferenced);
}
