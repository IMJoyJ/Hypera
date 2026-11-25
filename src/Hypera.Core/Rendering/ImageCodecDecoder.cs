using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace Hypera.Rendering;

/// <summary>
/// Options for encoding a single frame into a multi-frame animated WebP container.
/// </summary>
public readonly record struct AnimatedWebpFrameOptions(
    int OffsetX = 0,
    int OffsetY = 0,
    bool AlphaBlend = true,
    bool DisposeToBackground = false);

/// <summary>
/// Cross-platform static and multi-frame animated image codec built on <see cref="SKCodec"/>.
/// Supports PNG, BMP, JPG, GIF, static WebP, and multi-frame animated WebP with proper
/// <see cref="SKCodecFrameInfo.RequiredFrame"/> blending and disposal reconstruction.
/// </summary>
public static class ImageCodecDecoder
{
    public static IReadOnlyList<DecodedImageFrame> DecodeFrames(ReadOnlySpan<byte> encodedBytes)
    {
        if (encodedBytes.IsEmpty)
        {
            return [];
        }

        using var ms = new MemoryStream(encodedBytes.ToArray(), writable: false);
        return DecodeFrames(ms);
    }

    public static IReadOnlyList<DecodedImageFrame> DecodeFrames(Stream imageStream)
    {
        ArgumentNullException.ThrowIfNull(imageStream);

        // Copy to a seekable MemoryStream when needed because SKCodec requires random-access reads.
        Stream seekable = imageStream;
        MemoryStream? ownedCopy = null;
        if (!imageStream.CanSeek)
        {
            ownedCopy = new MemoryStream();
            imageStream.CopyTo(ownedCopy);
            ownedCopy.Position = 0;
            seekable = ownedCopy;
        }
        else
        {
            seekable.Position = 0;
        }

        try
        {
            using var managedStream = new SKManagedStream(seekable, disposeManagedStream: false);
            using SKCodec? codec = SKCodec.Create(managedStream);
            if (codec is null)
            {
                return [];
            }

            int width = codec.Info.Width;
            int height = codec.Info.Height;
            if (width <= 0 || height <= 0)
            {
                return [];
            }

            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            int frameCount = codec.FrameCount;
            if (frameCount <= 1)
            {
                var single = new SKBitmap(info);
                single.Erase(SKColors.Transparent);
                SKCodecResult result = codec.GetPixels(info, single.GetPixels());
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    single.Dispose();
                    return [];
                }

                int duration = frameCount == 1 && codec.FrameInfo.Length > 0
                    ? Math.Max(0, codec.FrameInfo[0].Duration)
                    : 0;
                return [new DecodedImageFrame(single, duration)];
            }

            SKCodecFrameInfo[] frameInfos = codec.FrameInfo;
            var frames = new List<DecodedImageFrame>(frameCount);

            for (int i = 0; i < frameCount; i++)
            {
                var bmp = new SKBitmap(info);
                int requiredFrame = i < frameInfos.Length ? frameInfos[i].RequiredFrame : -1;
                SKCodecOptions options;

                if (requiredFrame >= 0 && requiredFrame < frames.Count)
                {
                    // Copy prior required frame pixels so Skia blends/composites delta frames cleanly.
                    CopyBitmapPixels(frames[requiredFrame].Bitmap, bmp);
                    options = new SKCodecOptions(i, requiredFrame);
                }
                else
                {
                    bmp.Erase(SKColors.Transparent);
                    options = new SKCodecOptions(i);
                }

                SKCodecResult res = codec.GetPixels(info, bmp.GetPixels(), options);
                if (res is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    // Fallback attempt without prior frame hint if codec rejects incremental decode.
                    bmp.Erase(SKColors.Transparent);
                    res = codec.GetPixels(info, bmp.GetPixels(), new SKCodecOptions(i));
                    if (res is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                    {
                        bmp.Dispose();
                        continue;
                    }
                }

                int rawDuration = i < frameInfos.Length ? frameInfos[i].Duration : 100;
                int durationMs = rawDuration > 0 ? rawDuration : 100;
                frames.Add(new DecodedImageFrame(bmp, durationMs));
            }

            return frames;
        }
        finally
        {
            ownedCopy?.Dispose();
        }
    }

    public static byte[] EncodeToPng(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] EncodeToWebp(SKBitmap bitmap, int quality = 100)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        int clampedQuality = Math.Clamp(quality, 0, 100);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Webp, clampedQuality);
        return data.ToArray();
    }

    /// <summary>
    /// Encodes a sequence of frames into a standards-compliant multi-frame animated WebP container
    /// (RIFF WEBP with VP8X + ANIM + ANMF chunks) decodable by <see cref="SKCodec"/>.
    /// </summary>
    public static byte[] EncodeAnimatedWebp(
        IReadOnlyList<DecodedImageFrame> frames,
        int quality = 100,
        ushort loopCount = 0,
        IReadOnlyList<AnimatedWebpFrameOptions>? frameOptions = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            throw new ArgumentException("At least one frame is required to encode an animated WebP.", nameof(frames));
        }

        if (frames.Count == 1)
        {
            return EncodeToWebp(frames[0].Bitmap, quality);
        }

        int canvasW = 0;
        int canvasH = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            AnimatedWebpFrameOptions opt = frameOptions is not null && i < frameOptions.Count
                ? frameOptions[i]
                : default;
            canvasW = Math.Max(canvasW, opt.OffsetX + frames[i].Bitmap.Width);
            canvasH = Math.Max(canvasH, opt.OffsetY + frames[i].Bitmap.Height);
        }

        bool hasAlpha = false;
        var encodedSubChunks = new List<(byte[] Payload, int Width, int Height, int DurationMs, AnimatedWebpFrameOptions Opt)>(frames.Count);

        for (int i = 0; i < frames.Count; i++)
        {
            SKBitmap bmp = frames[i].Bitmap;
            byte[] singleWebp = EncodeToWebp(bmp, quality);
            byte[] payload = ExtractWebpFrameChunks(singleWebp, out bool frameHasAlpha);
            hasAlpha |= frameHasAlpha;

            AnimatedWebpFrameOptions opt = frameOptions is not null && i < frameOptions.Count
                ? frameOptions[i]
                : new AnimatedWebpFrameOptions(0, 0, AlphaBlend: true, DisposeToBackground: false);

            encodedSubChunks.Add((payload, bmp.Width, bmp.Height, Math.Max(1, frames[i].DurationMs), opt));
        }

        using var body = new MemoryStream();

        // 1. VP8X chunk (10 bytes payload)
        Span<byte> vp8x = stackalloc byte[10];
        vp8x[0] = (byte)(0x02 | (hasAlpha ? 0x10 : 0x00)); // Animation + optional Alpha flag
        WriteUInt24LE(vp8x[4..7], canvasW - 1);
        WriteUInt24LE(vp8x[7..10], canvasH - 1);
        WriteRiffChunk(body, "VP8X", vp8x);

        // 2. ANIM chunk (6 bytes payload)
        Span<byte> anim = stackalloc byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(anim[0..4], 0u); // transparent background
        BinaryPrimitives.WriteUInt16LittleEndian(anim[4..6], loopCount);
        WriteRiffChunk(body, "ANIM", anim);

        // 3. ANMF chunks
        foreach (var item in encodedSubChunks)
        {
            byte[] anmfPayload = new byte[16 + item.Payload.Length];
            WriteUInt24LE(anmfPayload.AsSpan(0, 3), item.Opt.OffsetX / 2);
            WriteUInt24LE(anmfPayload.AsSpan(3, 3), item.Opt.OffsetY / 2);
            WriteUInt24LE(anmfPayload.AsSpan(6, 3), item.Width - 1);
            WriteUInt24LE(anmfPayload.AsSpan(9, 3), item.Height - 1);
            WriteUInt24LE(anmfPayload.AsSpan(12, 3), item.DurationMs);

            // Bit 0: disposal (1 = dispose to background, 0 = do not dispose)
            // Bit 1: blending (0 = alpha blend, 1 = do not blend / overwrite)
            byte flags = 0;
            if (item.Opt.DisposeToBackground)
            {
                flags |= 0x01;
            }

            if (!item.Opt.AlphaBlend)
            {
                flags |= 0x02;
            }

            anmfPayload[15] = flags;
            Buffer.BlockCopy(item.Payload, 0, anmfPayload, 16, item.Payload.Length);
            WriteRiffChunk(body, "ANMF", anmfPayload);
        }

        byte[] bodyBytes = body.ToArray();
        byte[] riff = new byte[12 + bodyBytes.Length];
        Encoding.ASCII.GetBytes("RIFF", riff.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4, 4), (uint)(4 + bodyBytes.Length));
        Encoding.ASCII.GetBytes("WEBP", riff.AsSpan(8, 4));
        Buffer.BlockCopy(bodyBytes, 0, riff, 12, bodyBytes.Length);
        return riff;
    }

    private static byte[] ExtractWebpFrameChunks(byte[] singleWebp, out bool hasAlpha)
    {
        hasAlpha = false;
        if (singleWebp.Length < 12 ||
            singleWebp[0] != 'R' || singleWebp[1] != 'I' || singleWebp[2] != 'F' || singleWebp[3] != 'F' ||
            singleWebp[8] != 'W' || singleWebp[9] != 'E' || singleWebp[10] != 'B' || singleWebp[11] != 'P')
        {
            throw new InvalidDataException("Invalid single-frame WebP stream from Skia encoder.");
        }

        using var ms = new MemoryStream();
        int offset = 12;
        while (offset + 8 <= singleWebp.Length)
        {
            string fourCc = Encoding.ASCII.GetString(singleWebp, offset, 4);
            int chunkLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(singleWebp.AsSpan(offset + 4, 4));
            int paddedLen = chunkLen + (chunkLen & 1);
            if (offset + 8 + chunkLen > singleWebp.Length)
            {
                break;
            }

            if (fourCc is "ALPH" or "VP8 " or "VP8L")
            {
                if (fourCc is "ALPH" or "VP8L")
                {
                    hasAlpha = true;
                }

                ms.Write(singleWebp, offset, 8 + chunkLen);
                if ((chunkLen & 1) != 0)
                {
                    ms.WriteByte(0);
                }
            }

            offset += 8 + paddedLen;
        }

        return ms.ToArray();
    }

    private static void WriteRiffChunk(Stream stream, string fourCc, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        Encoding.ASCII.GetBytes(fourCc, header[0..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], (uint)payload.Length);
        stream.Write(header);
        stream.Write(payload);
        if ((payload.Length & 1) != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static void WriteUInt24LE(Span<byte> dst, int value)
    {
        uint v = (uint)Math.Max(0, value);
        dst[0] = (byte)(v & 0xFF);
        dst[1] = (byte)((v >> 8) & 0xFF);
        dst[2] = (byte)((v >> 16) & 0xFF);
    }

    private static unsafe void CopyBitmapPixels(SKBitmap source, SKBitmap destination)
    {
        int byteCount = Math.Min(source.ByteCount, destination.ByteCount);
        Buffer.MemoryCopy((void*)source.GetPixels(), (void*)destination.GetPixels(), destination.ByteCount, byteCount);
    }
}
