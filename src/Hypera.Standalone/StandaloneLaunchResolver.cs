using System.Globalization;
using Hypera.VirtualFs;

namespace Hypera.Standalone;

public enum StandaloneTargetKind : byte
{
    None = 0,
    EmbeddedBundle = 1,
    HepArchive = 2,
    HebBytecode = 3,
    ZipArchive = 4,
    Directory = 5,
}

public sealed record StandaloneLaunchTarget(
    StandaloneTargetKind Kind,
    string Path,
    string SaveDirectory,
    string? OverlayDirectory,
    BundleTrailer? EmbeddedTrailer = null);

public sealed record StandaloneLaunchOptions(
    StandaloneLaunchTarget Target,
    bool IsDebugMode,
    bool UseOverlay,
    string? HeadlessSmokePngPath,
    IReadOnlyList<string> ScriptedInputs,
    int MaxHeadlessSteps,
    bool AutoAdvanceTimedWaits);

/// <summary>
/// Resolves what game binary or directory <c>Hypera.Standalone</c> should execute, mirroring
/// classic Emuera's zero-config double-click launch ergonomics while prioritizing precompiled
/// <c>HYPRBNDL</c>, <c>.hep</c>, and <c>.heb</c> artifacts.
/// </summary>
public static class StandaloneLaunchResolver
{
    public static StandaloneLaunchOptions Resolve(string? processPath, string baseDirectory, string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string resolvedBaseDir = !string.IsNullOrWhiteSpace(processPath) && Path.GetDirectoryName(processPath) is { Length: > 0 } procDir
            ? Path.GetFullPath(procDir)
            : Path.GetFullPath(string.IsNullOrWhiteSpace(baseDirectory) ? Environment.CurrentDirectory : baseDirectory);

        bool isDebug = false;
        bool useOverlay = true;
        string? explicitSaveDir = null;
        string? headlessSmokePng = null;
        string? explicitTarget = null;
        int maxSteps = 2000;
        bool autoAdvanceTimedWaits = true;
        var scriptedInputs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--debug", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-debug", StringComparison.OrdinalIgnoreCase))
            {
                isDebug = true;
            }
            else if (arg.Equals("--no-overlay", StringComparison.OrdinalIgnoreCase))
            {
                useOverlay = false;
            }
            else if (arg.Equals("--no-auto-timeout", StringComparison.OrdinalIgnoreCase))
            {
                autoAdvanceTimedWaits = false;
            }
            else if ((arg.Equals("--save-dir", StringComparison.OrdinalIgnoreCase) || arg.Equals("-s", StringComparison.OrdinalIgnoreCase)) &&
                     i + 1 < args.Length)
            {
                explicitSaveDir = Path.GetFullPath(args[++i]);
            }
            else if (arg.Equals("--headless-smoke", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                headlessSmokePng = Path.GetFullPath(args[++i]);
            }
            else if (arg.Equals("--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                scriptedInputs.Add(args[++i]);
            }
            else if (arg.Equals("--max-steps", StringComparison.OrdinalIgnoreCase) &&
                     i + 1 < args.Length &&
                     int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSteps) &&
                     parsedSteps > 0)
            {
                maxSteps = parsedSteps;
            }
            else if (!arg.StartsWith("--", StringComparison.Ordinal) && explicitTarget is null)
            {
                explicitTarget = arg;
            }
        }

        // 1. Highest priority: self-contained HYPRBNDL trailer appended to the running executable
        if (explicitTarget is null &&
            !string.IsNullOrWhiteSpace(processPath) &&
            SingleFileBundleCodec.TryDetectTrailer(processPath, out BundleTrailer trailer) &&
            trailer.PayloadKind is BundlePayloadKind.Hep or BundlePayloadKind.Heb)
        {
            string saveDir = explicitSaveDir ?? resolvedBaseDir;
            string? overlayDir = useOverlay && trailer.PayloadKind == BundlePayloadKind.Hep ? resolvedBaseDir : null;
            var bundleTarget = new StandaloneLaunchTarget(
                StandaloneTargetKind.EmbeddedBundle,
                Path.GetFullPath(processPath),
                saveDir,
                overlayDir,
                trailer);
            return new StandaloneLaunchOptions(bundleTarget, isDebug, useOverlay, headlessSmokePng, scriptedInputs, maxSteps, autoAdvanceTimedWaits);
        }

        // 2. Explicit target passed on command line (or via Explorer drag-and-drop)
        if (!string.IsNullOrWhiteSpace(explicitTarget))
        {
            StandaloneLaunchTarget target = ResolveExplicitPath(explicitTarget, explicitSaveDir, useOverlay);
            return new StandaloneLaunchOptions(target, isDebug, useOverlay, headlessSmokePng, scriptedInputs, maxSteps, autoAdvanceTimedWaits);
        }

        // 3. Auto-discover side-by-side game binary or directory next to the executable
        string? exeStem = !string.IsNullOrWhiteSpace(processPath)
            ? Path.GetFileNameWithoutExtension(processPath)
            : null;
        StandaloneLaunchTarget discovered = DiscoverSideBySideTarget(resolvedBaseDir, exeStem, explicitSaveDir, useOverlay);
        return new StandaloneLaunchOptions(discovered, isDebug, useOverlay, headlessSmokePng, scriptedInputs, maxSteps, autoAdvanceTimedWaits);
    }

    public static StandaloneLaunchTarget ResolveExplicitPath(string rawPath, string? explicitSaveDir, bool useOverlay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPath);
        string fullPath = Path.GetFullPath(rawPath);

        if (Directory.Exists(fullPath))
        {
            return new StandaloneLaunchTarget(
                StandaloneTargetKind.Directory,
                fullPath,
                explicitSaveDir ?? fullPath,
                OverlayDirectory: null);
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Game package or directory not found: '{fullPath}'.", fullPath);
        }

        string parentDir = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        string saveDir = explicitSaveDir ?? parentDir;
        string ext = Path.GetExtension(fullPath);

        if (ext.Equals(".hep", StringComparison.OrdinalIgnoreCase))
        {
            return new StandaloneLaunchTarget(
                StandaloneTargetKind.HepArchive,
                fullPath,
                saveDir,
                useOverlay ? parentDir : null);
        }

        if (ext.Equals(".heb", StringComparison.OrdinalIgnoreCase))
        {
            return new StandaloneLaunchTarget(
                StandaloneTargetKind.HebBytecode,
                fullPath,
                saveDir,
                OverlayDirectory: null);
        }

        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return new StandaloneLaunchTarget(
                StandaloneTargetKind.ZipArchive,
                fullPath,
                saveDir,
                useOverlay ? parentDir : null);
        }

        throw new NotSupportedException($"Unsupported Hypera Standalone target extension '{ext}' for '{fullPath}'.");
    }

    public static StandaloneLaunchTarget DiscoverSideBySideTarget(
        string baseDirectory,
        string? executableStem,
        string? explicitSaveDir,
        bool useOverlay)
    {
        string fullBase = Path.GetFullPath(baseDirectory);
        string saveDir = explicitSaveDir ?? fullBase;

        if (!Directory.Exists(fullBase))
        {
            return new StandaloneLaunchTarget(StandaloneTargetKind.None, string.Empty, saveDir, null);
        }

        // Priority 3a: <ExeName>.hep -> game.hep -> single *.hep
        string? hepCandidate = FindPreferredFile(fullBase, executableStem, "game", ".hep");
        if (hepCandidate is not null)
        {
            return new StandaloneLaunchTarget(
                StandaloneTargetKind.HepArchive,
                hepCandidate,
                saveDir,
                useOverlay ? fullBase : null);
        }

        // Priority 3b: <ExeName>.heb -> game.heb -> single *.heb
        string? hebCandidate = FindPreferredFile(fullBase, executableStem, "game", ".heb");
        if (hebCandidate is not null)
        {
            return new StandaloneLaunchTarget(
                StandaloneTargetKind.HebBytecode,
                hebCandidate,
                saveDir,
                OverlayDirectory: null);
        }

        // Priority 3c: classic Emuera folder layout (CSV/ or ERB/ in the executable directory)
        if (Directory.Exists(Path.Combine(fullBase, "ERB")) ||
            Directory.Exists(Path.Combine(fullBase, "CSV")))
        {
            return new StandaloneLaunchTarget(
                StandaloneTargetKind.Directory,
                fullBase,
                saveDir,
                OverlayDirectory: null);
        }

        return new StandaloneLaunchTarget(StandaloneTargetKind.None, string.Empty, saveDir, null);
    }

    private static string? FindPreferredFile(string directory, string? exeStem, string defaultStem, string extension)
    {
        if (!string.IsNullOrWhiteSpace(exeStem))
        {
            string byExe = Path.Combine(directory, exeStem + extension);
            if (File.Exists(byExe))
            {
                return byExe;
            }
        }

        string byDefault = Path.Combine(directory, defaultStem + extension);
        if (File.Exists(byDefault))
        {
            return byDefault;
        }

        string[] matches = Directory.GetFiles(directory, "*" + extension, SearchOption.TopDirectoryOnly);
        if (matches.Length == 1)
        {
            return matches[0];
        }

        return null;
    }
}
