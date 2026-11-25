using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Hypera.Configuration;
using Hypera.Diagnostics;

namespace Hypera.Compiler;

[Flags]
public enum LexModeFlags : byte
{
    None = 0,
    AllowAssignment = 1 << 0,
    StopAtAssignment = 1 << 1,
    AnalyzePrintV = 1 << 2,
    AllowSingleQuoteString = 1 << 3,
    DisableMacroExpansion = 1 << 4,
}

public enum FormTerminator : byte
{
    EndOfLine = 0,
    DoubleQuote = 1,
    Comma = 2,
    Sharp = 3,
    YenAt = 4,
    CallTargetDelimiter = 5, // '(', '[', ',', or ';'
}

/// <summary>
/// Stateless lexical scanner for ERH/ERB expressions, statements, numeric literals (<c>0x</c>, <c>0b</c>, <c>1pN</c>, <c>1eN</c>),
/// <c>FORM</c> strings (<c>%...%</c>, <c>{...}</c>, <c>\@ ... \@</c>, triple symbols), and <c>#DEFINE</c> macro expansion.
/// </summary>
public sealed class ErbLexer
{
    public const int MaxMacroExpansions = 100;

    private static readonly SearchValues<char> IdentifierStopChars = SearchValues.Create(
        "+-*/%=!<>|&^~ \t\u3000\"(){}[].,:\\@$#?;'");

    private readonly CompilerOptions _options;
    private readonly bool _isDebugMode;
    private readonly Func<string, ImmutableArray<ErbToken>?>? _macroResolver;

    public ErbLexer(
        CompilerOptions options,
        bool isDebugMode = false,
        Func<string, ImmutableArray<ErbToken>?>? macroResolver = null)
    {
        _options = options ?? CompilerOptions.Default;
        _isDebugMode = isDebugMode;
        _macroResolver = macroResolver;
    }

    public CompilerOptions Options => _options;

    public ImmutableArray<ErbToken> Tokenize(
        string text,
        int fileId,
        string? filePath,
        int lineNo,
        int baseColumn,
        DiagnosticBag diagnostics,
        LexModeFlags flags = LexModeFlags.None)
    {
        int pos = 0;
        return TokenizeSlice(text, ref pos, fileId, filePath, lineNo, baseColumn, diagnostics, flags, stopChar: '\0');
    }

    public ImmutableArray<ErbToken> TokenizeSlice(
        string text,
        ref int pos,
        int fileId,
        string? filePath,
        int lineNo,
        int baseColumn,
        DiagnosticBag diagnostics,
        LexModeFlags flags,
        char stopChar)
    {
        var tokens = new List<ErbToken>(16);
        int parenDepth = 0;
        int bracketDepth = 0;
        bool stopAtAssign = (flags & LexModeFlags.StopAtAssignment) != 0;
        bool allowAssign = (flags & LexModeFlags.AllowAssignment) != 0;

        while (pos < text.Length)
        {
            char c = text[pos];

            if (parenDepth == 0 && bracketDepth == 0)
            {
                if (stopChar != '\0' && c == stopChar)
                {
                    break;
                }

                if (stopChar == ',' && c == ';')
                {
                    // Inline comment check
                    if (!TryConsumeSpecialCommentPrefix(text, ref pos))
                    {
                        pos = text.Length;
                        break;
                    }

                    continue;
                }
            }

            if (c is ' ' or '\t')
            {
                pos++;
                continue;
            }

            if (c == '　')
            {
                if (!_options.SystemAllowFullSpace)
                {
                    diagnostics.ReportError(
                        "ERA1101",
                        "Unexpected full-width space '　' when SystemAllowFullSpace is disabled.",
                        MakeSpan(fileId, lineNo, baseColumn, pos, 1),
                        filePath,
                        text);
                }

                pos++;
                continue;
            }

            if (c == ';')
            {
                if (TryConsumeSpecialCommentPrefix(text, ref pos))
                {
                    continue;
                }

                pos = text.Length;
                break;
            }

            if (char.IsAsciiDigit(c))
            {
                int start = pos;
                long val = ScanInt64Literal(text, ref pos, fileId, filePath, lineNo, baseColumn, diagnostics);
                tokens.Add(new ErbToken(ErbTokenKind.IntegerLiteral, MakeSpan(fileId, lineNo, baseColumn, start, pos - start), IntValue: val));
                continue;
            }

            if (c == '(')
            {
                parenDepth++;
                tokens.Add(new ErbToken(ErbTokenKind.OpenParen, MakeSpan(fileId, lineNo, baseColumn, pos, 1)));
                pos++;
                continue;
            }

            if (c == ')')
            {
                parenDepth--;
                tokens.Add(new ErbToken(ErbTokenKind.CloseParen, MakeSpan(fileId, lineNo, baseColumn, pos, 1)));
                pos++;
                continue;
            }

            if (c == '[')
            {
                if (pos + 1 < text.Length && text[pos + 1] == '[')
                {
                    int closeIdx = text.IndexOf("]]", pos + 2, StringComparison.Ordinal);
                    int len = closeIdx >= 0 ? (closeIdx + 2 - pos) : (text.Length - pos);
                    string rawKey = text.Substring(pos, len);
                    string msg = closeIdx < 0
                        ? "Unclosed '[[' rename token (missing ']]')."
                        : closeIdx == pos + 2
                            ? "Empty '[[]]' rename token is invalid."
                            : $"Unresolved rename key '{rawKey}'.";
                    diagnostics.ReportError("ERA1102", msg, MakeSpan(fileId, lineNo, baseColumn, pos, len), filePath, text);
                    pos += len;
                    continue;
                }

                bracketDepth++;
                tokens.Add(new ErbToken(ErbTokenKind.OpenBracket, MakeSpan(fileId, lineNo, baseColumn, pos, 1)));
                pos++;
                continue;
            }

            if (c == ']')
            {
                bracketDepth--;
                tokens.Add(new ErbToken(ErbTokenKind.CloseBracket, MakeSpan(fileId, lineNo, baseColumn, pos, 1)));
                pos++;
                continue;
            }

            if (c == ':')
            {
                tokens.Add(new ErbToken(ErbTokenKind.Colon, MakeSpan(fileId, lineNo, baseColumn, pos, 1)));
                pos++;
                continue;
            }

            if (c == ',')
            {
                tokens.Add(new ErbToken(ErbTokenKind.Comma, MakeSpan(fileId, lineNo, baseColumn, pos, 1)));
                pos++;
                continue;
            }

            if (c == '.')
            {
                tokens.Add(new ErbToken(ErbTokenKind.Dot, MakeSpan(fileId, lineNo, baseColumn, pos, 1)));
                pos++;
                continue;
            }

            if (c == '"')
            {
                int start = pos;
                pos++; // skip opening '"'
                string str = ScanPlainString(text, ref pos, '"', fileId, filePath, lineNo, baseColumn, diagnostics);
                if (pos >= text.Length || text[pos] != '"')
                {
                    diagnostics.ReportError(
                        "ERA1103",
                        "Unterminated string literal (missing closing '\"').",
                        MakeSpan(fileId, lineNo, baseColumn, start, pos - start),
                        filePath,
                        text);
                }
                else
                {
                    pos++; // skip closing '"'
                }

                tokens.Add(new ErbToken(ErbTokenKind.StringLiteral, MakeSpan(fileId, lineNo, baseColumn, start, pos - start), Text: str));
                continue;
            }

            if (c == '\'')
            {
                if (stopAtAssign && parenDepth == 0 && bracketDepth == 0 && pos + 1 < text.Length && text[pos + 1] == '=')
                {
                    break;
                }

                if ((flags & LexModeFlags.AllowAssignment) != 0 && pos + 1 < text.Length && text[pos + 1] == '=')
                {
                    tokens.Add(new ErbToken(ErbTokenKind.Operator, MakeSpan(fileId, lineNo, baseColumn, pos, 2), Operator: OperatorKind.AssignStr));
                    pos += 2;
                    continue;
                }

                if ((flags & LexModeFlags.AllowSingleQuoteString) != 0)
                {
                    int start = pos++;
                    string str = ScanPlainString(text, ref pos, '\'', fileId, filePath, lineNo, baseColumn, diagnostics);
                    if (pos >= text.Length || text[pos] != '\'')
                    {
                        diagnostics.ReportError("ERA1103", "Unterminated single-quoted string literal.", MakeSpan(fileId, lineNo, baseColumn, start, pos - start), filePath, text);
                    }
                    else
                    {
                        pos++;
                    }

                    tokens.Add(new ErbToken(ErbTokenKind.StringLiteral, MakeSpan(fileId, lineNo, baseColumn, start, pos - start), Text: str));
                    continue;
                }

                if ((flags & LexModeFlags.AnalyzePrintV) != 0)
                {
                    int start = pos++;
                    string str = ScanPlainStringUntilCommaOrEol(text, ref pos);
                    tokens.Add(new ErbToken(ErbTokenKind.StringLiteral, MakeSpan(fileId, lineNo, baseColumn, start, pos - start), Text: str));
                    continue;
                }

                diagnostics.ReportError("ERA1104", "Unexpected character ''' in expression.", MakeSpan(fileId, lineNo, baseColumn, pos, 1), filePath, text);
                pos++;
                continue;
            }

            if (c == '@')
            {
                if (pos + 1 < text.Length && text[pos + 1] == '"')
                {
                    int start = pos;
                    pos += 2; // skip @"
                    ImmutableArray<RawFormPart> parts = ScanFormattedString(
                        text, ref pos, FormTerminator.DoubleQuote, trimOuterSpaces: false, fileId, filePath, lineNo, baseColumn, diagnostics);
                    if (pos >= text.Length || text[pos] != '"')
                    {
                        diagnostics.ReportError(
                            "ERA1105",
                            "Unterminated @\"...\" formatted string literal.",
                            MakeSpan(fileId, lineNo, baseColumn, start, pos - start),
                            filePath,
                            text);
                    }
                    else
                    {
                        pos++;
                    }

                    tokens.Add(new ErbToken(
                        ErbTokenKind.FormStringLiteral,
                        MakeSpan(fileId, lineNo, baseColumn, start, pos - start),
                        FormParts: parts));
                    continue;
                }

                tokens.Add(new ErbToken(ErbTokenKind.At, MakeSpan(fileId, lineNo, baseColumn, pos, 1)));
                pos++;
                continue;
            }

            if (c == '\\')
            {
                if (pos + 1 < text.Length && text[pos + 1] == '@')
                {
                    int start = pos;
                    pos += 2;
                    RawFormPart yenAt = ScanYenAtPart(text, ref pos, start, fileId, filePath, lineNo, baseColumn, diagnostics);
                    tokens.Add(new ErbToken(
                        ErbTokenKind.InlineYenAtForm,
                        MakeSpan(fileId, lineNo, baseColumn, start, pos - start),
                        FormParts: [yenAt]));
                    continue;
                }

                diagnostics.ReportError("ERA1106", "Unexpected character '\\' outside string or '\\@' conditional.", MakeSpan(fileId, lineNo, baseColumn, pos, 1), filePath, text);
                pos++;
                continue;
            }

            if (c is '{' or '}' or '$')
            {
                diagnostics.ReportError("ERA1107", $"Unexpected character '{c}' in expression.", MakeSpan(fileId, lineNo, baseColumn, pos, 1), filePath, text);
                pos++;
                continue;
            }

            if (IsOperatorStartChar(c))
            {
                if (stopAtAssign && parenDepth == 0 && bracketDepth == 0)
                {
                    // In LHS of assignment, stop as soon as we hit any top-level operator (`=`, `+=`, `++`, etc.)
                    break;
                }

                int start = pos;
                OperatorKind op = ScanOperator(text, ref pos, allowAssign, fileId, filePath, lineNo, baseColumn, diagnostics);
                if (op != OperatorKind.None)
                {
                    tokens.Add(new ErbToken(ErbTokenKind.Operator, MakeSpan(fileId, lineNo, baseColumn, start, pos - start), Operator: op));
                }

                continue;
            }

            // Identifier
            int idStart = pos;
            string ident = ReadIdentifierSlice(text, ref pos);
            if (ident.Length == 0)
            {
                diagnostics.ReportError("ERA1108", $"Unexpected character '{text[pos]}'.", MakeSpan(fileId, lineNo, baseColumn, pos, 1), filePath, text);
                pos++;
                continue;
            }

            tokens.Add(new ErbToken(
                ErbTokenKind.Identifier,
                MakeSpan(fileId, lineNo, baseColumn, idStart, pos - idStart),
                Text: ident));
        }

        if (parenDepth != 0)
        {
            diagnostics.ReportError(
                "ERA1109",
                parenDepth > 0 ? "Unclosed parenthesis '('." : "Unexpected closing parenthesis ')'.",
                MakeSpan(fileId, lineNo, baseColumn, Math.Max(0, pos - 1), 1),
                filePath,
                text);
        }

        if (bracketDepth != 0)
        {
            diagnostics.ReportError(
                "ERA1110",
                bracketDepth > 0 ? "Unclosed bracket '['." : "Unexpected closing bracket ']'.",
                MakeSpan(fileId, lineNo, baseColumn, Math.Max(0, pos - 1), 1),
                filePath,
                text);
        }

        ImmutableArray<ErbToken> result = [.. tokens];
        if ((flags & LexModeFlags.DisableMacroExpansion) == 0 && _macroResolver is not null && result.Length > 0)
        {
            result = ExpandMacros(result, fileId, filePath, lineNo, diagnostics);
        }

        return result;
    }

    public AssignmentScanResult TryScanAssignmentOperator(
        string text,
        ref int pos,
        int fileId,
        string? filePath,
        int lineNo,
        int baseColumn,
        DiagnosticBag diagnostics)
    {
        SkipWhitespaceAndSpecialPrefixes(text, ref pos);
        if (pos >= text.Length)
        {
            return default;
        }

        int start = pos;
        char c0 = text[pos];
        char c1 = pos + 1 < text.Length ? text[pos + 1] : '\0';
        char c2 = pos + 2 < text.Length ? text[pos + 2] : '\0';

        OperatorKind op = (c0, c1, c2) switch
        {
            ('+', '+', _) => AdvanceBy(ref pos, 2, OperatorKind.Increment),
            ('-', '-', _) => AdvanceBy(ref pos, 2, OperatorKind.Decrement),
            ('+', '=', _) => AdvanceBy(ref pos, 2, OperatorKind.AddAssign),
            ('-', '=', _) => AdvanceBy(ref pos, 2, OperatorKind.SubAssign),
            ('*', '=', _) => AdvanceBy(ref pos, 2, OperatorKind.MulAssign),
            ('/', '=', _) => AdvanceBy(ref pos, 2, OperatorKind.DivAssign),
            ('%', '=', _) => AdvanceBy(ref pos, 2, OperatorKind.ModAssign),
            ('&', '=', _) => AdvanceBy(ref pos, 2, OperatorKind.BitAndAssign),
            ('|', '=', _) => AdvanceBy(ref pos, 2, OperatorKind.BitOrAssign),
            ('^', '=', _) => AdvanceBy(ref pos, 2, OperatorKind.BitXorAssign),
            ('\'', '=', _) => AdvanceBy(ref pos, 2, OperatorKind.AssignStr),
            ('<', '<', '=') => AdvanceBy(ref pos, 3, OperatorKind.ShiftLeftAssign),
            ('>', '>', '=') => AdvanceBy(ref pos, 3, OperatorKind.ShiftRightAssign),
            ('=', '=', _) => AdvanceBy(ref pos, 2, OperatorKind.Equal),
            ('=', _, _) => AdvanceBy(ref pos, 1, OperatorKind.Assign),
            _ => OperatorKind.None,
        };

        if (op == OperatorKind.Equal)
        {
            // Upstream Emuera compatibility: '==' in statement assignment position warns at level 0 and acts as '='.
            diagnostics.ReportInfo(
                "ERA1111",
                "'==' used as assignment operator; treating as '='.",
                MakeSpan(fileId, lineNo, baseColumn, start, 2),
                filePath,
                text);
            op = OperatorKind.Assign;
        }

        return new AssignmentScanResult(op != OperatorKind.None, op, MakeSpan(fileId, lineNo, baseColumn, start, pos - start));
    }

    public ImmutableArray<RawFormPart> ScanFormattedString(
        string text,
        ref int pos,
        FormTerminator terminator,
        bool trimOuterSpaces,
        int fileId,
        string? filePath,
        int lineNo,
        int baseColumn,
        DiagnosticBag diagnostics)
    {
        var parts = new List<RawFormPart>();
        var literal = new StringBuilder();
        int literalStart = pos;

        void FlushLiteral(int endPos)
        {
            if (literal.Length > 0)
            {
                parts.Add(new RawFormPart(
                    RawFormPartKind.Literal,
                    MakeSpan(fileId, lineNo, baseColumn, literalStart, Math.Max(1, endPos - literalStart)),
                    LiteralText: literal.ToString()));
                literal.Clear();
            }
        }

        while (pos < text.Length)
        {
            char c = text[pos];

            if (c == '"' && terminator == FormTerminator.DoubleQuote)
            {
                break;
            }

            if (c == '#' && terminator == FormTerminator.Sharp)
            {
                break;
            }

            if (c == ',' && terminator is FormTerminator.Comma or FormTerminator.CallTargetDelimiter)
            {
                break;
            }

            if (c is '(' or '[' or ';' && terminator == FormTerminator.CallTargetDelimiter)
            {
                break;
            }

            if (c == '%')
            {
                FlushLiteral(pos);
                int subStart = pos++;
                ImmutableArray<ErbToken> exprTokens = TokenizeSlice(
                    text, ref pos, fileId, filePath, lineNo, baseColumn, diagnostics, LexModeFlags.None, stopChar: '%');
                if (pos >= text.Length || text[pos] != '%')
                {
                    diagnostics.ReportError(
                        "ERA1112",
                        "Unclosed '%' placeholder in FORM string.",
                        MakeSpan(fileId, lineNo, baseColumn, subStart, pos - subStart),
                        filePath,
                        text);
                }
                else
                {
                    pos++;
                }

                parts.Add(new RawFormPart(
                    RawFormPartKind.PercentExpr,
                    MakeSpan(fileId, lineNo, baseColumn, subStart, pos - subStart),
                    ExprTokens: exprTokens));
                literalStart = pos;
                continue;
            }

            if (c == '{')
            {
                FlushLiteral(pos);
                int subStart = pos++;
                ImmutableArray<ErbToken> exprTokens = TokenizeSlice(
                    text, ref pos, fileId, filePath, lineNo, baseColumn, diagnostics, LexModeFlags.None, stopChar: '}');
                if (pos >= text.Length || text[pos] != '}')
                {
                    diagnostics.ReportError(
                        "ERA1113",
                        "Unclosed '{' placeholder in FORM string (missing '}').",
                        MakeSpan(fileId, lineNo, baseColumn, subStart, pos - subStart),
                        filePath,
                        text);
                }
                else
                {
                    pos++;
                }

                parts.Add(new RawFormPart(
                    RawFormPartKind.CurlyIntExpr,
                    MakeSpan(fileId, lineNo, baseColumn, subStart, pos - subStart),
                    ExprTokens: exprTokens));
                literalStart = pos;
                continue;
            }

            if (!_options.SystemIgnoreTripleSymbol && c is '*' or '+' or '=' or '/' or '$'
                && pos + 2 < text.Length && text[pos + 1] == c && text[pos + 2] == c)
            {
                FlushLiteral(pos);
                var span = MakeSpan(fileId, lineNo, baseColumn, pos, 3);
                pos += 3;
                parts.Add(new RawFormPart(RawFormPartKind.TripleSymbol, span, TripleSymbolChar: c));
                literalStart = pos;
                continue;
            }

            if (c == '\\')
            {
                if (pos + 1 < text.Length && text[pos + 1] == '@'
                    && terminator is FormTerminator.YenAt or FormTerminator.Sharp)
                {
                    // End of \@ ... # ... \@ branch (caller consumes \@ or #)
                    pos++; // move onto '@' so caller sees '@' when terminator == YenAt
                    break;
                }

                if (pos + 1 >= text.Length)
                {
                    diagnostics.ReportError(
                        "ERA1114",
                        "Trailing escape backslash '\\' at end of FORM string.",
                        MakeSpan(fileId, lineNo, baseColumn, pos, 1),
                        filePath,
                        text);
                    pos++;
                    break;
                }

                char esc = text[pos + 1];
                if (esc == '@')
                {
                    FlushLiteral(pos);
                    int subStart = pos;
                    pos += 2;
                    parts.Add(ScanYenAtPart(text, ref pos, subStart, fileId, filePath, lineNo, baseColumn, diagnostics));
                    literalStart = pos;
                    continue;
                }

                if (literal.Length == 0)
                {
                    literalStart = pos;
                }

                AppendEscapedChar(literal, esc);
                pos += 2;
                continue;
            }

            if (literal.Length == 0)
            {
                literalStart = pos;
            }

            literal.Append(c);
            pos++;
        }

        FlushLiteral(pos);

        if (trimOuterSpaces && parts.Count > 0)
        {
            if (parts[0].Kind == RawFormPartKind.Literal && parts[0].LiteralText is { } firstLit)
            {
                string trimmedFirst = firstLit.TrimStart(' ', '\t');
                parts[0] = parts[0] with { LiteralText = trimmedFirst };
            }

            if (parts[^1].Kind == RawFormPartKind.Literal && parts[^1].LiteralText is { } lastLit)
            {
                string trimmedLast = lastLit.TrimEnd(' ', '\t');
                parts[^1] = parts[^1] with { LiteralText = trimmedLast };
            }
        }

        return [.. parts];
    }

    private RawFormPart ScanYenAtPart(
        string text,
        ref int pos,
        int startPos,
        int fileId,
        string? filePath,
        int lineNo,
        int baseColumn,
        DiagnosticBag diagnostics)
    {
        ImmutableArray<ErbToken> condTokens = TokenizeSlice(
            text, ref pos, fileId, filePath, lineNo, baseColumn, diagnostics, LexModeFlags.None, stopChar: '?');

        if (pos >= text.Length || text[pos] != '?')
        {
            diagnostics.ReportError(
                "ERA1115",
                "Missing '?' in '\\@ condition ? trueBranch # falseBranch \\@' expression.",
                MakeSpan(fileId, lineNo, baseColumn, startPos, Math.Max(1, pos - startPos)),
                filePath,
                text);
            return new RawFormPart(RawFormPartKind.YenAtConditional, MakeSpan(fileId, lineNo, baseColumn, startPos, Math.Max(1, pos - startPos)), ExprTokens: condTokens);
        }

        pos++; // skip '?'
        ImmutableArray<RawFormPart> trueBranch = ScanFormattedString(
            text, ref pos, FormTerminator.Sharp, trimOuterSpaces: true, fileId, filePath, lineNo, baseColumn, diagnostics);

        if (pos < text.Length && text[pos] == '@')
        {
            // \@ cond ? trueBranch \@ without '#' (valid in Emuera with warning, false branch = "")
            pos++;
            return new RawFormPart(
                RawFormPartKind.YenAtConditional,
                MakeSpan(fileId, lineNo, baseColumn, startPos, pos - startPos),
                ExprTokens: condTokens,
                TrueBranchParts: trueBranch,
                FalseBranchParts: [],
                HasFalseBranch: false);
        }

        if (pos >= text.Length || text[pos] != '#')
        {
            diagnostics.ReportError(
                "ERA1116",
                "Unclosed '\\@' conditional (expected '#' or '\\@').",
                MakeSpan(fileId, lineNo, baseColumn, startPos, Math.Max(1, pos - startPos)),
                filePath,
                text);
            return new RawFormPart(
                RawFormPartKind.YenAtConditional,
                MakeSpan(fileId, lineNo, baseColumn, startPos, Math.Max(1, pos - startPos)),
                ExprTokens: condTokens,
                TrueBranchParts: trueBranch);
        }

        pos++; // skip '#'
        ImmutableArray<RawFormPart> falseBranch = ScanFormattedString(
            text, ref pos, FormTerminator.YenAt, trimOuterSpaces: true, fileId, filePath, lineNo, baseColumn, diagnostics);

        if (pos >= text.Length || text[pos] != '@')
        {
            diagnostics.ReportError(
                "ERA1117",
                "Missing closing '\\@' for conditional FORM expression.",
                MakeSpan(fileId, lineNo, baseColumn, startPos, Math.Max(1, pos - startPos)),
                filePath,
                text);
        }
        else
        {
            pos++; // skip '@' of '\@'
        }

        return new RawFormPart(
            RawFormPartKind.YenAtConditional,
            MakeSpan(fileId, lineNo, baseColumn, startPos, pos - startPos),
            ExprTokens: condTokens,
            TrueBranchParts: trueBranch,
            FalseBranchParts: falseBranch,
            HasFalseBranch: true);
    }

    private ImmutableArray<ErbToken> ExpandMacros(
        ImmutableArray<ErbToken> tokens,
        int fileId,
        string? filePath,
        int lineNo,
        DiagnosticBag diagnostics)
    {
        var list = new List<ErbToken>(tokens);
        int expansions = 0;
        int i = 0;

        while (i < list.Count)
        {
            ErbToken tok = list[i];
            if (tok.Kind != ErbTokenKind.Identifier || string.IsNullOrEmpty(tok.Text))
            {
                i++;
                continue;
            }

            ImmutableArray<ErbToken>? replacement = _macroResolver!(tok.Text);
            if (replacement is null)
            {
                i++;
                continue;
            }

            expansions++;
            if (expansions > MaxMacroExpansions)
            {
                diagnostics.ReportError(
                    "ERA1118",
                    $"Macro expansion exceeded maximum depth ({MaxMacroExpansions}); circular #DEFINE macro '{tok.Text}' detected.",
                    tok.Span,
                    filePath);
                break;
            }

            list.RemoveAt(i);
            ImmutableArray<ErbToken> repTokens = replacement.Value;
            for (int k = 0; k < repTokens.Length; k++)
            {
                // Preserve call-site span for accurate error attribution
                list.Insert(i + k, repTokens[k] with { Span = tok.Span });
            }
        }

        return [.. list];
    }

    public static string ReadIdentifierSlice(string text, ref int pos)
    {
        if (pos >= text.Length)
        {
            return string.Empty;
        }

        ReadOnlySpan<char> rem = text.AsSpan(pos);
        int stop = rem.IndexOfAny(IdentifierStopChars);
        if (stop < 0)
        {
            string rest = text[pos..];
            pos = text.Length;
            return rest;
        }

        string slice = text.Substring(pos, stop);
        pos += stop;
        return slice;
    }

    public void SkipWhitespaceAndSpecialPrefixes(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            char c = text[pos];
            if (c is ' ' or '\t' || (c == '　' && _options.SystemAllowFullSpace))
            {
                pos++;
                continue;
            }

            if (c == ';')
            {
                if (TryConsumeSpecialCommentPrefix(text, ref pos))
                {
                    continue;
                }

                pos = text.Length;
                return;
            }

            break;
        }
    }

    private bool TryConsumeSpecialCommentPrefix(string text, ref int pos)
    {
        if (!_options.EnableSpecialCommentPrefixes || pos + 2 >= text.Length || text[pos] != ';' || text[pos + 2] != ';')
        {
            return false;
        }

        char mid = text[pos + 1];
        if (mid is '!' or '^' || (mid == '#' && _isDebugMode))
        {
            pos += 3;
            return true;
        }

        return false;
    }

    private static long ScanInt64Literal(
        string text,
        ref int pos,
        int fileId,
        string? filePath,
        int lineNo,
        int baseColumn,
        DiagnosticBag diagnostics)
    {
        int literalStart = pos;
        int radix = 10;

        if (text[pos] == '0' && pos + 1 < text.Length)
        {
            char prefix = text[pos + 1];
            if (prefix is 'x' or 'X')
            {
                radix = 16;
                pos += 2;
            }
            else if (prefix is 'b' or 'B')
            {
                radix = 2;
                pos += 2;
            }
        }

        if (!TryScanRadixDigits(text, ref pos, radix, allowSign: false, out long significand, out string? digitErr))
        {
            diagnostics.ReportError(
                "ERA1119",
                digitErr ?? $"Invalid integer literal '{text[literalStart..pos]}'.",
                MakeSpan(fileId, lineNo, baseColumn, literalStart, Math.Max(1, pos - literalStart)),
                filePath,
                text);
            return 0;
        }

        if (pos < text.Length && radix == 10 && text[pos] is 'p' or 'P' or 'e' or 'E')
        {
            bool isBinaryExp = text[pos] is 'p' or 'P';
            pos++;
            if (!TryScanRadixDigits(text, ref pos, 10, allowSign: true, out long exponent, out string? expErr))
            {
                diagnostics.ReportError(
                    "ERA1120",
                    expErr ?? $"Invalid exponent in numeric literal '{text[literalStart..pos]}'.",
                    MakeSpan(fileId, lineNo, baseColumn, literalStart, pos - literalStart),
                    filePath,
                    text);
                return 0;
            }

            if (exponent != 0 && significand != 0)
            {
                if (isBinaryExp && exponent is > 0 and < 63)
                {
                    Int128 shifted = (Int128)significand << (int)exponent;
                    if (shifted > long.MaxValue || shifted < long.MinValue)
                    {
                        diagnostics.ReportError(
                            "ERA1121",
                            $"Integer literal '{text[literalStart..pos]}' overflows 64-bit signed integer.",
                            MakeSpan(fileId, lineNo, baseColumn, literalStart, pos - literalStart),
                            filePath,
                            text);
                        return 0;
                    }

                    return (long)shifted;
                }

                double scaled = significand * Math.Pow(isBinaryExp ? 2.0 : 10.0, exponent);
                if (double.IsNaN(scaled) || double.IsInfinity(scaled) || scaled > long.MaxValue || scaled < long.MinValue)
                {
                    diagnostics.ReportError(
                        "ERA1121",
                        $"Integer literal '{text[literalStart..pos]}' overflows 64-bit signed integer.",
                        MakeSpan(fileId, lineNo, baseColumn, literalStart, pos - literalStart),
                        filePath,
                        text);
                    return 0;
                }

                significand = (long)scaled;
            }
        }

        return significand;
    }

    private static bool TryScanRadixDigits(
        string text,
        ref int pos,
        int radix,
        bool allowSign,
        out long value,
        out string? errorMessage)
    {
        value = 0;
        errorMessage = null;
        int start = pos;

        if (allowSign && pos < text.Length && text[pos] is '+' or '-')
        {
            pos++;
        }

        int digitsStart = pos;
        while (pos < text.Length)
        {
            char c = text[pos];
            if (radix == 10 && char.IsAsciiDigit(c))
            {
                pos++;
            }
            else if (radix == 16 && Uri.IsHexDigit(c))
            {
                pos++;
            }
            else if (radix == 2 && char.IsAsciiDigit(c))
            {
                if (c is not ('0' or '1'))
                {
                    pos++;
                    errorMessage = $"Invalid digit '{c}' in binary literal.";
                    return false;
                }

                pos++;
            }
            else
            {
                break;
            }
        }

        if (pos == digitsStart)
        {
            errorMessage = "Expected digits in numeric literal.";
            return false;
        }

        ReadOnlySpan<char> slice = text.AsSpan(start, pos - start);
        try
        {
            if (radix == 10)
            {
                value = long.Parse(slice, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                return true;
            }

            value = Convert.ToInt64(slice.ToString(), radix);
            return true;
        }
        catch (OverflowException)
        {
            errorMessage = $"Numeric literal '{slice}' overflows 64-bit integer.";
            return false;
        }
        catch (FormatException)
        {
            errorMessage = $"Malformed numeric literal '{slice}'.";
            return false;
        }
    }

    private static string ScanPlainString(
        string text,
        ref int pos,
        char quoteChar,
        int fileId,
        string? filePath,
        int lineNo,
        int baseColumn,
        DiagnosticBag diagnostics)
    {
        var sb = new StringBuilder();
        while (pos < text.Length)
        {
            char c = text[pos];
            if (c == quoteChar)
            {
                break;
            }

            if (c == '\\')
            {
                pos++;
                if (pos >= text.Length)
                {
                    diagnostics.ReportError(
                        "ERA1114",
                        "Trailing escape backslash '\\' at end of string literal.",
                        MakeSpan(fileId, lineNo, baseColumn, pos - 1, 1),
                        filePath,
                        text);
                    break;
                }

                AppendEscapedChar(sb, text[pos]);
                pos++;
                continue;
            }

            sb.Append(c);
            pos++;
        }

        return sb.ToString();
    }

    private static string ScanPlainStringUntilCommaOrEol(string text, ref int pos)
    {
        var sb = new StringBuilder();
        while (pos < text.Length)
        {
            char c = text[pos];
            if (c == ',')
            {
                break;
            }

            if (c == '\\' && pos + 1 < text.Length)
            {
                pos++;
                AppendEscapedChar(sb, text[pos]);
                pos++;
                continue;
            }

            sb.Append(c);
            pos++;
        }

        return sb.ToString();
    }

    private static void AppendEscapedChar(StringBuilder sb, char esc)
    {
        switch (esc)
        {
            case 's': sb.Append(' '); break;
            case 'S': sb.Append('　'); break;
            case 't': sb.Append('\t'); break;
            case 'n': sb.Append('\n'); break;
            case '\n': break;
            default: sb.Append(esc); break;
        }
    }

    private static bool IsOperatorStartChar(char c) =>
        c is '+' or '-' or '*' or '/' or '%' or '=' or '!' or '<' or '>' or '|' or '&' or '^' or '~' or '?' or '#';

    private static OperatorKind ScanOperator(
        string text,
        ref int pos,
        bool allowAssignment,
        int fileId,
        string? filePath,
        int lineNo,
        int baseColumn,
        DiagnosticBag diagnostics)
    {
        int start = pos;
        char cur = text[pos++];
        char next = pos < text.Length ? text[pos] : '\0';

        switch (cur)
        {
            case '+':
                if (next == '+') { pos++; return OperatorKind.Increment; }
                if (allowAssignment && next == '=') { pos++; return OperatorKind.AddAssign; }
                return OperatorKind.Plus;

            case '-':
                if (next == '-') { pos++; return OperatorKind.Decrement; }
                if (allowAssignment && next == '=') { pos++; return OperatorKind.SubAssign; }
                return OperatorKind.Minus;

            case '*':
                if (allowAssignment && next == '=') { pos++; return OperatorKind.MulAssign; }
                return OperatorKind.Multiply;

            case '/':
                if (allowAssignment && next == '=') { pos++; return OperatorKind.DivAssign; }
                return OperatorKind.Divide;

            case '%':
                if (allowAssignment && next == '=') { pos++; return OperatorKind.ModAssign; }
                return OperatorKind.Modulo;

            case '=':
                if (next == '=') { pos++; return OperatorKind.Equal; }
                if (allowAssignment) { return OperatorKind.Assign; }
                diagnostics.ReportError("ERA1122", "Unexpected '=' in expression (did you mean '=='?).", MakeSpan(fileId, lineNo, baseColumn, start, 1), filePath, text);
                return OperatorKind.None;

            case '!':
                if (next == '=') { pos++; return OperatorKind.NotEqual; }
                if (next == '&') { pos++; return OperatorKind.LogicalNand; }
                if (next == '|') { pos++; return OperatorKind.LogicalNor; }
                return OperatorKind.LogicalNot;

            case '<':
                if (next == '=') { pos++; return OperatorKind.LessEqual; }
                if (next == '<')
                {
                    pos++;
                    if (allowAssignment && pos < text.Length && text[pos] == '=')
                    {
                        pos++;
                        return OperatorKind.ShiftLeftAssign;
                    }

                    return OperatorKind.ShiftLeft;
                }

                return OperatorKind.Less;

            case '>':
                if (next == '=') { pos++; return OperatorKind.GreaterEqual; }
                if (next == '>')
                {
                    pos++;
                    if (allowAssignment && pos < text.Length && text[pos] == '=')
                    {
                        pos++;
                        return OperatorKind.ShiftRightAssign;
                    }

                    return OperatorKind.ShiftRight;
                }

                return OperatorKind.Greater;

            case '|':
                if (next == '|') { pos++; return OperatorKind.LogicalOr; }
                if (allowAssignment && next == '=') { pos++; return OperatorKind.BitOrAssign; }
                return OperatorKind.BitOr;

            case '&':
                if (next == '&') { pos++; return OperatorKind.LogicalAnd; }
                if (allowAssignment && next == '=') { pos++; return OperatorKind.BitAndAssign; }
                return OperatorKind.BitAnd;

            case '^':
                if (next == '^') { pos++; return OperatorKind.LogicalXor; }
                if (allowAssignment && next == '=') { pos++; return OperatorKind.BitXorAssign; }
                return OperatorKind.BitXor;

            case '~':
                return OperatorKind.BitNot;

            case '?':
                return OperatorKind.TernaryQuestion;

            case '#':
                return OperatorKind.TernaryHash;

            default:
                diagnostics.ReportError("ERA1123", $"Unrecognized operator '{cur}'.", MakeSpan(fileId, lineNo, baseColumn, start, 1), filePath, text);
                return OperatorKind.None;
        }
    }

    private static OperatorKind AdvanceBy(ref int pos, int count, OperatorKind op)
    {
        pos += count;
        return op;
    }

    private static SourceSpan MakeSpan(int fileId, int lineNo, int baseColumn, int offset, int length)
    {
        int startCol = Math.Max(1, baseColumn + offset);
        int endCol = Math.Max(startCol, startCol + Math.Max(1, length) - 1);
        return new SourceSpan(fileId, lineNo, startCol, lineNo, endCol);
    }
}

public readonly record struct AssignmentScanResult(
    bool IsValid,
    OperatorKind Operator,
    SourceSpan Span);
