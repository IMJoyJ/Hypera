namespace Hypera.VirtualFs;

/// <summary>
/// Stateless, cross-platform virtual path normalizer (converts '\' to '/', collapses redundant segments,
/// prevents directory escape above virtual root, and provides case-insensitive comparison).
/// </summary>
public static class VfsPathNormalizer
{
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string unified = path.Replace('\\', '/').Trim();
        string[] rawSegments = unified.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Length == 0)
        {
            return string.Empty;
        }

        var stack = new List<string>(rawSegments.Length);
        foreach (string segment in rawSegments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count == 0)
                {
                    throw new InvalidOperationException($"Virtual path '{path}' attempts to traverse outside virtual root.");
                }

                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(segment);
        }

        return stack.Count == 0 ? string.Empty : string.Join('/', stack);
    }

    public static string Combine(string? basePath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return Normalize(relativePath);
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return Normalize(basePath);
        }

        return Normalize($"{basePath}/{relativePath}");
    }

    public static bool EqualsIgnoreCase(string? left, string? right) =>
        Comparer.Equals(Normalize(left), Normalize(right));

    public static string GetDirectoryName(string? path)
    {
        string normalized = Normalize(path);
        int lastSlash = normalized.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : normalized[..lastSlash];
    }

    public static string GetFileName(string? path)
    {
        string normalized = Normalize(path);
        int lastSlash = normalized.LastIndexOf('/');
        return lastSlash < 0 ? normalized : normalized[(lastSlash + 1)..];
    }

    public static string GetExtension(string? path)
    {
        string fileName = GetFileName(path);
        int lastDot = fileName.LastIndexOf('.');
        return lastDot < 0 ? string.Empty : fileName[lastDot..];
    }
}
