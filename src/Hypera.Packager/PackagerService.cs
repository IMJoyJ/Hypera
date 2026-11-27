using System.IO;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.Diagnostics;
using Hypera.Persistence;
using Hypera.VirtualFs;

namespace Hypera.Packager;

/// <summary>
/// Authoritative compile + pack execution pipeline delegating directly to <c>Hypera.Core</c>.
/// </summary>
public static class PackagerService
{
    public static PackResult ExecutePack(
        PackRequest request,
        IProgress<PackProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string sourceRaw = request.SourceDirectory?.Trim().Trim('"') ?? string.Empty;
        string outputRaw = request.OutputHepPath?.Trim().Trim('"') ?? string.Empty;

        if (string.IsNullOrWhiteSpace(sourceRaw))
        {
            return ValidationFailure(sourceRaw, outputRaw, "ValidationMissingSource", "Source directory is required.");
        }

        string sourceDir;
        try
        {
            sourceDir = Path.GetFullPath(sourceRaw);
        }
        catch (Exception ex)
        {
            return new PackResult(
                Success: false,
                SourceDirectory: sourceRaw,
                OutputHepPath: outputRaw,
                EntryCount: 0,
                OutputSizeBytes: 0,
                FunctionCount: 0,
                InstructionCount: 0,
                IncludesPrebuiltBytecode: false,
                WarningCount: 0,
                ErrorCount: 0,
                Diagnostics: [],
                ErrorMessage: ex.Message,
                Exception: ex);
        }

        if (!Directory.Exists(sourceDir))
        {
            return ValidationFailure(sourceDir, outputRaw, "ValidationSourceNotFound", $"Source directory not found: '{sourceDir}'.");
        }

        if (string.IsNullOrWhiteSpace(outputRaw))
        {
            return ValidationFailure(sourceDir, outputRaw, "ValidationMissingOutput", "Output .hep path is required.");
        }

        string outputHepPath;
        try
        {
            outputHepPath = Path.GetFullPath(outputRaw);
        }
        catch (Exception ex)
        {
            return new PackResult(
                Success: false,
                SourceDirectory: sourceDir,
                OutputHepPath: outputRaw,
                EntryCount: 0,
                OutputSizeBytes: 0,
                FunctionCount: 0,
                InstructionCount: 0,
                IncludesPrebuiltBytecode: false,
                WarningCount: 0,
                ErrorCount: 0,
                Diagnostics: [],
                ErrorMessage: ex.Message,
                Exception: ex);
        }

        if (Directory.Exists(outputHepPath))
        {
            return ValidationFailure(sourceDir, outputHepPath, "ValidationOutputIsDirectory", $"Output path is an existing directory: '{outputHepPath}'.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new PackProgress("StageLoadingConfig"));

            using var vfs = new PhysicalDirectoryVfs(sourceDir);
            ConfigLoadResult config = ConfigLoader.Default.Load(vfs);
            CompilerOptions compilerOptions = request.RandomMode switch
            {
                RandomModeOverride.OldSfmt => config.CompilerOptions with { UseNewRandom = false },
                RandomModeOverride.NewRandom => config.CompilerOptions with { UseNewRandom = true },
                _ => config.CompilerOptions,
            };

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new PackProgress("StageLoadingCsv"));
            GameData gameData = CsvGameDataLoader.Default.Load(vfs, compilerOptions);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new PackProgress("StageCompiling"));
            CompilerExternalContext external = CompilerExternalContext.FromGameData(gameData, compilerOptions.IgnoreCase);
            var compiler = new ErbProjectCompiler(compilerOptions, external);
            CompilationResult compileResult = compiler.CompileProject(vfs, throwOnError: true);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new PackProgress("StagePacking"));

            bool hasBytecode = compileResult.Module.Functions.Length > 0 || compileResult.Module.Instructions.Length > 0;
            ReadOnlyMemory<byte>? prebuiltBytes = hasBytecode
                ? BytecodeSerializer.Serialize(compileResult.Module)
                : null;

            Func<string, bool>? filter = BuildIncludeFilter(sourceDir, outputHepPath, request.CompiledOnly, request.ExcludePaths);

            ArchivePackageHeader header = HepPackageWriter.PackDirectory(
                sourceDirectory: sourceDir,
                outputHepPath: outputHepPath,
                prebuiltBytecode: prebuiltBytes,
                prebuiltBytecodeVirtualPath: HepPackageWriter.DefaultPrebuiltBytecodePath,
                compress: request.Compress,
                includeFileFilter: filter);

            long sizeBytes = new FileInfo(outputHepPath).Length;
            int warnings = compileResult.Diagnostics.Count(static d => d.Severity == DiagnosticSeverity.Warning);

            return new PackResult(
                Success: true,
                SourceDirectory: sourceDir,
                OutputHepPath: outputHepPath,
                EntryCount: header.EntryCount,
                OutputSizeBytes: sizeBytes,
                FunctionCount: compileResult.Module.Functions.Length,
                InstructionCount: compileResult.Module.Instructions.Length,
                IncludesPrebuiltBytecode: hasBytecode,
                WarningCount: warnings,
                ErrorCount: 0,
                Diagnostics: compileResult.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EmueraCompileException ex)
        {
            int errors = ex.Diagnostics.Count(static d => d.Severity == DiagnosticSeverity.Error);
            int warnings = ex.Diagnostics.Count(static d => d.Severity == DiagnosticSeverity.Warning);
            return new PackResult(
                Success: false,
                SourceDirectory: sourceDir,
                OutputHepPath: outputHepPath,
                EntryCount: 0,
                OutputSizeBytes: 0,
                FunctionCount: 0,
                InstructionCount: 0,
                IncludesPrebuiltBytecode: false,
                WarningCount: warnings,
                ErrorCount: errors,
                Diagnostics: ex.Diagnostics,
                ErrorMessage: ex.Message,
                Exception: ex);
        }
        catch (Exception ex)
        {
            return new PackResult(
                Success: false,
                SourceDirectory: sourceDir,
                OutputHepPath: outputHepPath,
                EntryCount: 0,
                OutputSizeBytes: 0,
                FunctionCount: 0,
                InstructionCount: 0,
                IncludesPrebuiltBytecode: false,
                WarningCount: 0,
                ErrorCount: 0,
                Diagnostics: [],
                ErrorMessage: ex.Message,
                Exception: ex);
        }
    }

    private static Func<string, bool>? BuildIncludeFilter(
        string sourceDir,
        string outputHepPath,
        bool compiledOnly,
        IReadOnlyList<string>? excludes)
    {
        List<string> effectiveExcludes = excludes is { Count: > 0 }
            ? [.. excludes]
            : [];

        // Guard against self-inclusion sharing violation if output .hep is placed inside sourceDir when compiledOnly is false.
        string normalizedSource = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedOutput = Path.GetFullPath(outputHepPath);
        if (normalizedOutput.StartsWith(normalizedSource, StringComparison.OrdinalIgnoreCase))
        {
            string relativeOut = Path.GetRelativePath(sourceDir, normalizedOutput);
            effectiveExcludes.Add(relativeOut);
        }

        if (compiledOnly)
        {
            return HepPackageWriter.CreateCompiledDistributionFilter(effectiveExcludes);
        }

        if (effectiveExcludes.Count == 0)
        {
            return null;
        }

        var exactSet = new HashSet<string>(VfsPathNormalizer.Comparer);
        var prefixList = new List<string>();
        foreach (string raw in effectiveExcludes)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            bool dirPrefix = raw.EndsWith('/') || raw.EndsWith('\\');
            string norm = VfsPathNormalizer.Normalize(raw);
            if (norm.Length == 0)
            {
                continue;
            }

            if (dirPrefix)
            {
                prefixList.Add(norm + "/");
            }
            else
            {
                exactSet.Add(norm);
                prefixList.Add(norm + "/");
            }
        }

        return normalizedPath =>
        {
            if (exactSet.Contains(normalizedPath))
            {
                return false;
            }

            foreach (string prefix in prefixList)
            {
                if (normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        };
    }

    private static PackResult ValidationFailure(string sourceDir, string outputPath, string key, string fallbackMessage) =>
        new(
            Success: false,
            SourceDirectory: sourceDir,
            OutputHepPath: outputPath,
            EntryCount: 0,
            OutputSizeBytes: 0,
            FunctionCount: 0,
            InstructionCount: 0,
            IncludesPrebuiltBytecode: false,
            WarningCount: 0,
            ErrorCount: 0,
            Diagnostics: [],
            ValidationErrorKey: key,
            ErrorMessage: fallbackMessage);
}
