using System.IO;
using Xunit;

namespace Hypera.Packager.Tests;

public sealed class PackagerPathHelperTests
{
    [Fact]
    public void GetDefaultOutputHepPath_ReturnsParentSlashDirNameDotHep()
    {
        string gameDir = Path.Combine(Path.GetTempPath(), "SampleGameDir");
        string expected = Path.Combine(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), "SampleGameDir.hep");

        Assert.Equal(expected, PackagerPathHelper.GetDefaultOutputHepPath(gameDir));
        Assert.Equal(expected, PackagerPathHelper.GetDefaultOutputHepPath(gameDir + Path.DirectorySeparatorChar));
        Assert.Equal(expected, PackagerPathHelper.GetDefaultOutputHepPath($"\"{gameDir}/\""));
    }

    [Fact]
    public void ParseExcludePaths_SplitsSemicolonsAndNewlinesAndDeduplicates()
    {
        string raw = "temp/; saves/\r\nextra/debug.txt; TEMP/; \"logs/run.log\"";
        IReadOnlyList<string> parsed = PackagerPathHelper.ParseExcludePaths(raw);

        Assert.Equal(["temp/", "saves/", "extra/debug.txt", "logs/run.log"], parsed);
    }

    [Fact]
    public void FormatByteSize_FormatsBytesKilobytesAndMegabytes()
    {
        Assert.Equal("512 B", PackagerPathHelper.FormatByteSize(512));
        Assert.Contains("KB", PackagerPathHelper.FormatByteSize(2048));
        Assert.Contains("MB", PackagerPathHelper.FormatByteSize(2 * 1024 * 1024));
    }
}
