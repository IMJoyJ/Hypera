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
        for (int i = 0; i < data.Length - 1; i++)
        {
            byte b0 = data[i];
            if (b0 < 0x80)
            {
                continue;
            }

            byte b1 = data[i + 1];
            if ((b0 == 0x82 && b1 is >= 0x9F and <= 0xF1) || (b0 == 0x83 && b1 is >= 0x40 and <= 0x96))
            {
                score += 3;
                i++;
                continue;
            }

            if (b0 is >= 0xB0 and <= 0xDF && b1 is >= 0xA1 and <= 0xFE)
            {
                score -= 2;
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
