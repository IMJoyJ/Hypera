using System.Buffers.Binary;
using System.Text;
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
    private static readonly string[] s_defaultFallbackLanguages = ["ja", "zh"];

    private readonly record struct StyleCacheKey(string FamilyName, bool Bold, bool Italic);
    private readonly record struct CharFallbackKey(int CodePoint, bool Bold, bool Italic);

    private readonly Dictionary<string, List<SKTypeface>> _customFamilies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _customTileFamilies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<StyleCacheKey, SKTypeface> _systemTypefaces = [];
    private readonly Dictionary<CharFallbackKey, SKTypeface?> _fallbackTypefaces = [];
    private readonly Dictionary<StyleCacheKey, SKTypeface> _fallbackOwnedByStyle = [];
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

                    IReadOnlyList<string> ttfNames = ExtractTrueTypeFamilyNames(bytes, index);
                    RegisterTypeface(typeface, isCustomTileFont: true, alias: index == 0 ? stem : null, extraAliases: ttfNames);
                    loadedCount++;
                }
            }
            else
            {
                using SKData data = SKData.CreateCopy(bytes);
                SKTypeface? typeface = SKTypeface.FromData(data);
                if (typeface is not null)
                {
                    IReadOnlyList<string> ttfNames = ExtractTrueTypeFamilyNames(bytes, ttcFaceIndex: 0);
                    RegisterTypeface(typeface, isCustomTileFont: true, alias: stem, extraAliases: ttfNames);
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
    public void RegisterTypeface(
        SKTypeface typeface,
        bool isCustomTileFont = true,
        string? alias = null,
        IEnumerable<string>? extraAliases = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(typeface);

        _ownedTypefaces.Add(typeface);

        if (!string.IsNullOrWhiteSpace(typeface.FamilyName))
        {
            AddCustomEntry(typeface.FamilyName.Trim(), typeface, isCustomTileFont);
        }

        if (!string.IsNullOrWhiteSpace(alias))
        {
            string trimmedAlias = alias.Trim();
            if (!trimmedAlias.Equals(typeface.FamilyName, StringComparison.OrdinalIgnoreCase))
            {
                AddCustomEntry(trimmedAlias, typeface, isCustomTileFont);
            }
        }

        if (extraAliases is not null)
        {
            foreach (string extra in extraAliases)
            {
                if (string.IsNullOrWhiteSpace(extra))
                {
                    continue;
                }

                string trimmedExtra = extra.Trim();
                if (!_customFamilies.TryGetValue(trimmedExtra, out List<SKTypeface>? existing) || !existing.Contains(typeface))
                {
                    AddCustomEntry(trimmedExtra, typeface, isCustomTileFont);
                }
            }
        }
    }

    /// <summary>
    /// Extracts GDI legacy family names (nameID=1) and OpenType typographic family names (nameID=16)
    /// from a raw TrueType/OpenType/TTC binary so DirectWrite's subfamily concatenation (e.g.
    /// <c>"xonFont xonng_angreed328"</c>) never breaks Emuera's <c>CHKFONT("xonFont")</c> / <c>SETFONT</c>.
    /// </summary>
    public static IReadOnlyList<string> ExtractTrueTypeFamilyNames(ReadOnlySpan<byte> fontBytes, int ttcFaceIndex = 0)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (fontBytes.Length < 12)
        {
            return results;
        }

        int sfntOffset = 0;
        if (fontBytes[0] == (byte)'t' && fontBytes[1] == (byte)'t' && fontBytes[2] == (byte)'c' && fontBytes[3] == (byte)'f')
        {
            if (fontBytes.Length < 12)
            {
                return results;
            }

            uint numFonts = BinaryPrimitives.ReadUInt32BigEndian(fontBytes.Slice(8, 4));
            if (ttcFaceIndex < 0 || (uint)ttcFaceIndex >= numFonts)
            {
                return results;
            }

            int offsetPos = 12 + (ttcFaceIndex * 4);
            if (offsetPos + 4 > fontBytes.Length)
            {
                return results;
            }

            uint faceOffset = BinaryPrimitives.ReadUInt32BigEndian(fontBytes.Slice(offsetPos, 4));
            if (faceOffset > (uint)fontBytes.Length - 12u)
            {
                return results;
            }

            sfntOffset = (int)faceOffset;
        }

        int numTables = BinaryPrimitives.ReadUInt16BigEndian(fontBytes.Slice(sfntOffset + 4, 2));
        int tableDirStart = sfntOffset + 12;
        if (tableDirStart + (numTables * 16) > fontBytes.Length)
        {
            return results;
        }

        int nameTableOffset = -1;
        int nameTableLength = 0;
        for (int i = 0; i < numTables; i++)
        {
            int rec = tableDirStart + (i * 16);
            if (fontBytes[rec] == (byte)'n' &&
                fontBytes[rec + 1] == (byte)'a' &&
                fontBytes[rec + 2] == (byte)'m' &&
                fontBytes[rec + 3] == (byte)'e')
            {
                uint off = BinaryPrimitives.ReadUInt32BigEndian(fontBytes.Slice(rec + 8, 4));
                uint len = BinaryPrimitives.ReadUInt32BigEndian(fontBytes.Slice(rec + 12, 4));
                if (off <= (uint)fontBytes.Length && len <= (uint)fontBytes.Length - off && len >= 6)
                {
                    nameTableOffset = (int)off;
                    nameTableLength = (int)len;
                }

                break;
            }
        }

        if (nameTableOffset < 0)
        {
            return results;
        }

        ReadOnlySpan<byte> nameTable = fontBytes.Slice(nameTableOffset, nameTableLength);
        int count = BinaryPrimitives.ReadUInt16BigEndian(nameTable.Slice(2, 2));
        int stringStorageOffset = BinaryPrimitives.ReadUInt16BigEndian(nameTable.Slice(4, 2));
        if (stringStorageOffset > nameTable.Length)
        {
            return results;
        }

        for (int n = 0; n < count; n++)
        {
            int nr = 6 + (n * 12);
            if (nr + 12 > nameTable.Length)
            {
                break;
            }

            ushort platformId = BinaryPrimitives.ReadUInt16BigEndian(nameTable.Slice(nr, 2));
            ushort encodingId = BinaryPrimitives.ReadUInt16BigEndian(nameTable.Slice(nr + 2, 2));
            ushort nameId = BinaryPrimitives.ReadUInt16BigEndian(nameTable.Slice(nr + 6, 2));
            ushort strLen = BinaryPrimitives.ReadUInt16BigEndian(nameTable.Slice(nr + 8, 2));
            ushort strOff = BinaryPrimitives.ReadUInt16BigEndian(nameTable.Slice(nr + 10, 2));

            if (nameId is not (1 or 16) || strLen == 0)
            {
                continue;
            }

            int absStart = stringStorageOffset + strOff;
            if (absStart < 0 || absStart + strLen > nameTable.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> raw = nameTable.Slice(absStart, strLen);
            string decoded;
            try
            {
                if (platformId is 3 or 0)
                {
                    decoded = Encoding.BigEndianUnicode.GetString(raw);
                }
                else if (platformId == 1 && encodingId == 1)
                {
                    decoded = Encoding.GetEncoding(932).GetString(raw);
                }
                else
                {
                    decoded = Encoding.Latin1.GetString(raw);
                }
            }
            catch
            {
                continue;
            }

            string clean = decoded.Replace("\0", string.Empty).Trim();
            if (clean.Length > 0 && seen.Add(clean))
            {
                results.Add(clean);
            }
        }

        return results;
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

    private static readonly Dictionary<string, string> LocalizedFamilyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ＭＳ ゴシック"] = "MS Gothic",
        ["MS ゴシック"] = "MS Gothic",
        ["ＭＳ Ｐゴシック"] = "MS PGothic",
        ["MS Ｐゴシック"] = "MS PGothic",
        ["MS Pゴシック"] = "MS PGothic",
        ["ＭＳ 明朝"] = "MS Mincho",
        ["MS 明朝"] = "MS Mincho",
        ["ＭＳ Ｐ明朝"] = "MS PMincho",
        ["MS Ｐ明朝"] = "MS PMincho",
        ["MS P明朝"] = "MS PMincho",
        ["メイリオ"] = "Meiryo",
        ["游ゴシック"] = "Yu Gothic",
        ["游明朝"] = "Yu Mincho",
        ["新宋体"] = "NSimSun",
        ["宋体"] = "SimSun",
        ["黑体"] = "SimHei",
        ["微软雅黑"] = "Microsoft YaHei",
    };

    private static string ResolveLocalizedSystemFamilyName(string familyName) =>
        LocalizedFamilyAliases.TryGetValue(familyName, out string? mapped) ? mapped : familyName;

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
        if (matched is not null &&
            matched.FamilyName.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string mapped = ResolveLocalizedSystemFamilyName(trimmed);
        if (!mapped.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            using SKTypeface? aliasMatched = SKFontManager.Default.MatchFamily(mapped);
            if (aliasMatched is not null &&
                aliasMatched.FamilyName.Equals(mapped, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

            string systemQuery = ResolveLocalizedSystemFamilyName(trimmed);
            SKTypeface resolved = SKTypeface.FromFamilyName(systemQuery, fontStyle)
                                  ?? SKTypeface.FromFamilyName(trimmed, fontStyle)
                                  ?? SKTypeface.Default;
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
    /// Resolves a cached system fallback <see cref="SKTypeface"/> that contains a glyph for
    /// <paramref name="codePoint"/> when the primary typeface lacks it (e.g. non-ASCII Kana/Kanji
    /// or Unicode symbols rendered with a Latin-only default font). Deduplicates native handles.
    /// </summary>
    public SKTypeface? ResolveCharacterFallback(int codePoint, FontStyleFlags style = FontStyleFlags.Regular)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (codePoint < 0x20)
        {
            return null;
        }

        bool wantBold = (style & FontStyleFlags.Bold) != 0;
        bool wantItalic = (style & FontStyleFlags.Italic) != 0;
        var cacheKey = new CharFallbackKey(codePoint, wantBold, wantItalic);
        if (_fallbackTypefaces.TryGetValue(cacheKey, out SKTypeface? cached))
        {
            return cached;
        }

        var fontStyle = new SKFontStyle(
            wantBold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            wantItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

        SKTypeface? matched = SKFontManager.Default.MatchCharacter(null, fontStyle, s_defaultFallbackLanguages, codePoint)
                              ?? SKFontManager.Default.MatchCharacter(null, fontStyle, null, codePoint);
        if (matched is null)
        {
            _fallbackTypefaces[cacheKey] = null;
            return null;
        }

        bool hasGlyph;
        using (var probe = new SKFont(matched, 16f))
        {
            hasGlyph = probe.GetGlyph(codePoint) != 0;
        }

        if (!hasGlyph)
        {
            if (!ReferenceEquals(matched, SKTypeface.Default))
            {
                matched.Dispose();
            }

            _fallbackTypefaces[cacheKey] = null;
            return null;
        }

        SKTypeface resolved = matched;
        if (!ReferenceEquals(matched, SKTypeface.Default))
        {
            var styleKey = new StyleCacheKey((matched.FamilyName ?? string.Empty).ToUpperInvariant(), matched.IsBold, matched.IsItalic);
            if (_fallbackOwnedByStyle.TryGetValue(styleKey, out SKTypeface? existing))
            {
                bool existingHasGlyph;
                using (var probeExisting = new SKFont(existing, 16f))
                {
                    existingHasGlyph = probeExisting.GetGlyph(codePoint) != 0;
                }

                if (existingHasGlyph)
                {
                    if (!ReferenceEquals(matched, existing))
                    {
                        matched.Dispose();
                    }

                    resolved = existing;
                }
                else
                {
                    _ownedTypefaces.Add(matched);
                }
            }
            else
            {
                _fallbackOwnedByStyle[styleKey] = matched;
                _ownedTypefaces.Add(matched);
            }
        }

        _fallbackTypefaces[cacheKey] = resolved;
        return resolved;
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
        _fallbackTypefaces.Clear();
        _fallbackOwnedByStyle.Clear();
    }
}
