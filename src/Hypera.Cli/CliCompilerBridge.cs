using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Data;
using Hypera.VirtualFs;

namespace Hypera.Cli;

/// <summary>
/// Delegates <c>hypera compile</c> and <c>hypera pack</c> directly to <c>Hypera.Core</c>'s authoritative
/// <see cref="ConfigLoader"/>, <see cref="CsvGameDataLoader"/>, and <see cref="ErbProjectCompiler"/> pipeline.
/// </summary>
public static class CliCompilerBridge
{
    public static BytecodeModule CompileDirectory(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        using var vfs = new PhysicalDirectoryVfs(gameDirectory);
        return CompileVfs(vfs);
    }

    public static BytecodeModule CompileVfs(IVirtualFileSystem vfs)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ConfigLoadResult config = ConfigLoader.Default.Load(vfs);
        GameData gameData = CsvGameDataLoader.Default.Load(vfs, config.CompilerOptions);
        CompilerExternalContext external = CompilerExternalContext.FromGameData(gameData, config.CompilerOptions.IgnoreCase);
        var compiler = new ErbProjectCompiler(config.CompilerOptions, external);
        return compiler.CompileProject(vfs, throwOnError: true).Module;
    }
}
