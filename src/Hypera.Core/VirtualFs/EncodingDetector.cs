using System.Text;

namespace Hypera.VirtualFs;

public enum DetectedEncodingKind : byte
{
    Utf8Bom = 0,
    Utf8 = 1,
    ShiftJisCp932 = 2,
    Gb18030 = 3,
    Utf16LeBom = 4,
    Utf16BeBom = 5,
}

public readonly record struct EncodingDetectionResult(DetectedEncodingKind Kind, Encoding Encoding, int BomLength);

public interface IEncodingDetector
{
    EncodingDetectionResult Detect(ReadOnlySpan<byte> data);
    string Decode(ReadOnlySpan<byte> data, out EncodingDetectionResult detection);
}

/// <summary>
/// Stateless text encoding detector supporting UTF-8 BOM, strict UTF-8, Shift-JIS (CP932), and GB18030.
/// </summary>
public sealed class EncodingDetector : IEncodingDetector
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: false);
    private static readonly Encoding Cp932;
    private static readonly Encoding Gb18030;

    static EncodingDetector()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp932 = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        Gb18030 = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static EncodingDetector Default { get; } = new();

    public EncodingDetectionResult Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return new EncodingDetectionResult(DetectedEncodingKind.Utf8Bom, Utf8WithBom, 3);
        }

        if (data.Length >= 2)
        {
            if (data[0] == 0xFF && data[1] == 0xFE)
            {
                return new EncodingDetectionResult(DetectedEncodingKind.Utf16LeBom, Encoding.Unicode, 2);
            }

            if (data[0] == 0xFE && data[1] == 0xFF)
            {
                return new EncodingDetectionResult(DetectedEncodingKind.Utf16BeBom, Encoding.BigEndianUnicode, 2);
            }
        }

        if (IsValidEncoding(data, StrictUtf8))
        {
            return new EncodingDetectionResult(DetectedEncodingKind.Utf8, StrictUtf8, 0);
        }

        bool validCp932 = IsValidEncoding(data, Cp932);
        bool validGb18030 = IsValidEncoding(data, Gb18030);

        if (validCp932 && !validGb18030)
        {
            return new EncodingDetectionResult(DetectedEncodingKind.ShiftJisCp932, Encoding.GetEncoding(932), 0);
        }

        if (!validCp932 && validGb18030)
        {
            return new EncodingDetectionResult(DetectedEncodingKind.Gb18030, Encoding.GetEncoding("GB18030"), 0);
        }

        if (validCp932 && validGb18030)
        {
            int sjisScore = ScoreShiftJisKanaAndLeadBytes(data);
            return sjisScore >= 0
                ? new EncodingDetectionResult(DetectedEncodingKind.ShiftJisCp932, Encoding.GetEncoding(932), 0)
                : new EncodingDetectionResult(DetectedEncodingKind.Gb18030, Encoding.GetEncoding("GB18030"), 0);
        }

        return new EncodingDetectionResult(DetectedEncodingKind.ShiftJisCp932, Encoding.GetEncoding(932), 0);
    }

    public string Decode(ReadOnlySpan<byte> data, out EncodingDetectionResult detection)
    {
        detection = Detect(data);
        ReadOnlySpan<byte> payload = data[detection.BomLength..];
        return detection.Encoding.GetString(payload);
    }

    private static bool IsValidEncoding(ReadOnlySpan<byte> data, Encoding strictEncoding)
    {
        try
        {
            _ = strictEncoding.GetCharCount(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static int ScoreShiftJisKanaAndLeadBytes(ReadOnlySpan<byte> data)
    {
        int score = 0;
        for (int i = 0; i < data.Length; i++)
        {
            byte b0 = data[i];
            if (b0 < 0x80)
            {
                continue;
            }

            // GB18030 4-byte sequence: 0x81..0xFE, 0x30..0x39, 0x81..0xFE, 0x30..0x39
            if (i + 3 < data.Length &&
                b0 is >= 0x81 and <= 0xFE &&
                data[i + 1] is >= 0x30 and <= 0x39 &&
                data[i + 2] is >= 0x81 and <= 0xFE &&
                data[i + 3] is >= 0x30 and <= 0x39)
            {
                score -= 5;
                i += 3;
                continue;
            }

            if (i + 1 >= data.Length)
            {
                break;
            }

            byte b1 = data[i + 1];

            // Shift-JIS Hiragana (0x829F..0x82F1) and Katakana (0x8340..0x8396)
            if ((b0 == 0x82 && b1 is >= 0x9F and <= 0xF1) ||
                (b0 == 0x83 && b1 is >= 0x40 and <= 0x96))
            {
                score += 4;
                i++;
                continue;
            }

            // Shift-JIS common punctuation & fullwidth ASCII (0x8140..0x81AC, 0x824F..0x829A)
            if ((b0 == 0x81 && b1 is >= 0x40 and <= 0xAC && b1 != 0x7F) ||
                (b0 == 0x82 && b1 is >= 0x4F and <= 0x9A && b1 != 0x7F))
            {
                score += 2;
                i++;
                continue;
            }

            // GB2312/GB18030 Level 1 Simplified Chinese Hanzi (0xB0..0xD7, 0xA1..0xFE)
            // and GB2312 punctuation (0xA1..0xA9, 0xA1..0xFE)
            if ((b0 is >= 0xB0 and <= 0xD7 && b1 is >= 0xA1 and <= 0xFE) ||
                (b0 is >= 0xA1 and <= 0xA9 && b1 is >= 0xA1 and <= 0xFE))
            {
                score -= 3;
                i++;
                continue;
            }

            // GB2312 Level 2 Hanzi (0xD8..0xF7, 0xA1..0xFE)
            if (b0 is >= 0xD8 and <= 0xDF && b1 is >= 0xA1 and <= 0xFE)
            {
                score -= 2;
                i++;
                continue;
            }

            // Shift-JIS lead bytes with trail byte in 0x40..0x7E (less common in GB2312)
            if (b0 is (>= 0x88 and <= 0x9F) or (>= 0xE0 and <= 0xEA) && b1 is >= 0x40 and <= 0x7E)
            {
                score += 1;
                i++;
                continue;
            }

            if (b0 is (>= 0x81 and <= 0x9F) or (>= 0xE0 and <= 0xFC))
            {
                i++;
            }
        }

        return score;
    }
}
