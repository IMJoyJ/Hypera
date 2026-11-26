using System.Runtime.InteropServices;
using Hypera.Audio;

namespace Hypera.Standalone;

/// <summary>
/// Lightweight, zero-dependency Windows <c>winmm.dll</c> waveOut audio playback driver pulling
/// stereo float32 PCM from <see cref="CrossPlatformAudioMixer"/> and streaming 16-bit PCM to the
/// system default audio device. Falls back cleanly to silent no-op on headless/no-audio systems.
/// </summary>
public sealed class WinMmAudioDriver : IDisposable
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BitsPerSample = 16;
    private const int FramesPerBuffer = 1024; // ~21.3ms per buffer
    private const int BufferCount = 4;
    private const uint WaveMapper = 0xFFFF_FFFFu;
    private const ushort WaveFormatPcm = 1;
    private const uint WhdrDone = 0x0000_0001u;

    private readonly CrossPlatformAudioMixer _mixer;
    private readonly ManualResetEventSlim _stopEvent = new(false);
    private readonly Thread? _pumpThread;
    private IntPtr _hWaveOut;
    private volatile bool _isMuted;
    private bool _disposed;

    public WinMmAudioDriver(CrossPlatformAudioMixer mixer)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        _mixer = mixer;

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fmt = new WaveFormatEx
        {
            FormatTag = WaveFormatPcm,
            Channels = Channels,
            SamplesPerSec = SampleRate,
            BitsPerSample = BitsPerSample,
            BlockAlign = Channels * (BitsPerSample / 8),
            AvgBytesPerSec = SampleRate * Channels * (BitsPerSample / 8),
            CbSize = 0,
        };

        try
        {
            int mmResult = waveOutOpen(out IntPtr handle, WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (mmResult == 0 && handle != IntPtr.Zero)
            {
                _hWaveOut = handle;
                IsHardwareAttached = true;
                _pumpThread = new Thread(AudioPumpLoop)
                {
                    Name = "Hypera.WinMmAudioPump",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                };
                _pumpThread.Start();
            }
        }
        catch
        {
            _hWaveOut = IntPtr.Zero;
            IsHardwareAttached = false;
        }
    }

    public bool IsHardwareAttached { get; }

    public bool IsMuted
    {
        get => _isMuted;
        set => _isMuted = value;
    }

    private void AudioPumpLoop()
    {
        int samplesPerBuffer = FramesPerBuffer * Channels;
        int bytesPerBuffer = samplesPerBuffer * sizeof(short);
        float[] floatBuffer = new float[samplesPerBuffer];

        var pcmBuffers = new short[BufferCount][];
        var pcmPins = new GCHandle[BufferCount];
        var headerPins = new GCHandle[BufferCount];
        var prepared = new bool[BufferCount];
        var inFlight = new bool[BufferCount];

        try
        {
            int hdrSize = Marshal.SizeOf<WaveHdr>();
            for (int i = 0; i < BufferCount; i++)
            {
                pcmBuffers[i] = new short[samplesPerBuffer];
                pcmPins[i] = GCHandle.Alloc(pcmBuffers[i], GCHandleType.Pinned);
                var hdr = new WaveHdr
                {
                    Data = pcmPins[i].AddrOfPinnedObject(),
                    BufferLength = (uint)bytesPerBuffer,
                    Flags = 0,
                };
                headerPins[i] = GCHandle.Alloc(hdr, GCHandleType.Pinned);
                if (waveOutPrepareHeader(_hWaveOut, headerPins[i].AddrOfPinnedObject(), (uint)hdrSize) == 0)
                {
                    prepared[i] = true;
                }
            }

            while (!_stopEvent.IsSet)
            {
                if (!HasAnyActiveVoice() || _isMuted)
                {
                    _stopEvent.Wait(20);
                    continue;
                }

                bool submittedAny = false;
                for (int i = 0; i < BufferCount && !_stopEvent.IsSet; i++)
                {
                    if (!prepared[i])
                    {
                        continue;
                    }

                    IntPtr hdrPtr = headerPins[i].AddrOfPinnedObject();
                    if (inFlight[i])
                    {
                        uint flags = (uint)Marshal.ReadInt32(hdrPtr, Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.Flags)).ToInt32());
                        if ((flags & WhdrDone) == 0)
                        {
                            continue;
                        }

                        inFlight[i] = false;
                    }

                    _mixer.RenderInterleavedStereoF32(floatBuffer);
                    short[] dst = pcmBuffers[i];
                    for (int s = 0; s < samplesPerBuffer; s++)
                    {
                        float clamped = Math.Clamp(floatBuffer[s], -1f, 1f);
                        dst[s] = (short)MathF.Round(clamped * 32767f);
                    }

                    if (waveOutWrite(_hWaveOut, hdrPtr, (uint)hdrSize) == 0)
                    {
                        inFlight[i] = true;
                        submittedAny = true;
                    }
                }

                if (!submittedAny)
                {
                    _stopEvent.Wait(6);
                }
            }
        }
        catch
        {
            // Keep audio thread isolated from tearing down the UI host.
        }
        finally
        {
            if (_hWaveOut != IntPtr.Zero)
            {
                try
                {
                    _ = waveOutReset(_hWaveOut);
                    int hdrSize = Marshal.SizeOf<WaveHdr>();
                    for (int i = 0; i < BufferCount; i++)
                    {
                        if (prepared[i] && headerPins[i].IsAllocated)
                        {
                            _ = waveOutUnprepareHeader(_hWaveOut, headerPins[i].AddrOfPinnedObject(), (uint)hdrSize);
                        }
                    }

                    _ = waveOutClose(_hWaveOut);
                }
                catch
                {
                    // Best-effort native resource cleanup.
                }

                _hWaveOut = IntPtr.Zero;
            }

            for (int i = 0; i < BufferCount; i++)
            {
                if (headerPins[i].IsAllocated) headerPins[i].Free();
                if (pcmPins[i].IsAllocated) pcmPins[i].Free();
            }
        }
    }

    private bool HasAnyActiveVoice()
    {
        try
        {
            if (_mixer.GetBgmChannel().IsPlaying)
            {
                return true;
            }

            for (int i = 0; i < IAudioBackend.SoundEffectChannelCount; i++)
            {
                if (_mixer.GetSoundEffectChannel(i).IsPlaying)
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopEvent.Set();
        if (_pumpThread is { IsAlive: true })
        {
            _pumpThread.Join(1000);
        }

        _stopEvent.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort CbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern int waveOutOpen(
        out IntPtr phwo,
        uint uDeviceID,
        ref WaveFormatEx pwfx,
        IntPtr dwCallback,
        IntPtr dwInstance,
        uint fdwOpen);

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern int waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern int waveOutClose(IntPtr hwo);
}
