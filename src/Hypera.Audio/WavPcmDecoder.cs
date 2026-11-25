using System.Buffers.Binary;
using System.Text;

namespace Hypera.Audio;

/// <summary>
/// Decoded interleaved stereo 32-bit float PCM clip held completely in memory (never file-locked).
/// </summary>
public sealed class DecodedAudioClip
{
    public DecodedAudioClip(int sampleRate, float[] interleavedStereoSamples, AudioContainerFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentNullException.ThrowIfNull(interleavedStereoSamples);

        SampleRate = sampleRate;
        InterleavedStereoSamples = interleavedStereoSamples;
        Format = format;
    }

    public int SampleRate { get; }
    public float[] InterleavedStereoSamples { get; }
    public int FrameCount => InterleavedStereoSamples.Length / 2;
    public AudioContainerFormat Format { get; }
}

/// <summary>
/// Pluggable codec decoder for turning raw audio bytes into <see cref="DecodedAudioClip"/>.
/// </summary>
public interface IAudioStreamDecoder
{
    bool CanDecode(AudioContainerFormat format, ReadOnlySpan<byte> data);
    DecodedAudioClip Decode(ReadOnlySpan<byte> data, int targetSampleRate);
}

/// <summary>
/// Built-in zero-dependency RIFF/WAVE PCM and IEEE float decoder supporting 8/16/24/32-bit PCM
/// and 32/64-bit IEEE float across mono, stereo, and multi-channel layouts, with linear resampling.
/// </summary>
public sealed class WavPcmDecoder : IAudioStreamDecoder
{
    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    public bool CanDecode(AudioContainerFormat format, ReadOnlySpan<byte> data)
    {
        if (format == AudioContainerFormat.Wav)
        {
            return true;
        }

        return data.Length >= 12 &&
               data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
               data[8] == (byte)'W' && data[9] == (byte)'A' && data[10] == (byte)'V' && data[11] == (byte)'E';
    }

    public DecodedAudioClip Decode(ReadOnlySpan<byte> data, int targetSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSampleRate);

        if (data.Length < 12 ||
            !data[0..4].SequenceEqual("RIFF"u8) ||
            !data[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Invalid RIFF/WAVE header.");
        }

        ushort audioFormat = 0;
        ushort channels = 0;
        int sampleRate = 0;
        ushort bitsPerSample = 0;
        bool hasFmt = false;
        ReadOnlySpan<byte> pcmData = default;
        bool hasData = false;

        int offset = 12;
        while (offset + 8 <= data.Length)
        {
            ReadOnlySpan<byte> chunkId = data.Slice(offset, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            offset += 8;

            int available = Math.Min((int)Math.Min(chunkSize, (uint)(data.Length - offset)), data.Length - offset);
            ReadOnlySpan<byte> chunkBody = data.Slice(offset, available);

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunkBody.Length < 16)
                {
                    throw new InvalidDataException("Truncated WAV fmt chunk.");
                }

                audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(chunkBody[0..2]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunkBody[2..4]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(chunkBody[4..8]);
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(chunkBody[14..16]);

                if (audioFormat == FormatExtensible && chunkBody.Length >= 40)
                {
                    ushort subFormatCode = BinaryPrimitives.ReadUInt16LittleEndian(chunkBody[24..26]);
                    audioFormat = subFormatCode;
                }

                hasFmt = true;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                pcmData = chunkBody;
                hasData = true;
            }

            int paddedSize = (int)((chunkSize + 1) & ~1u);
            if (offset + paddedSize < offset || offset + paddedSize > data.Length)
            {
                break;
            }

            offset += paddedSize;
        }

        if (!hasFmt || !hasData)
        {
            throw new InvalidDataException("WAV stream missing required 'fmt ' or 'data' chunk.");
        }

        if (channels == 0 || sampleRate <= 0)
        {
            throw new InvalidDataException($"Unsupported WAV format parameters: channels={channels}, sampleRate={sampleRate}.");
        }

        if (audioFormat != FormatPcm && audioFormat != FormatIeeeFloat)
        {
            throw new NotSupportedException($"Unsupported WAV audio format tag: 0x{audioFormat:X4}.");
        }

        int bytesPerSample = bitsPerSample / 8;
        int bytesPerFrame = bytesPerSample * channels;
        if (bytesPerSample == 0 || bytesPerFrame == 0)
        {
            throw new InvalidDataException($"Invalid WAV bits per sample: {bitsPerSample}.");
        }

        int sourceFrames = pcmData.Length / bytesPerFrame;
        float[] stereoSource = new float[sourceFrames * 2];

        for (int frame = 0; frame < sourceFrames; frame++)
        {
            int frameOffset = frame * bytesPerFrame;
            float left;
            float right;

            if (channels == 1)
            {
                float mono = ReadSampleNormalized(pcmData.Slice(frameOffset, bytesPerSample), audioFormat, bitsPerSample);
                left = mono;
                right = mono;
            }
            else
            {
                left = ReadSampleNormalized(pcmData.Slice(frameOffset, bytesPerSample), audioFormat, bitsPerSample);
                right = ReadSampleNormalized(pcmData.Slice(frameOffset + bytesPerSample, bytesPerSample), audioFormat, bitsPerSample);
            }

            stereoSource[frame * 2] = left;
            stereoSource[(frame * 2) + 1] = right;
        }

        float[] finalSamples = sampleRate == targetSampleRate
            ? stereoSource
            : ResampleLinearStereo(stereoSource, sampleRate, targetSampleRate);

        return new DecodedAudioClip(targetSampleRate, finalSamples, AudioContainerFormat.Wav);
    }

    private static float ReadSampleNormalized(ReadOnlySpan<byte> bytes, ushort audioFormat, ushort bitsPerSample)
    {
        if (audioFormat == FormatIeeeFloat)
        {
            return bitsPerSample switch
            {
                32 => Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(bytes), -1f, 1f),
                64 => Math.Clamp((float)BinaryPrimitives.ReadDoubleLittleEndian(bytes), -1f, 1f),
                _ => throw new NotSupportedException($"Unsupported IEEE float bit depth: {bitsPerSample}."),
            };
        }

        return bitsPerSample switch
        {
            8 => (bytes[0] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
            24 => SignExtend24(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16)) / 8388608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
            _ => throw new NotSupportedException($"Unsupported PCM bit depth: {bitsPerSample}."),
        };
    }

    private static int SignExtend24(int raw) => (raw & 0x800000) != 0 ? raw | unchecked((int)0xFF000000) : raw;

    private static float[] ResampleLinearStereo(float[] source, int sourceRate, int targetRate)
    {
        int sourceFrames = source.Length / 2;
        if (sourceFrames == 0)
        {
            return [];
        }

        int targetFrames = Math.Max(1, (int)Math.Round(sourceFrames * ((double)targetRate / sourceRate)));
        float[] output = new float[targetFrames * 2];
        double ratio = (double)sourceRate / targetRate;

        for (int i = 0; i < targetFrames; i++)
        {
            double srcPos = i * ratio;
            int idx0 = Math.Min((int)srcPos, sourceFrames - 1);
            int idx1 = Math.Min(idx0 + 1, sourceFrames - 1);
            float frac = (float)(srcPos - idx0);

            output[i * 2] = source[idx0 * 2] + ((source[idx1 * 2] - source[idx0 * 2]) * frac);
            output[(i * 2) + 1] = source[(idx0 * 2) + 1] + ((source[(idx1 * 2) + 1] - source[(idx0 * 2) + 1]) * frac);
        }

        return output;
    }

    /// <summary>
    /// Helper that encodes interleaved stereo float32 samples into a standard 16-bit PCM RIFF/WAVE payload.
    /// </summary>
    public static byte[] EncodeStereoPcm16Wav(ReadOnlySpan<float> interleavedStereoSamples, int sampleRate = 48_000)
    {
        int sampleCount = interleavedStereoSamples.Length;
        int dataBytes = sampleCount * 2;
        byte[] wav = new byte[44 + dataBytes];

        "RIFF"u8.CopyTo(wav.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4, 4), (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(wav.AsSpan(8, 4));
        "fmt "u8.CopyTo(wav.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16, 4), 16u);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20, 2), FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22, 2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24, 4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28, 4), (uint)(sampleRate * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32, 2), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34, 2), 16);
        "data"u8.CopyTo(wav.AsSpan(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40, 4), (uint)dataBytes);

        for (int i = 0; i < sampleCount; i++)
        {
            float clamped = Math.Clamp(interleavedStereoSamples[i], -1f, 1f);
            short pcm = (short)Math.Round(clamped * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + (i * 2), 2), pcm);
        }

        return wav;
    }
}
