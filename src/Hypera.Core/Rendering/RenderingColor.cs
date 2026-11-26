using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Hypera.Rendering;

/// <summary>
/// Stateless color conversion, HTML/CSS/Win32 named color lookup, and display-only text projection utilities.
/// </summary>
public static class RenderingColor
{
    private static readonly Dictionary<string, uint> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["transparent"] = 0x00000000u,
        ["aliceblue"] = 0xF0F8FFFFu,
        ["antiquewhite"] = 0xFAEBD7FFu,
        ["aqua"] = 0x00FFFFFFu,
        ["aquamarine"] = 0x7FFFD4FFu,
        ["azure"] = 0xF0FFFFFFu,
        ["beige"] = 0xF5F5DCFFu,
        ["bisque"] = 0xFFE4C4FFu,
        ["black"] = 0x000000FFu,
        ["blanchedalmond"] = 0xFFEBCDFFu,
        ["blue"] = 0x0000FFFFu,
        ["blueviolet"] = 0x8A2BE2FFu,
        ["brown"] = 0xA52A2AFFu,
        ["burlywood"] = 0xDEB887FFu,
        ["cadetblue"] = 0x5F9EA0FFu,
        ["chartreuse"] = 0x7FFF00FFu,
        ["chocolate"] = 0xD2691EFFu,
        ["coral"] = 0xFF7F50FFu,
        ["cornflowerblue"] = 0x6495EDFFu,
        ["cornsilk"] = 0xFFF8DCFFu,
        ["crimson"] = 0xDC143CFFu,
        ["cyan"] = 0x00FFFFFFu,
        ["darkblue"] = 0x00008BFFu,
        ["darkcyan"] = 0x008B8BFFu,
        ["darkgoldenrod"] = 0xB8860BFFu,
        ["darkgray"] = 0xA9A9A9FFu,
        ["darkgreen"] = 0x006400FFu,
        ["darkgrey"] = 0xA9A9A9FFu,
        ["darkkhaki"] = 0xBDB76BFFu,
        ["darkmagenta"] = 0x8B008BFFu,
        ["darkolivegreen"] = 0x556B2FFFu,
        ["darkorange"] = 0xFF8C00FFu,
        ["darkorchid"] = 0x9932CCFFu,
        ["darkred"] = 0x8B0000FFu,
        ["darksalmon"] = 0xE9967AFFu,
        ["darkseagreen"] = 0x8FBC8FFFu,
        ["darkslateblue"] = 0x483D8BFFu,
        ["darkslategray"] = 0x2F4F4FFFu,
        ["darkslategrey"] = 0x2F4F4FFFu,
        ["darkturquoise"] = 0x00CED1FFu,
        ["darkviolet"] = 0x9400D3FFu,
        ["deeppink"] = 0xFF1493FFu,
        ["deepskyblue"] = 0x00BFFFFFu,
        ["dimgray"] = 0x696969FFu,
        ["dimgrey"] = 0x696969FFu,
        ["dodgerblue"] = 0x1E90FFFFu,
        ["firebrick"] = 0xB22222FFu,
        ["floralwhite"] = 0xFFFAF0FFu,
        ["forestgreen"] = 0x228B22FFu,
        ["fuchsia"] = 0xFF00FFFFu,
        ["gainsboro"] = 0xDCDCDCFFu,
        ["ghostwhite"] = 0xF8F8FFFFu,
        ["gold"] = 0xFFD700FFu,
        ["goldenrod"] = 0xDAA520FFu,
        ["gray"] = 0x808080FFu,
        ["green"] = 0x008000FFu,
        ["greenyellow"] = 0xADFF2FFFu,
        ["grey"] = 0x808080FFu,
        ["honeydew"] = 0xF0FFF0FFu,
        ["hotpink"] = 0xFF69B4FFu,
        ["indianred"] = 0xCD5C5CFFu,
        ["indigo"] = 0x4B0082FFu,
        ["ivory"] = 0xFFFFF0FFu,
        ["khaki"] = 0xF0E68CFFu,
        ["lavender"] = 0xE6E6FAFFu,
        ["lavenderblush"] = 0xFFF0F5FFu,
        ["lawngreen"] = 0x7CFC00FFu,
        ["lemonchiffon"] = 0xFFFACDFFu,
        ["lightblue"] = 0xADD8E6FFu,
        ["lightcoral"] = 0xF08080FFu,
        ["lightcyan"] = 0xE0FFFFFFu,
        ["lightgoldenrodyellow"] = 0xFAFAD2FFu,
        ["lightgray"] = 0xD3D3D3FFu,
        ["lightgreen"] = 0x90EE90FFu,
        ["lightgrey"] = 0xD3D3D3FFu,
        ["lightpink"] = 0xFFB6C1FFu,
        ["lightsalmon"] = 0xFFA07AFFu,
        ["lightseagreen"] = 0x20B2AAFFu,
        ["lightskyblue"] = 0x87CEFAFFu,
        ["lightslategray"] = 0x778899FFu,
        ["lightslategrey"] = 0x778899FFu,
        ["lightsteelblue"] = 0xB0C4DEFFu,
        ["lightyellow"] = 0xFFFFE0FFu,
        ["lime"] = 0x00FF00FFu,
        ["limegreen"] = 0x32CD32FFu,
        ["linen"] = 0xFAF0E6FFu,
        ["magenta"] = 0xFF00FFFFu,
        ["maroon"] = 0x800000FFu,
        ["mediumaquamarine"] = 0x66CDAAFFu,
        ["mediumblue"] = 0x0000CDFFu,
        ["mediumorchid"] = 0xBA55D3FFu,
        ["mediumpurple"] = 0x9370DBFFu,
        ["mediumseagreen"] = 0x3CB371FFu,
        ["mediumslateblue"] = 0x7B68EEFFu,
        ["mediumspringgreen"] = 0x00FA9AFFu,
        ["mediumturquoise"] = 0x48D1CCFFu,
        ["mediumvioletred"] = 0xC71585FFu,
        ["midnightblue"] = 0x191970FFu,
        ["mintcream"] = 0xF5FFFAFFu,
        ["mistyrose"] = 0xFFE4E1FFu,
        ["moccasin"] = 0xFFE4B5FFu,
        ["navajowhite"] = 0xFFDEADFFu,
        ["navy"] = 0x000080FFu,
        ["oldlace"] = 0xFDF5E6FFu,
        ["olive"] = 0x808000FFu,
        ["olivedrab"] = 0x6B8E23FFu,
        ["orange"] = 0xFFA500FFu,
        ["orangered"] = 0xFF4500FFu,
        ["orchid"] = 0xDA70D6FFu,
        ["palegoldenrod"] = 0xEEE8AAFFu,
        ["palegreen"] = 0x98FB98FFu,
        ["paleturquoise"] = 0xAFEEEEFFu,
        ["palevioletred"] = 0xDB7093FFu,
        ["papayawhip"] = 0xFFEFD5FFu,
        ["peachpuff"] = 0xFFDAB9FFu,
        ["peru"] = 0xCD853FFFu,
        ["pink"] = 0xFFC0CBFFu,
        ["plum"] = 0xDDA0DDFFu,
        ["powderblue"] = 0xB0E0E6FFu,
        ["purple"] = 0x800080FFu,
        ["rebeccapurple"] = 0x663399FFu,
        ["red"] = 0xFF0000FFu,
        ["rosybrown"] = 0xBC8F8FFFu,
        ["royalblue"] = 0x4169E1FFu,
        ["saddlebrown"] = 0x8B4513FFu,
        ["salmon"] = 0xFA8072FFu,
        ["sandybrown"] = 0xF4A460FFu,
        ["seagreen"] = 0x2E8B57FFu,
        ["seashell"] = 0xFFF5EEFFu,
        ["sienna"] = 0xA0522DFFu,
        ["silver"] = 0xC0C0C0FFu,
        ["skyblue"] = 0x87CEEBFFu,
        ["slateblue"] = 0x6A5ACDFFu,
        ["slategray"] = 0x708090FFu,
        ["slategrey"] = 0x708090FFu,
        ["snow"] = 0xFFFAFAFFu,
        ["springgreen"] = 0x00FF7FFFu,
        ["steelblue"] = 0x4682B4FFu,
        ["tan"] = 0xD2B48CFFu,
        ["teal"] = 0x008080FFu,
        ["thistle"] = 0xD8BFD8FFu,
        ["tomato"] = 0xFF6347FFu,
        ["turquoise"] = 0x40E0D0FFu,
        ["violet"] = 0xEE82EEFFu,
        ["wheat"] = 0xF5DEB3FFu,
        ["white"] = 0xFFFFFFFFu,
        ["whitesmoke"] = 0xF5F5F5FFu,
        ["yellow"] = 0xFFFF00FFu,
        ["yellowgreen"] = 0x9ACD32FFu,
    };

    public static SKColor FromRgba(uint rgba) =>
        new(
            red: (byte)((rgba >> 24) & 0xFF),
            green: (byte)((rgba >> 16) & 0xFF),
            blue: (byte)((rgba >> 8) & 0xFF),
            alpha: (byte)(rgba & 0xFF));

    public static uint ToRgba(SKColor color) =>
        ((uint)color.Red << 24) | ((uint)color.Green << 16) | ((uint)color.Blue << 8) | color.Alpha;

    public static SKColor FromArgb(uint argb) =>
        new(
            red: (byte)((argb >> 16) & 0xFF),
            green: (byte)((argb >> 8) & 0xFF),
            blue: (byte)(argb & 0xFF),
            alpha: (byte)((argb >> 24) & 0xFF));

    public static uint ToArgb(SKColor color) =>
        ((uint)color.Alpha << 24) | ((uint)color.Red << 16) | ((uint)color.Green << 8) | color.Blue;

    public static uint FromRgb24(long rgb24, byte alpha = 0xFF)
    {
        uint rgb = (uint)(rgb24 & 0xFFFFFF);
        return (rgb << 8) | alpha;
    }

    public static int ToRgb24(uint rgba) => (int)((rgba >> 8) & 0xFFFFFF);

    public static bool TryParseHtmlColor(string? input, out uint rgba)
    {
        rgba = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        ReadOnlySpan<char> span = input.AsSpan().Trim();
        if (span.Length == 0)
        {
            return false;
        }

        if (span[0] == '#')
        {
            ReadOnlySpan<char> hex = span[1..];
            if (hex.Length == 3 &&
                uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v3))
            {
                uint r = ((v3 >> 8) & 0xF) * 17;
                uint g = ((v3 >> 4) & 0xF) * 17;
                uint b = (v3 & 0xF) * 17;
                rgba = (r << 24) | (g << 16) | (b << 8) | 0xFFu;
                return true;
            }

            if (hex.Length == 6 &&
                uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v6))
            {
                rgba = (v6 << 8) | 0xFFu;
                return true;
            }

            // Emuera HTML uses #AARRGGBB for 8-hex colors
            if (hex.Length == 8 &&
                uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v8))
            {
                uint a = (v8 >> 24) & 0xFF;
                uint r = (v8 >> 16) & 0xFF;
                uint g = (v8 >> 8) & 0xFF;
                uint b = v8 & 0xFF;
                rgba = (r << 24) | (g << 16) | (b << 8) | a;
                return true;
            }

            return false;
        }

        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<char> hex = span[2..];
            if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
            {
                if (hex.Length <= 6)
                {
                    rgba = ((v & 0xFFFFFFu) << 8) | 0xFFu;
                }
                else
                {
                    uint a = (v >> 24) & 0xFF;
                    uint r = (v >> 16) & 0xFF;
                    uint g = (v >> 8) & 0xFF;
                    uint b = v & 0xFF;
                    rgba = (r << 24) | (g << 16) | (b << 8) | a;
                }

                return true;
            }

            return false;
        }

        if (NamedColors.TryGetValue(span.ToString(), out uint named))
        {
            rgba = named;
            return true;
        }

        // Bare 6-digit hex fallback used in some Emuera scripts
        if (span.Length == 6 &&
            uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bare6))
        {
            rgba = (bare6 << 8) | 0xFFu;
            return true;
        }

        return false;
    }

    public static bool TryColorFromName(string? name, out long rgb24)
    {
        if (TryParseHtmlColor(name, out uint rgba))
        {
            rgb24 = (rgba >> 8) & 0xFFFFFFu;
            return true;
        }

        rgb24 = -1;
        return false;
    }

    public static string ToHtmlHex(uint rgba)
    {
        byte r = (byte)((rgba >> 24) & 0xFF);
        byte g = (byte)((rgba >> 16) & 0xFF);
        byte b = (byte)((rgba >> 8) & 0xFF);
        byte a = (byte)(rgba & 0xFF);
        return a == 0xFF
            ? $"#{r:X2}{g:X2}{b:X2}"
            : $"#{a:X2}{r:X2}{g:X2}{b:X2}";
    }
}

/// <summary>
/// Display-only text projection that transforms control characters for rendering without mutating script string data.
/// Strips ETX ('\u0003') and C0/C1 control characters, optionally replaces '\' with '¥', and matches Emuera's
/// drawing interface semantics for '\t': 0px (stripped) under <c>TEXTRENDERER</c> (GDI DrawText without DT_EXPANDTABS),
/// or expanded to 8 spaces under legacy <c>GRAPHICS</c> mode (<c>StringMeasure</c>).
/// </summary>
public static class DisplayTextProjector
{
    public const int TabSpaces = 8;

    public static string ProjectForDisplay(
        string? rawText,
        bool replaceBackslashWithYen = false,
        bool expandTabs = false)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return string.Empty;
        }

        bool needsChange = false;
        for (int i = 0; i < rawText.Length; i++)
        {
            char c = rawText[i];
            if (c < 0x20 ||
                c == 0x7F ||
                (c >= 0x80 && c <= 0x9F) ||
                (replaceBackslashWithYen && c == '\\'))
            {
                needsChange = true;
                break;
            }
        }

        if (!needsChange)
        {
            return rawText;
        }

        var sb = new StringBuilder(rawText.Length + (expandTabs ? 8 : 0));
        for (int i = 0; i < rawText.Length; i++)
        {
            char c = rawText[i];
            if (c == '\t')
            {
                if (expandTabs)
                {
                    sb.Append(' ', TabSpaces);
                }
            }
            else if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F))
            {
                // Strip ETX (\u0003) and all other C0/DEL/C1 control codes on display only.
            }
            else if (replaceBackslashWithYen && c == '\\')
            {
                sb.Append('¥');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
