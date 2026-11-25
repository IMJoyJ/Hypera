using Hypera.VirtualFs;

namespace Hypera.Audio;

/// <summary>
/// Pluggable low-level PCM output sink consumed by <see cref="CrossPlatformAudioMixer"/>.
/// </summary>
public interface IAudioDeviceSink : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    void MixFloat32Interleaved(Span<float> destination);
}

/// <summary>
/// Cross-platform 10 SE + 1 looping BGM software mixer backed by an instance-scoped <see cref="IVirtualFileSystem"/>.
/// Supports WAV PCM decoding, ring-buffer SE allocation, unlocked VFS reads, and pluggable <see cref="IAudioOutputSink"/>.
/// </summary>
public sealed class CrossPlatformAudioMixer : IAudioBackend, IAudioDeviceSink
{
    private readonly MixingAudioEngine _engine;
    private bool _disposed;

    public CrossPlatformAudioMixer(
        IVirtualFileSystem? vfs = null,
        int sampleRate = 48_000,
        IAudioOutputSink? outputSink = null,
        IEnumerable<IAudioStreamDecoder>? extraDecoders = null)
    {
        _engine = new MixingAudioEngine(vfs, sampleRate, outputSink, extraDecoders);
    }

    public int SampleRate => _engine.SampleRate;
    public int Channels => _engine.OutputSink.Channels;
    public IAudioOutputSink OutputSink => _engine.OutputSink;
    public MixingAudioEngine Engine => _engine;

    public int MasterVolume
    {
        get => _engine.MasterVolume;
        set => _engine.MasterVolume = value;
    }

    public int SoundEffectMasterVolume
    {
        get => _engine.SoundEffectMasterVolume;
        set => _engine.SoundEffectMasterVolume = value;
    }

    public bool ExistSound(string? virtualPathOrName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _engine.ExistSound(virtualPathOrName);
    }

    public int PlaySoundEffectRing(string virtualPath, int volume = IAudioBackend.MaxVolume, bool loop = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _engine.PlaySoundEffectRing(virtualPath, volume, loop);
    }

    public void PlaySoundEffect(int channelIndex, string virtualPath, int volume = IAudioBackend.MaxVolume, bool loop = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine.PlaySoundEffect(channelIndex, virtualPath, volume, loop);
    }

    public void StopSoundEffect(int channelIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine.StopSoundEffect(channelIndex);
    }

    public void StopAllSoundEffects()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine.StopAllSoundEffects();
    }

    public void SetSoundEffectVolume(int channelIndex, int volume)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine.SetSoundEffectVolume(channelIndex, volume);
    }

    public AudioChannelSnapshot GetSoundEffectChannel(int channelIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _engine.GetSoundEffectChannel(channelIndex);
    }

    public void PlayBgm(string virtualPath, int volume = IAudioBackend.MaxVolume, bool loop = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine.PlayBgm(virtualPath, volume, loop);
    }

    public void StopBgm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine.StopBgm();
    }

    public void SetBgmVolume(int volume)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine.SetBgmVolume(volume);
    }

    public AudioChannelSnapshot GetBgmChannel()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _engine.GetBgmChannel();
    }

    public void RenderInterleavedStereoF32(Span<float> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine.MixFloat32Interleaved(outputBuffer);
    }

    public void MixFloat32Interleaved(Span<float> destination) =>
        RenderInterleavedStereoF32(destination);

    public void PumpToSink(int frameCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine.PumpToSink(frameCount);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _engine.Dispose();
        _disposed = true;
    }
}
