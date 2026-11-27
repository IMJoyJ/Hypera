using Hypera.Diagnostics;

namespace Hypera.Packager;

/// <summary>
/// Controls whether compilation overrides <c>setting.json</c>'s <c>UseNewRandom</c> option.
/// </summary>
public enum RandomModeOverride : byte
{
    /// <summary>Use whatever <c>UseNewRandom</c> value is configured in <c>setting.json</c> / config.</summary>
    FollowSettingJson = 0,

    /// <summary>Force deterministic rollback-safe SFMT RNG (<c>UseNewRandom = false</c>).</summary>
    OldSfmt = 1,

    /// <summary>Force .NET <see cref="Random"/> (<c>UseNewRandom = true</c>, fresh on rollback).</summary>
    NewRandom = 2,
}

/// <summary>
/// Immutable parameters for a single compile + pack operation.
/// </summary>
public sealed record PackRequest(
    string SourceDirectory,
    string OutputHepPath,
    bool CompiledOnly = true,
    bool Compress = true,
    RandomModeOverride RandomMode = RandomModeOverride.FollowSettingJson,
    IReadOnlyList<string>? ExcludePaths = null);

/// <summary>
/// Progress notification emitted by <see cref="PackagerService"/> during compile and pack phases.
/// </summary>
public sealed record PackProgress(string StageKey, string? Detail = null);

/// <summary>
/// Captures the complete outcome of a compile + pack operation, including compile diagnostics or runtime errors.
/// </summary>
public sealed record PackResult(
    bool Success,
    string SourceDirectory,
    string OutputHepPath,
    uint EntryCount,
    long OutputSizeBytes,
    int FunctionCount,
    int InstructionCount,
    bool IncludesPrebuiltBytecode,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<Diagnostic> Diagnostics,
    string? ValidationErrorKey = null,
    string? ErrorMessage = null,
    Exception? Exception = null)
{
    public bool HasCompileFailure => !Success && Exception is EmueraCompileException;
}
