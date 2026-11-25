using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Hypera.Runtime;

/// <summary>
/// Instance-isolated regular expression cache with conservative literal-pattern fast-path detection
/// for <c>FINDELEMENT</c>, <c>FINDLASTELEMENT</c>, <c>STRCOUNT</c>, <c>REPLACE</c>, and <c>REGEXPMATCH</c>.
/// </summary>
public sealed class RegexCache
{
    private static readonly SearchValues<char> RegexMetaChars =
        SearchValues.Create(@"\^$.*+?()[]{}|");

    private readonly Dictionary<string, Regex> _cache = new(StringComparer.Ordinal);
    private readonly int _maxEntries;

    public RegexCache(int maxEntries = 2048)
    {
        _maxEntries = Math.Max(16, maxEntries);
    }

    public int CachedPatternCount => _cache.Count;

    /// <summary>
    /// Conservatively determines whether <paramref name="pattern"/> contains zero regex metacharacters,
    /// allowing <c>FINDELEMENT</c>/<c>STRCOUNT</c>/<c>REPLACE</c> to bypass <see cref="Regex"/> allocation and execution.
    /// </summary>
    public static bool TryGetLiteralPattern(string? pattern, [NotNullWhen(true)] out string? literal)
    {
        if (pattern is null)
        {
            literal = null;
            return false;
        }

        if (pattern.AsSpan().IndexOfAny(RegexMetaChars) >= 0)
        {
            literal = null;
            return false;
        }

        literal = pattern;
        return true;
    }

    public Regex GetRegex(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (_cache.TryGetValue(pattern, out Regex? existing))
        {
            return existing;
        }

        if (_cache.Count >= _maxEntries)
        {
            _cache.Clear();
        }

        var created = new Regex(pattern, RegexOptions.CultureInvariant);
        _cache[pattern] = created;
        return created;
    }

    /// <summary>
    /// Executes Emuera <c>REGEXPMATCH</c>: returns match count, outputs group count per match,
    /// and flattens captured group values across all matches into <paramref name="groupOutput"/>.
    /// </summary>
    public int Match(string input, string pattern, out int groupCount, Span<string> groupOutput = default)
    {
        string source = input ?? string.Empty;
        Regex regex = GetRegex(pattern);
        MatchCollection matches = regex.Matches(source);
        string[] groupNames = regex.GetGroupNames();
        groupCount = groupNames.Length;

        int matchCount = matches.Count;
        if (matchCount > 0 && !groupOutput.IsEmpty)
        {
            int outIdx = 0;
            foreach (Match m in matches)
            {
                for (int g = 0; g < groupNames.Length; g++)
                {
                    if (outIdx >= groupOutput.Length)
                    {
                        return matchCount;
                    }

                    groupOutput[outIdx++] = m.Groups[groupNames[g]].Value;
                }
            }
        }

        return matchCount;
    }

    public void Clear() => _cache.Clear();
}
