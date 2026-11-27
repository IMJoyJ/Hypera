using System.Globalization;
using System.IO;

namespace Hypera.Packager;

/// <summary>
/// Pure path and input-parsing utilities for <see cref="Hypera.Packager"/>.
/// </summary>
public static class PackagerPathHelper
{
    private static readonly char[] ExcludeSeparators = [';', '\r', '\n'];

    /// <summary>
    /// Computes the default output <c>.hep</c> path adjacent to <paramref name="sourceDirectory"/>:
    /// <c>&lt;parent&gt;/&lt;dirName&gt;.hep</c>.
    /// </summary>
    public static string GetDefaultOutputHepPath(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return string.Empty;
        }

        string trimmed = sourceDirectory.Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return string.Empty;
        }

        string? parent = Path.GetDirectoryName(fullPath);
        string dirName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(dirName))
        {
            // Drive root fallback such as "D:\"
            return Path.Combine(fullPath + Path.DirectorySeparatorChar, "game.hep");
        }

        string targetDir = string.IsNullOrWhiteSpace(parent) ? fullPath : parent;
        return Path.Combine(targetDir, dirName + ".hep");
    }

    /// <summary>
    /// Parses semicolon- or newline-delimited exclude paths into a deduplicated list.
    /// </summary>
    public static IReadOnlyList<string> ParseExcludePaths(string? rawExcludes)
    {
        if (string.IsNullOrWhiteSpace(rawExcludes))
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string segment in rawExcludes.Split(ExcludeSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string cleaned = segment.Trim().Trim('"');
            if (cleaned.Length > 0 && seen.Add(cleaned))
            {
                result.Add(cleaned);
            }
        }

        return result;
    }

    /// <summary>
    /// Formats a byte count cleanly with exact byte count and human-friendly KB/MB unit.
    /// </summary>
    public static string FormatByteSize(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:N0} B", bytes);
        }

        if (bytes < 1024 * 1024)
        {
            double kb = bytes / 1024.0;
            return string.Format(CultureInfo.InvariantCulture, "{0:N0} B ({1:F2} KB)", bytes, kb);
        }

        double mb = bytes / (1024.0 * 1024.0);
        return string.Format(CultureInfo.InvariantCulture, "{0:N0} B ({1:F2} MB)", bytes, mb);
    }
}
