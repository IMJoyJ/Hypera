using System.Buffers.Binary;
using System.Diagnostics;
using Hypera.Bytecode;
using Hypera.VirtualFs;
using Xunit;

namespace Hypera.Tests.Hosts;

public sealed class CliAndBundlerTests
{
    [Fact]
    public void HyperaCli_CompilePackHeadlessRenderAndSingleFileBundleRoundtrip()
    {
        string cliBinDir = LocateHyperaCliOutputDirectory();
        string hyperaDll = Path.Combine(cliBinDir, "hypera.dll");
        string hyperaExe = Path.Combine(cliBinDir, OperatingSystem.IsWindows() ? "hypera.exe" : "hypera");
        Assert.True(File.Exists(hyperaDll), $"Expected hypera.dll at '{hyperaDll}'.");
        Assert.True(File.Exists(hyperaExe), $"Expected host binary at '{hyperaExe}'.");

        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_cli_test_{Guid.NewGuid():N}");
        string gameDir = Path.Combine(workDir, "Game");
        string erbDir = Path.Combine(gameDir, "ERB");
        string framesDir = Path.Combine(workDir, "frames");
        string bundleFramesDir = Path.Combine(workDir, "bundle_frames");
        string hebBundleFramesDir = Path.Combine(workDir, "heb_bundle_frames");
        Directory.CreateDirectory(erbDir);

        try
        {
            File.WriteAllText(
                Path.Combine(erbDir, "Title.erb"),
                """
                @SYSTEM_TITLE
                PRINTL Hello Hypera World
                PRINTBUTTON "[Start Game]", 1
                WAIT
                QUIT
                """);

            string hebPath = Path.Combine(workDir, "game.heb");
            string hepPath = Path.Combine(workDir, "game.hep");

            // 0. hypera --version prints dynamic assembly version (1.0.0+)
            string versionOut = RunProcess("dotnet", [hyperaDll, "--version"], workDir).Trim();
            Assert.StartsWith("hypera 1.", versionOut, StringComparison.Ordinal);

            // 1. hypera compile <dir> -o <out.heb>
            RunProcess("dotnet", [hyperaDll, "compile", gameDir, "-o", hebPath], workDir);
            Assert.True(File.Exists(hebPath));
            byte[] hebHeader = File.ReadAllBytes(hebPath)[..BytecodeModuleHeader.SizeInBytes];
            Assert.True(BytecodeModuleHeader.TryRead(hebHeader, out BytecodeModuleHeader parsedHeb));
            Assert.Equal(BytecodeModuleHeader.ExpectedMagic, parsedHeb.Magic);
            Assert.True(parsedHeb.FunctionCount >= 1);

            // 2. hypera pack <dir> -o <out.hep>
            RunProcess("dotnet", [hyperaDll, "pack", gameDir, "-o", hepPath], workDir);
            Assert.True(File.Exists(hepPath));
            byte[] hepHeader = File.ReadAllBytes(hepPath)[..ArchivePackageHeader.SizeInBytes];
            Assert.True(ArchivePackageHeader.TryRead(hepHeader, out ArchivePackageHeader parsedHep));
            Assert.Equal(ArchivePackageHeader.ExpectedMagic, parsedHep.Magic);
            Assert.True(parsedHep.Flags.HasFlag(ArchivePackageFlags.IncludesPrebuiltBytecode));

            // 3. hypera run <out.hep> --headless-frames <framesDir>
            string ansiStdout = RunProcess("dotnet", [hyperaDll, "run", hepPath, "--headless-frames", framesDir, "--input", "1"], workDir);
            Assert.Contains("\x1b[", ansiStdout); // TrueColor ANSI escapes emitted
            Assert.Contains("Hello Hypera World", ansiStdout);
            string[] dumpedPngs = Directory.GetFiles(framesDir, "frame_*.png");
            Assert.NotEmpty(dumpedPngs);
            byte[] pngMagic = File.ReadAllBytes(dumpedPngs[0])[..8];
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, pngMagic);

            // 4. hypera bundle <out.hep> --host <hypera.exe> -o <Game.exe>
            string bundleDir = Path.Combine(workDir, "Dist");
            CopyDirectoryRecursive(cliBinDir, bundleDir);

            string bundledExeName = OperatingSystem.IsWindows() ? "Game.exe" : "Game";
            string bundledExePath = Path.Combine(bundleDir, bundledExeName);
            RunProcess("dotnet", [hyperaDll, "bundle", hepPath, "--host", hyperaExe, "-o", bundledExePath], workDir);
            Assert.True(File.Exists(bundledExePath));

            // Verify exact 24-byte YU-RIS style HYPRBNDL trailer
            byte[] bundledBytes = File.ReadAllBytes(bundledExePath);
            byte[] hostBytes = File.ReadAllBytes(hyperaExe);
            byte[] hepBytes = File.ReadAllBytes(hepPath);
            Assert.Equal(hostBytes.Length + hepBytes.Length + 24, bundledBytes.Length);

            ReadOnlySpan<byte> trailer = bundledBytes.AsSpan(bundledBytes.Length - 24, 24);
            long payloadOffset = BinaryPrimitives.ReadInt64LittleEndian(trailer[0..8]);
            long payloadLength = BinaryPrimitives.ReadInt64LittleEndian(trailer[8..16]);
            Assert.Equal(hostBytes.LongLength, payloadOffset);
            Assert.Equal(hepBytes.LongLength, payloadLength);
            Assert.Equal("HYPRBNDL"u8.ToArray(), trailer[16..24].ToArray());

            // 5. Execute renamed Game.exe directly and verify Program.Main auto-detects HYPRBNDL .hep trailer!
            string stdout = RunProcess(bundledExePath, ["--headless-frames", bundleFramesDir, "--input", "1"], bundleDir);
            Assert.Contains("Hello Hypera World", stdout);
            Assert.Contains("[Start Game]", stdout);
            Assert.NotEmpty(Directory.GetFiles(bundleFramesDir, "frame_*.png"));

            // 6. Also verify HYPRBNDL roundtrip with direct .heb payload!
            string bundledHebExePath = Path.Combine(bundleDir, OperatingSystem.IsWindows() ? "GameHeb.exe" : "GameHeb");
            RunProcess("dotnet", [hyperaDll, "bundle", hebPath, "--host", hyperaExe, "-o", bundledHebExePath], workDir);
            string hebStdout = RunProcess(bundledHebExePath, ["--headless-frames", hebBundleFramesDir, "--input", "1"], bundleDir);
            Assert.Contains("Hello Hypera World", hebStdout);
            Assert.NotEmpty(Directory.GetFiles(hebBundleFramesDir, "frame_*.png"));
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }
    }

    [Fact]
    public void HyperaCli_CompilePackAndRun_PrintAllDiagnosticsSortedWithSummaryAndExitCodeOne()
    {
        string cliBinDir = LocateHyperaCliOutputDirectory();
        string hyperaDll = Path.Combine(cliBinDir, "hypera.dll");
        Assert.True(File.Exists(hyperaDll), $"Expected hypera.dll at '{hyperaDll}'.");

        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_cli_diag_test_{Guid.NewGuid():N}");
        string gameDir = Path.Combine(workDir, "Game");
        string erbDir = Path.Combine(gameDir, "ERB");
        Directory.CreateDirectory(erbDir);

        try
        {
            File.WriteAllText(
                Path.Combine(erbDir, "A_Errors.erb"),
                """
                @SYSTEM_TITLE
                CALL B_WARNINGS_AND_ERRORS
                A:0 = UNKNOWN_IN_A
                """);

            File.WriteAllText(
                Path.Combine(erbDir, "B_Errors.erb"),
                """
                @B_WARNINGS_AND_ERRORS
                A:1 == 1
                A:2 = UNKNOWN_IN_B_FIRST
                A:3 = UNKNOWN_IN_B_SECOND
                """);

            string hebOut = Path.Combine(workDir, "should_not_exist.heb");
            string hepOut = Path.Combine(workDir, "should_not_exist.hep");

            // 1. hypera compile prints ALL diagnostics + summary to stderr and exits with code 1
            (int compileExit, _, string compileStderr) = RunProcessCapturingExit(
                "dotnet",
                [hyperaDll, "compile", gameDir, "-o", hebOut],
                workDir);
            Assert.Equal(1, compileExit);
            Assert.False(File.Exists(hebOut));

            AssertContainsAllOrderedDiagnostics(compileStderr);

            // 2. hypera pack also catches EmueraCompileException and prints batch diagnostics
            (int packExit, _, string packStderr) = RunProcessCapturingExit(
                "dotnet",
                [hyperaDll, "pack", gameDir, "-o", hepOut],
                workDir);
            Assert.Equal(1, packExit);
            Assert.False(File.Exists(hepOut));
            AssertContainsAllOrderedDiagnostics(packStderr);

            // 3. hypera run also catches EmueraCompileException and prints batch diagnostics
            (int runExit, _, string runStderr) = RunProcessCapturingExit(
                "dotnet",
                [hyperaDll, "run", gameDir],
                workDir);
            Assert.Equal(1, runExit);
            AssertContainsAllOrderedDiagnostics(runStderr);
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }
    }

    private static void AssertContainsAllOrderedDiagnostics(string stderr)
    {
        Assert.Contains("ERA1426", stderr);
        Assert.Contains("ERA1555", stderr);
        Assert.Contains("UNKNOWN_IN_A", stderr);
        Assert.Contains("UNKNOWN_IN_B_FIRST", stderr);
        Assert.Contains("UNKNOWN_IN_B_SECOND", stderr);
        Assert.Contains("[hypera] Compilation failed: 3 error(s), 1 warning(s) across 2 file(s).", stderr);

        // Severity ordering: errors sorted before warning; file ordering: A_Errors before B_Errors
        int idxA = stderr.IndexOf("UNKNOWN_IN_A", StringComparison.Ordinal);
        int idxB1 = stderr.IndexOf("UNKNOWN_IN_B_FIRST", StringComparison.Ordinal);
        int idxB2 = stderr.IndexOf("UNKNOWN_IN_B_SECOND", StringComparison.Ordinal);
        int idxWarn = stderr.IndexOf("ERA1555", StringComparison.Ordinal);

        Assert.True(idxA >= 0 && idxB1 > idxA && idxB2 > idxB1 && idxWarn > idxB2,
            $"Unexpected diagnostic ordering in stderr:\n{stderr}");
    }

    private static string LocateHyperaCliOutputDirectory()
    {
        string testsBin = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string tfm = Path.GetFileName(testsBin);
        string? configDir = Path.GetDirectoryName(testsBin);
        string configName = configDir is not null ? Path.GetFileName(configDir) : "Release";

        // 1. Standard repo layout: src/Hypera.Cli/bin/<Config>/<TFM>
        string? repoRoot = FindRepoRoot(testsBin);
        if (repoRoot is not null)
        {
            string matchedConfig = Path.Combine(repoRoot, "src", "Hypera.Cli", "bin", configName, tfm);
            if (File.Exists(Path.Combine(matchedConfig, "hypera.dll")))
            {
                return matchedConfig;
            }
        }

        // 2. Isolated ArtifactsPath layout: <ArtifactsPath>/bin/Hypera.Cli/<Config>
        string? binRoot = configDir is not null ? Path.GetDirectoryName(configDir) : null;
        if (!string.IsNullOrEmpty(binRoot) && !string.IsNullOrEmpty(tfm))
        {
            string candidate = Path.Combine(binRoot, "Hypera.Cli", tfm);
            if (File.Exists(Path.Combine(candidate, "hypera.dll")))
            {
                return candidate;
            }
        }

        if (repoRoot is not null)
        {
            foreach (string fallbackCfg in new[] { "Release", "Debug" })
            {
                string candidate = Path.Combine(repoRoot, "src", "Hypera.Cli", "bin", fallbackCfg, tfm);
                if (File.Exists(Path.Combine(candidate, "hypera.dll")))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException($"Could not locate Hypera.Cli build output relative to '{AppContext.BaseDirectory}'.");
    }

    private static string? FindRepoRoot(string startDir)
    {
        DirectoryInfo? dir = new(startDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hypera.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file);
            string destPath = Path.Combine(destinationDir, relative);
            string? destParent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destParent))
            {
                Directory.CreateDirectory(destParent);
            }

            File.Copy(file, destPath, overwrite: true);
        }
    }

    private static string RunProcess(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        (int exitCode, string stdout, string stderr) = RunProcessCapturingExit(fileName, arguments, workingDirectory);
        Assert.True(exitCode == 0, $"Command '{fileName} {string.Join(' ', arguments)}' exited with {exitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        return stdout;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcessCapturingExit(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return (proc.ExitCode, stdout, stderr);
    }
}
