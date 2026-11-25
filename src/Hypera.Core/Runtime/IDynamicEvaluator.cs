using System.Globalization;
using System.Text;
using Hypera.Bytecode;

namespace Hypera.Runtime;

/// <summary>
/// Pluggable runtime dynamic expression and <c>FORM</c>-string evaluator hook for <see cref="EmueraEngine"/>.
/// Used by <c>DynamicEval*</c> opcodes (<c>PRINTFORMS</c>, <c>STRFORM</c>, <c>RETURNFORM</c>, <c>GETVAR</c>, <c>GETVARS</c>, <c>SETVAR</c>, <c>EXISTVAR</c>)
/// with full visibility into both global variables and the active <see cref="CallFrame"/>'s local/dynamic/ref slots.
/// </summary>
public interface IDynamicEvaluator
{
    long EvaluateIntExpression(EmueraEngine engine, CallFrame? activeFrame, string expression);

    string EvaluateStrExpression(EmueraEngine engine, CallFrame? activeFrame, string expression);

    string EvaluateStrForm(EmueraEngine engine, CallFrame? activeFrame, string formTemplate);

    long GetVariableInt(EmueraEngine engine, CallFrame? activeFrame, string variableExpression);

    string GetVariableStr(EmueraEngine engine, CallFrame? activeFrame, string variableExpression);

    void SetVariableInt(EmueraEngine engine, CallFrame? activeFrame, string variableExpression, long value);

    void SetVariableStr(EmueraEngine engine, CallFrame? activeFrame, string variableExpression, string value);

    long VariableExists(EmueraEngine engine, CallFrame? activeFrame, string variableName);
}

/// <summary>
/// Built-in zero-dependency dynamic evaluator supporting <c>GETVAR</c>/<c>GETVARS</c>/<c>SETVAR</c>/<c>EXISTVAR</c>,
/// <c>FORM</c> string interpolation (<c>%str%</c>, <c>{int,width,LEFT|RIGHT}</c>, <c>\@ cond ? a # b \@</c>),
/// and arithmetic/comparison/logical expressions across global, character, function-static, and frame-local/ref variables.
/// Can be overridden via <see cref="EmueraEngine.DynamicEvaluator"/> by <c>RuntimeDynamicCompiler</c>.
/// </summary>
public sealed class DefaultRuntimeDynamicEvaluator : IDynamicEvaluator
{
    public static DefaultRuntimeDynamicEvaluator Instance { get; } = new();

    public long EvaluateIntExpression(EmueraEngine engine, CallFrame? activeFrame, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return 0L;
        }

        var parser = new MiniExprParser(engine, activeFrame, expression);
        return parser.ParseTernaryInt();
    }

    public string EvaluateStrExpression(EmueraEngine engine, CallFrame? activeFrame, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> trimmed = expression.AsSpan().Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            return EvaluateStrForm(engine, activeFrame, trimmed[1..^1].ToString());
        }

        var parser = new MiniExprParser(engine, activeFrame, expression);
        return parser.ParseStringExpr();
    }

    public string EvaluateStrForm(EmueraEngine engine, CallFrame? activeFrame, string formTemplate)
    {
        if (string.IsNullOrEmpty(formTemplate))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(formTemplate.Length + 16);
        int i = 0;
        while (i < formTemplate.Length)
        {
            char c = formTemplate[i];
            if (c == '\\' && i + 1 < formTemplate.Length)
            {
                char next = formTemplate[i + 1];
                if (next == '@')
                {
                    int endAt = FindMatchingFormTernaryEnd(formTemplate, i + 2, out int qPos, out int hashPos);
                    if (endAt > i)
                    {
                        string condText = formTemplate.Substring(i + 2, qPos - (i + 2));
                        string trueForm = hashPos >= 0
                            ? formTemplate.Substring(qPos + 1, hashPos - (qPos + 1))
                            : formTemplate.Substring(qPos + 1, endAt - 2 - (qPos + 1));
                        string falseForm = hashPos >= 0
                            ? formTemplate.Substring(hashPos + 1, endAt - 2 - (hashPos + 1))
                            : string.Empty;

                        long cond = EvaluateIntExpression(engine, activeFrame, condText);
                        sb.Append(EvaluateStrForm(engine, activeFrame, cond != 0 ? trueForm.TrimStart() : falseForm.TrimStart()));
                        i = endAt;
                        continue;
                    }
                }

                sb.Append(next);
                i += 2;
                continue;
            }

            if (c is '%' or '{')
            {
                // Triple symbol handling (%%% or {{{) when !SystemIgnoreTripleSymbol
                if (!engine.Module.CompilerOptions.SystemIgnoreTripleSymbol &&
                    i + 2 < formTemplate.Length &&
                    formTemplate[i + 1] == c &&
                    formTemplate[i + 2] == c)
                {
                    sb.Append(c, 3);
                    i += 3;
                    continue;
                }

                char closing = c == '%' ? '%' : '}';
                int closeIdx = FindClosingDelimiter(formTemplate, i + 1, closing);
                if (closeIdx > i)
                {
                    string inner = formTemplate.Substring(i + 1, closeIdx - (i + 1));
                    AppendFormattedSegment(engine, activeFrame, sb, inner, isStrSegment: c == '%');
                    i = closeIdx + 1;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    public long GetVariableInt(EmueraEngine engine, CallFrame? activeFrame, string variableExpression)
    {
        ParseVariableDesignator(engine, activeFrame, variableExpression, out string varName, out string? funcScope, out Span<long> indices, out int indexCount);
        ReadOnlySpan<long> idxSpan = indices[..indexCount];

        if (funcScope is null && activeFrame is not null && activeFrame.TryGetLocal(varName, engine.Module.CompilerOptions.IgnoreCase, out FrameLocalStorage? local) && local is not null)
        {
            if (local.ValueKind != VariableValueKind.Int)
            {
                throw new InvalidOperationException($"Local variable '{varName}' is not an integer variable.");
            }

            int flat = local.ComputeFlatIndex(idxSpan);
            return local.IntArray![flat];
        }

        VariableStore store = engine.RequireVariableStore();
        string lookupName = ResolveLookupVariableName(store, activeFrame, varName, funcScope);
        if (!store.TryResolveSlot(lookupName, out int slotId))
        {
            throw new KeyNotFoundException($"Unknown integer variable '{variableExpression}'.");
        }

        return store.GetInt(slotId, idxSpan);
    }

    public string GetVariableStr(EmueraEngine engine, CallFrame? activeFrame, string variableExpression)
    {
        ParseVariableDesignator(engine, activeFrame, variableExpression, out string varName, out string? funcScope, out Span<long> indices, out int indexCount);
        ReadOnlySpan<long> idxSpan = indices[..indexCount];

        if (funcScope is null && activeFrame is not null && activeFrame.TryGetLocal(varName, engine.Module.CompilerOptions.IgnoreCase, out FrameLocalStorage? local) && local is not null)
        {
            if (local.ValueKind != VariableValueKind.Str)
            {
                throw new InvalidOperationException($"Local variable '{varName}' is not a string variable.");
            }

            int flat = local.ComputeFlatIndex(idxSpan);
            return local.StrArray![flat];
        }

        VariableStore store = engine.RequireVariableStore();
        string lookupName = ResolveLookupVariableName(store, activeFrame, varName, funcScope);
        if (!store.TryResolveSlot(lookupName, out int slotId))
        {
            throw new KeyNotFoundException($"Unknown string variable '{variableExpression}'.");
        }

        return store.GetStr(slotId, idxSpan);
    }

    public void SetVariableInt(EmueraEngine engine, CallFrame? activeFrame, string variableExpression, long value)
    {
        ParseVariableDesignator(engine, activeFrame, variableExpression, out string varName, out string? funcScope, out Span<long> indices, out int indexCount);
        ReadOnlySpan<long> idxSpan = indices[..indexCount];

        if (funcScope is null && activeFrame is not null && activeFrame.TryGetLocal(varName, engine.Module.CompilerOptions.IgnoreCase, out FrameLocalStorage? local) && local is not null)
        {
            if (local.ValueKind != VariableValueKind.Int)
            {
                throw new InvalidOperationException($"Local variable '{varName}' is not an integer variable.");
            }

            int flat = local.ComputeFlatIndex(idxSpan);
            local.IntArray![flat] = value;
            return;
        }

        VariableStore store = engine.RequireVariableStore();
        string lookupName = ResolveLookupVariableName(store, activeFrame, varName, funcScope);
        if (!store.TryResolveSlot(lookupName, out int slotId))
        {
            throw new KeyNotFoundException($"Unknown integer variable '{variableExpression}'.");
        }

        store.SetInt(slotId, value, idxSpan);
    }

    public void SetVariableStr(EmueraEngine engine, CallFrame? activeFrame, string variableExpression, string value)
    {
        ParseVariableDesignator(engine, activeFrame, variableExpression, out string varName, out string? funcScope, out Span<long> indices, out int indexCount);
        ReadOnlySpan<long> idxSpan = indices[..indexCount];

        if (funcScope is null && activeFrame is not null && activeFrame.TryGetLocal(varName, engine.Module.CompilerOptions.IgnoreCase, out FrameLocalStorage? local) && local is not null)
        {
            if (local.ValueKind != VariableValueKind.Str)
            {
                throw new InvalidOperationException($"Local variable '{varName}' is not a string variable.");
            }

            int flat = local.ComputeFlatIndex(idxSpan);
            local.StrArray![flat] = value ?? string.Empty;
            return;
        }

        VariableStore store = engine.RequireVariableStore();
        string lookupName = ResolveLookupVariableName(store, activeFrame, varName, funcScope);
        if (!store.TryResolveSlot(lookupName, out int slotId))
        {
            throw new KeyNotFoundException($"Unknown string variable '{variableExpression}'.");
        }

        store.SetStr(slotId, value ?? string.Empty, idxSpan);
    }

    public long VariableExists(EmueraEngine engine, CallFrame? activeFrame, string variableName)
    {
        if (string.IsNullOrWhiteSpace(variableName))
        {
            return 0L;
        }

        string trimmed = variableName.Trim();
        int colon = trimmed.IndexOf(':');
        if (colon > 0)
        {
            trimmed = trimmed[..colon].Trim();
        }

        if (activeFrame is not null && activeFrame.TryGetLocal(trimmed, engine.Module.CompilerOptions.IgnoreCase, out FrameLocalStorage? local) && local is not null)
        {
            return local.ValueKind == VariableValueKind.Str ? 2L : 1L;
        }

        VariableStore store = engine.RequireVariableStore();
        string lookup = ResolveLookupVariableName(store, activeFrame, trimmed, null);
        if (store.TryResolveSlot(lookup, out int slotId))
        {
            VariableSlotStorage slot = store.GetSlotOrThrow(slotId);
            return slot.ValueKind == VariableValueKind.Str ? 2L : 1L;
        }

        return 0L;
    }

    // -------------------------------------------------------------------------
    // Formatting & Mini Expression Parser Helpers
    // -------------------------------------------------------------------------

    private void AppendFormattedSegment(
        EmueraEngine engine,
        CallFrame? activeFrame,
        StringBuilder sb,
        string rawSegment,
        bool isStrSegment)
    {
        SplitTopLevelCommas(rawSegment, out string exprPart, out string? widthPart, out string? alignPart);
        string rendered;
        if (isStrSegment)
        {
            rendered = TryEvaluateStringOrVariable(engine, activeFrame, exprPart);
        }
        else
        {
            long val = EvaluateIntExpression(engine, activeFrame, exprPart);
            rendered = val.ToString(CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(widthPart))
        {
            sb.Append(rendered);
            return;
        }

        long targetWidth = EvaluateIntExpression(engine, activeFrame, widthPart);
        if (targetWidth <= 0)
        {
            sb.Append(rendered);
            return;
        }

        bool leftAlign = alignPart is not null && alignPart.Trim().Equals("LEFT", StringComparison.OrdinalIgnoreCase);
        int currentByteWidth = EastAsianStringOps.GetStrlenS(rendered, engine.Options.LanguageCodePage);
        int padSpaces = (int)targetWidth - currentByteWidth;
        if (padSpaces <= 0)
        {
            sb.Append(rendered);
            return;
        }

        if (leftAlign)
        {
            sb.Append(rendered).Append(' ', padSpaces);
        }
        else
        {
            sb.Append(' ', padSpaces).Append(rendered);
        }
    }

    private string TryEvaluateStringOrVariable(EmueraEngine engine, CallFrame? activeFrame, string expr)
    {
        string trimmed = expr.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.Contains('+') || (trimmed.StartsWith('"') && trimmed.EndsWith('"')))
        {
            return EvaluateStrExpression(engine, activeFrame, trimmed);
        }

        return GetVariableStr(engine, activeFrame, trimmed);
    }

    private void ParseVariableDesignator(
        EmueraEngine engine,
        CallFrame? activeFrame,
        string rawDesignator,
        out string varName,
        out string? funcScope,
        out Span<long> indicesOut,
        out int indexCount)
    {
        indicesOut = new long[3];
        indexCount = 0;
        funcScope = null;

        string s = (rawDesignator ?? string.Empty).Trim();
        var parts = SplitTopLevelColons(s);
        string head = parts[0].Trim();

        int atPos = head.IndexOf('@');
        if (atPos > 0)
        {
            varName = head[..atPos].Trim();
            funcScope = head[(atPos + 1)..].Trim();
        }
        else
        {
            varName = head;
        }

        VariableStore store = engine.RequireVariableStore();
        for (int p = 1; p < parts.Count && indexCount < 3; p++)
        {
            string sub = parts[p].Trim();
            if (sub.Length >= 2 && sub[0] == '(' && sub[^1] == ')')
            {
                sub = sub[1..^1].Trim();
            }

            if (long.TryParse(sub, NumberStyles.Integer, CultureInfo.InvariantCulture, out long literalIdx))
            {
                indicesOut[indexCount++] = literalIdx;
            }
            else if (store.TryResolveKeywordIndex(varName, p, sub, out long kwIdx))
            {
                indicesOut[indexCount++] = kwIdx;
            }
            else
            {
                indicesOut[indexCount++] = EvaluateIntExpression(engine, activeFrame, sub);
            }
        }
    }

    internal static string ResolveLookupVariableName(
        VariableStore store,
        CallFrame? activeFrame,
        string varName,
        string? explicitFuncScope)
    {
        if (!string.IsNullOrEmpty(explicitFuncScope))
        {
            string candidate = $"{varName}@{explicitFuncScope}";
            if (store.TryResolveSlot(candidate, out _))
            {
                return candidate;
            }
        }

        if (activeFrame is not null)
        {
            // Check function-private static #DIM (`FUNC::VAR` or `VAR@FUNC`) and function-static `LOCAL@FUNC`
            string scopedAt = $"{varName}@{activeFrame.FunctionName}";
            if (_HasDirectName(store, scopedAt))
            {
                return scopedAt;
            }

            string scopedColons = $"{activeFrame.FunctionName}::{varName}";
            if (_HasDirectName(store, scopedColons))
            {
                return scopedColons;
            }

            if (varName.Equals("LOCAL", StringComparison.OrdinalIgnoreCase) ||
                varName.Equals("LOCALS", StringComparison.OrdinalIgnoreCase) ||
                varName.Equals("ARG", StringComparison.OrdinalIgnoreCase) ||
                varName.Equals("ARGS", StringComparison.OrdinalIgnoreCase))
            {
                return scopedAt;
            }
        }

        return varName;
    }

    private static bool _HasDirectName(VariableStore store, string candidate)
    {
        // Check existing registered names before auto-creating
        foreach (string existing in store.VariableNames)
        {
            if (string.Equals(existing, candidate, store.CompilerOptions.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> SplitTopLevelColons(string s)
    {
        var parts = new List<string>(4);
        int depth = 0;
        bool inQuotes = false;
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"' && (i == 0 || s[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes)
            {
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ':' && depth == 0)
                {
                    parts.Add(s[start..i]);
                    start = i + 1;
                }
            }
        }

        parts.Add(s[start..]);
        return parts;
    }

    private static void SplitTopLevelCommas(string s, out string first, out string? second, out string? third)
    {
        second = null;
        third = null;
        int depth = 0;
        bool inQuotes = false;
        int firstComma = -1;
        int secondComma = -1;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"' && (i == 0 || s[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes)
            {
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    if (firstComma < 0)
                    {
                        firstComma = i;
                    }
                    else if (secondComma < 0)
                    {
                        secondComma = i;
                        break;
                    }
                }
            }
        }

        if (firstComma < 0)
        {
            first = s;
        }
        else if (secondComma < 0)
        {
            first = s[..firstComma];
            second = s[(firstComma + 1)..];
        }
        else
        {
            first = s[..firstComma];
            second = s.Substring(firstComma + 1, secondComma - (firstComma + 1));
            third = s[(secondComma + 1)..];
        }
    }

    private static int FindClosingDelimiter(string s, int start, char delimiter)
    {
        int parenDepth = 0;
        bool inQuotes = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes)
            {
                if (c == '(') parenDepth++;
                else if (c == ')') parenDepth--;
                else if (c == delimiter && parenDepth <= 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int FindMatchingFormTernaryEnd(string s, int start, out int questionPos, out int hashPos)
    {
        questionPos = -1;
        hashPos = -1;
        int nested = 0;
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                if (s[i + 1] == '@')
                {
                    if (questionPos < 0)
                    {
                        nested++;
                    }
                    else if (nested > 0)
                    {
                        nested--;
                    }
                    else
                    {
                        return i + 2;
                    }

                    i++;
                    continue;
                }

                i++;
                continue;
            }

            if (nested == 0)
            {
                if (s[i] == '?' && questionPos < 0)
                {
                    questionPos = i;
                }
                else if (s[i] == '#' && questionPos >= 0 && hashPos < 0)
                {
                    hashPos = i;
                }
            }
        }

        return -1;
    }

    private ref struct MiniExprParser
    {
        private readonly EmueraEngine _engine;
        private readonly CallFrame? _frame;
        private readonly string _src;
        private int _pos;

        public MiniExprParser(EmueraEngine engine, CallFrame? frame, string src)
        {
            _engine = engine;
            _frame = frame;
            _src = src;
            _pos = 0;
        }

        public long ParseTernaryInt()
        {
            long cond = ParseLogicalOr();
            SkipWs();
            if (_pos < _src.Length && _src[_pos] == '?')
            {
                _pos++;
                long whenTrue = ParseTernaryInt();
                SkipWs();
                long whenFalse = 0;
                if (_pos < _src.Length && _src[_pos] == '#')
                {
                    _pos++;
                    whenFalse = ParseTernaryInt();
                }

                return cond != 0 ? whenTrue : whenFalse;
            }

            return cond;
        }

        public string ParseStringExpr()
        {
            var sb = new StringBuilder();
            while (true)
            {
                SkipWs();
                sb.Append(ParseStringAtom());
                SkipWs();
                if (_pos < _src.Length && _src[_pos] == '+')
                {
                    _pos++;
                    continue;
                }

                break;
            }

            return sb.ToString();
        }

        private string ParseStringAtom()
        {
            SkipWs();
            if (_pos < _src.Length && _src[_pos] == '"')
            {
                _pos++;
                int start = _pos;
                while (_pos < _src.Length && !(_src[_pos] == '"' && _src[_pos - 1] != '\\'))
                {
                    _pos++;
                }

                string inner = _src[start..Math.Min(_pos, _src.Length)];
                if (_pos < _src.Length) _pos++;
                return Instance.EvaluateStrForm(_engine, _frame, inner);
            }

            string designator = ReadVariableDesignator();
            return Instance.GetVariableStr(_engine, _frame, designator);
        }

        private long ParseLogicalOr()
        {
            long left = ParseLogicalAnd();
            while (true)
            {
                SkipWs();
                if (Match("||"))
                {
                    long right = ParseLogicalAnd();
                    left = (left != 0 || right != 0) ? 1L : 0L;
                }
                else if (Match("^^"))
                {
                    long right = ParseLogicalAnd();
                    left = ((left != 0) ^ (right != 0)) ? 1L : 0L;
                }
                else
                {
                    return left;
                }
            }
        }

        private long ParseLogicalAnd()
        {
            long left = ParseBitwiseOr();
            while (true)
            {
                SkipWs();
                if (Match("&&"))
                {
                    long right = ParseBitwiseOr();
                    left = (left != 0 && right != 0) ? 1L : 0L;
                }
                else
                {
                    return left;
                }
            }
        }

        private long ParseBitwiseOr()
        {
            long left = ParseBitwiseXor();
            while (true)
            {
                SkipWs();
                if (_pos < _src.Length && _src[_pos] == '|' && (_pos + 1 >= _src.Length || _src[_pos + 1] != '|'))
                {
                    _pos++;
                    left |= ParseBitwiseXor();
                }
                else
                {
                    return left;
                }
            }
        }

        private long ParseBitwiseXor()
        {
            long left = ParseBitwiseAnd();
            while (true)
            {
                SkipWs();
                if (_pos < _src.Length && _src[_pos] == '^' && (_pos + 1 >= _src.Length || _src[_pos + 1] != '^'))
                {
                    _pos++;
                    left ^= ParseBitwiseAnd();
                }
                else
                {
                    return left;
                }
            }
        }

        private long ParseBitwiseAnd()
        {
            long left = ParseEquality();
            while (true)
            {
                SkipWs();
                if (_pos < _src.Length && _src[_pos] == '&' && (_pos + 1 >= _src.Length || _src[_pos + 1] != '&'))
                {
                    _pos++;
                    left &= ParseEquality();
                }
                else
                {
                    return left;
                }
            }
        }

        private long ParseEquality()
        {
            SkipWs();
            if (_pos < _src.Length && _src[_pos] == '"')
            {
                string sLeft = ParseStringAtom();
                SkipWs();
                if (Match("=="))
                {
                    string sRight = ParseStringAtom();
                    return string.Equals(sLeft, sRight, StringComparison.Ordinal) ? 1L : 0L;
                }

                if (Match("!="))
                {
                    string sRight = ParseStringAtom();
                    return !string.Equals(sLeft, sRight, StringComparison.Ordinal) ? 1L : 0L;
                }

                return sLeft.Length > 0 ? 1L : 0L;
            }

            long left = ParseRelational();
            while (true)
            {
                SkipWs();
                if (Match("==")) left = left == ParseRelational() ? 1L : 0L;
                else if (Match("!=")) left = left != ParseRelational() ? 1L : 0L;
                else return left;
            }
        }

        private long ParseRelational()
        {
            long left = ParseShift();
            while (true)
            {
                SkipWs();
                if (Match("<=")) left = left <= ParseShift() ? 1L : 0L;
                else if (Match(">=")) left = left >= ParseShift() ? 1L : 0L;
                else if (_pos < _src.Length && _src[_pos] == '<' && (_pos + 1 >= _src.Length || _src[_pos + 1] != '<'))
                {
                    _pos++;
                    left = left < ParseShift() ? 1L : 0L;
                }
                else if (_pos < _src.Length && _src[_pos] == '>' && (_pos + 1 >= _src.Length || _src[_pos + 1] != '>'))
                {
                    _pos++;
                    left = left > ParseShift() ? 1L : 0L;
                }
                else
                {
                    return left;
                }
            }
        }

        private long ParseShift()
        {
            long left = ParseAdditive();
            while (true)
            {
                SkipWs();
                if (Match("<<")) left <<= (int)ParseAdditive();
                else if (Match(">>")) left >>= (int)ParseAdditive();
                else return left;
            }
        }

        private long ParseAdditive()
        {
            long left = ParseMultiplicative();
            while (true)
            {
                SkipWs();
                if (_pos < _src.Length && _src[_pos] == '+')
                {
                    _pos++;
                    unchecked { left += ParseMultiplicative(); }
                }
                else if (_pos < _src.Length && _src[_pos] == '-')
                {
                    _pos++;
                    unchecked { left -= ParseMultiplicative(); }
                }
                else
                {
                    return left;
                }
            }
        }

        private long ParseMultiplicative()
        {
            long left = ParseUnary();
            while (true)
            {
                SkipWs();
                if (_pos < _src.Length && _src[_pos] == '*')
                {
                    _pos++;
                    unchecked { left *= ParseUnary(); }
                }
                else if (_pos < _src.Length && _src[_pos] == '/')
                {
                    _pos++;
                    long div = ParseUnary();
                    if (div == 0) throw new DivideByZeroException("Division by zero in dynamic expression.");
                    left /= div;
                }
                else if (_pos < _src.Length && _src[_pos] == '%')
                {
                    _pos++;
                    long mod = ParseUnary();
                    if (mod == 0) throw new DivideByZeroException("Modulo by zero in dynamic expression.");
                    left %= mod;
                }
                else
                {
                    return left;
                }
            }
        }

        private long ParseUnary()
        {
            SkipWs();
            if (_pos < _src.Length)
            {
                if (_src[_pos] == '+') { _pos++; return ParseUnary(); }
                if (_src[_pos] == '-') { _pos++; return unchecked(-ParseUnary()); }
                if (_src[_pos] == '!' && (_pos + 1 >= _src.Length || _src[_pos + 1] != '=')) { _pos++; return ParseUnary() == 0 ? 1L : 0L; }
                if (_src[_pos] == '~') { _pos++; return ~ParseUnary(); }
            }

            return ParsePrimary();
        }

        private long ParsePrimary()
        {
            SkipWs();
            if (_pos < _src.Length && _src[_pos] == '(')
            {
                _pos++;
                long inner = ParseTernaryInt();
                SkipWs();
                if (_pos < _src.Length && _src[_pos] == ')') _pos++;
                return inner;
            }

            if (_pos < _src.Length && char.IsAsciiDigit(_src[_pos]))
            {
                int start = _pos;
                if (_pos + 2 <= _src.Length && _src[_pos] == '0' && (_src[_pos + 1] is 'x' or 'X'))
                {
                    _pos += 2;
                    int hexStart = _pos;
                    while (_pos < _src.Length && Uri.IsHexDigit(_src[_pos])) _pos++;
                    return long.Parse(_src.AsSpan(hexStart, _pos - hexStart), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }

                while (_pos < _src.Length && char.IsAsciiDigit(_src[_pos])) _pos++;
                long num = long.Parse(_src.AsSpan(start, _pos - start), CultureInfo.InvariantCulture);
                if (_pos < _src.Length && (_src[_pos] is 'p' or 'P'))
                {
                    _pos++;
                    long shift = ParseUnary();
                    return num << (int)shift;
                }

                return num;
            }

            string designator = ReadVariableDesignator();
            if (designator.Length == 0)
            {
                return 0L;
            }

            if (Instance.VariableExists(_engine, _frame, designator) == 2L)
            {
                string lhs = Instance.GetVariableStr(_engine, _frame, designator);
                SkipWs();
                if (Match("=="))
                {
                    string rhs = ParseStringAtom();
                    return string.Equals(lhs, rhs, StringComparison.Ordinal) ? 1L : 0L;
                }

                if (Match("!="))
                {
                    string rhs = ParseStringAtom();
                    return !string.Equals(lhs, rhs, StringComparison.Ordinal) ? 1L : 0L;
                }

                return lhs.Length;
            }

            return Instance.GetVariableInt(_engine, _frame, designator);
        }

        private string ReadVariableDesignator()
        {
            SkipWs();
            int start = _pos;
            int parenDepth = 0;
            while (_pos < _src.Length)
            {
                char c = _src[_pos];
                if (c == '(')
                {
                    parenDepth++;
                    _pos++;
                }
                else if (c == ')')
                {
                    if (parenDepth == 0) break;
                    parenDepth--;
                    _pos++;
                }
                else if (parenDepth == 0 && (char.IsWhiteSpace(c) || c is '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^' or '=' or '!' or '<' or '>' or '?' or '#' or ','))
                {
                    break;
                }
                else
                {
                    _pos++;
                }
            }

            return _src[start.._pos].Trim();
        }

        private void SkipWs()
        {
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos]))
            {
                _pos++;
            }
        }

        private bool Match(string token)
        {
            if (_pos + token.Length <= _src.Length && _src.AsSpan(_pos, token.Length).SequenceEqual(token))
            {
                _pos += token.Length;
                return true;
            }

            return false;
        }
    }
}
