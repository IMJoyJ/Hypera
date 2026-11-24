using System.Text;
using Hypera.Audio;
using Hypera.Bytecode;
using Hypera.Runtime;
using Hypera.VirtualFs;
using MinorShift.Emuera.Runtime.Utils.PluginSystem;
using Xunit;

namespace Hypera.Tests;

public sealed class AudioVfsAndPluginIsolationTests
{
    [Fact]
    public void NullAudioBackend_Tracks10SeChannelsAndLoopingBgmWithVolumeClamping()
    {
        using var audio = new NullAudioBackend();

        for (int ch = 0; ch < IAudioBackend.SoundEffectChannelCount; ch++)
        {
            Assert.False(audio.GetSoundEffectChannel(ch).IsPlaying);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => audio.PlaySoundEffect(-1, "sound/hit.wav"));
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.PlaySoundEffect(10, "sound/hit.wav"));

        audio.PlaySoundEffect(0, "sound/se0.wav", volume: 150, loop: false);
        audio.PlaySoundEffect(9, "sound/se9.ogg", volume: -20, loop: true);

        AudioChannelSnapshot ch0 = audio.GetSoundEffectChannel(0);
        Assert.True(ch0.IsPlaying);
        Assert.Equal("sound/se0.wav", ch0.VirtualPath);
        Assert.Equal(100, ch0.Volume);
        Assert.False(ch0.IsLooping);

        AudioChannelSnapshot ch9 = audio.GetSoundEffectChannel(9);
        Assert.True(ch9.IsPlaying);
        Assert.Equal(0, ch9.Volume);
        Assert.True(ch9.IsLooping);

        audio.PlayBgm("bgm/theme.mp3", volume: 80, loop: true);
        AudioChannelSnapshot bgm = audio.GetBgmChannel();
        Assert.True(bgm.IsPlaying);
        Assert.Equal("bgm/theme.mp3", bgm.VirtualPath);
        Assert.Equal(80, bgm.Volume);
        Assert.True(bgm.IsLooping);

        audio.StopSoundEffect(0);
        Assert.False(audio.GetSoundEffectChannel(0).IsPlaying);
        Assert.True(audio.GetSoundEffectChannel(9).IsPlaying);

        audio.StopAllSoundEffects();
        Assert.False(audio.GetSoundEffectChannel(9).IsPlaying);

        audio.StopBgm();
        Assert.False(audio.GetBgmChannel().IsPlaying);
    }

    [Fact]
    public void VfsNormalizerAndEncodingDetector_HandleCrossPlatformPathsAndCjkEncodings()
    {
        Assert.Equal("ERB/sub/Main.erb", VfsPathNormalizer.Normalize(@"  \ERB\foo\..\sub\.\Main.erb "));
        Assert.True(VfsPathNormalizer.EqualsIgnoreCase(@"erb\SYSTEM.ERB", "ERB/system.erb"));
        Assert.Throws<InvalidOperationException>(() => VfsPathNormalizer.Normalize("ERB/../../secret.txt"));

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var detector = EncodingDetector.Default;

        byte[] utf8Bom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("テスト")];
        Assert.Equal("テスト", detector.Decode(utf8Bom, out EncodingDetectionResult rBom));
        Assert.Equal(DetectedEncodingKind.Utf8Bom, rBom.Kind);

        byte[] sjis = Encoding.GetEncoding(932).GetBytes("あいうえおカキクケコ");
        Assert.Equal("あいうえおカキクケコ", detector.Decode(sjis, out EncodingDetectionResult rSjis));
        Assert.Equal(DetectedEncodingKind.ShiftJisCp932, rSjis.Kind);

        byte[] gb = Encoding.GetEncoding("GB18030").GetBytes("简体中文编码自动识别测试");
        Assert.Equal("简体中文编码自动识别测试", detector.Decode(gb, out EncodingDetectionResult rGb));
        Assert.Equal(DetectedEncodingKind.Gb18030, rGb.Kind);
    }

    [Fact]
    public async Task LegacyPluginShim_IsolatesPluginManagerPerAsyncEngineFlow()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hypera_plugin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var vfs = new PhysicalDirectoryVfs(tempDir);
            using var engineA = new EmueraEngine(BytecodeModule.Empty, vfs);
            using var engineB = new EmueraEngine(BytecodeModule.Empty, vfs);
            using var pmA = new PluginManager(engineA);
            using var pmB = new PluginManager(engineB);

            Task taskA = Task.Run(async () =>
            {
                using (pmA.EnterScope())
                {
                    await Task.Delay(20);
                    Assert.Same(engineA, PluginManager.GetInstance().BoundEngine);
                }
            });

            Task taskB = Task.Run(async () =>
            {
                using (pmB.EnterScope())
                {
                    await Task.Delay(10);
                    Assert.Same(engineB, PluginManager.GetInstance().BoundEngine);
                }
            });

            await Task.WhenAll(taskA, taskB);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
