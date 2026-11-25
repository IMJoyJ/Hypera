using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Hypera.Configuration;
using Hypera.Diagnostics;
using Hypera.VirtualFs;

namespace Hypera.Compiler;

/// <summary>
/// Per-file preprocessor and file enumerator over <see cref="IVirtualFileSystem"/>.
/// Handles <c>_Rename.csv</c> <c>[[...]]</c> substitution, <c>;!;</c>/<c>;^;</c>/<c>;#;</c> prefixes,
/// <c>{ ... }</c> multi-line continuations, and <c>[SKIPSTART]</c>/<c>[IF]</c>/<c>[IF_DEBUG]</c> blocks
/// while preserving exact <c>(FileId, LineNo)</c> locations for <see cref="SourceMapBuilder"/>.
/// </summary>
public sealed partial class ErbPreprocessor
{
    private readonly CompilerOptions _options;
    private readonly bool _isDebugMode;
    private readonly IReadOnlyDictionary<string, string>? _renameDictionary;

    public ErbPreprocessor(
        CompilerOptions options,
        bool isDebugMode = false,
        IReadOnlyDictionary<string, string>? renameDictionary = null)
    {
        _options = options ?? CompilerOptions.Default;
        _isDebugMode = isDebugMode;
        _renameDictionary = renameDictionary;
    }

    [GeneratedRegex(@"\[\[.*?\]\]", RegexOptions.CultureInvariant)]
    private static partial Regex RenamePattern();

    /// <summary>
    /// Enumerates <c>.ERH</c> or <c>.ERB</c> virtual paths honoring <c>SearchSubdirectory</c>,
    /// <c>SortWithFilename</c>, and EM+EE <c>*#*</c> priority directories.
    /// </summary>
    public ImmutableArray<string> DiscoverScriptFiles(
        IVirtualFileSystem vfs,
        string rootSubDirectory,
        string extensionWithDot)
    {
        ArgumentNullException.ThrowIfNull(vfs);

        string targetDir = FindMatchingDirectory(vfs, rootSubDirectory);
        if (targetDir.Length == 0 && !string.IsNullOrEmpty(rootSubDirectory))
        {
            return [];
        }

        var allMatching = vfs.EnumerateFiles(targetDir, "*" + extensionWithDot, recursive: _options.SearchSubdirectory)
            .Where(p => VfsPathNormalizer.GetExtension(p).Equals(extensionWithDot, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (allMatching.Count <= 1)
        {
            return [.. allMatching];
        }

        bool isErb = extensionWithDot.Equals(".ERB", StringComparison.OrdinalIgnoreCase);

        // Upstream EE rule: subdirectories containing '#' in their name load before normal directories.
        var priorityFiles = new List<string>();
        var normalFiles = new List<string>();

        foreach (string path in allMatching)
        {
            if (isErb && IsInSharpPriorityDirectory(path, targetDir))
            {
                priorityFiles.Add(path);
            }
            else
            {
                normalFiles.Add(path);
            }
        }

        SortScriptPaths(priorityFiles, _options.SortWithFilename);
        SortScriptPaths(normalFiles, _options.SortWithFilename);

        var ordered = new List<string>(allMatching.Count);
        ordered.AddRange(priorityFiles);
        ordered.AddRange(normalFiles);
        return [.. ordered];
    }

    /// <summary>
    /// Preprocesses all lines of a single script file, resetting preprocessor block state per file.
    /// </summary>
    public ImmutableArray<PreprocessedLine> PreprocessFile(
        int fileId,
        string virtualPath,
        string sourceText,
        DiagnosticBag diagnostics,
        Func<string, bool>? isMacroDefined = null,
        bool isErhFile = false)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string[] physicalLines = SplitPhysicalLines(sourceText);
        var output = new List<PreprocessedLine>(physicalLines.Length);
        var ppState = new FilePreprocessorState(_isDebugMode, isMacroDefined);
        string continuationSeparator = UnquoteContinuationSeparator(_options.ReplaceContinuationBR);
        bool applyRename = isErhFile
            ? _renameDictionary is { Count: > 0 }
            : _options.UseRenameFile && _renameDictionary is { Count: > 0 };

        int index = 0;
        while (index < physicalLines.Length)
        {
            int lineNo = index + 1;
            string rawPhysical = physicalLines[index++];
            if (rawPhysical.Length == 0)
            {
                continue;
            }

            string working = applyRename ? ApplyRename(rawPhysical) : rawPhysical;
            int pos = SkipLeadingWhitespaceAndCommentPrefixes(working, 0);
            if (pos >= working.Length)
            {
                continue;
            }

            // Preprocessor directive: [SKIPSTART], [SKIPEND], [IF ...], [ELSEIF ...], [ELSE], [ENDIF], [IF_DEBUG], [IF_NDEBUG]
            // Note: [[...]] is a rename token, NOT a preprocessor directive.
            if (!isErhFile && working[pos] == '[' && (pos + 1 >= working.Length || working[pos + 1] != '['))
            {
                ProcessDirectiveLine(fileId, virtualPath, lineNo, working, pos, rawPhysical, ppState, diagnostics);
                continue;
            }

            if (ppState.IsDisabled)
            {
                continue;
            }

            // Unexpected '}' outside continuation block
            if (working[pos] == '}')
            {
                diagnostics.ReportError(
                    "ERA1001",
                    "Unexpected line-continuation closing brace '}' without matching '{'.",
                    SourceSpan.SingleLine(fileId, lineNo, pos + 1, pos + 1),
                    virtualPath,
                    rawPhysical);
                continue;
            }

            // Multi-line '{ ... }' continuation block
            if (working[pos] == '{')
            {
                int afterBrace = SkipLeadingWhitespaceAndCommentPrefixes(working, pos + 1);
                if (afterBrace < working.Length)
                {
                    diagnostics.ReportError(
                        "ERA1002",
                        "Unexpected characters after line-continuation opening brace '{'.",
                        SourceSpan.SingleLine(fileId, lineNo, afterBrace + 1, working.Length),
                        virtualPath,
                        rawPhysical);
                    continue;
                }

                var builder = new StringBuilder();
                bool closed = false;
                int closeLineNo = lineNo;

                while (index < physicalLines.Length)
                {
                    int innerLineNo = index + 1;
                    string innerRaw = physicalLines[index++];
                    string innerWork = applyRename ? ApplyRename(innerRaw) : innerRaw;

                    ReadOnlySpan<char> trimmedStart = TrimLeadingSpaces(innerWork.AsSpan());
                    if (trimmedStart.Length > 0)
                    {
                        if (trimmedStart[0] == '}')
                        {
                            int closeIdx = innerWork.Length - trimmedStart.Length;
                            int afterClose = SkipLeadingWhitespaceAndCommentPrefixes(innerWork, closeIdx + 1);
                            if (afterClose < innerWork.Length)
                            {
                                diagnostics.ReportError(
                                    "ERA1003",
                                    "Unexpected characters after line-continuation closing brace '}'.",
                                    SourceSpan.SingleLine(fileId, innerLineNo, afterClose + 1, innerWork.Length),
                                    virtualPath,
                                    innerRaw);
                            }

                            closed = true;
                            closeLineNo = innerLineNo;
                            break;
                        }

                        if (trimmedStart.TrimEnd().SequenceEqual("{"))
                        {
                            diagnostics.ReportError(
                                "ERA1004",
                                "Nested line-continuation opening brace '{' is not allowed.",
                                SourceSpan.SingleLine(fileId, innerLineNo, 1, innerRaw.Length),
                                virtualPath,
                                innerRaw);
                        }
                    }

                    builder.Append(innerWork);
                    builder.Append(continuationSeparator);
                }

                if (!closed)
                {
                    diagnostics.ReportError(
                        "ERA1005",
                        "Unclosed line-continuation block '{' reached end of file without '}'.",
                        SourceSpan.SingleLine(fileId, lineNo, pos + 1, pos + 1),
                        virtualPath,
                        rawPhysical);
                    break;
                }

                string joined = builder.ToString();
                int joinedStart = SkipLeadingWhitespaceAndCommentPrefixes(joined, 0);
                if (joinedStart < joined.Length)
                {
                    output.Add(new PreprocessedLine(
                        fileId,
                        virtualPath,
                        lineNo,
                        closeLineNo,
                        1,
                        joined[joinedStart..],
                        joined));
                }

                continue;
            }

            output.Add(new PreprocessedLine(
                fileId,
                virtualPath,
                lineNo,
                lineNo,
                pos + 1,
                working[pos..],
                rawPhysical));
        }

        ppState.VerifyEndOfFile(fileId, virtualPath, Math.Max(1, physicalLines.Length), diagnostics);
        return [.. output];
    }

    private string ApplyRename(string line)
    {
        if (_renameDictionary is null || !line.Contains("[[", StringComparison.Ordinal))
        {
            return line;
        }

        return RenamePattern().Replace(line, match =>
        {
            string fullToken = match.Value; // [[key]]
            if (_renameDictionary.TryGetValue(fullToken, out string? mapped))
            {
                return mapped;
            }

            string innerKey = fullToken[2..^2];
            if (_renameDictionary.TryGetValue(innerKey, out mapped))
            {
                return mapped;
            }

            return fullToken;
        });
    }

    private int SkipLeadingWhitespaceAndCommentPrefixes(string line, int start)
    {
        int i = start;
        while (i < line.Length)
        {
            char c = line[i];
            if (c is ' ' or '\t')
            {
                i++;
                continue;
            }

            if (c == '　')
            {
                if (!_options.SystemAllowFullSpace)
                {
                    return i;
                }

                i++;
                continue;
            }

            if (c == ';')
            {
                if (_options.EnableSpecialCommentPrefixes && i + 2 < line.Length && line[i + 2] == ';')
                {
                    char mid = line[i + 1];
                    if (mid is '!' or '^')
                    {
                        i += 3;
                        continue;
                    }

                    if (mid == '#' && _isDebugMode)
                    {
                        i += 3;
                        continue;
                    }
                }

                return line.Length;
            }

            break;
        }

        return i;
    }

    private ReadOnlySpan<char> TrimLeadingSpaces(ReadOnlySpan<char> span)
    {
        int i = 0;
        while (i < span.Length)
        {
            char c = span[i];
            if (c is ' ' or '\t' || (c == '　' && _options.SystemAllowFullSpace))
            {
                i++;
                continue;
            }

            break;
        }

        return span[i..];
    }

    private void ProcessDirectiveLine(
        int fileId,
        string virtualPath,
        int lineNo,
        string line,
        int openBracketPos,
        string rawLine,
        FilePreprocessorState state,
        DiagnosticBag diagnostics)
    {
        int i = openBracketPos + 1;
        string directive = ReadDirectiveWord(line, ref i);
        SkipSpacesOnly(line, ref i);
        string arg = ReadDirectiveWord(line, ref i);
        SkipSpacesOnly(line, ref i);

        var span = SourceSpan.SingleLine(fileId, lineNo, openBracketPos + 1, Math.Max(openBracketPos + 1, i + 1));
        if (string.IsNullOrEmpty(directive) || i >= line.Length || line[i] != ']')
        {
            if (!state.InSkipBlock)
            {
                diagnostics.ReportError(
                    "ERA1006",
                    $"Malformed preprocessor directive on line {lineNo}.",
                    span,
                    virtualPath,
                    rawLine);
            }

            return;
        }

        StringComparison cmp = _options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        state.ProcessDirective(directive, arg, cmp, span, virtualPath, rawLine, diagnostics);
    }

    private static string ReadDirectiveWord(string line, ref int pos)
    {
        int start = pos;
        while (pos < line.Length)
        {
            char c = line[pos];
            if (c is ']' or ' ' or '\t' or '　' or ';')
            {
                break;
            }

            pos++;
        }

        return line[start..pos];
    }

    private void SkipSpacesOnly(string line, ref int pos)
    {
        while (pos < line.Length)
        {
            char c = line[pos];
            if (c is ' ' or '\t' || (c == '　' && _options.SystemAllowFullSpace))
            {
                pos++;
                continue;
            }

            break;
        }
    }

    private static string UnquoteContinuationSeparator(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            return raw[1..^1];
        }

        return raw;
    }

    /// <summary>
    /// Splits text on <c>\r\n</c>, <c>\n</c>, and standalone <c>\r</c> (matching Emuera's CR/LF/CRLF handling).
    /// </summary>
    public static string[] SplitPhysicalLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                lines.Add(text[start..i]);
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
            else if (c == '\n')
            {
                lines.Add(text[start..i]);
                start = i + 1;
            }
        }

        if (start <= text.Length)
        {
            // Avoid adding a phantom empty trailing line after a terminal newline
            if (start < text.Length)
            {
                lines.Add(text[start..]);
            }
        }

        return [.. lines];
    }

    private static string FindMatchingDirectory(IVirtualFileSystem vfs, string subDirectory)
    {
        string normalized = VfsPathNormalizer.Normalize(subDirectory);
        if (normalized.Length == 0 || vfs.DirectoryExists(normalized))
        {
            return normalized;
        }

        return string.Empty;
    }

    private static bool IsInSharpPriorityDirectory(string normalizedPath, string rootDir)
    {
        string dir = VfsPathNormalizer.GetDirectoryName(normalizedPath);
        if (dir.Length <= rootDir.Length)
        {
            return false;
        }

        ReadOnlySpan<char> relDir = rootDir.Length == 0
            ? dir.AsSpan()
            : dir.AsSpan(rootDir.Length + 1);
        return relDir.Contains('#');
    }

    private static void SortScriptPaths(List<string> paths, bool sortWithFilename)
    {
        if (sortWithFilename)
        {
            paths.Sort(static (a, b) =>
            {
                int cmp = StringComparer.OrdinalIgnoreCase.Compare(
                    VfsPathNormalizer.GetFileName(a),
                    VfsPathNormalizer.GetFileName(b));
                return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a, b);
            });
        }
        else
        {
            paths.Sort(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class FilePreprocessorState
    {
        private readonly bool _isDebugMode;
        private readonly Func<string, bool>? _isMacroDefined;
        private readonly Stack<BlockFrame> _stack = new();
        private int _skipDepth;

        public FilePreprocessorState(bool isDebugMode, Func<string, bool>? isMacroDefined)
        {
            _isDebugMode = isDebugMode;
            _isMacroDefined = isMacroDefined;
        }

        public bool InSkipBlock => _skipDepth > 0;

        public bool IsDisabled => _skipDepth > 0 || (_stack.Count > 0 && !_stack.Peek().CurrentBranchActive);

        public void ProcessDirective(
            string directive,
            string arg,
            StringComparison cmp,
            SourceSpan span,
            string filePath,
            string rawLine,
            DiagnosticBag diagnostics)
        {
            if (directive.Equals("SKIPSTART", cmp))
            {
                if (!string.IsNullOrEmpty(arg))
                {
                    diagnostics.ReportWarning("ERA1007", "[SKIPSTART] does not accept arguments.", span, filePath, rawLine);
                }

                _skipDepth++;
                _stack.Push(new BlockFrame(IsSkipStart: true, ParentActive: false, CurrentBranchActive: false, BranchTaken: true, HasElse: false, Span: span));
                return;
            }

            if (directive.Equals("SKIPEND", cmp))
            {
                if (!string.IsNullOrEmpty(arg))
                {
                    diagnostics.ReportWarning("ERA1007", "[SKIPEND] does not accept arguments.", span, filePath, rawLine);
                }

                if (_stack.Count == 0 || !_stack.Peek().IsSkipStart)
                {
                    diagnostics.ReportError("ERA1008", "Unexpected [SKIPEND] without matching [SKIPSTART].", span, filePath, rawLine);
                    return;
                }

                _stack.Pop();
                _skipDepth--;
                return;
            }

            // Inside [SKIPSTART]...[SKIPEND], ignore [IF]/[ENDIF] directives completely!
            if (_skipDepth > 0)
            {
                return;
            }

            bool parentActive = _stack.Count == 0 || _stack.Peek().CurrentBranchActive;

            if (directive.Equals("IF_DEBUG", cmp))
            {
                if (!string.IsNullOrEmpty(arg))
                {
                    diagnostics.ReportError("ERA1009", "[IF_DEBUG] does not accept arguments.", span, filePath, rawLine);
                    return;
                }

                bool cond = _isDebugMode;
                _stack.Push(new BlockFrame(false, parentActive, parentActive && cond, cond, false, span));
                return;
            }

            if (directive.Equals("IF_NDEBUG", cmp))
            {
                if (!string.IsNullOrEmpty(arg))
                {
                    diagnostics.ReportError("ERA1009", "[IF_NDEBUG] does not accept arguments.", span, filePath, rawLine);
                    return;
                }

                bool cond = !_isDebugMode;
                _stack.Push(new BlockFrame(false, parentActive, parentActive && cond, cond, false, span));
                return;
            }

            if (directive.Equals("IF", cmp))
            {
                if (string.IsNullOrEmpty(arg))
                {
                    diagnostics.ReportError("ERA1010", "[IF] requires a macro or constant condition identifier.", span, filePath, rawLine);
                    return;
                }

                bool cond = EvaluateMacroCondition(arg);
                _stack.Push(new BlockFrame(false, parentActive, parentActive && cond, cond, false, span));
                return;
            }

            if (directive.Equals("ELSEIF", cmp))
            {
                if (string.IsNullOrEmpty(arg))
                {
                    diagnostics.ReportError("ERA1010", "[ELSEIF] requires a macro or constant condition identifier.", span, filePath, rawLine);
                    return;
                }

                if (_stack.Count == 0 || _stack.Peek().IsSkipStart || _stack.Peek().HasElse)
                {
                    diagnostics.ReportError("ERA1011", "Unexpected [ELSEIF] without matching open [IF].", span, filePath, rawLine);
                    return;
                }

                BlockFrame top = _stack.Pop();
                bool cond = !top.BranchTaken && EvaluateMacroCondition(arg);
                _stack.Push(top with
                {
                    CurrentBranchActive = top.ParentActive && cond,
                    BranchTaken = top.BranchTaken || cond,
                });
                return;
            }

            if (directive.Equals("ELSE", cmp))
            {
                if (!string.IsNullOrEmpty(arg))
                {
                    diagnostics.ReportError("ERA1009", "[ELSE] does not accept arguments.", span, filePath, rawLine);
                    return;
                }

                if (_stack.Count == 0 || _stack.Peek().IsSkipStart || _stack.Peek().HasElse)
                {
                    diagnostics.ReportError("ERA1012", "Unexpected [ELSE] without matching open [IF].", span, filePath, rawLine);
                    return;
                }

                BlockFrame top = _stack.Pop();
                bool takeElse = !top.BranchTaken;
                _stack.Push(top with
                {
                    CurrentBranchActive = top.ParentActive && takeElse,
                    BranchTaken = true,
                    HasElse = true,
                });
                return;
            }

            if (directive.Equals("ENDIF", cmp))
            {
                if (!string.IsNullOrEmpty(arg))
                {
                    diagnostics.ReportError("ERA1009", "[ENDIF] does not accept arguments.", span, filePath, rawLine);
                    return;
                }

                if (_stack.Count == 0 || _stack.Peek().IsSkipStart)
                {
                    diagnostics.ReportError("ERA1013", "Unexpected [ENDIF] without matching [IF].", span, filePath, rawLine);
                    return;
                }

                _stack.Pop();
                return;
            }

            diagnostics.ReportError("ERA1014", $"Unrecognized preprocessor directive '[{directive}]'.", span, filePath, rawLine);
        }

        public void VerifyEndOfFile(int fileId, string filePath, int eofLineNo, DiagnosticBag diagnostics)
        {
            while (_stack.Count > 0)
            {
                BlockFrame unclosed = _stack.Pop();
                string expected = unclosed.IsSkipStart ? "[SKIPEND]" : "[ENDIF]";
                diagnostics.ReportError(
                    "ERA1015",
                    $"Unclosed preprocessor block; expected {expected} before end of file.",
                    unclosed.Span,
                    filePath);
            }

            _skipDepth = 0;
        }

        private bool EvaluateMacroCondition(string token)
        {
            if (long.TryParse(token, out long num))
            {
                return num != 0;
            }

            return _isMacroDefined?.Invoke(token) ?? false;
        }

        private readonly record struct BlockFrame(
            bool IsSkipStart,
            bool ParentActive,
            bool CurrentBranchActive,
            bool BranchTaken,
            bool HasElse,
            SourceSpan Span);
    }
}
