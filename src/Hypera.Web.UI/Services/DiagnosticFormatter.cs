using Hypera.Diagnostics;
using Hypera.Localization;
using Hypera.Platform.Services;

namespace Hypera.Web.UI.Services;

public sealed record LocalizedDiagnosticEntry(
    string Code,
    DiagnosticSeverity Severity,
    string Location,
    string FormattedMessage,
    string? RawLineText);

public static class DiagnosticFormatter
{
    public static IReadOnlyList<LocalizedDiagnosticEntry> ParseAndFormat(
        string? diagnosticsJson,
        HyperaLocalizer localizer,
        string? cultureCode)
    {
        IReadOnlyList<Diagnostic> diagnostics = SubmissionDiagnosticsSerializer.DeserializeDiagnostics(diagnosticsJson);
        if (diagnostics.Count == 0)
        {
            return [];
        }

        var list = new List<LocalizedDiagnosticEntry>(diagnostics.Count);
        foreach (Diagnostic diagnostic in diagnostics)
        {
            string location = !string.IsNullOrWhiteSpace(diagnostic.FilePath)
                ? (diagnostic.Span.StartLine > 0
                    ? $"{diagnostic.FilePath}({diagnostic.Span.StartLine},{diagnostic.Span.StartColumn})"
                    : diagnostic.FilePath)
                : (diagnostic.Span.StartLine > 0
                    ? $"L{diagnostic.Span.StartLine}:{diagnostic.Span.StartColumn}"
                    : string.Empty);

            string formatted = diagnostic.FormatMessage(localizer, cultureCode);
            list.Add(new LocalizedDiagnosticEntry(
                diagnostic.Code,
                diagnostic.Severity,
                location,
                formatted,
                diagnostic.RawLineText));
        }

        return list;
    }
}
