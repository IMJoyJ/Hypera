using Hypera.VirtualFs;

namespace Hypera.Audio;

/// <summary>
/// Software float32 stereo mixer engine managing 10 SE channels (with ring-buffer auto allocation)
/// plus 1 looping BGM channel, independent 0..100 volume controls, and a pluggable <see cref="IAudioOutputSink"/>.
/// </summary>
public sealed class MixingAudioEngine : IDisposable
{
    private sealed class ActiveVoice
    {
        public bool IsPlaying;
        public string? VirtualPath;
        public int Volume = IAudioBackend.MaxVolume;
        public bool IsLooping;
        public DecodedAudioClip? Clip;
        public int FrameCursor;
        public long SequenceId;
    }

    private readonly IVirtualFileSystem? _vfs;
    private readonly IAudioOutputSink _outputSink;
    private readonly bool _ownsSink;
    private readonly List<IAudioStreamDecoder> _decoders;
    private readonly Lock _sync = new();
    private readonly ActiveVoice[] _seVoices = new ActiveVoice[IAudioBackend.SoundEffectChannelCount];
    private readonly ActiveVoice _bgmVoice = new();

    private int _masterVolume = IAudioBackend.MaxVolume;
    private int _soundEffectMasterVolume = IAudioBackend.MaxVolume;
    private long _nextSequenceId;
    private int _ringCursor;
    private bool _disposed;

    public MixingAudioEngine(
        IVirtualFileSystem? vfs = null,
        int sampleRate = 48_000,
        IAudioOutputSink? outputSink = null,
        IEnumerable<IAudioStreamDecoder>? extraDecoders = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _vfs = vfs;
        SampleRate = sampleRate;
        _ownsSink = outputSink is null;
        _outputSink = outputSink ?? new HeadlessMemoryAudioOutputSink(sampleRate, channels: 2);
        _decoders = [new WavPcmDecoder()];
        if (extraDecoders is not null)
        {
            _decoders.AddRange(extraDecoders);
        }

        for (int i = 0; i < _seVoices.Length; i++)
        {
            _seVoices[i] = new ActiveVoice();
        }
    }

    public int SampleRate { get; }
    public IAudioOutputSink OutputSink => _outputSink;

    public int MasterVolume
    {
        get { lock (_sync) { return _masterVolume; } }
        set { lock (_sync) { _masterVolume = NullAudioBackend.ClampVolume(value); } }
    }

    public int SoundEffectMasterVolume
    {
        get { lock (_sync) { return _soundEffectMasterVolume; } }
        set { lock (_sync) { _soundEffectMasterVolume = NullAudioBackend.ClampVolume(value); } }
    }

    public bool ExistSound(string? virtualPathOrName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vfs is null)
        {
            return false;
        }

        return SoundAssetResolver.ExistSound(_vfs, virtualPathOrName);
    }

    public void PlaySoundEffect(int channelIndex, string virtualPath, int volume = IAudioBackend.MaxVolume, bool loop = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateChannel(channelIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);

        (string resolvedPath, DecodedAudioClip? clip) = LoadClipIfPossible(virtualPath, "Audio asset");

        lock (_sync)
        {
            ActiveVoice voice = _seVoices[channelIndex];
            voice.IsPlaying = true;
            voice.VirtualPath = resolvedPath;
            voice.Volume = NullAudioBackend.ClampVolume(volume);
            voice.IsLooping = loop;
            voice.Clip = clip;
            voice.FrameCursor = 0;
            voice.SequenceId = ++_nextSequenceId;
        }
    }

    /// <summary>
    /// Allocates an SE slot using a 10-slot ring policy (prefers the next idle slot from the ring cursor;
    /// if all 10 slots are busy, steals the oldest active slot) and starts playback.
    /// </summary>
    public int PlaySoundEffectRing(string virtualPath, int volume = IAudioBackend.MaxVolume, bool loop = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);

        (string resolvedPath, DecodedAudioClip? clip) = LoadClipIfPossible(virtualPath, "Audio asset");

        lock (_sync)
        {
            int chosenSlot = -1;
            for (int offset = 0; offset < _seVoices.Length; offset++)
            {
                int candidate = (_ringCursor + offset) % _seVoices.Length;
                if (!_seVoices[candidate].IsPlaying)
                {
                    chosenSlot = candidate;
                    break;
                }
            }

            if (chosenSlot < 0)
            {
                chosenSlot = 0;
                long oldestSeq = _seVoices[0].SequenceId;
                for (int i = 1; i < _seVoices.Length; i++)
                {
                    if (_seVoices[i].SequenceId < oldestSeq)
                    {
                        oldestSeq = _seVoices[i].SequenceId;
                        chosenSlot = i;
                    }
                }
            }

            _ringCursor = (chosenSlot + 1) % _seVoices.Length;

            ActiveVoice voice = _seVoices[chosenSlot];
            voice.IsPlaying = true;
            voice.VirtualPath = resolvedPath;
            voice.Volume = NullAudioBackend.ClampVolume(volume);
            voice.IsLooping = loop;
            voice.Clip = clip;
            voice.FrameCursor = 0;
            voice.SequenceId = ++_nextSequenceId;
            return chosenSlot;
        }
    }

    public void StopSoundEffect(int channelIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateChannel(channelIndex);

        lock (_sync)
        {
            ActiveVoice voice = _seVoices[channelIndex];
            voice.IsPlaying = false;
            voice.VirtualPath = null;
            voice.IsLooping = false;
            voice.Clip = null;
            voice.FrameCursor = 0;
        }
    }

    public void StopAllSoundEffects()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            for (int i = 0; i < _seVoices.Length; i++)
            {
                ActiveVoice voice = _seVoices[i];
                voice.IsPlaying = false;
                voice.VirtualPath = null;
                voice.IsLooping = false;
                voice.Clip = null;
                voice.FrameCursor = 0;
            }
        }
    }

    public void SetSoundEffectVolume(int channelIndex, int volume)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateChannel(channelIndex);

        lock (_sync)
        {
            _seVoices[channelIndex].Volume = NullAudioBackend.ClampVolume(volume);
        }
    }

    public AudioChannelSnapshot GetSoundEffectChannel(int channelIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateChannel(channelIndex);

        lock (_sync)
        {
            ActiveVoice v = _seVoices[channelIndex];
            return new AudioChannelSnapshot(v.IsPlaying, v.VirtualPath, v.Volume, v.IsLooping);
        }
    }

    public void PlayBgm(string virtualPath, int volume = IAudioBackend.MaxVolume, bool loop = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);

        (string resolvedPath, DecodedAudioClip? clip) = LoadClipIfPossible(virtualPath, "BGM asset");

        lock (_sync)
        {
            _bgmVoice.IsPlaying = true;
            _bgmVoice.VirtualPath = resolvedPath;
            _bgmVoice.Volume = NullAudioBackend.ClampVolume(volume);
            _bgmVoice.IsLooping = loop;
            _bgmVoice.Clip = clip;
            _bgmVoice.FrameCursor = 0;
            _bgmVoice.SequenceId = ++_nextSequenceId;
        }
    }

    public void StopBgm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _bgmVoice.IsPlaying = false;
            _bgmVoice.VirtualPath = null;
            _bgmVoice.IsLooping = false;
            _bgmVoice.Clip = null;
            _bgmVoice.FrameCursor = 0;
        }
    }

    public void SetBgmVolume(int volume)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _bgmVoice.Volume = NullAudioBackend.ClampVolume(volume);
        }
    }

    public AudioChannelSnapshot GetBgmChannel()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            return new AudioChannelSnapshot(_bgmVoice.IsPlaying, _bgmVoice.VirtualPath, _bgmVoice.Volume, _bgmVoice.IsLooping);
        }
    }

    public void MixFloat32Interleaved(Span<float> outputBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        outputBuffer.Clear();
        int frameCount = outputBuffer.Length / 2;
        if (frameCount <= 0)
        {
            return;
        }

        lock (_sync)
        {
            float masterGain = _masterVolume / 100f;
            float seMasterGain = _soundEffectMasterVolume / 100f;

            for (int i = 0; i < _seVoices.Length; i++)
            {
                MixVoice(_seVoices[i], outputBuffer, frameCount, masterGain * seMasterGain);
            }

            MixVoice(_bgmVoice, outputBuffer, frameCount, masterGain);
        }

        for (int i = 0; i < outputBuffer.Length; i++)
        {
            outputBuffer[i] = Math.Clamp(outputBuffer[i], -1f, 1f);
        }
    }

    /// <summary>
    /// Mixes <paramref name="frameCount"/> stereo frames and pushes them into <see cref="OutputSink"/>.
    /// </summary>
    public void PumpToSink(int frameCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);

        float[] temp = new float[frameCount * 2];
        MixFloat32Interleaved(temp);
        _outputSink.WriteFloat32Interleaved(temp);
    }

    private static void MixVoice(ActiveVoice voice, Span<float> destination, int frameCount, float groupGain)
    {
        if (!voice.IsPlaying || voice.Clip is null || voice.Clip.FrameCount == 0)
        {
            return;
        }

        float gain = groupGain * (voice.Volume / 100f);
        float[] samples = voice.Clip.InterleavedStereoSamples;
        int totalFrames = voice.Clip.FrameCount;

        for (int frame = 0; frame < frameCount; frame++)
        {
            if (voice.FrameCursor >= totalFrames)
            {
                if (voice.IsLooping)
                {
                    voice.FrameCursor = 0;
                }
                else
                {
                    voice.IsPlaying = false;
                    voice.VirtualPath = null;
                    voice.Clip = null;
                    voice.FrameCursor = 0;
                    break;
                }
            }

            if (gain > 0f)
            {
                int srcIdx = voice.FrameCursor * 2;
                int dstIdx = frame * 2;
                destination[dstIdx] += samples[srcIdx] * gain;
                destination[dstIdx + 1] += samples[srcIdx + 1] * gain;
            }

            voice.FrameCursor++;
        }

        if (voice.IsPlaying && voice.FrameCursor >= totalFrames)
        {
            if (voice.IsLooping)
            {
                voice.FrameCursor %= totalFrames;
            }
            else
            {
                voice.IsPlaying = false;
                voice.VirtualPath = null;
                voice.Clip = null;
                voice.FrameCursor = 0;
            }
        }
    }

    private (string ResolvedPath, DecodedAudioClip? Clip) LoadClipIfPossible(string virtualPath, string errorLabel)
    {
        string normalized = VfsPathNormalizer.Normalize(virtualPath);
        if (_vfs is null)
        {
            return (normalized, null);
        }

        string resolvedPath;
        if (_vfs.FileExists(normalized))
        {
            resolvedPath = normalized;
        }
        else if (!SoundAssetResolver.TryResolveSoundPath(_vfs, normalized, out resolvedPath))
        {
            throw new FileNotFoundException($"{errorLabel} not found in VFS: '{normalized}'.", normalized);
        }

        AudioContainerFormat format = NullAudioBackend.DetectFormatFromPath(resolvedPath);
        if (format == AudioContainerFormat.Unknown)
        {
            throw new NotSupportedException($"Unsupported audio container format for '{resolvedPath}'.");
        }

        byte[] unlockedBytes = SoundAssetResolver.ReadUnlockedBytes(_vfs, resolvedPath);
        foreach (IAudioStreamDecoder decoder in _decoders)
        {
            if (decoder.CanDecode(format, unlockedBytes))
            {
                return (resolvedPath, decoder.Decode(unlockedBytes, SampleRate));
            }
        }

        // Non-WAV compressed formats (.mp3/.ogg/.flac/.m4a) remain tracked as active channels without hardware codec crashes.
        return (resolvedPath, null);
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
        if (_ownsSink)
        {
            _outputSink.Dispose();
        }

        _disposed = true;
    }
}
