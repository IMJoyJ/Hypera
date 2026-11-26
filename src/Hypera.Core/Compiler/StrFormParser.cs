using System.Collections.Immutable;
using System.Text;
using Hypera.Bytecode;
using Hypera.Diagnostics;

namespace Hypera.Compiler;

/// <summary>
/// Compiles raw <c>FORM</c> string parts (<c>%strExpr[,width[,LEFT|RIGHT]]%</c>,
/// <c>{intExpr[,width[,LEFT|RIGHT]]}</c>, <c>\@ cond ? trueForm # falseForm \@</c>,
/// and <c>***</c>/<c>+++</c>/<c>===</c>/<c>///</c>/<c>$$$</c> triple symbols)
/// into typed <see cref="StrFormExpr"/> AST nodes with constant folding.
/// </summary>
public sealed class StrFormParser
{
    private readonly SymbolTable _symbols;
    private readonly ExpressionParser _expressionParser;

    public StrFormParser(SymbolTable symbols, ExpressionParser expressionParser)
    {
        _symbols = symbols;
        _expressionParser = expressionParser;
    }

    public ExprNode ParsePartsToExpression(
        ImmutableArray<RawFormPart> parts,
        SourceSpan wholeSpan,
        UserFunctionSymbol? activeFunction,
        string? filePath,
        DiagnosticBag diagnostics,
        Action<UserFunctionSymbol>? onUserFunctionReferenced = null)
    {
        StrFormExpr form = CompileParts(parts, wholeSpan, activeFunction, filePath, diagnostics, onUserFunctionReferenced);

        if (form.Segments.Length == 0)
        {
            return new StrLiteralExpr(string.Empty, wholeSpan);
        }

        if (form.Segments.Length == 1 && form.Segments[0] is LiteralFormSegment singleLit)
        {
            return new StrLiteralExpr(singleLit.Text, wholeSpan);
        }

        // Single unformatted %strExpr% with no surrounding literals simplifies directly to strExpr
        if (form.Segments.Length == 1
            && form.Segments[0] is ValueFormSegment { Width: null, Value.ValueKind: VariableValueKind.Str } singleStr)
        {
            return singleStr.Value is StrLiteralExpr singleStrLit
                ? new StrLiteralExpr(singleStrLit.Value, wholeSpan)
                : singleStr.Value;
        }

        return form;
    }

    public StrFormExpr CompileParts(
        ImmutableArray<RawFormPart> parts,
        SourceSpan wholeSpan,
        UserFunctionSymbol? activeFunction,
        string? filePath,
        DiagnosticBag diagnostics,
        Action<UserFunctionSymbol>? onUserFunctionReferenced = null)
    {
        if (parts.IsDefaultOrEmpty)
        {
            return new StrFormExpr([], wholeSpan);
        }

        var segments = new List<CompiledFormSegment>(parts.Length);
        var pendingLiteral = new StringBuilder();
        SourceSpan pendingSpan = wholeSpan;

        void FlushPendingLiteral()
        {
            if (pendingLiteral.Length > 0)
            {
                segments.Add(new LiteralFormSegment(pendingLiteral.ToString(), pendingSpan));
                pendingLiteral.Clear();
            }
        }

        foreach (RawFormPart part in parts)
        {
            switch (part.Kind)
            {
                case RawFormPartKind.Literal:
                    if (!string.IsNullOrEmpty(part.LiteralText))
                    {
                        if (pendingLiteral.Length == 0)
                        {
                            pendingSpan = part.Span;
                        }

                        pendingLiteral.Append(part.LiteralText);
                    }
                    break;

                case RawFormPartKind.TripleSymbol:
                    {
                        FlushPendingLiteral();
                        ExprNode tripleExpr = BuildTripleSymbolAccess(part.TripleSymbolChar, part.Span);
                        segments.Add(new ValueFormSegment(tripleExpr, Width: null, LeftAlign: false, part.Span));
                        break;
                    }

                case RawFormPartKind.YenAtConditional:
                    {
                        ExprNode cond;
                        if (part.ExprTokens.IsDefaultOrEmpty)
                        {
                            cond = new IntLiteralExpr(0, part.Span);
                        }
                        else
                        {
                            var ts = new TokenStream(part.ExprTokens, part.Span);
                            cond = _expressionParser.ParseIntExpression(ts, activeFunction, filePath, diagnostics, onUserFunctionReferenced);
                            if (!ts.IsAtEnd)
                            {
                                diagnostics.ReportError("ERA1301", "Unexpected tokens in '\\@' condition expression.", ts.Current.Span, filePath);
                            }
                        }

                        StrFormExpr trueForm = CompileParts(part.TrueBranchParts, part.Span, activeFunction, filePath, diagnostics, onUserFunctionReferenced);
                        StrFormExpr? falseForm = part.HasFalseBranch
                            ? CompileParts(part.FalseBranchParts, part.Span, activeFunction, filePath, diagnostics, onUserFunctionReferenced)
                            : null;

                        if (cond is IntLiteralExpr constCond)
                        {
                            StrFormExpr? chosen = constCond.Value != 0 ? trueForm : falseForm;
                            if (chosen is not null)
                            {
                                foreach (CompiledFormSegment seg in chosen.Segments)
                                {
                                    if (seg is LiteralFormSegment lit)
                                    {
                                        if (pendingLiteral.Length == 0) pendingSpan = lit.Span;
                                        pendingLiteral.Append(lit.Text);
                                    }
                                    else
                                    {
                                        FlushPendingLiteral();
                                        segments.Add(seg);
                                    }
                                }
                            }

                            break;
                        }

                        FlushPendingLiteral();
                        segments.Add(new ConditionalFormSegment(cond, trueForm, falseForm, part.Span));
                        break;
                    }

                case RawFormPartKind.CurlyIntExpr:
                case RawFormPartKind.PercentExpr:
                    {
                        bool expectInt = part.Kind == RawFormPartKind.CurlyIntExpr;
                        if (part.ExprTokens.IsDefaultOrEmpty)
                        {
                            diagnostics.ReportError(
                                "ERA1302",
                                expectInt ? "Empty '{}' placeholder in FORM string." : "Empty '%%' placeholder in FORM string.",
                                part.Span,
                                filePath,
                                null,
                                [expectInt ? "{}" : "%%"]);
                            break;
                        }

                        var ts = new TokenStream(part.ExprTokens, part.Span);
                        ExprNode valExpr = _expressionParser.ParseExpression(
                            ts, activeFunction, filePath, diagnostics, stopAtComma: true, onUserFunctionReferenced: onUserFunctionReferenced);

                        if (expectInt && valExpr.ValueKind != VariableValueKind.Int)
                        {
                            diagnostics.ReportError("ERA1303", "'{}' FORM placeholder requires an integer expression (use '%%' for strings).", valExpr.Span, filePath);
                        }
                        else if (!expectInt && valExpr.ValueKind != VariableValueKind.Str)
                        {
                            diagnostics.ReportError("ERA1304", "'%%' FORM placeholder requires a string expression (use '{}' for integers).", valExpr.Span, filePath);
                        }

                        ExprNode? widthExpr = null;
                        bool leftAlign = false;

                        if (ts.Match(ErbTokenKind.Comma) && !ts.IsAtEnd)
                        {
                            widthExpr = _expressionParser.ParseExpression(
                                ts, activeFunction, filePath, diagnostics, stopAtComma: true, onUserFunctionReferenced: onUserFunctionReferenced);
                            if (widthExpr.ValueKind != VariableValueKind.Int)
                            {
                                diagnostics.ReportError("ERA1305", "FORM placeholder width specifier must be an integer expression.", widthExpr.Span, filePath);
                            }

                            if (ts.Match(ErbTokenKind.Comma) && !ts.IsAtEnd)
                            {
                                if (ts.Current.Kind != ErbTokenKind.Identifier || string.IsNullOrEmpty(ts.Current.Text))
                                {
                                    diagnostics.ReportError("ERA1306", "Expected 'LEFT' or 'RIGHT' alignment keyword in FORM placeholder.", ts.Current.Span, filePath, null, [], "diag.ERA1306.expected");
                                }
                                else
                                {
                                    string alignText = ts.Advance().Text!;
                                    StringComparison cmp = _symbols.Options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                                    if (alignText.Equals("LEFT", cmp))
                                    {
                                        leftAlign = true;
                                    }
                                    else if (alignText.Equals("RIGHT", cmp))
                                    {
                                        leftAlign = false;
                                    }
                                    else
                                    {
                                        diagnostics.ReportError("ERA1306", $"Invalid FORM alignment '{alignText}'; expected 'LEFT' or 'RIGHT'.", part.Span, filePath, null, [alignText], "diag.ERA1306.invalid");
                                    }
                                }
                            }
                        }

                        if (!ts.IsAtEnd)
                        {
                            string tokText = ts.Current.Text ?? ts.Current.Kind.ToString();
                            diagnostics.ReportError("ERA1307", $"Unexpected token '{tokText}' inside FORM placeholder.", ts.Current.Span, filePath, null, [tokText]);
                        }

                        // Constant fold integer literal without dynamic width
                        if (expectInt && valExpr is IntLiteralExpr constInt && (widthExpr is null || widthExpr is IntLiteralExpr))
                        {
                            string formatted = constInt.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            if (widthExpr is IntLiteralExpr constW && constW.Value > formatted.Length)
                            {
                                int w = (int)Math.Min(constW.Value, 4096);
                                formatted = leftAlign ? formatted.PadRight(w, ' ') : formatted.PadLeft(w, ' ');
                            }

                            if (pendingLiteral.Length == 0) pendingSpan = part.Span;
                            pendingLiteral.Append(formatted);
                            break;
                        }

                        // Constant fold pure ASCII string literal when width is absent
                        if (!expectInt && valExpr is StrLiteralExpr constStr && widthExpr is null)
                        {
                            if (pendingLiteral.Length == 0) pendingSpan = part.Span;
                            pendingLiteral.Append(constStr.Value);
                            break;
                        }

                        FlushPendingLiteral();
                        segments.Add(new ValueFormSegment(valExpr, widthExpr, leftAlign, part.Span));
                        break;
                    }
            }
        }

        FlushPendingLiteral();
        return new StrFormExpr([.. segments], wholeSpan);
    }

    private ExprNode BuildTripleSymbolAccess(char symbol, SourceSpan span)
    {
        (string charaVar, string indexVar) = symbol switch
        {
            '*' => ("NAME", "TARGET"),
            '+' => ("CALLNAME", "MASTER"),
            '=' => ("CALLNAME", "PLAYER"),
            '/' => ("NAME", "ASSI"),
            '$' => ("CALLNAME", "TARGET"),
            _ => ("NAME", "TARGET"),
        };

        _symbols.TryResolveVariable(charaVar, null, null, out ResolvedVariable? charaSym);
        _symbols.TryResolveVariable(indexVar, null, null, out ResolvedVariable? idxSym);

        var idxAccess = new VariableAccessExpr(idxSym!, [new IntLiteralExpr(0, span)], IsBareArrayReference: false, span);
        return new VariableAccessExpr(charaSym!, [idxAccess], IsBareArrayReference: false, span);
    }
}
