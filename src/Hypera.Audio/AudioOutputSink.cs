namespace Hypera.Audio;

/// <summary>
/// Pluggable audio output sink receiving interleaved 32-bit floating-point stereo PCM frames.
/// </summary>
public interface IAudioOutputSink : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    bool IsHardwareAttached { get; }

    /// <summary>
    /// Submits interleaved float32 samples (length = frameCount * Channels) to the output device or buffer.
    /// </summary>
    void WriteFloat32Interleaved(ReadOnlySpan<float> interleavedSamples);
}

/// <summary>
/// Safe headless memory sink used by default when no hardware audio device is attached.
/// Retains the most recent mixed audio ring buffer for inspection and testing.
/// </summary>
public sealed class HeadlessMemoryAudioOutputSink : IAudioOutputSink
{
    private readonly Lock _sync = new();
    private readonly float[] _ringBuffer;
    private int _writeCursor;
    private long _totalSamplesWritten;
    private bool _disposed;

    public HeadlessMemoryAudioOutputSink(int sampleRate = 48_000, int channels = 2, int capacityFrames = 48_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);

        SampleRate = sampleRate;
        Channels = channels;
        _ringBuffer = new float[capacityFrames * channels];
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public bool IsHardwareAttached => false;

    public long TotalFramesWritten
    {
        get { lock (_sync) { return _totalSamplesWritten / Channels; } }
    }

    public void WriteFloat32Interleaved(ReadOnlySpan<float> interleavedSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (interleavedSamples.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            foreach (float sample in interleavedSamples)
            {
                _ringBuffer[_writeCursor] = sample;
                _writeCursor = (_writeCursor + 1) % _ringBuffer.Length;
            }

            _totalSamplesWritten += interleavedSamples.Length;
        }
    }

    public float[] SnapshotRecentSamples(int maxSampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSampleCount);
        lock (_sync)
        {
            int available = (int)Math.Min(_totalSamplesWritten, _ringBuffer.Length);
            int count = Math.Min(available, maxSampleCount);
            if (count == 0)
            {
                return [];
            }

            float[] result = new float[count];
            int start = (_writeCursor - count + _ringBuffer.Length) % _ringBuffer.Length;
            for (int i = 0; i < count; i++)
            {
                result[i] = _ringBuffer[(start + i) % _ringBuffer.Length];
            }

            return result;
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
