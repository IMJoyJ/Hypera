namespace Hypera.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        if (args[0] is "-v" or "--version" or "version")
        {
            Console.WriteLine("hypera 0.1.0");
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        ReadOnlySpan<string> commandArgs = args.AsSpan(1);

        return command switch
        {
            "run" => HandleRun(commandArgs),
            "compile" => HandleCompile(commandArgs),
            "pack" => HandlePack(commandArgs),
            "bundle" => HandleBundle(commandArgs),
            _ => ReportUnknownCommand(command),
        };
    }

    private static int HandleRun(ReadOnlySpan<string> args)
    {
        if (args.IsEmpty)
        {
            Console.Error.WriteLine("Usage: hypera run <game-directory-or-hep>");
            return 2;
        }

        Console.WriteLine($"[hypera] 'run' target '{args[0]}' ready for Wave 2 VM & CLI runner integration.");
        return 0;
    }

    private static int HandleCompile(ReadOnlySpan<string> args)
    {
        if (args.IsEmpty)
        {
            Console.Error.WriteLine("Usage: hypera compile <game-directory> [-o <output.heb>]");
            return 2;
        }

        Console.WriteLine($"[hypera] 'compile' target '{args[0]}' ready for Wave 2 strict compiler integration.");
        return 0;
    }

    private static int HandlePack(ReadOnlySpan<string> args)
    {
        if (args.IsEmpty)
        {
            Console.Error.WriteLine("Usage: hypera pack <game-directory> [-o <output.hep>]");
            return 2;
        }

        Console.WriteLine($"[hypera] 'pack' target '{args[0]}' ready for Wave 2 .hep archive builder integration.");
        return 0;
    }

    private static int HandleBundle(ReadOnlySpan<string> args)
    {
        if (args.IsEmpty)
        {
            Console.Error.WriteLine("Usage: hypera bundle <game-directory> [-o <output-dir>]");
            return 2;
        }

        Console.WriteLine($"[hypera] 'bundle' target '{args[0]}' ready for Wave 2 standalone game bundler integration.");
        return 0;
    }

    private static int ReportUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: '{command}'.");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Hypera Engine CLI (hypera)");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  hypera run <game-dir-or-hep>             Run an Emuera game directory or .hep package");
        Console.WriteLine("  hypera compile <game-dir> [-o <out.heb>] Compile CSV/ERH/ERB scripts to .heb bytecode");
        Console.WriteLine("  hypera pack <game-dir> [-o <out.hep>]    Pack scripts and assets into a single .hep archive");
        Console.WriteLine("  hypera bundle <game-dir> [-o <out-dir>]  Bundle a standalone distributable game");
    }
}
