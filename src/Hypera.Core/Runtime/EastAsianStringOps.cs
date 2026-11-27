using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Hypera.Runtime;

/// <summary>
/// Pure cross-platform East-Asian string width, kana/width conversion, and Emuera string built-ins.
/// Zero dependency on Windows NLS (<c>Microsoft.VisualBasic.Strings.StrConv</c>).
/// </summary>
public static class EastAsianStringOps
{
    private static readonly Encoding Cp932Encoding = InitCodePage(932);
    private static readonly Dictionary<char, string> WideToNarrowKana = BuildWideToNarrowKana();

    private static Encoding InitCodePage(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }

    public static Encoding GetCodePageEncoding(int codePage)
    {
        if (codePage == 932)
        {
            return Cp932Encoding;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }

    /// <summary>
    /// Converts full-width ASCII, full-width space, and full-width Katakana (including dakuten/handakuten)
    /// into half-width equivalents (<c>TOHALF</c>).
    /// </summary>
    public static string ToHalf(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length * 2);
        foreach (char c in value)
        {
            if (c == '\u3000')
            {
                sb.Append(' ');
            }
            else if (c is >= '\uFF01' and <= '\uFF5E')
            {
                sb.Append((char)(c - 0xFEE0));
            }
            else if (WideToNarrowKana.TryGetValue(c, out string? narrow))
            {
                sb.Append(narrow);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Converts half-width ASCII, half-width space, and half-width Katakana (merging <c>\uFF9E</c>/<c>\uFF9F</c>
    /// dakuten/handakuten marks) into full-width equivalents (<c>TOFULL</c>).
    /// </summary>
    public static string ToFull(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == ' ')
            {
                sb.Append('\u3000');
            }
            else if (c is >= '!' and <= '~')
            {
                sb.Append((char)(c + 0xFEE0));
            }
            else if (c is >= '\uFF61' and <= '\uFF9F')
            {
                int len = i + 1 < value.Length && value[i + 1] is '\uFF9E' or '\uFF9F' ? 2 : 1;
                sb.Append(value.AsSpan(i, len).ToString().Normalize(NormalizationForm.FormKC));
                i += len - 1;
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Converts Hiragana (<c>U+3041..U+3096</c>, <c>U+309D..U+309E</c>) to Katakana (<c>TOKATAKANA</c>).
    /// </summary>
    public static string ToKatakana(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return string.Create(value.Length, value, static (dst, src) =>
        {
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                dst[i] = c switch
                {
                    >= '\u3041' and <= '\u3096' => (char)(c + 0x60),
                    '\u309D' or '\u309E' => (char)(c + 0x60),
                    _ => c,
                };
            }
        });
    }

    /// <summary>
    /// Converts Katakana (<c>U+30A1..U+30F6</c>, <c>U+30FD..U+30FE</c>) to Hiragana (<c>TOHIRAGANA</c>).
    /// </summary>
    public static string ToHiragana(string? value, bool widenHalfwidth = true)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string source = widenHalfwidth ? ToFull(value) : value;
        return string.Create(source.Length, source, static (dst, src) =>
        {
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                dst[i] = c switch
                {
                    >= '\u30A1' and <= '\u30F6' => (char)(c - 0x60),
                    '\u30FD' or '\u30FE' => (char)(c - 0x60),
                    _ => c,
                };
            }
        });
    }

    public static string ToUpper(string? value) => string.IsNullOrEmpty(value) ? string.Empty : value.ToUpperInvariant();

    public static string ToLower(string? value) => string.IsNullOrEmpty(value) ? string.Empty : value.ToLowerInvariant();

    /// <summary>
    /// Computes East-Asian byte length (<c>STRLENS</c> / <c>STRLEN</c>) using CP932 / configured codepage width.
    /// </summary>
    public static int GetStrlenS(ReadOnlySpan<char> str, int codePage = 932)
    {
        if (str.IsEmpty)
        {
            return 0;
        }

        if (Ascii.IsValid(str))
        {
            return str.Length;
        }

        Encoding primary = GetCodePageEncoding(codePage);
        int length = 0;
        Span<char> singleChar = stackalloc char[1];
        Span<byte> byteBuf = stackalloc byte[8];
        Span<char> roundTrip = stackalloc char[4];

        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];
            if (c <= 0x7F)
            {
                length += 1;
                continue;
            }

            if (c is >= '\uFF61' and <= '\uFF9F')
            {
                length += 1;
                continue;
            }

            singleChar[0] = c;
            int written = primary.GetBytes(singleChar, byteBuf);
            int charsBack = primary.GetChars(byteBuf[..written], roundTrip);
            if (charsBack == 1 && roundTrip[0] == c)
            {
                length += written;
                continue;
            }

            if (codePage != 932)
            {
                int jpWritten = Cp932Encoding.GetBytes(singleChar, byteBuf);
                int jpBack = Cp932Encoding.GetChars(byteBuf[..jpWritten], roundTrip);
                if (jpBack == 1 && roundTrip[0] == c)
                {
                    length += jpWritten;
                    continue;
                }
            }

            if (char.IsHighSurrogate(c) && i + 1 < str.Length && char.IsLowSurrogate(str[i + 1]))
            {
                // Surrogate pair (e.g. CJK Extension B / Emoji): treat as a single width-2 glyph instead of 2+2.
                length += 2;
                i++;
                continue;
            }

            // Fallback for characters not representable in the active ANSI codepage:
            // Emuera's Config.Encode.GetByteCount returns 1 for unmapped chars (CP932 '?' fallback),
            // and MS Gothic / monospace fonts render Latin-Extended ('š', 'À', 'Ø'), narrow symbols ('•', '♫', '♡', '♥', '—'),
            // etc. at 1 half-width column. Only treat true East-Asian Wide / Fullwidth glyphs as width 2.
            length += written > 1 ? written : (IsEastAsianWideOrFullwidth(c) ? 2 : 1);
        }

        return length;
    }

    private static bool IsEastAsianWideOrFullwidth(char c) =>
        c is (>= '\u1100' and <= '\u115F')
            or '\u2329' or '\u232A'
            or (>= '\u2E80' and <= '\u303E')
            or (>= '\u3041' and <= '\u33FF')
            or (>= '\u3400' and <= '\u4DBF')
            or (>= '\u4E00' and <= '\uA4CF')
            or (>= '\uA960' and <= '\uA97F')
            or (>= '\uAC00' and <= '\uD7A3')
            or (>= '\uF900' and <= '\uFAFF')
            or (>= '\uFE10' and <= '\uFE19')
            or (>= '\uFE30' and <= '\uFE6F')
            or (>= '\uFF01' and <= '\uFF60')
            or (>= '\uFFE0' and <= '\uFFE6');

    public static int GetStrlenSU(string? str) => str?.Length ?? 0;

    public static int GetCharByteWidth(char c, int codePage = 932)
    {
        if (c <= 0x7F || c is >= '\uFF61' and <= '\uFF9F')
        {
            return 1;
        }

        ReadOnlySpan<char> span = new(in c);
        return GetStrlenS(span, codePage);
    }

    public static int GetUtf16IndexFromByteOffset(string str, int byteIndex, int codePage = 932)
    {
        if (byteIndex <= 0 || string.IsNullOrEmpty(str))
        {
            return 0;
        }

        if (Ascii.IsValid(str))
        {
            return Math.Min(byteIndex, str.Length);
        }

        int totalBytes = GetStrlenS(str, codePage);
        if (byteIndex >= totalBytes)
        {
            return str.Length;
        }

        int utfPos = 0;
        int byteAcc = 0;
        while (utfPos < str.Length)
        {
            byteAcc += GetCharByteWidth(str[utfPos], codePage);
            utfPos++;
            if (byteAcc >= byteIndex)
            {
                break;
            }
        }

        return utfPos;
    }

    /// <summary>
    /// Byte-width <c>SUBSTRING</c> matching upstream <c>LangManager.GetSubStringLang</c>.
    /// </summary>
    public static string SubstringS(string? str, long startByte, long byteLength = -1, int codePage = 932)
    {
        if (string.IsNullOrEmpty(str) || byteLength == 0)
        {
            return string.Empty;
        }

        int totalByte = GetStrlenS(str, codePage);
        if (startByte >= totalByte)
        {
            return string.Empty;
        }

        long len = (byteLength < 0 || byteLength > totalByte) ? totalByte : byteLength;
        int utfIndex = 0;

        if (startByte <= 0)
        {
            if (len == totalByte)
            {
                return str;
            }
        }
        else
        {
            int jisAcc = 0;
            while (utfIndex < str.Length)
            {
                jisAcc += GetCharByteWidth(str[utfIndex], codePage);
                utfIndex++;
                if (jisAcc >= startByte)
                {
                    break;
                }
            }

            if (utfIndex >= str.Length)
            {
                return string.Empty;
            }
        }

        var sb = new StringBuilder();
        int outBytes = 0;
        while (utfIndex < str.Length)
        {
            char c = str[utfIndex];
            sb.Append(c);
            outBytes += GetCharByteWidth(c, codePage);
            utfIndex++;
            if (outBytes >= len)
            {
                break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// UTF-16 code-unit <c>SUBSTRINGU</c>.
    /// </summary>
    public static string SubstringU(string? str, long startChar, long charLength = -1)
    {
        if (string.IsNullOrEmpty(str) || charLength == 0 || startChar >= str.Length)
        {
            return string.Empty;
        }

        int len = (charLength < 0 || charLength > str.Length) ? str.Length : (int)charLength;
        int start = startChar <= 0 ? 0 : (int)startChar;
        if (start == 0 && len == str.Length)
        {
            return str;
        }

        if (start + len > str.Length)
        {
            len = str.Length - start;
        }

        return str.Substring(start, len);
    }

    public static string CharAtU(string? str, long index)
    {
        if (string.IsNullOrEmpty(str) || (ulong)index >= (ulong)str.Length)
        {
            return string.Empty;
        }

        return str[(int)index].ToString();
    }

    public static int StrFind(string? target, string? word, long startByte = 0, int codePage = 932)
    {
        string src = target ?? string.Empty;
        string needle = word ?? string.Empty;

        if (startByte < 0)
        {
            return -1;
        }

        int utfStart = startByte == 0 ? 0 : GetUtf16IndexFromByteOffset(src, (int)Math.Min(startByte, int.MaxValue), codePage);
        if (utfStart < 0 || utfStart >= src.Length)
        {
            return -1;
        }

        int idx = src.IndexOf(needle, utfStart, StringComparison.Ordinal);
        if (idx <= 0)
        {
            return idx;
        }

        return GetStrlenS(src.AsSpan(0, idx), codePage);
    }

    public static int StrFindU(string? target, string? word, long startChar = 0)
    {
        string src = target ?? string.Empty;
        string needle = word ?? string.Empty;
        if (startChar < 0 || startChar >= src.Length)
        {
            return -1;
        }

        return src.IndexOf(needle, (int)startChar, StringComparison.Ordinal);
    }

    public static int StrCount(string? target, string? pattern, RegexCache regexCache)
    {
        string src = target ?? string.Empty;
        string pat = pattern ?? string.Empty;

        if (pat.Length == 0)
        {
            return src.Length + 1;
        }

        if (RegexCache.TryGetLiteralPattern(pat, out string? literal))
        {
            int count = 0;
            ReadOnlySpan<char> remaining = src.AsSpan();
            ReadOnlySpan<char> needle = literal.AsSpan();
            while (true)
            {
                int pos = remaining.IndexOf(needle, StringComparison.Ordinal);
                if (pos < 0)
                {
                    break;
                }

                count++;
                remaining = remaining[(pos + needle.Length)..];
            }

            return count;
        }

        return regexCache.GetRegex(pat).Matches(src).Count;
    }

    public static int Split(string? input, string? delimiter, Span<string> destination)
    {
        string src = input ?? string.Empty;
        string delim = delimiter ?? string.Empty;

        if (delim.Length == 0)
        {
            int count = src.Length;
            int writeCount = Math.Min(count, destination.Length);
            for (int i = 0; i < writeCount; i++)
            {
                destination[i] = src[i].ToString();
            }

            return count;
        }

        string[] parts = src.Split(delim, StringSplitOptions.None);
        int limit = Math.Min(parts.Length, destination.Length);
        for (int i = 0; i < limit; i++)
        {
            destination[i] = parts[i];
        }

        return parts.Length;
    }

    public static string StrJoin(ReadOnlySpan<string> elements, string delimiter = ",", int start = 0, int count = -1)
    {
        if (elements.IsEmpty || start < 0 || start >= elements.Length || count == 0)
        {
            return string.Empty;
        }

        int actualCount = count < 0 ? elements.Length - start : Math.Min(count, elements.Length - start);
        if (actualCount <= 0)
        {
            return string.Empty;
        }

        return string.Join(delimiter ?? string.Empty, elements.Slice(start, actualCount).ToArray());
    }

    public static string StrJoinInt(ReadOnlySpan<long> elements, string delimiter = ",", int start = 0, int count = -1)
    {
        if (elements.IsEmpty || start < 0 || start >= elements.Length || count == 0)
        {
            return string.Empty;
        }

        int actualCount = count < 0 ? elements.Length - start : Math.Min(count, elements.Length - start);
        if (actualCount <= 0)
        {
            return string.Empty;
        }

        string sep = delimiter ?? string.Empty;
        var sb = new StringBuilder();
        for (int i = 0; i < actualCount; i++)
        {
            if (i > 0)
            {
                sb.Append(sep);
            }

            sb.Append(elements[start + i].ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    public static string Replace(
        string? input,
        string? pattern,
        string? replacement,
        int mode,
        RegexCache regexCache,
        ReadOnlySpan<string> replacementArray = default)
    {
        string src = input ?? string.Empty;
        string pat = pattern ?? string.Empty;
        string rep = replacement ?? string.Empty;

        if (mode == 2)
        {
            return pat.Length == 0 ? src : src.Replace(pat, rep, StringComparison.Ordinal);
        }

        if (mode == 1)
        {
            Regex reg1 = regexCache.GetRegex(pat);
            string[] items = replacementArray.ToArray();
            int idx = 0;
            return reg1.Replace(src, _ => idx < items.Length ? (items[idx++] ?? string.Empty) : string.Empty);
        }

        if (RegexCache.TryGetLiteralPattern(pat, out string? lit) && !rep.Contains('$'))
        {
            return lit.Length == 0 ? src : src.Replace(lit, rep, StringComparison.Ordinal);
        }

        return regexCache.GetRegex(pat).Replace(src, rep);
    }

    public static string Escape(string? value) => string.IsNullOrEmpty(value) ? string.Empty : Regex.Escape(value);

    public static string Unicode(long codePoint)
    {
        if (codePoint <= 0 || codePoint > 0x10FFFF || (codePoint is >= 0xD800 and <= 0xDFFF))
        {
            throw new ArgumentOutOfRangeException(nameof(codePoint), codePoint, "Invalid Unicode code point.");
        }

        // Control chars barred by Emuera UNICODE except standard whitespace if valid
        return char.ConvertFromUtf32((int)codePoint);
    }

    public static long EncodeToUni(string? value, int charIndex = 0)
    {
        if (string.IsNullOrEmpty(value) || (uint)charIndex >= (uint)value.Length)
        {
            return -1L;
        }

        if (char.IsSurrogatePair(value, charIndex))
        {
            return char.ConvertToUtf32(value, charIndex);
        }

        return value[charIndex];
    }

    public static long UnicodeByte(string? value) => string.IsNullOrEmpty(value) ? 0L : (long)value.Length * 2L;

    public static string BarStr(long value, long max, long length, char barChar1 = '*', char barChar2 = '.')
    {
        if (max <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, "BAR maximum must be positive.");
        }

        if (length <= 0 || length >= 100)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "BAR length must be in 1..99.");
        }

        int totalLen = (int)length;
        int filled;
        if (value <= 0)
        {
            filled = 0;
        }
        else if (value >= max)
        {
            filled = totalLen;
        }
        else
        {
            filled = (int)((value * totalLen) / max);
        }

        return string.Create(totalLen + 2, (filled, totalLen, barChar1, barChar2), static (dst, state) =>
        {
            dst[0] = '[';
            dst.Slice(1, state.filled).Fill(state.barChar1);
            dst.Slice(1 + state.filled, state.totalLen - state.filled).Fill(state.barChar2);
            dst[^1] = ']';
        });
    }

    public static string MoneyStr(long amount, string? format = null, string moneyLabel = "$", bool moneyFirst = true)
    {
        string formatted = string.IsNullOrEmpty(format)
            ? amount.ToString(CultureInfo.InvariantCulture)
            : amount.ToString(format, CultureInfo.InvariantCulture);
        return moneyFirst ? string.Concat(moneyLabel, formatted) : string.Concat(formatted, moneyLabel);
    }

    public static string ConvertRadix(long value, long radix)
    {
        if (radix is not (2 or 8 or 10 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(radix), radix, "CONVERT radix must be 2, 8, 10, or 16.");
        }

        return Convert.ToString(value, (int)radix);
    }

    public static long ParseEmueraInt(string? str, int codePage = 932)
    {
        if (string.IsNullOrEmpty(str))
        {
            return 0L;
        }

        // Upstream TOINT rejects strings containing full-width characters
        if (str.Length < GetStrlenS(str, codePage))
        {
            return 0L;
        }

        ReadOnlySpan<char> s = str.AsSpan().Trim();
        if (s.IsEmpty)
        {
            return 0L;
        }

        int idx = 0;
        bool neg = false;
        if (s[0] is '+' or '-')
        {
            neg = s[0] == '-';
            idx++;
            if (idx >= s.Length || !char.IsAsciiDigit(s[idx]))
            {
                return 0L;
            }
        }
        else if (!char.IsAsciiDigit(s[0]))
        {
            return 0L;
        }

        // Hex 0x or Binary 0b support
        if (idx + 2 <= s.Length && s[idx] == '0' && (s[idx + 1] is 'x' or 'X' or 'b' or 'B'))
        {
            bool isHex = s[idx + 1] is 'x' or 'X';
            ReadOnlySpan<char> digits = s[(idx + 2)..];
            if (digits.IsEmpty)
            {
                return 0L;
            }

            ulong acc = 0;
            foreach (char d in digits)
            {
                int val = isHex
                    ? (d is >= '0' and <= '9' ? d - '0' : d is >= 'a' and <= 'f' ? d - 'a' + 10 : d is >= 'A' and <= 'F' ? d - 'A' + 10 : -1)
                    : (d is '0' or '1' ? d - '0' : -1);
                if (val < 0)
                {
                    return 0L;
                }

                acc = (acc * (ulong)(isHex ? 16 : 2)) + (ulong)val;
            }

            long signed = unchecked((long)acc);
            return neg ? unchecked(-signed) : signed;
        }

        int dotPos = s[idx..].IndexOf('.');
        ReadOnlySpan<char> intPart = dotPos >= 0 ? s.Slice(idx, dotPos) : s[idx..];
        if (dotPos >= 0)
        {
            ReadOnlySpan<char> fracPart = s[(idx + dotPos + 1)..];
            foreach (char fc in fracPart)
            {
                if (!char.IsAsciiDigit(fc))
                {
                    return 0L;
                }
            }
        }

        if (!long.TryParse(intPart, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
        {
            return 0L;
        }

        return neg ? -parsed : parsed;
    }

    public static bool IsNumeric(string? str)
    {
        if (string.IsNullOrWhiteSpace(str))
        {
            return false;
        }

        ReadOnlySpan<char> s = str.AsSpan().Trim();
        if (s.Length >= 3 && s[0] == '0' && (s[1] is 'x' or 'X'))
        {
            for (int i = 2; i < s.Length; i++)
            {
                if (!Uri.IsHexDigit(s[i]))
                {
                    return false;
                }
            }

            return true;
        }

        return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
    }

    private static Dictionary<char, string> BuildWideToNarrowKana()
    {
        var map = new Dictionary<char, string>(128);
        for (char hw = '\uFF61'; hw <= '\uFF9F'; hw++)
        {
            string s = hw.ToString();
            string norm = s.Normalize(NormalizationForm.FormKC);
            if (norm.Length == 1)
            {
                map[norm[0]] = s;
            }
        }

        for (char hw = '\uFF66'; hw <= '\uFF9D'; hw++)
        {
            string dakuten = string.Concat(hw, '\uFF9E');
            string normD = dakuten.Normalize(NormalizationForm.FormKC);
            if (normD.Length == 1)
            {
                map[normD[0]] = dakuten;
            }

            string handakuten = string.Concat(hw, '\uFF9F');
            string normH = handakuten.Normalize(NormalizationForm.FormKC);
            if (normH.Length == 1)
            {
                map[normH[0]] = handakuten;
            }
        }

        return map;
    }
}
