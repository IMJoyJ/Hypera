using System.Collections.Immutable;
using System.Text.Json;
using Hypera.Configuration;
using Hypera.Diagnostics;
using Hypera.Localization;
using Hypera.Platform.Domain;

namespace Hypera.Platform.Services;

public sealed record PackageUploadRequest(
    Guid VersionId,
    string LanguageCode,
    Guid UploadedByUserId,
    bool AllowSourceDownload,
    Stream ZipArchiveStream,
    long? DeclaredByteLength = null,
    CompilerOptionsOverride? CompilerOptions = null);

public sealed record LocalPackageFileImportRequest(
    Guid VersionId,
    string LanguageCode,
    Guid UploadedByUserId,
    string FilePath,
    CompilerOptionsOverride? CompilerOptions = null,
    bool ForceRecompile = false);

public sealed record LocalDirectoryImportRequest(
    Guid VersionId,
    string LanguageCode,
    Guid UploadedByUserId,
    string DirectoryPath,
    CompilerOptionsOverride? CompilerOptions = null);

/// <summary>
/// Structured compiler diagnostic payload persisted in <see cref="GameLanguageSubmission.DiagnosticsJson"/>.
/// Compatible both with direct deserialization as <see cref="Diagnostic"/> and explicit property inspection.
/// </summary>
public sealed record SubmissionDiagnosticItem(
    string Code,
    DiagnosticSeverity Severity,
    string? MessageKey,
    string[] Arguments,
    string? FilePath,
    int Line,
    int Col,
    string FallbackMessage,
    string Message,
    SourceSpan Span,
    string? RawLineText = null)
{
    public Diagnostic ToDiagnostic() =>
        new(
            Code,
            Severity,
            !string.IsNullOrEmpty(FallbackMessage) ? FallbackMessage : Message,
            Span.StartLine > 0 ? Span : SourceSpan.SingleLine(0, Math.Max(1, Line), Math.Max(1, Col)),
            FilePath,
            RawLineText,
            Arguments is { Length: > 0 } ? [.. Arguments] : ImmutableArray<string>.Empty,
            MessageKey);

    public string FormatLocalized(HyperaLocalizer localizer, string? cultureCode = null) =>
        ToDiagnostic().FormatFull(localizer, cultureCode);
}

public static class SubmissionDiagnosticsSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static string Serialize(IEnumerable<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        SubmissionDiagnosticItem[] items = diagnostics
            .Select(static d => new SubmissionDiagnosticItem(
                Code: d.Code,
                Severity: d.Severity,
                MessageKey: d.MessageKey,
                Arguments: d.Arguments.IsDefaultOrEmpty ? [] : [.. d.Arguments],
                FilePath: d.FilePath,
                Line: d.Span.StartLine,
                Col: d.Span.StartColumn,
                FallbackMessage: d.Message,
                Message: d.Message,
                Span: d.Span,
                RawLineText: d.RawLineText))
            .ToArray();

        return JsonSerializer.Serialize(items, JsonOptions);
    }

    public static IReadOnlyList<Diagnostic> DeserializeDiagnostics(string? diagnosticsJson)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsJson))
        {
            return [];
        }

        try
        {
            SubmissionDiagnosticItem[]? parsed = JsonSerializer.Deserialize<SubmissionDiagnosticItem[]>(diagnosticsJson, JsonOptions);
            if (parsed is { Length: > 0 })
            {
                return parsed.Select(static p => p.ToDiagnostic()).ToArray();
            }
        }
        catch (JsonException)
        {
            // Ignore malformed JSON and return empty below.
        }

        return [];
    }

    public static IReadOnlyList<string> FormatAll(
        string? diagnosticsJson,
        HyperaLocalizer localizer,
        string? cultureCode = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        IReadOnlyList<Diagnostic> diagnostics = DeserializeDiagnostics(diagnosticsJson);
        if (diagnostics.Count == 0)
        {
            return [];
        }

        var formatted = new string[diagnostics.Count];
        for (int i = 0; i < diagnostics.Count; i++)
        {
            formatted[i] = diagnostics[i].FormatFull(localizer, cultureCode);
        }

        return formatted;
    }
}

/// <summary>
/// Orchestrates <c>.zip</c> source inspection (<c>ZipArchiveVfs</c>), binary (<c>.dll</c>/<c>.exe</c>) hashing,
/// strict <c>ErbProjectCompiler</c> compilation, <c>.hep</c> packaging, and review/publication workflow.
/// </summary>
public interface IPackagePipelineService
{
    /// <summary>
    /// Uploads, scans, strictly compiles, and packages a language build for <c>(VersionId, LanguageCode)</c>.
    /// Never disrupts an existing live <see cref="GameLanguageVariant.ActiveSubmissionId"/> until approved
    /// (or immediately upon compile success in <see cref="PlatformHostMode.SingleUserLocalAdmin"/>).
    /// </summary>
    Task<GameLanguageSubmission> SubmitZipPackageAsync(
        PackageUploadRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a local <c>.zip</c> or pre-packaged <c>.hep</c> file directly from disk without WebView stream buffering.
    /// Intended for <see cref="PlatformHostMode.SingleUserLocalAdmin"/> and local administrator imports.
    /// </summary>
    Task<GameLanguageSubmission> SubmitLocalFileAsync(
        LocalPackageFileImportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compiles and imports an unpacked local game folder directly into an immutable <c>.hep</c> package.
    /// Intended for <see cref="PlatformHostMode.SingleUserLocalAdmin"/> and local administrator imports.
    /// </summary>
    Task<GameLanguageSubmission> SubmitLocalDirectoryAsync(
        LocalDirectoryImportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new compilation job from an existing submission's source archive, compiled <c>.hep</c>, or local source
    /// directory using the supplied <paramref name="compilerOptions"/> override.
    /// </summary>
    Task<GameLanguageSubmission> RecompileSubmissionAsync(
        Guid actorUserId,
        Guid submissionId,
        CompilerOptionsOverride? compilerOptions = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Awaits completion of background compilation for <paramref name="submissionId"/> and returns its post-compile state.
    /// </summary>
    Task<GameLanguageSubmission> WaitForCompilationAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GameLanguageSubmission>> ListSubmissionsForAppAsync(
        Guid appId,
        Guid? viewerUserId = null,
        CancellationToken cancellationToken = default);

    Task<GameLanguageSubmission?> GetSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GameLanguageSubmission>> ListPendingReviewsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a pending submission: atomically marks any previous active submission on the same
    /// <c>(VersionId, LanguageCode)</c> as <see cref="SubmissionStatus.Superseded"/>, activates the new <c>.hep</c>,
    /// promotes <see cref="GameApp.IsPublicPool"/> to <see langword="true"/>, and optionally records its binaries
    /// as <see cref="BinaryReputationStatus.Trusted"/>.
    /// </summary>
    Task<GameLanguageSubmission> ApproveSubmissionAsync(
        Guid adminUserId,
        Guid submissionId,
        bool markBinariesTrusted = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rejects a submission with a reason and optionally flags selected binary entries as
    /// <see cref="BinaryReputationStatus.HighRisk"/> in the reputation ledger.
    /// </summary>
    Task<GameLanguageSubmission> RejectSubmissionAsync(
        Guid adminUserId,
        Guid submissionId,
        string rejectionReason,
        IReadOnlyCollection<Guid>? highRiskBinaryEntryIds = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a read-only stream to the uploaded <c>.zip</c> source archive if permitted by
    /// <see cref="GameLanguageSubmission.AllowSourceDownload"/> or ownership/admin privileges.
    /// </summary>
    Task<Stream> OpenSourceZipStreamAsync(
        Guid requesterUserId,
        Guid submissionId,
        CancellationToken cancellationToken = default);
}
