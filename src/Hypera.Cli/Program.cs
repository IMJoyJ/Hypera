using Hypera.Bytecode;
using Hypera.Diagnostics;
using Hypera.Runtime;
using Hypera.VirtualFs;

namespace Hypera.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        // Self-detect YU-RIS style HYPRBNDL trailer appended to the running executable (e.g., Game.exe).
        string? processPath = Environment.ProcessPath;
        if (SingleFileBundler.TryDetectTrailer(processPath, out BundleTrailer trailer) && processPath is not null)
        {
            return RunBundledExecutable(processPath, trailer, args);
        }

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        if (args[0] is "-v" or "--version" or "version")
        {
            Console.WriteLine("hypera 0.1.0");
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        string[] commandArgs = args[1..];

        return command switch
        {
            "run" => HandleRun(commandArgs),
            "compile" => HandleCompile(commandArgs),
            "pack" => HandlePack(commandArgs),
            "bundle" => HandleBundle(commandArgs),
            _ => ReportUnknownCommand(command),
        };
    }

    private static int RunBundledExecutable(string exePath, BundleTrailer trailer, string[] args)
    {
        (string? headlessFramesDir, List<string> inputs) = ParseRunFlags(args);
        using MemoryStream payloadStream = SingleFileBundler.ExtractPayloadToMemory(exePath, trailer);
        using var host = new AnsiConsoleHost(headlessFramesDir: headlessFramesDir, scriptedInputs: inputs);

        if (trailer.PayloadKind == BundlePayloadKind.Heb)
        {
            BytecodeModule module = HebBinaryCodec.Read(payloadStream);
            string baseDir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
            using var vfs = new PhysicalDirectoryVfs(baseDir);
            using HyperaGameHost gameHost = HyperaGameHost.LoadFromVfs(vfs, ownsVfs: false, preloadedModule: module);
            return host.RunHost(gameHost);
        }

        if (trailer.PayloadKind == BundlePayloadKind.Hep)
        {
            using HyperaGameHost gameHost = HyperaGameHost.LoadFromHepStream(payloadStream, exePath, leaveOpen: true);
            return host.RunHost(gameHost);
        }

        Console.Error.WriteLine("[hypera] Embedded HYPRBNDL payload is neither .heb nor .hep.");
        return 1;
    }

    private static int HandleRun(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: hypera run <game-directory-or-heb-or-hep-or-zip> [--headless-frames <dir>]");
            return 2;
        }

        string targetPath = args[0];
        if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
        {
            Console.Error.WriteLine($"[hypera] Target not found: '{targetPath}'.");
            return 2;
        }

        (string? headlessFramesDir, List<string> inputs) = ParseRunFlags(args[1..]);
        using var host = new AnsiConsoleHost(headlessFramesDir: headlessFramesDir, scriptedInputs: inputs);
        try
        {
            using HyperaGameHost gameHost = HyperaGameHost.Load(targetPath);
            return host.RunHost(gameHost);
        }
        catch (EmueraCompileException ex)
        {
            return ReportCompileDiagnostics(ex);
        }
    }

    private static int HandleCompile(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: hypera compile <game-directory> [-o <output.heb>]");
            return 2;
        }

        string sourceDir = args[0];
        if (!Directory.Exists(sourceDir))
        {
            Console.Error.WriteLine($"[hypera] Directory not found: '{sourceDir}'.");
            return 2;
        }

        string outputPath = ParseOptionValue(args[1..], "-o", "--output")
            ?? Path.Combine(Path.GetFullPath(sourceDir), "game.heb");

        BytecodeModule module;
        try
        {
            module = CliCompilerBridge.CompileDirectory(sourceDir);
        }
        catch (EmueraCompileException ex)
        {
            return ReportCompileDiagnostics(ex);
        }

        string? outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        HebBinaryCodec.Write(fs, module);
        Console.WriteLine($"[hypera] Compiled {module.Functions.Length} functions ({module.Instructions.Length} instructions) -> '{outputPath}'.");
        return 0;
    }

    private static int HandlePack(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: hypera pack <game-directory> [-o <output.hep>] [--no-compress] [--compiled-only] [--exclude <path>]...");
            return 2;
        }

        string sourceDir = args[0];
        if (!Directory.Exists(sourceDir))
        {
            Console.Error.WriteLine($"[hypera] Directory not found: '{sourceDir}'.");
            return 2;
        }

        string outputPath = ParseOptionValue(args[1..], "-o", "--output")
            ?? Path.Combine(Directory.GetCurrentDirectory(), Path.GetFileName(Path.GetFullPath(sourceDir).TrimEnd('\\', '/')) + ".hep");
        bool compress = !args.Contains("--no-compress", StringComparer.OrdinalIgnoreCase);
        bool compiledOnly = args.Contains("--compiled-only", StringComparer.OrdinalIgnoreCase) ||
                            args.Contains("--strip-erb", StringComparer.OrdinalIgnoreCase);
        List<string> excludes = ParseOptionValues(args[1..], "--exclude", "-x");

        BytecodeModule? module;
        try
        {
            module = CliCompilerBridge.CompileDirectory(sourceDir);
        }
        catch (EmueraCompileException ex)
        {
            return ReportCompileDiagnostics(ex);
        }

        if (module.Functions.Length == 0 && module.Instructions.Length == 0)
        {
            module = null;
        }

        Func<string, bool>? filter = null;
        if (compiledOnly)
        {
            filter = HepPackageWriter.CreateCompiledDistributionFilter(excludes);
        }
        else if (excludes.Count > 0)
        {
            var excludeSet = excludes
                .Where(static e => !string.IsNullOrWhiteSpace(e))
                .Select(static e => VfsPathNormalizer.Normalize(e))
                .Where(static e => e.Length > 0)
                .ToList();
            filter = normalizedPath =>
            {
                foreach (string ex in excludeSet)
                {
                    if (normalizedPath.Equals(ex, StringComparison.OrdinalIgnoreCase) ||
                        normalizedPath.StartsWith(ex + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            };
        }

        ArchivePackageHeader header = HepPackageCodec.PackDirectory(sourceDir, outputPath, compress, module, filter);
        long sizeBytes = new FileInfo(outputPath).Length;
        Console.WriteLine(
            $"[hypera] Packed '{sourceDir}' ({header.EntryCount} entries, {sizeBytes:N0} bytes, compiledOnly={compiledOnly}) -> '{outputPath}'.");
        return 0;
    }

    private static int HandleBundle(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: hypera bundle <source-heb-or-hep> [--host <apphost-template>] -o <Game.exe>");
            return 2;
        }

        string sourcePayload = args[0];
        string? hostTemplate = ParseOptionValue(args[1..], "--host") ?? Environment.ProcessPath;
        string? outputExe = ParseOptionValue(args[1..], "-o", "--output");

        if (string.IsNullOrWhiteSpace(hostTemplate))
        {
            Console.Error.WriteLine("[hypera] Unable to locate host executable template. Specify --host <path>.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(outputExe))
        {
            outputExe = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(sourcePayload)) ?? Environment.CurrentDirectory,
                Path.GetFileNameWithoutExtension(sourcePayload) + ".exe");
        }

        BundleTrailer trailer = SingleFileBundler.PackBundle(sourcePayload, hostTemplate, outputExe);
        Console.WriteLine($"[hypera] Bundled '{sourcePayload}' ({trailer.PayloadLength} bytes, {trailer.PayloadKind}) -> '{outputExe}' (offset={trailer.PayloadOffset}).");
        return 0;
    }

    private static (string? HeadlessFramesDir, List<string> Inputs) ParseRunFlags(string[] args)
    {
        string? headlessDir = null;
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--headless-frames" && i + 1 < args.Length)
            {
                headlessDir = args[++i];
            }
            else if (args[i] is "--input" && i + 1 < args.Length)
            {
                inputs.Add(args[++i]);
            }
        }

        return (headlessDir, inputs);
    }

    private static List<string> ParseOptionValues(string[] args, params string[] names)
    {
        var values = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (names.Contains(args[i], StringComparer.OrdinalIgnoreCase))
            {
                values.Add(args[++i]);
            }
        }

        return values;
    }

    private static string? ParseOptionValue(string[] args, params string[] names)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (names.Contains(args[i], StringComparer.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int ReportCompileDiagnostics(EmueraCompileException ex)
    {
        IReadOnlyList<Diagnostic> diagnostics = ex.Diagnostics;
        var ordered = diagnostics
            .OrderByDescending(static d => (int)d.Severity)
            .ThenBy(static d => d.FilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static d => d.Span.StartLine)
            .ThenBy(static d => d.Span.StartColumn)
            .ToList();

        foreach (Diagnostic d in ordered)
        {
            Console.Error.WriteLine(d.ToString());
        }

        int errorCount = diagnostics.Count(static d => d.Severity == DiagnosticSeverity.Error);
        int warningCount = diagnostics.Count(static d => d.Severity == DiagnosticSeverity.Warning);
        int fileCount = diagnostics
            .Select(static d => !string.IsNullOrEmpty(d.FilePath) ? d.FilePath : $"#{d.Span.FileId}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        Console.Error.WriteLine($"[hypera] Compilation failed: {errorCount} error(s), {warningCount} warning(s) across {fileCount} file(s).");
        return 1;
    }

    private static int ReportUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: '{command}'.");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Hypera Engine CLI (hypera)");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  hypera run <path> [--headless-frames <dir>]            Run a game directory, .heb, .hep, or .zip");
        Console.WriteLine("  hypera compile <dir> [-o <out.heb>]                    Compile scripts to .heb bytecode");
        Console.WriteLine("  hypera pack <dir> [-o <out.hep>] [--compiled-only] [--exclude <p>]  Pack into a .hep archive");
        Console.WriteLine("  hypera bundle <heb-or-hep> --host <exe> -o <Game.exe>  Create single-file HYPRBNDL executable");
    }
}
