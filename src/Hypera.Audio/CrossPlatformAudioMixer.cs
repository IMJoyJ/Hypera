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
/// Cross-platform 10 SE + 1 looping BGM software mixer skeleton backed by an instance-scoped <see cref="IVirtualFileSystem"/>.
/// Supports WAV/MP3/OGG/FLAC/M4A asset resolution and thread-safe channel state management.
/// </summary>
public sealed class CrossPlatformAudioMixer : IAudioBackend
{
    private readonly IVirtualFileSystem? _vfs;
    private readonly Lock _sync = new();
    private readonly AudioChannelSnapshot[] _seChannels = new AudioChannelSnapshot[IAudioBackend.SoundEffectChannelCount];
    private AudioChannelSnapshot _bgmChannel = new(false, null, IAudioBackend.MaxVolume, false);
    private int _masterVolume = IAudioBackend.MaxVolume;
    private bool _disposed;

    public CrossPlatformAudioMixer(IVirtualFileSystem? vfs = null, int sampleRate = 48_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _vfs = vfs;
        SampleRate = sampleRate;
        for (int i = 0; i < _seChannels.Length; i++)
        {
            _seChannels[i] = new AudioChannelSnapshot(false, null, IAudioBackend.MaxVolume, false);
        }
    }

    public int SampleRate { get; }

    public int MasterVolume
    {
        get { lock (_sync) { return _masterVolume; } }
        set { lock (_sync) { _masterVolume = NullAudioBackend.ClampVolume(value); } }
    }

    public void PlaySoundEffect(int channelIndex, string virtualPath, int volume = IAudioBackend.MaxVolume, bool loop = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateChannel(channelIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);

        string normalized = VfsPathNormalizer.Normalize(virtualPath);
        if (_vfs is not null && !_vfs.FileExists(normalized))
        {
            throw new FileNotFoundException($"Audio asset not found in VFS: '{normalized}'.", normalized);
        }

        lock (_sync)
        {
            _seChannels[channelIndex] = new AudioChannelSnapshot(
                IsPlaying: true,
                VirtualPath: normalized,
                Volume: NullAudioBackend.ClampVolume(volume),
                IsLooping: loop);
        }
    }

    public void StopSoundEffect(int channelIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateChannel(channelIndex);

        lock (_sync)
        {
            _seChannels[channelIndex] = _seChannels[channelIndex] with { IsPlaying = false, VirtualPath = null, IsLooping = false };
        }
    }

    public void StopAllSoundEffects()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            for (int i = 0; i < _seChannels.Length; i++)
            {
                _seChannels[i] = _seChannels[i] with { IsPlaying = false, VirtualPath = null, IsLooping = false };
            }
        }
    }

    public void SetSoundEffectVolume(int channelIndex, int volume)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateChannel(channelIndex);

        lock (_sync)
        {
            _seChannels[channelIndex] = _seChannels[channelIndex] with { Volume = NullAudioBackend.ClampVolume(volume) };
        }
    }

    public AudioChannelSnapshot GetSoundEffectChannel(int channelIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateChannel(channelIndex);
        lock (_sync)
        {
            return _seChannels[channelIndex];
        }
    }

    public void PlayBgm(string virtualPath, int volume = IAudioBackend.MaxVolume, bool loop = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);

        string normalized = VfsPathNormalizer.Normalize(virtualPath);
        if (_vfs is not null && !_vfs.FileExists(normalized))
        {
            throw new FileNotFoundException($"BGM asset not found in VFS: '{normalized}'.", normalized);
        }

        lock (_sync)
        {
            _bgmChannel = new AudioChannelSnapshot(
                IsPlaying: true,
                VirtualPath: normalized,
                Volume: NullAudioBackend.ClampVolume(volume),
                IsLooping: loop);
        }
    }

    public void StopBgm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _bgmChannel = _bgmChannel with { IsPlaying = false, VirtualPath = null, IsLooping = false };
        }
    }

    public void SetBgmVolume(int volume)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _bgmChannel = _bgmChannel with { Volume = NullAudioBackend.ClampVolume(volume) };
        }
    }

    public AudioChannelSnapshot GetBgmChannel()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            return _bgmChannel;
        }
    }

    public void RenderInterleavedStereoF32(Span<float> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        outputBuffer.Clear();
    }

    private static void ValidateChannel(int channelIndex)
    {
        if ((uint)channelIndex >= IAudioBackend.SoundEffectChannelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channelIndex), channelIndex, "SE channel index must be 0..9.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopAllSoundEffects();
        StopBgm();
        _disposed = true;
    }
}
