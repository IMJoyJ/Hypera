using System.Buffers.Binary;
using Hypera.Bytecode;

namespace Hypera.VirtualFs;

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
/// Read-only seekable slice over a parent <see cref="Stream"/> so embedded <c>.hep</c> payloads inside
/// a <c>HYPRBNDL</c> executable can be mounted without copying the entire archive into memory.
/// </summary>
public sealed class BoundedSubStream : Stream
{
    private readonly Stream _inner;
    private readonly long _offset;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private long _position;
    private bool _disposed;

    public BoundedSubStream(Stream inner, long offset, long length, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead || !inner.CanSeek)
        {
            throw new ArgumentException("Inner stream must support reading and seeking.", nameof(inner));
        }

        if (offset < 0 || length < 0 || offset + length > inner.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Sub-stream bounds exceed inner stream length.");
        }

        _inner = inner;
        _offset = offset;
        _length = length;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _length;
        }
    }

    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _position;
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (value < 0 || value > _length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long remaining = _length - _position;
        if (remaining <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        int toRead = (int)Math.Min(buffer.Length, remaining);
        _inner.Position = _offset + _position;
        int read = _inner.Read(buffer[..toRead]);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0 || target > _length)
        {
            throw new IOException("Attempted to seek outside bounded sub-stream range.");
        }

        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing && !_leaveOpen)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Shared codec for building, detecting, and streaming single-file <c>HYPRBNDL</c> executable bundles
/// used by both <c>Hypera.Cli</c> and <c>Hypera.Standalone</c>.
/// </summary>
public static class SingleFileBundleCodec
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

        string? outDir = Path.GetDirectoryName(Path.GetFullPath(outputExecutablePath));
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        long payloadOffset;
        long payloadLength;
        BundlePayloadKind kind;

        using (var outStream = new FileStream(outputExecutablePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var hostStream = new FileStream(hostTemplatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                payloadOffset = hostStream.Length;
                hostStream.CopyTo(outStream);
            }

            using (var payloadStream = new FileStream(sourceHebOrHepPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                payloadLength = payloadStream.Length;
                Span<byte> magicBuf = stackalloc byte[4];
                int read = payloadStream.Read(magicBuf);
                kind = DetectPayloadKind(magicBuf[..read]);
                payloadStream.Position = 0;
                payloadStream.CopyTo(outStream);
            }

            Span<byte> trailer = stackalloc byte[TrailerSizeInBytes];
            BinaryPrimitives.WriteInt64LittleEndian(trailer[0..8], payloadOffset);
            BinaryPrimitives.WriteInt64LittleEndian(trailer[8..16], payloadLength);
            TrailerMagic.CopyTo(trailer[16..24]);
            outStream.Write(trailer);
        }

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

    /// <summary>
    /// Opens a zero-copy seekable <see cref="BoundedSubStream"/> directly over the embedded payload slice.
    /// </summary>
    public static Stream OpenPayloadStream(string executablePath, BundleTrailer trailer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fs = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return new BoundedSubStream(fs, trailer.PayloadOffset, trailer.PayloadLength, leaveOpen: false);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public static MemoryStream ExtractPayloadToMemory(string executablePath, BundleTrailer trailer)
    {
        using var fs = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Position = trailer.PayloadOffset;
        byte[] bytes = new byte[checked((int)trailer.PayloadLength)];
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
