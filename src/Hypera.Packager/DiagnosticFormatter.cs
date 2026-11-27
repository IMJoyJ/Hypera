using System.Text;
using Hypera.Diagnostics;
using Hypera.Localization;

namespace Hypera.Packager;

/// <summary>
/// Formats <see cref="PackResult"/> summaries, full <see cref="EmueraCompileException"/> diagnostics,
/// and I/O or validation errors in the active UI language.
/// </summary>
public static class DiagnosticFormatter
{
    public static string FormatResult(PackResult result, string? languageCode = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        string lang = PackagerStrings.NormalizeLanguage(languageCode);
        var sb = new StringBuilder();

        if (result.Success)
        {
            sb.AppendLine(PackagerStrings.Get(lang, "SummarySuccessHeader"));
            sb.AppendLine(PackagerStrings.Format(lang, "SummarySource", result.SourceDirectory));
            sb.AppendLine(PackagerStrings.Format(lang, "SummaryOutput", result.OutputHepPath));
            sb.AppendLine(PackagerStrings.Format(lang, "SummaryEntries", result.EntryCount));
            sb.AppendLine(PackagerStrings.Format(lang, "SummarySize", PackagerPathHelper.FormatByteSize(result.OutputSizeBytes)));
            sb.AppendLine(result.IncludesPrebuiltBytecode
                ? PackagerStrings.Format(lang, "SummaryBytecode", result.FunctionCount, result.InstructionCount)
                : PackagerStrings.Get(lang, "SummaryBytecodeNone"));
            sb.AppendLine(PackagerStrings.Format(lang, "SummaryWarnings", result.WarningCount));

            if (result.Diagnostics.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(PackagerStrings.Get(lang, "SummaryDiagnosticsHeader"));
                AppendDiagnostics(sb, result.Diagnostics, lang);
            }

            return sb.ToString().TrimEnd();
        }

        if (result.HasCompileFailure || result.Diagnostics.Count > 0)
        {
            int fileCount = CountDistinctFiles(result.Diagnostics);
            sb.AppendLine(PackagerStrings.Get(lang, "SummaryCompileFailedHeader"));
            sb.AppendLine(PackagerStrings.Format(lang, "SummarySource", result.SourceDirectory));
            sb.AppendLine(PackagerStrings.Format(lang, "SummaryErrors", result.ErrorCount));
            sb.AppendLine(PackagerStrings.Format(lang, "SummaryWarnings", result.WarningCount));
            sb.AppendLine(PackagerStrings.Format(lang, "SummaryFileCount", fileCount));
            sb.AppendLine();
            sb.AppendLine(PackagerStrings.Get(lang, "SummaryDiagnosticsHeader"));
            AppendDiagnostics(sb, result.Diagnostics, lang);
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine(PackagerStrings.Get(lang, "SummaryErrorHeader"));
        if (!string.IsNullOrWhiteSpace(result.SourceDirectory))
        {
            sb.AppendLine(PackagerStrings.Format(lang, "SummarySource", result.SourceDirectory));
        }

        if (!string.IsNullOrWhiteSpace(result.OutputHepPath))
        {
            sb.AppendLine(PackagerStrings.Format(lang, "SummaryOutput", result.OutputHepPath));
        }

        string message = GetLocalizedErrorMessage(result, lang);
        sb.AppendLine(message);

        if (result.Exception is not null)
        {
            sb.AppendLine();
            sb.AppendLine(PackagerStrings.Get(lang, "SummaryExceptionHeader"));
            sb.AppendLine(result.Exception.ToString());
        }

        return sb.ToString().TrimEnd();
    }

    public static string GetLocalizedErrorMessage(PackResult result, string? languageCode = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        string lang = PackagerStrings.NormalizeLanguage(languageCode);

        if (!string.IsNullOrWhiteSpace(result.ValidationErrorKey))
        {
            string arg = result.ValidationErrorKey switch
            {
                "ValidationSourceNotFound" => result.SourceDirectory,
                "ValidationOutputIsDirectory" => result.OutputHepPath,
                _ => string.Empty,
            };
            return PackagerStrings.Format(lang, result.ValidationErrorKey, arg);
        }

        if (result.Exception is EmueraCompileException compileEx)
        {
            return compileEx.FormatMessage(HyperaLocalizer.Default, lang);
        }

        return result.ErrorMessage ?? result.Exception?.Message ?? "Unknown error.";
    }

    public static string FormatStatusLine(PackResult result, string? languageCode = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        string lang = PackagerStrings.NormalizeLanguage(languageCode);

        if (result.Success)
        {
            return PackagerStrings.Format(
                lang,
                "StatusSuccess",
                result.EntryCount,
                result.OutputHepPath,
                PackagerPathHelper.FormatByteSize(result.OutputSizeBytes));
        }

        if (result.HasCompileFailure || result.Diagnostics.Count > 0)
        {
            return PackagerStrings.Format(lang, "StatusCompileFailed", result.ErrorCount, result.WarningCount);
        }

        return PackagerStrings.Format(lang, "StatusError", GetLocalizedErrorMessage(result, lang));
    }

    private static void AppendDiagnostics(StringBuilder sb, IReadOnlyList<Diagnostic> diagnostics, string lang)
    {
        List<Diagnostic> ordered = OrderDiagnostics(diagnostics);
        foreach (Diagnostic d in ordered)
        {
            sb.AppendLine(d.FormatFull(HyperaLocalizer.Default, lang));
            if (!string.IsNullOrWhiteSpace(d.RawLineText))
            {
                sb.Append("    | ").AppendLine(d.RawLineText.TrimEnd());
            }
        }
    }

    public static List<Diagnostic> OrderDiagnostics(IReadOnlyList<Diagnostic> diagnostics) =>
        diagnostics
            .OrderByDescending(static d => (int)d.Severity)
            .ThenBy(static d => d.FilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static d => d.Span.StartLine)
            .ThenBy(static d => d.Span.StartColumn)
            .ToList();

    public static int CountDistinctFiles(IReadOnlyList<Diagnostic> diagnostics) =>
        diagnostics
            .Select(static d => !string.IsNullOrEmpty(d.FilePath) ? d.FilePath : $"#{d.Span.FileId}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
}
