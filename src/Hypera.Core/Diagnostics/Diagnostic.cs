using System.Collections.Immutable;
using Hypera.Localization;

namespace Hypera.Diagnostics;

public enum DiagnosticSeverity : byte
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

public readonly record struct Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    SourceSpan Span,
    string? FilePath = null,
    string? RawLineText = null)
{
    private readonly ImmutableArray<string> _arguments;

    public Diagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        SourceSpan span,
        string? filePath,
        string? rawLineText,
        ImmutableArray<string> arguments,
        string? messageKey = null)
        : this(code, severity, message, span, filePath, rawLineText)
    {
        _arguments = arguments;
        MessageKey = messageKey;
    }

    public ImmutableArray<string> Arguments
    {
        get => _arguments.IsDefault ? ImmutableArray<string>.Empty : _arguments;
        init => _arguments = value;
    }

    public string? MessageKey { get; init; }

    public string FormatMessage(HyperaLocalizer localizer, string? cultureCode = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        string? template = null;
        if (!string.IsNullOrEmpty(MessageKey)
            && localizer.TryGetTemplate(MessageKey, cultureCode, out string keyedTemplate))
        {
            template = keyedTemplate;
        }
        else if (localizer.TryGetTemplate($"diag.{Code}", cultureCode, out string codeTemplate))
        {
            template = codeTemplate;
        }

        if (template is null)
        {
            return Message;
        }

        return Arguments.IsEmpty
            ? template
            : HyperaLocalizer.FormatTemplateSafe(template, Arguments.AsSpan());
    }

    public string FormatFull(HyperaLocalizer localizer, string? cultureCode = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        string loc = string.IsNullOrEmpty(FilePath)
            ? $"#{Span.FileId}:{Span.StartLine}:{Span.StartColumn}"
            : $"{FilePath}({Span.StartLine},{Span.StartColumn})";

        string severityKey = Severity switch
        {
            DiagnosticSeverity.Info => "diag.severity.info",
            DiagnosticSeverity.Warning => "diag.severity.warning",
            _ => "diag.severity.error",
        };

        string severityLabel = localizer.Get(severityKey, cultureCode, Severity.ToString().ToLowerInvariant());
        string formattedMessage = FormatMessage(localizer, cultureCode);
        return $"{loc}: {severityLabel} {Code}: {formattedMessage}";
    }

    public override string ToString()
    {
        string loc = string.IsNullOrEmpty(FilePath)
            ? $"#{Span.FileId}:{Span.StartLine}:{Span.StartColumn}"
            : $"{FilePath}({Span.StartLine},{Span.StartColumn})";
        return $"{loc}: {Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
    }
}

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];
    private readonly Lock _sync = new();
    private int _errorCount;
    private int _warningCount;

    public bool HasErrors => Volatile.Read(ref _errorCount) > 0;
    public int ErrorCount => Volatile.Read(ref _errorCount);
    public int WarningCount => Volatile.Read(ref _warningCount);
    public int Count { get { lock (_sync) { return _items.Count; } } }

    public void Add(Diagnostic diagnostic)
    {
        lock (_sync)
        {
            _items.Add(diagnostic);
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                _errorCount++;
            }
            else if (diagnostic.Severity == DiagnosticSeverity.Warning)
            {
                _warningCount++;
            }
        }
    }

    public void ReportError(string code, string message, SourceSpan span, string? filePath = null, string? rawLineText = null) =>
        Add(new Diagnostic(code, DiagnosticSeverity.Error, message, span, filePath, rawLineText));

    public void ReportError(
        string code,
        string message,
        SourceSpan span,
        string? filePath,
        string? rawLineText,
        ImmutableArray<string> arguments,
        string? messageKey = null) =>
        Add(new Diagnostic(code, DiagnosticSeverity.Error, message, span, filePath, rawLineText, arguments, messageKey));

    public void ReportWarning(string code, string message, SourceSpan span, string? filePath = null, string? rawLineText = null) =>
        Add(new Diagnostic(code, DiagnosticSeverity.Warning, message, span, filePath, rawLineText));

    public void ReportWarning(
        string code,
        string message,
        SourceSpan span,
        string? filePath,
        string? rawLineText,
        ImmutableArray<string> arguments,
        string? messageKey = null) =>
        Add(new Diagnostic(code, DiagnosticSeverity.Warning, message, span, filePath, rawLineText, arguments, messageKey));

    public void ReportInfo(string code, string message, SourceSpan span, string? filePath = null, string? rawLineText = null) =>
        Add(new Diagnostic(code, DiagnosticSeverity.Info, message, span, filePath, rawLineText));

    public void ReportInfo(
        string code,
        string message,
        SourceSpan span,
        string? filePath,
        string? rawLineText,
        ImmutableArray<string> arguments,
        string? messageKey = null) =>
        Add(new Diagnostic(code, DiagnosticSeverity.Info, message, span, filePath, rawLineText, arguments, messageKey));

    public ImmutableArray<Diagnostic> ToImmutableArray()
    {
        lock (_sync)
        {
            return [.. _items];
        }
    }

    public void ThrowIfErrors()
    {
        if (HasErrors)
        {
            throw new EmueraCompileException(ToImmutableArray());
        }
    }
}

/// <summary>
/// Thrown when strict compilation fails due to syntax, symbol, or type errors.
/// Hypera enforces strict compile-time validation rather than deferring broken lines to runtime.
/// </summary>
public sealed class EmueraCompileException : Exception
{
    public EmueraCompileException(IReadOnlyList<Diagnostic> diagnostics)
        : base(FormatMessage(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public string FormatMessage(HyperaLocalizer localizer, string? cultureCode = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        int errors = Diagnostics.Count(static d => d.Severity == DiagnosticSeverity.Error);
        Diagnostic? first = Diagnostics.FirstOrDefault(static d => d.Severity == DiagnosticSeverity.Error);
        return first is { } f
            ? localizer.Format(
                "diag.compile_failed",
                cultureCode,
                "Compilation failed with {0} error(s). First error: {1}",
                errors.ToString(),
                f.FormatFull(localizer, cultureCode))
            : localizer.Format(
                "diag.compile_failed_count",
                cultureCode,
                "Compilation failed with {0} diagnostic(s).",
                Diagnostics.Count.ToString());
    }

    private static string FormatMessage(IReadOnlyList<Diagnostic> diagnostics)
    {
        int errors = diagnostics.Count(static d => d.Severity == DiagnosticSeverity.Error);
        Diagnostic? first = diagnostics.FirstOrDefault(static d => d.Severity == DiagnosticSeverity.Error);
        return first is { } f
            ? $"Compilation failed with {errors} error(s). First error: {f}"
            : $"Compilation failed with {diagnostics.Count} diagnostic(s).";
    }
}
