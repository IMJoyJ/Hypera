using System.IO;
using System.Reflection;
using System.Text;

namespace Hypera.Packager;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        PackagerLaunchOptions launch = PackagerCliArgs.Parse(args);
        if (launch.Mode != PackagerLaunchMode.Gui)
        {
            TryConfigureUtf8Console();
            return ExecuteNonGui(launch, Console.Out, Console.Error);
        }

        var app = new App();
        app.InitializeComponent();
        var mainWindow = new MainWindow(launch);
        return app.Run(mainWindow);
    }

    internal static int ExecuteNonGui(PackagerLaunchOptions launch, TextWriter stdout, TextWriter stderr)
    {
        switch (launch.Mode)
        {
            case PackagerLaunchMode.ShowHelp:
                PrintUsage(stdout);
                return 0;

            case PackagerLaunchMode.ShowVersion:
                stdout.WriteLine($"Hypera.Packager {GetVersionString()}");
                return 0;

            case PackagerLaunchMode.InvalidCli:
                stderr.WriteLine($"[Hypera.Packager] {launch.CliErrorMessage ?? "Invalid arguments."}");
                return 2;

            case PackagerLaunchMode.CliPack:
                return RunCliPack(launch, stdout, stderr);

            default:
                return 2;
        }
    }

    private static int RunCliPack(PackagerLaunchOptions launch, TextWriter stdout, TextWriter stderr)
    {
        string sourceDir = launch.InitialSourceDirectory ?? string.Empty;
        string outputPath = launch.InitialOutputHepPath ?? string.Empty;

        var request = new PackRequest(
            SourceDirectory: sourceDir,
            OutputHepPath: outputPath,
            CompiledOnly: launch.CompiledOnly,
            Compress: launch.Compress,
            RandomMode: launch.RandomMode,
            ExcludePaths: launch.ExcludePaths);

        PackResult result = PackagerService.ExecutePack(request);
        if (result.Success)
        {
            stdout.WriteLine(
                $"[Hypera.Packager] Packed '{result.SourceDirectory}' ({result.EntryCount} entries, {result.OutputSizeBytes} bytes, compiledOnly={launch.CompiledOnly}) -> '{result.OutputHepPath}'.");
            if (result.WarningCount > 0)
            {
                stdout.WriteLine(DiagnosticFormatter.FormatResult(result, launch.LanguageCode));
            }

            return 0;
        }

        stderr.WriteLine(DiagnosticFormatter.FormatResult(result, launch.LanguageCode));
        return result.ValidationErrorKey is not null ? 2 : 1;
    }

    public static string GetVersionString()
    {
        string? info = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plusIdx = info.IndexOf('+');
            return plusIdx >= 0 ? info[..plusIdx] : info;
        }

        Version? version = typeof(Program).Assembly.GetName().Version;
        return version is not null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
    }

    private static void PrintUsage(TextWriter stdout)
    {
        stdout.WriteLine($"Hypera.Packager {GetVersionString()}");
        stdout.WriteLine();
        stdout.WriteLine("Usage:");
        stdout.WriteLine("  Hypera.Packager.exe                               Open interactive WPF packager UI");
        stdout.WriteLine("  Hypera.Packager.exe <game-directory>              Open UI and auto-pack directory into <parent>/<dir>.hep");
        stdout.WriteLine("  Hypera.Packager.exe --cli-pack <dir> [options]    Headless pack without UI");
        stdout.WriteLine();
        stdout.WriteLine("Options:");
        stdout.WriteLine("  -o, --output <path.hep>   Output .hep file path (default: <parent>/<dir>.hep)");
        stdout.WriteLine("  --compiled-only           Strip ERB/ERH/sav/binaries (default)");
        stdout.WriteLine("  --no-compiled-only        Include raw ERB/ERH source files");
        stdout.WriteLine("  --no-compress             Disable Deflate compression");
        stdout.WriteLine("  --new-random              Force UseNewRandom = true");
        stdout.WriteLine("  --old-random              Force UseNewRandom = false (Old SFMT)");
        stdout.WriteLine("  -x, --exclude <paths>     Semicolon-separated extra exclude paths");
        stdout.WriteLine("  --lang <ja|zh-CN|en>      UI / diagnostic language (default: ja)");
    }

    private static void TryConfigureUtf8Console()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
            // Ignore when stdout is not attached.
        }
    }
}
