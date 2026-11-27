using System.IO;
using Hypera.Bytecode;
using Hypera.Persistence;
using Hypera.VirtualFs;
using Xunit;

namespace Hypera.Packager.Tests;

public sealed class PackagerServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public PackagerServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"hypera_packager_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void ExecutePack_ValidProjectCompiledOnly_CreatesHepWithBytecodeAndStripsSourcesAndExcludes()
    {
        string gameDir = Path.Combine(_tempRoot, "ValidGame");
        Directory.CreateDirectory(Path.Combine(gameDir, "ERB"));
        Directory.CreateDirectory(Path.Combine(gameDir, "CSV"));
        Directory.CreateDirectory(Path.Combine(gameDir, "sav"));
        Directory.CreateDirectory(Path.Combine(gameDir, "resources"));
        Directory.CreateDirectory(Path.Combine(gameDir, "custom_skip"));

        File.WriteAllText(
            Path.Combine(gameDir, "ERB", "Title.erb"),
            "@SYSTEM_TITLE\nPRINTL Hello Packager\nWAIT\nQUIT\n");
        File.WriteAllText(Path.Combine(gameDir, "CSV", "GameBase.csv"), "コード,1000\nバージョン,100\n");
        File.WriteAllText(Path.Combine(gameDir, "resources", "readme.txt"), "runtime asset");
        File.WriteAllText(Path.Combine(gameDir, "sav", "save00.sav"), "dummy save");
        File.WriteAllText(Path.Combine(gameDir, "custom_skip", "secret.txt"), "skip me");
        File.WriteAllText(Path.Combine(gameDir, "Emuera.exe"), "dummy binary");

        string outHep = Path.Combine(_tempRoot, "ValidGame.hep");
        PackResult result = PackagerService.ExecutePack(new PackRequest(
            SourceDirectory: gameDir,
            OutputHepPath: outHep,
            CompiledOnly: true,
            Compress: true,
            RandomMode: RandomModeOverride.NewRandom,
            ExcludePaths: ["custom_skip/"]));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(outHep));
        Assert.True(result.OutputSizeBytes > 0);
        Assert.True(result.IncludesPrebuiltBytecode);
        Assert.True(result.FunctionCount >= 1);

        using var archive = new HepArchiveVfs(outHep);
        Assert.True(archive.FileExists("game.heb"));
        Assert.True(archive.FileExists("resources/readme.txt"));
        Assert.True(archive.FileExists("csv/gamebase.csv"));
        Assert.False(archive.FileExists("erb/title.erb"));
        Assert.False(archive.FileExists("sav/save00.sav"));
        Assert.False(archive.FileExists("custom_skip/secret.txt"));
        Assert.False(archive.FileExists("emuera.exe"));

        using MemoryStream ms = new(archive.ReadAllBytes("game.heb"));
        BytecodeModule module = BytecodeSerializer.Deserialize(ms);
        Assert.True(module.CompilerOptions.UseNewRandom);
    }

    [Fact]
    public void ExecutePack_OldRandomOverride_BakesUseNewRandomFalseEvenIfSettingJsonTrue()
    {
        string gameDir = Path.Combine(_tempRoot, "SettingGame");
        Directory.CreateDirectory(Path.Combine(gameDir, "ERB"));
        File.WriteAllText(Path.Combine(gameDir, "setting.json"), "{\"UseNewRandom\":true}");
        File.WriteAllText(Path.Combine(gameDir, "ERB", "Title.erb"), "@SYSTEM_TITLE\nQUIT\n");

        string outHep = Path.Combine(_tempRoot, "SettingGame.hep");
        PackResult result = PackagerService.ExecutePack(new PackRequest(
            SourceDirectory: gameDir,
            OutputHepPath: outHep,
            CompiledOnly: true,
            Compress: true,
            RandomMode: RandomModeOverride.OldSfmt));

        Assert.True(result.Success, result.ErrorMessage);
        using var archive = new HepArchiveVfs(outHep);
        using MemoryStream ms = new(archive.ReadAllBytes("game.heb"));
        BytecodeModule module = BytecodeSerializer.Deserialize(ms);
        Assert.False(module.CompilerOptions.UseNewRandom);
    }

    [Fact]
    public void ExecutePack_IncludeSourceMode_KeepsErbAndSkipsSelfOutputFile()
    {
        string gameDir = Path.Combine(_tempRoot, "IncludeSourceGame");
        Directory.CreateDirectory(Path.Combine(gameDir, "ERB"));
        File.WriteAllText(Path.Combine(gameDir, "ERB", "Title.erb"), "@SYSTEM_TITLE\nQUIT\n");

        // Intentionally place output inside sourceDir to verify self-exclusion prevents sharing violation.
        string outHep = Path.Combine(gameDir, "self.hep");
        PackResult result = PackagerService.ExecutePack(new PackRequest(
            SourceDirectory: gameDir,
            OutputHepPath: outHep,
            CompiledOnly: false,
            Compress: false));

        Assert.True(result.Success, result.ErrorMessage);
        using var archive = new HepArchiveVfs(outHep);
        Assert.True(archive.FileExists("erb/title.erb"));
        Assert.False(archive.FileExists("self.hep"));
    }

    [Fact]
    public void ExecutePack_BrokenErb_CapturesFullCompileDiagnosticsAndFormatsAcrossLocales()
    {
        string gameDir = Path.Combine(_tempRoot, "BrokenGame");
        Directory.CreateDirectory(Path.Combine(gameDir, "ERB"));
        File.WriteAllText(
            Path.Combine(gameDir, "ERB", "Broken.erb"),
            "@SYSTEM_TITLE\nCALL NON_EXISTENT_FUNC_XYZ\n");

        string outHep = Path.Combine(_tempRoot, "BrokenGame.hep");
        PackResult result = PackagerService.ExecutePack(new PackRequest(gameDir, outHep));

        Assert.False(result.Success);
        Assert.True(result.HasCompileFailure);
        Assert.True(result.ErrorCount >= 1);
        Assert.NotEmpty(result.Diagnostics);
        Assert.False(File.Exists(outHep));

        foreach (string lang in new[] { "ja", "zh-CN", "en" })
        {
            string formatted = DiagnosticFormatter.FormatResult(result, lang);
            Assert.Contains("NON_EXISTENT_FUNC_XYZ", formatted, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Broken.erb", formatted, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(result.Diagnostics[0].Code, formatted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExecuteNonGui_CliPackValidAndInvalid_ReturnsExpectedExitCodes()
    {
        string validDir = Path.Combine(_tempRoot, "CliValid");
        Directory.CreateDirectory(Path.Combine(validDir, "ERB"));
        File.WriteAllText(Path.Combine(validDir, "ERB", "Title.erb"), "@SYSTEM_TITLE\nQUIT\n");
        string outHep = Path.Combine(_tempRoot, "cli_out.hep");

        using var outOk = new StringWriter();
        using var errOk = new StringWriter();
        int codeOk = Program.ExecuteNonGui(
            PackagerCliArgs.Parse(["--cli-pack", validDir, "-o", outHep]),
            outOk,
            errOk);

        Assert.Equal(0, codeOk);
        Assert.True(File.Exists(outHep));
        Assert.Contains("Packed", outOk.ToString(), StringComparison.Ordinal);

        string brokenDir = Path.Combine(_tempRoot, "CliBroken");
        Directory.CreateDirectory(Path.Combine(brokenDir, "ERB"));
        File.WriteAllText(Path.Combine(brokenDir, "ERB", "Bad.erb"), "@SYSTEM_TITLE\nCALL MISSING_TARGET\n");
        string badHep = Path.Combine(_tempRoot, "cli_bad.hep");

        using var outBad = new StringWriter();
        using var errBad = new StringWriter();
        int codeBad = Program.ExecuteNonGui(
            PackagerCliArgs.Parse(["--cli-pack", brokenDir, "-o", badHep, "--lang", "en"]),
            outBad,
            errBad);

        Assert.Equal(1, codeBad);
        Assert.False(File.Exists(badHep));
        Assert.Contains("MISSING_TARGET", errBad.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
