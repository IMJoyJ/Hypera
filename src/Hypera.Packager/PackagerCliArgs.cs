using System.IO;

namespace Hypera.Packager;

public enum PackagerLaunchMode : byte
{
    Gui = 0,
    CliPack = 1,
    ShowHelp = 2,
    ShowVersion = 3,
    InvalidCli = 4,
}

public sealed record PackagerLaunchOptions(
    PackagerLaunchMode Mode,
    string? InitialSourceDirectory = null,
    string? InitialOutputHepPath = null,
    bool AutoStartPack = false,
    bool CompiledOnly = true,
    bool Compress = true,
    RandomModeOverride RandomMode = RandomModeOverride.FollowSettingJson,
    IReadOnlyList<string>? ExcludePaths = null,
    string LanguageCode = PackagerStrings.DefaultLanguage,
    string? CliErrorMessage = null);

/// <summary>
/// Parses command-line arguments for both Windows Explorer <c>%1</c> drag-drop auto-pack
/// and headless <c>--cli-pack</c> smoke/automation mode.
/// </summary>
public static class PackagerCliArgs
{
    public static PackagerLaunchOptions Parse(string[] args)
    {
        if (args is null || args.Length == 0)
        {
            return new PackagerLaunchOptions(PackagerLaunchMode.Gui);
        }

        string language = PackagerStrings.DefaultLanguage;
        bool hasCliPack = false;
        bool hasHelp = false;
        bool hasVersion = false;
        bool compiledOnly = true;
        bool compress = true;
        bool hasNewRandom = false;
        bool hasOldRandom = false;
        string? explicitOutput = null;
        string? positionalPath = null;
        var excludes = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (arg.Equals("--cli-pack", StringComparison.OrdinalIgnoreCase))
            {
                hasCliPack = true;
                continue;
            }

            if (arg is "-h" or "--help" or "/?")
            {
                hasHelp = true;
                continue;
            }

            if (arg is "-v" or "--version")
            {
                hasVersion = true;
                continue;
            }

            if (arg is "-o" or "--output")
            {
                if (i + 1 >= args.Length)
                {
                    return Invalid($"Missing value for '{arg}'.");
                }

                explicitOutput = CleanPathArg(args[++i]);
                continue;
            }

            if (arg.Equals("--no-compress", StringComparison.OrdinalIgnoreCase))
            {
                compress = false;
                continue;
            }

            if (arg.Equals("--compress", StringComparison.OrdinalIgnoreCase))
            {
                compress = true;
                continue;
            }

            if (arg.Equals("--compiled-only", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--strip-erb", StringComparison.OrdinalIgnoreCase))
            {
                compiledOnly = true;
                continue;
            }

            if (arg.Equals("--no-compiled-only", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--include-source", StringComparison.OrdinalIgnoreCase))
            {
                compiledOnly = false;
                continue;
            }

            if (arg.Equals("--new-random", StringComparison.OrdinalIgnoreCase))
            {
                hasNewRandom = true;
                continue;
            }

            if (arg.Equals("--old-random", StringComparison.OrdinalIgnoreCase))
            {
                hasOldRandom = true;
                continue;
            }

            if (arg is "--exclude" or "-x")
            {
                if (i + 1 >= args.Length)
                {
                    return Invalid($"Missing value for '{arg}'.");
                }

                excludes.AddRange(PackagerPathHelper.ParseExcludePaths(args[++i]));
                continue;
            }

            if (arg.Equals("--lang", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    return Invalid("Missing value for '--lang'.");
                }

                language = PackagerStrings.NormalizeLanguage(args[++i]);
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                return Invalid($"Unknown option: '{arg}'.");
            }

            if (positionalPath is null)
            {
                positionalPath = CleanPathArg(arg);
            }
            else
            {
                return Invalid($"Unexpected positional argument: '{arg}'.");
            }
        }

        if (hasHelp)
        {
            return new PackagerLaunchOptions(PackagerLaunchMode.ShowHelp, LanguageCode: language);
        }

        if (hasVersion)
        {
            return new PackagerLaunchOptions(PackagerLaunchMode.ShowVersion, LanguageCode: language);
        }

        if (hasNewRandom && hasOldRandom)
        {
            return Invalid("Cannot specify both '--new-random' and '--old-random' at the same time.");
        }

        RandomModeOverride randomMode = hasNewRandom
            ? RandomModeOverride.NewRandom
            : hasOldRandom
                ? RandomModeOverride.OldSfmt
                : RandomModeOverride.FollowSettingJson;

        if (hasCliPack)
        {
            if (string.IsNullOrWhiteSpace(positionalPath))
            {
                return Invalid("Usage: Hypera.Packager --cli-pack <game-directory> [-o <output.hep>] [--no-compress] [--no-compiled-only] [--new-random|--old-random] [--exclude <path>]");
            }

            string resolvedSource = TryGetFullPath(positionalPath);
            string resolvedOutput = !string.IsNullOrWhiteSpace(explicitOutput)
                ? TryGetFullPath(explicitOutput)
                : PackagerPathHelper.GetDefaultOutputHepPath(resolvedSource);

            return new PackagerLaunchOptions(
                Mode: PackagerLaunchMode.CliPack,
                InitialSourceDirectory: resolvedSource,
                InitialOutputHepPath: resolvedOutput,
                AutoStartPack: true,
                CompiledOnly: compiledOnly,
                Compress: compress,
                RandomMode: randomMode,
                ExcludePaths: excludes,
                LanguageCode: language);
        }

        if (!string.IsNullOrWhiteSpace(positionalPath))
        {
            string resolvedSource = TryGetFullPath(positionalPath);
            string resolvedOutput = !string.IsNullOrWhiteSpace(explicitOutput)
                ? TryGetFullPath(explicitOutput)
                : PackagerPathHelper.GetDefaultOutputHepPath(resolvedSource);

            return new PackagerLaunchOptions(
                Mode: PackagerLaunchMode.Gui,
                InitialSourceDirectory: resolvedSource,
                InitialOutputHepPath: resolvedOutput,
                AutoStartPack: true,
                CompiledOnly: compiledOnly,
                Compress: compress,
                RandomMode: randomMode,
                ExcludePaths: excludes,
                LanguageCode: language);
        }

        return new PackagerLaunchOptions(
            Mode: PackagerLaunchMode.Gui,
            InitialSourceDirectory: null,
            InitialOutputHepPath: explicitOutput is not null ? TryGetFullPath(explicitOutput) : null,
            AutoStartPack: false,
            CompiledOnly: compiledOnly,
            Compress: compress,
            RandomMode: randomMode,
            ExcludePaths: excludes,
            LanguageCode: language);
    }

    private static PackagerLaunchOptions Invalid(string message) =>
        new(PackagerLaunchMode.InvalidCli, CliErrorMessage: message);

    private static string CleanPathArg(string value) =>
        value.Trim().Trim('"');

    private static string TryGetFullPath(string path)
    {
        string cleaned = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (cleaned.Length == 0)
        {
            return path;
        }

        try
        {
            // Keep drive root suffix if needed (e.g., "D:")
            if (cleaned.Length == 2 && cleaned[1] == ':')
            {
                cleaned += Path.DirectorySeparatorChar;
            }

            return Path.GetFullPath(cleaned);
        }
        catch
        {
            return path;
        }
    }
}
