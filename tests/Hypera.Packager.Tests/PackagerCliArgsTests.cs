using System.IO;
using Xunit;

namespace Hypera.Packager.Tests;

public sealed class PackagerCliArgsTests
{
    [Fact]
    public void Parse_NoArgs_StartsInteractiveGuiWithJapaneseDefault()
    {
        PackagerLaunchOptions opts = PackagerCliArgs.Parse([]);

        Assert.Equal(PackagerLaunchMode.Gui, opts.Mode);
        Assert.False(opts.AutoStartPack);
        Assert.Null(opts.InitialSourceDirectory);
        Assert.Equal("ja", opts.LanguageCode);
        Assert.True(opts.CompiledOnly);
        Assert.True(opts.Compress);
    }

    [Fact]
    public void Parse_PercentOneDirectory_SetsSourceDefaultOutputAndAutoStartPack()
    {
        string gameDir = Path.Combine(Path.GetTempPath(), "DragDropGame");
        PackagerLaunchOptions opts = PackagerCliArgs.Parse([gameDir]);

        Assert.Equal(PackagerLaunchMode.Gui, opts.Mode);
        Assert.True(opts.AutoStartPack);
        Assert.Equal(Path.GetFullPath(gameDir), opts.InitialSourceDirectory);
        Assert.Equal(PackagerPathHelper.GetDefaultOutputHepPath(gameDir), opts.InitialOutputHepPath);
    }

    [Fact]
    public void Parse_CliPackWithAllOptions_ParsesFlagsCorrectly()
    {
        string gameDir = Path.Combine(Path.GetTempPath(), "CliGame");
        string outHep = Path.Combine(Path.GetTempPath(), "custom.hep");

        PackagerLaunchOptions opts = PackagerCliArgs.Parse([
            "--cli-pack", gameDir,
            "-o", outHep,
            "--no-compress",
            "--no-compiled-only",
            "--new-random",
            "-x", "temp/;debug.log",
            "--lang", "zh-CN",
        ]);

        Assert.Equal(PackagerLaunchMode.CliPack, opts.Mode);
        Assert.Equal(Path.GetFullPath(gameDir), opts.InitialSourceDirectory);
        Assert.Equal(Path.GetFullPath(outHep), opts.InitialOutputHepPath);
        Assert.False(opts.Compress);
        Assert.False(opts.CompiledOnly);
        Assert.Equal(RandomModeOverride.NewRandom, opts.RandomMode);
        Assert.Equal(["temp/", "debug.log"], opts.ExcludePaths);
        Assert.Equal("zh-CN", opts.LanguageCode);
    }

    [Fact]
    public void Parse_ConflictingRandomFlags_ReturnsInvalidCli()
    {
        PackagerLaunchOptions opts = PackagerCliArgs.Parse(["--cli-pack", "game", "--new-random", "--old-random"]);
        Assert.Equal(PackagerLaunchMode.InvalidCli, opts.Mode);
        Assert.NotNull(opts.CliErrorMessage);
    }
}
