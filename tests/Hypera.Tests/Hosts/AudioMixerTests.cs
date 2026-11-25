using System.Buffers.Binary;
using Hypera.Audio;
using Hypera.VirtualFs;
using Xunit;

namespace Hypera.Tests.Hosts;

public sealed class AudioMixerTests
{
    [Fact]
    public void ExistSound_ResolvesCaseInsensitiveSupportedFormatsInSoundDirectory()
    {
        string dir = CreateTempDir();
        try
        {
            string soundDir = Path.Combine(dir, "sound");
            Directory.CreateDirectory(soundDir);
            File.WriteAllBytes(Path.Combine(soundDir, "Click.WAV"), WavPcmDecoder.EncodeStereoPcm16Wav([0.5f, -0.5f]));
            File.WriteAllBytes(Path.Combine(soundDir, "Theme.ogg"), [0x4F, 0x67, 0x67, 0x53]);
            File.WriteAllBytes(Path.Combine(soundDir, "Voice.MP3"), [0x49, 0x44, 0x33]);
            File.WriteAllBytes(Path.Combine(soundDir, "Lossless.flac"), [0x66, 0x4C, 0x61, 0x43]);
            File.WriteAllBytes(Path.Combine(soundDir, "Aac.m4a"), [0x00, 0x00, 0x00, 0x20]);
            File.WriteAllText(Path.Combine(soundDir, "readme.txt"), "not audio");

            using var vfs = new PhysicalDirectoryVfs(dir);
            using var mixer = new CrossPlatformAudioMixer(vfs);

            Assert.True(mixer.ExistSound("click.wav"));
            Assert.True(mixer.ExistSound("sound/CLICK.WAV"));
            Assert.True(mixer.ExistSound("theme"));
            Assert.True(mixer.ExistSound("voice.mp3"));
            Assert.True(mixer.ExistSound("lossless.FLAC"));
            Assert.True(mixer.ExistSound("aac.m4a"));
            Assert.False(mixer.ExistSound("readme.txt"));
            Assert.False(mixer.ExistSound("missing.wav"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UnlockedVfsLoading_AllowsImmediateFileDeletionWhilePlaying()
    {
        string dir = CreateTempDir();
        try
        {
            string soundDir = Path.Combine(dir, "sound");
            Directory.CreateDirectory(soundDir);
            string wavPath = Path.Combine(soundDir, "hit.wav");
            File.WriteAllBytes(wavPath, WavPcmDecoder.EncodeStereoPcm16Wav([0.5f, 0.5f, 0.5f, 0.5f], 48_000));

            using var vfs = new PhysicalDirectoryVfs(dir);
            using var mixer = new CrossPlatformAudioMixer(vfs);

            mixer.PlaySoundEffect(0, "hit.wav");
            mixer.PlayBgm("sound/hit.wav", loop: true);

            // Physical file must NOT be locked by the audio mixer
            File.Delete(wavPath);
            Assert.False(File.Exists(wavPath));

            float[] buffer = new float[4];
            mixer.RenderInterleavedStereoF32(buffer);
            Assert.True(buffer[0] > 0.5f);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SeRingAndBgmLoopAndWavMixing_RespectIndependentVolumesAndSink()
    {
        string dir = CreateTempDir();
        try
        {
            string soundDir = Path.Combine(dir, "sound");
            Directory.CreateDirectory(soundDir);

            // 2-frame SE clip (+0.5) and 2-frame BGM clip (+0.25)
            File.WriteAllBytes(Path.Combine(soundDir, "se.wav"), WavPcmDecoder.EncodeStereoPcm16Wav([0.5f, 0.5f, 0.5f, 0.5f], 48_000));
            File.WriteAllBytes(Path.Combine(soundDir, "bgm.wav"), WavPcmDecoder.EncodeStereoPcm16Wav([0.25f, 0.25f, 0.25f, 0.25f], 48_000));

            using var sink = new HeadlessMemoryAudioOutputSink(48_000, 2, capacityFrames: 256);
            using var vfs = new PhysicalDirectoryVfs(dir);
            using var mixer = new CrossPlatformAudioMixer(vfs, 48_000, sink);

            // Fill all 10 SE ring slots (0..9), then verify 11th overwrites slot 0 (oldest)
            for (int i = 0; i < IAudioBackend.SoundEffectChannelCount; i++)
            {
                int slot = mixer.PlaySoundEffectRing("se.wav", volume: 100, loop: false);
                Assert.Equal(i, slot);
            }

            int wrappedSlot = mixer.PlaySoundEffectRing("se.wav", volume: 100, loop: false);
            Assert.Equal(0, wrappedSlot);

            mixer.StopAllSoundEffects();
            mixer.PlaySoundEffect(0, "se.wav", volume: 50, loop: false);
            mixer.PlayBgm("bgm.wav", volume: 80, loop: true);
            mixer.SoundEffectMasterVolume = 100;
            mixer.MasterVolume = 100;

            // Pump 4 frames: frames 0..1 include SE (0.5 * 0.5 = 0.25) + BGM (0.25 * 0.8 = 0.20) = 0.45
            // frames 2..3 SE has finished, BGM loops cleanly = 0.20
            mixer.PumpToSink(4);

            Assert.Equal(4, sink.TotalFramesWritten);
            float[] samples = sink.SnapshotRecentSamples(8);
            Assert.Equal(8, samples.Length);
            Assert.InRange(samples[0], 0.43f, 0.47f);
            Assert.InRange(samples[4], 0.18f, 0.22f);
            Assert.False(mixer.GetSoundEffectChannel(0).IsPlaying);
            Assert.True(mixer.GetBgmChannel().IsPlaying);

            // Adjusting SoundEffectMasterVolume does not affect BGM volume
            mixer.SoundEffectMasterVolume = 0;
            mixer.RenderInterleavedStereoF32(samples.AsSpan(0, 4));
            Assert.InRange(samples[0], 0.18f, 0.22f);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WavPcmDecoder_DecodesMonoFloatAndResamplesToTargetRate()
    {
        // Build a 2-frame mono IEEE-float 24kHz WAV (+0.5f, +0.5f)
        byte[] wav = new byte[44 + 8];
        "RIFF"u8.CopyTo(wav.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4, 4), (uint)(36 + 8));
        "WAVE"u8.CopyTo(wav.AsSpan(8, 4));
        "fmt "u8.CopyTo(wav.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16, 4), 16u);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20, 2), 3); // IEEE float
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22, 2), 1); // mono
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24, 4), 24_000u);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28, 4), 24_000u * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32, 2), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34, 2), 32);
        "data"u8.CopyTo(wav.AsSpan(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40, 4), 8u);
        BinaryPrimitives.WriteSingleLittleEndian(wav.AsSpan(44, 4), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(wav.AsSpan(48, 4), 0.5f);

        var decoder = new WavPcmDecoder();
        DecodedAudioClip clip = decoder.Decode(wav, targetSampleRate: 48_000);

        Assert.Equal(48_000, clip.SampleRate);
        Assert.Equal(4, clip.FrameCount); // 2 frames @ 24kHz resampled to 4 frames @ 48kHz
        Assert.InRange(clip.InterleavedStereoSamples[0], 0.49f, 0.51f);
        Assert.InRange(clip.InterleavedStereoSamples[1], 0.49f, 0.51f);
    }

    [Fact]
    public async Task MixerChannelOperations_AreThreadSafeAcrossConcurrentTasks()
    {
        string dir = CreateTempDir();
        try
        {
            string soundDir = Path.Combine(dir, "sound");
            Directory.CreateDirectory(soundDir);
            File.WriteAllBytes(Path.Combine(soundDir, "beep.wav"), WavPcmDecoder.EncodeStereoPcm16Wav([0.2f, 0.2f, 0.2f, 0.2f], 48_000));

            using var vfs = new PhysicalDirectoryVfs(dir);
            using var mixer = new CrossPlatformAudioMixer(vfs);

            Task[] tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
            {
                float[] buf = new float[64];
                for (int i = 0; i < 25; i++)
                {
                    int slot = (t + i) % IAudioBackend.SoundEffectChannelCount;
                    mixer.PlaySoundEffect(slot, "beep.wav", volume: (i * 10) % 101);
                    mixer.SetSoundEffectVolume(slot, 75);
                    if (i % 5 == 0)
                    {
                        mixer.PlayBgm("beep.wav", volume: 60, loop: true);
                    }

                    mixer.RenderInterleavedStereoF32(buf);
                }
            })).ToArray();

            await Task.WhenAll(tasks);
            Assert.True(mixer.GetBgmChannel().IsPlaying);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hypera_audio_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
