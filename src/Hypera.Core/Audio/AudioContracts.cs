namespace Hypera.Audio;

public enum AudioContainerFormat : byte
{
    Unknown = 0,
    Wav = 1,
    Mp3 = 2,
    Ogg = 3,
    Flac = 4,
    M4a = 5,
}

public readonly record struct AudioChannelSnapshot(
    bool IsPlaying,
    string? VirtualPath,
    int Volume,
    bool IsLooping);

/// <summary>
/// Instance-scoped audio playback contract providing 10 sound-effect (SE) channels (0..9)
/// and 1 looping BGM channel, with volume clamped to 0..100 across WAV/MP3/OGG/FLAC/M4A.
/// </summary>
public interface IAudioBackend : IDisposable
{
    public const int SoundEffectChannelCount = 10;
    public const int MinVolume = 0;
    public const int MaxVolume = 100;

    void PlaySoundEffect(int channelIndex, string virtualPath, int volume = MaxVolume, bool loop = false);
    void StopSoundEffect(int channelIndex);
    void StopAllSoundEffects();
    void SetSoundEffectVolume(int channelIndex, int volume);
    AudioChannelSnapshot GetSoundEffectChannel(int channelIndex);

    void PlayBgm(string virtualPath, int volume = MaxVolume, bool loop = true);
    void StopBgm();
    void SetBgmVolume(int volume);
    AudioChannelSnapshot GetBgmChannel();
}

/// <summary>
/// Deterministic, zero-device audio backend for headless server execution, CLI runs, and unit tests.
/// Tracks channel state (10 SE + 1 BGM) and validates channel bounds and volume clamping.
/// </summary>
public sealed class NullAudioBackend : IAudioBackend
{
    private readonly AudioChannelSnapshot[] _seChannels = new AudioChannelSnapshot[IAudioBackend.SoundEffectChannelCount];
    private AudioChannelSnapshot _bgmChannel = new(false, null, IAudioBackend.MaxVolume, false);
    private bool _disposed;

    public NullAudioBackend()
    {
        for (int i = 0; i < _seChannels.Length; i++)
        {
            _seChannels[i] = new AudioChannelSnapshot(false, null, IAudioBackend.MaxVolume, false);
        }
    }

    public static AudioContainerFormat DetectFormatFromPath(string? virtualPath)
    {
        string ext = Path.GetExtension(virtualPath ?? string.Empty);
        return ext.ToLowerInvariant() switch
        {
            ".wav" => AudioContainerFormat.Wav,
            ".mp3" => AudioContainerFormat.Mp3,
            ".ogg" => AudioContainerFormat.Ogg,
            ".flac" => AudioContainerFormat.Flac,
            ".m4a" or ".aac" or ".mp4" => AudioContainerFormat.M4a,
            _ => AudioContainerFormat.Unknown,
        };
    }

    public void PlaySoundEffect(int channelIndex, string virtualPath, int volume = IAudioBackend.MaxVolume, bool loop = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateChannelIndex(channelIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);

        _seChannels[channelIndex] = new AudioChannelSnapshot(
            IsPlaying: true,
            VirtualPath: virtualPath,
            Volume: ClampVolume(volume),
            IsLooping: loop);
    }

    public void StopSoundEffect(int channelIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateChannelIndex(channelIndex);

        int existingVolume = _seChannels[channelIndex].Volume;
        _seChannels[channelIndex] = new AudioChannelSnapshot(false, null, existingVolume, false);
    }

    public void StopAllSoundEffects()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int i = 0; i < _seChannels.Length; i++)
        {
            int vol = _seChannels[i].Volume;
            _seChannels[i] = new AudioChannelSnapshot(false, null, vol, false);
        }
    }

    public void SetSoundEffectVolume(int channelIndex, int volume)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateChannelIndex(channelIndex);

        AudioChannelSnapshot current = _seChannels[channelIndex];
        _seChannels[channelIndex] = current with { Volume = ClampVolume(volume) };
    }

    public AudioChannelSnapshot GetSoundEffectChannel(int channelIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateChannelIndex(channelIndex);
        return _seChannels[channelIndex];
    }

    public void PlayBgm(string virtualPath, int volume = IAudioBackend.MaxVolume, bool loop = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);

        _bgmChannel = new AudioChannelSnapshot(
            IsPlaying: true,
            VirtualPath: virtualPath,
            Volume: ClampVolume(volume),
            IsLooping: loop);
    }

    public void StopBgm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _bgmChannel = new AudioChannelSnapshot(false, null, _bgmChannel.Volume, false);
    }

    public void SetBgmVolume(int volume)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _bgmChannel = _bgmChannel with { Volume = ClampVolume(volume) };
    }

    public AudioChannelSnapshot GetBgmChannel()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _bgmChannel;
    }

    public static int ClampVolume(int volume) =>
        Math.Clamp(volume, IAudioBackend.MinVolume, IAudioBackend.MaxVolume);

    private static void ValidateChannelIndex(int channelIndex)
    {
        if ((uint)channelIndex >= IAudioBackend.SoundEffectChannelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelIndex),
                channelIndex,
                $"SE channel index must be in [0..{IAudioBackend.SoundEffectChannelCount - 1}].");
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
