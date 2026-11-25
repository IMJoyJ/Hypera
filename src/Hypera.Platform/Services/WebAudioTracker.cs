using Hypera.Audio;
using Hypera.VirtualFs;

namespace Hypera.Platform.Services;

public enum WebAudioEventKind : byte
{
    PlayBgm = 0,
    StopBgm = 1,
    SetBgmVolume = 2,
    PlaySoundEffect = 3,
    StopSoundEffect = 4,
    StopAllSoundEffects = 5,
    SetSoundEffectVolume = 6,
}

public sealed record WebAudioEvent(
    long SequenceId,
    WebAudioEventKind Kind,
    int ChannelIndex,
    string? VirtualPath,
    int Volume,
    bool IsLooping,
    AudioContainerFormat Format);

public sealed record WebAudioStateSnapshot(
    AudioChannelSnapshot Bgm,
    IReadOnlyList<AudioChannelSnapshot> SoundEffects,
    IReadOnlyList<WebAudioEvent> RecentEvents,
    long LatestSequenceId);

/// <summary>
/// Thread-safe <see cref="IAudioBackend"/> capturing active BGM + 10 SE channel states and monotonic playback events
/// for Web/Blazor/MAUI WebView streaming via <c>WebAudio</c> / <c>HTMLAudioElement</c>.
/// </summary>
public sealed class WebAudioTracker : IAudioBackend
{
    private const int MaxRetainedEvents = 128;

    private readonly Lock _sync = new();
    private readonly AudioChannelSnapshot[] _seChannels = new AudioChannelSnapshot[IAudioBackend.SoundEffectChannelCount];
    private readonly List<WebAudioEvent> _events = [];
    private AudioChannelSnapshot _bgm = new(false, null, IAudioBackend.MaxVolume, false);
    private long _sequence;
    private bool _disposed;

    public WebAudioTracker()
    {
        for (int i = 0; i < _seChannels.Length; i++)
        {
            _seChannels[i] = new AudioChannelSnapshot(false, null, IAudioBackend.MaxVolume, false);
        }
    }

    public void PlaySoundEffect(int channelIndex, string virtualPath, int volume = IAudioBackend.MaxVolume, bool loop = false)
    {
        ValidateChannel(channelIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);

        string normalized = VfsPathNormalizer.Normalize(virtualPath);
        int clamped = NullAudioBackend.ClampVolume(volume);
        AudioContainerFormat fmt = NullAudioBackend.DetectFormatFromPath(normalized);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _seChannels[channelIndex] = new AudioChannelSnapshot(true, normalized, clamped, loop);
            AppendEventLocked(WebAudioEventKind.PlaySoundEffect, channelIndex, normalized, clamped, loop, fmt);
        }
    }

    public void StopSoundEffect(int channelIndex)
    {
        ValidateChannel(channelIndex);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int vol = _seChannels[channelIndex].Volume;
            _seChannels[channelIndex] = new AudioChannelSnapshot(false, null, vol, false);
            AppendEventLocked(WebAudioEventKind.StopSoundEffect, channelIndex, null, vol, false, AudioContainerFormat.Unknown);
        }
    }

    public void StopAllSoundEffects()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (int i = 0; i < _seChannels.Length; i++)
            {
                int vol = _seChannels[i].Volume;
                _seChannels[i] = new AudioChannelSnapshot(false, null, vol, false);
            }

            AppendEventLocked(WebAudioEventKind.StopAllSoundEffects, -1, null, 0, false, AudioContainerFormat.Unknown);
        }
    }

    public void SetSoundEffectVolume(int channelIndex, int volume)
    {
        ValidateChannel(channelIndex);
        int clamped = NullAudioBackend.ClampVolume(volume);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            AudioChannelSnapshot current = _seChannels[channelIndex];
            _seChannels[channelIndex] = current with { Volume = clamped };
            AppendEventLocked(
                WebAudioEventKind.SetSoundEffectVolume,
                channelIndex,
                current.VirtualPath,
                clamped,
                current.IsLooping,
                NullAudioBackend.DetectFormatFromPath(current.VirtualPath));
        }
    }

    public AudioChannelSnapshot GetSoundEffectChannel(int channelIndex)
    {
        ValidateChannel(channelIndex);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _seChannels[channelIndex];
        }
    }

    public void PlayBgm(string virtualPath, int volume = IAudioBackend.MaxVolume, bool loop = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        string normalized = VfsPathNormalizer.Normalize(virtualPath);
        int clamped = NullAudioBackend.ClampVolume(volume);
        AudioContainerFormat fmt = NullAudioBackend.DetectFormatFromPath(normalized);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _bgm = new AudioChannelSnapshot(true, normalized, clamped, loop);
            AppendEventLocked(WebAudioEventKind.PlayBgm, -1, normalized, clamped, loop, fmt);
        }
    }

    public void StopBgm()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int vol = _bgm.Volume;
            _bgm = new AudioChannelSnapshot(false, null, vol, false);
            AppendEventLocked(WebAudioEventKind.StopBgm, -1, null, vol, false, AudioContainerFormat.Unknown);
        }
    }

    public void SetBgmVolume(int volume)
    {
        int clamped = NullAudioBackend.ClampVolume(volume);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _bgm = _bgm with { Volume = clamped };
            AppendEventLocked(
                WebAudioEventKind.SetBgmVolume,
                -1,
                _bgm.VirtualPath,
                clamped,
                _bgm.IsLooping,
                NullAudioBackend.DetectFormatFromPath(_bgm.VirtualPath));
        }
    }

    public AudioChannelSnapshot GetBgmChannel()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bgm;
        }
    }

    public WebAudioStateSnapshot CaptureState(long afterSequenceId = 0)
    {
        lock (_sync)
        {
            AudioChannelSnapshot[] seCopy = (AudioChannelSnapshot[])_seChannels.Clone();
            List<WebAudioEvent> recent = _events
                .Where(e => e.SequenceId > afterSequenceId)
                .ToList();
            return new WebAudioStateSnapshot(_bgm, seCopy, recent, _sequence);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _events.Clear();
        }
    }

    private void AppendEventLocked(
        WebAudioEventKind kind,
        int channelIndex,
        string? virtualPath,
        int volume,
        bool isLooping,
        AudioContainerFormat format)
    {
        long seq = ++_sequence;
        _events.Add(new WebAudioEvent(seq, kind, channelIndex, virtualPath, volume, isLooping, format));
        if (_events.Count > MaxRetainedEvents)
        {
            _events.RemoveRange(0, _events.Count - MaxRetainedEvents);
        }
    }

    private static void ValidateChannel(int channelIndex)
    {
        if ((uint)channelIndex >= IAudioBackend.SoundEffectChannelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelIndex),
                channelIndex,
                $"SE channel index must be in [0..{IAudioBackend.SoundEffectChannelCount - 1}].");
        }
    }
}
