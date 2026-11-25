using System.Buffers.Binary;
using Hypera.Bytecode;
using Hypera.VirtualFs;

namespace Hypera.Cli;

public enum BundlePayloadKind : byte
{
    Unknown = 0,
    Heb = 1,
    Hep = 2,
}

/// <summary>
/// 24-byte YU-RIS style trailer appended to a runner binary:
/// [PayloadOffset:8B LE][PayloadLength:8B LE]["HYPRBNDL":8B ASCII]
/// </summary>
public readonly record struct BundleTrailer(
    long PayloadOffset,
    long PayloadLength,
    BundlePayloadKind PayloadKind);

/// <summary>
/// Builds and detects single-file executable bundles using the 24-byte <c>HYPRBNDL</c> trailer.
/// </summary>
public static class SingleFileBundler
{
    public const int TrailerSizeInBytes = 24;
    public static ReadOnlySpan<byte> TrailerMagic => "HYPRBNDL"u8;

    public static BundleTrailer PackBundle(
        string sourceHebOrHepPath,
        string hostTemplatePath,
        string outputExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHebOrHepPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostTemplatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputExecutablePath);

        if (!File.Exists(sourceHebOrHepPath))
        {
            throw new FileNotFoundException("Source .heb or .hep payload not found.", sourceHebOrHepPath);
        }

        if (!File.Exists(hostTemplatePath))
        {
            throw new FileNotFoundException("Host executable template not found.", hostTemplatePath);
        }

        byte[] hostBytes = File.ReadAllBytes(hostTemplatePath);
        byte[] payloadBytes = File.ReadAllBytes(sourceHebOrHepPath);
        BundlePayloadKind kind = DetectPayloadKind(payloadBytes);

        string? outDir = Path.GetDirectoryName(Path.GetFullPath(outputExecutablePath));
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        long payloadOffset = hostBytes.LongLength;
        long payloadLength = payloadBytes.LongLength;

        using var outStream = new FileStream(outputExecutablePath, FileMode.Create, FileAccess.Write, FileShare.None);
        outStream.Write(hostBytes);
        outStream.Write(payloadBytes);

        Span<byte> trailer = stackalloc byte[TrailerSizeInBytes];
        BinaryPrimitives.WriteInt64LittleEndian(trailer[0..8], payloadOffset);
        BinaryPrimitives.WriteInt64LittleEndian(trailer[8..16], payloadLength);
        TrailerMagic.CopyTo(trailer[16..24]);
        outStream.Write(trailer);

        return new BundleTrailer(payloadOffset, payloadLength, kind);
    }

    public static bool TryDetectTrailer(string? executablePath, out BundleTrailer trailer)
    {
        trailer = default;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        using var fs = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return TryDetectTrailer(fs, out trailer);
    }

    public static bool TryDetectTrailer(Stream stream, out BundleTrailer trailer)
    {
        ArgumentNullException.ThrowIfNull(stream);
        trailer = default;

        if (!stream.CanSeek || stream.Length < TrailerSizeInBytes)
        {
            return false;
        }

        Span<byte> buf = stackalloc byte[TrailerSizeInBytes];
        stream.Position = stream.Length - TrailerSizeInBytes;
        stream.ReadExactly(buf);

        if (!buf[16..24].SequenceEqual(TrailerMagic))
        {
            return false;
        }

        long payloadOffset = BinaryPrimitives.ReadInt64LittleEndian(buf[0..8]);
        long payloadLength = BinaryPrimitives.ReadInt64LittleEndian(buf[8..16]);
        if (payloadOffset < 0 || payloadLength <= 0 || payloadOffset + payloadLength != stream.Length - TrailerSizeInBytes)
        {
            return false;
        }

        Span<byte> magicBuf = stackalloc byte[4];
        BundlePayloadKind kind = BundlePayloadKind.Unknown;
        if (payloadLength >= 4)
        {
            stream.Position = payloadOffset;
            stream.ReadExactly(magicBuf);
            kind = DetectPayloadKind(magicBuf);
        }

        trailer = new BundleTrailer(payloadOffset, payloadLength, kind);
        return true;
    }

    public static MemoryStream ExtractPayloadToMemory(string executablePath, BundleTrailer trailer)
    {
        using var fs = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Position = trailer.PayloadOffset;
        byte[] bytes = new byte[trailer.PayloadLength];
        fs.ReadExactly(bytes);
        return new MemoryStream(bytes, writable: false);
    }

    public static BundlePayloadKind DetectPayloadKind(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length < 4)
        {
            return BundlePayloadKind.Unknown;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[0..4]);
        return magic switch
        {
            BytecodeModuleHeader.ExpectedMagic => BundlePayloadKind.Heb,
            ArchivePackageHeader.ExpectedMagic => BundlePayloadKind.Hep,
            _ => BundlePayloadKind.Unknown,
        };
    }
}
