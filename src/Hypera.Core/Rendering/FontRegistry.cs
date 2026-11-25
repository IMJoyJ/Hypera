using Hypera.VirtualFs;
using SkiaSharp;

namespace Hypera.Rendering;

/// <summary>
/// Matches Emuera FONTSTYLE bitmask: 1=Bold, 2=Italic, 4=Strikeout, 8=Underline.
/// </summary>
[Flags]
public enum FontStyleFlags : byte
{
    Regular = 0,
    Bold = 1,
    Italic = 2,
    Strikeout = 4,
    Underline = 8,
}

/// <summary>
/// Instance-scoped font registry loading custom TTF/OTF/TTC fonts from <see cref="IVirtualFileSystem"/>
/// without leaking into any global static state.
/// </summary>
public sealed class FontRegistry : IDisposable
{
    private readonly record struct StyleCacheKey(string FamilyName, bool Bold, bool Italic);

    private readonly Dictionary<string, List<SKTypeface>> _customFamilies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _customTileFamilies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<StyleCacheKey, SKTypeface> _systemTypefaces = [];
    private readonly List<SKTypeface> _ownedTypefaces = [];
    private bool _disposed;

    public int CustomFontCount => _customFamilies.Count;

    public IReadOnlyCollection<string> CustomFamilyNames => _customFamilies.Keys;

    /// <summary>
    /// Loads all .ttf, .otf, and .ttc font faces from <paramref name="fontDirectory"/> in the VFS into this instance.
    /// </summary>
    public int RegisterFromVfs(IVirtualFileSystem vfs, string fontDirectory = "font")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(vfs);

        if (!vfs.DirectoryExists(fontDirectory))
        {
            return 0;
        }

        int loadedCount = 0;
        foreach (string path in vfs.EnumerateFiles(fontDirectory, "*.*", recursive: true))
        {
            string ext = VfsPathNormalizer.GetExtension(path);
            if (!ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".otf", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] bytes = vfs.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                continue;
            }

            string stem = Path.GetFileNameWithoutExtension(path);
            if (ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase))
            {
                for (int index = 0; ; index++)
                {
                    using SKData data = SKData.CreateCopy(bytes);
                    SKTypeface? typeface = SKTypeface.FromData(data, index);
                    if (typeface is null)
                    {
                        break;
                    }

                    RegisterTypeface(typeface, isCustomTileFont: true, alias: index == 0 ? stem : null);
                    loadedCount++;
                }
            }
            else
            {
                using SKData data = SKData.CreateCopy(bytes);
                SKTypeface? typeface = SKTypeface.FromData(data);
                if (typeface is not null)
                {
                    RegisterTypeface(typeface, isCustomTileFont: true, alias: stem);
                    loadedCount++;
                }
            }
        }

        return loadedCount;
    }

    /// <summary>
    /// Registers an <see cref="SKTypeface"/> directly into this instance-scoped registry.
    /// Takes ownership of <paramref name="typeface"/> and disposes it when the registry is disposed.
    /// </summary>
    public void RegisterTypeface(SKTypeface typeface, bool isCustomTileFont = true, string? alias = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(typeface);

        _ownedTypefaces.Add(typeface);

        if (!string.IsNullOrWhiteSpace(typeface.FamilyName))
        {
            AddCustomEntry(typeface.FamilyName, typeface, isCustomTileFont);
        }

        if (!string.IsNullOrWhiteSpace(alias) &&
            !alias.Equals(typeface.FamilyName, StringComparison.OrdinalIgnoreCase))
        {
            AddCustomEntry(alias, typeface, isCustomTileFont);
        }
    }

    public bool IsCustomFont(string? familyName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return !string.IsNullOrWhiteSpace(familyName) && _customFamilies.ContainsKey(familyName.Trim());
    }

    public bool IsCustomTileFont(string? familyName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return !string.IsNullOrWhiteSpace(familyName) && _customTileFamilies.Contains(familyName.Trim());
    }

    /// <summary>
    /// Implements Emuera CHKFONT: checks instance-scoped custom VFS fonts first, then installed host fonts.
    /// </summary>
    public bool HasFont(string? familyName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(familyName))
        {
            return false;
        }

        string trimmed = familyName.Trim();
        if (_customFamilies.ContainsKey(trimmed))
        {
            return true;
        }

        using SKTypeface? matched = SKFontManager.Default.MatchFamily(trimmed);
        return matched is not null &&
               matched.FamilyName.Equals(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    public SKTypeface ResolveTypeface(string? familyName, FontStyleFlags style = FontStyleFlags.Regular) =>
        ResolveTypeface(familyName, style, out _);

    public SKTypeface ResolveTypeface(string? familyName, FontStyleFlags style, out bool isCustomTileFont)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool wantBold = (style & FontStyleFlags.Bold) != 0;
        bool wantItalic = (style & FontStyleFlags.Italic) != 0;

        if (!string.IsNullOrWhiteSpace(familyName))
        {
            string trimmed = familyName.Trim();
            if (_customFamilies.TryGetValue(trimmed, out List<SKTypeface>? candidates) && candidates.Count > 0)
            {
                isCustomTileFont = _customTileFamilies.Contains(trimmed);
                return PickBestVariant(candidates, wantBold, wantItalic);
            }

            isCustomTileFont = false;
            var key = new StyleCacheKey(trimmed.ToUpperInvariant(), wantBold, wantItalic);
            if (_systemTypefaces.TryGetValue(key, out SKTypeface? cached))
            {
                return cached;
            }

            var fontStyle = new SKFontStyle(
                wantBold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal,
                wantItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

            SKTypeface resolved = SKTypeface.FromFamilyName(trimmed, fontStyle) ?? SKTypeface.Default;
            if (!ReferenceEquals(resolved, SKTypeface.Default))
            {
                _ownedTypefaces.Add(resolved);
            }

            _systemTypefaces[key] = resolved;
            return resolved;
        }

        isCustomTileFont = false;
        if (!wantBold && !wantItalic)
        {
            return SKTypeface.Default;
        }

        string defaultName = SKTypeface.Default.FamilyName ?? string.Empty;
        var defKey = new StyleCacheKey(defaultName.ToUpperInvariant(), wantBold, wantItalic);
        if (_systemTypefaces.TryGetValue(defKey, out SKTypeface? cachedDefault))
        {
            return cachedDefault;
        }

        var defStyle = new SKFontStyle(
            wantBold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            wantItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

        SKTypeface styledDefault = SKTypeface.FromFamilyName(defaultName, defStyle) ?? SKTypeface.Default;
        if (!ReferenceEquals(styledDefault, SKTypeface.Default))
        {
            _ownedTypefaces.Add(styledDefault);
        }

        _systemTypefaces[defKey] = styledDefault;
        return styledDefault;
    }

    /// <summary>
    /// Creates an <see cref="SKFont"/> configured for either zero-seam crisp tile rendering or smooth anti-aliased typography.
    /// </summary>
    public SKFont CreateFont(
        string? familyName,
        float fontSize,
        FontStyleFlags style = FontStyleFlags.Regular,
        PixelSamplingMode samplingMode = PixelSamplingMode.Auto) =>
        CreateFont(familyName, fontSize, style, samplingMode, out _);

    public SKFont CreateFont(
        string? familyName,
        float fontSize,
        FontStyleFlags style,
        PixelSamplingMode samplingMode,
        out bool isCrispTileMode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SKTypeface typeface = ResolveTypeface(familyName, style, out bool isCustomTileFont);
        isCrispTileMode = samplingMode == PixelSamplingMode.CrispNearestNeighbor ||
                          (samplingMode == PixelSamplingMode.Auto && isCustomTileFont);

        float effectiveSize = MathF.Max(1f, isCrispTileMode ? MathF.Round(fontSize) : fontSize);
        bool wantBold = (style & FontStyleFlags.Bold) != 0;
        bool wantItalic = (style & FontStyleFlags.Italic) != 0;

        var font = new SKFont(typeface, effectiveSize)
        {
            Edging = isCrispTileMode ? SKFontEdging.Alias : SKFontEdging.SubpixelAntialias,
            Subpixel = !isCrispTileMode,
            Hinting = isCrispTileMode ? SKFontHinting.None : SKFontHinting.Slight,
            LinearMetrics = false,
            ForceAutoHinting = false,
            Embolden = wantBold && !typeface.IsBold,
            SkewX = wantItalic && !typeface.IsItalic ? -0.25f : 0f,
        };

        return font;
    }

    private void AddCustomEntry(string familyName, SKTypeface typeface, bool isCustomTileFont)
    {
        if (!_customFamilies.TryGetValue(familyName, out List<SKTypeface>? list))
        {
            list = [];
            _customFamilies[familyName] = list;
        }

        list.Add(typeface);
        if (isCustomTileFont)
        {
            _customTileFamilies.Add(familyName);
        }
    }

    private static SKTypeface PickBestVariant(List<SKTypeface> candidates, bool wantBold, bool wantItalic)
    {
        SKTypeface best = candidates[0];
        int bestScore = -1;
        foreach (SKTypeface tf in candidates)
        {
            int score = (tf.IsBold == wantBold ? 2 : 0) + (tf.IsItalic == wantItalic ? 1 : 0);
            if (score > bestScore)
            {
                bestScore = score;
                best = tf;
            }
        }

        return best;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (SKTypeface tf in _ownedTypefaces)
        {
            tf.Dispose();
        }

        _ownedTypefaces.Clear();
        _customFamilies.Clear();
        _customTileFamilies.Clear();
        _systemTypefaces.Clear();
    }
}
