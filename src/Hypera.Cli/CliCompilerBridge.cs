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
    public static BytecodeModule CompileDirectory(string gameDirectory, bool? useNewRandomOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        using var vfs = new PhysicalDirectoryVfs(gameDirectory);
        return CompileVfs(vfs, useNewRandomOverride);
    }

    public static BytecodeModule CompileVfs(IVirtualFileSystem vfs, bool? useNewRandomOverride = null)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ConfigLoadResult config = ConfigLoader.Default.Load(vfs);
        Configuration.CompilerOptions compilerOptions = useNewRandomOverride.HasValue
            ? config.CompilerOptions with { UseNewRandom = useNewRandomOverride.Value }
            : config.CompilerOptions;
        GameData gameData = CsvGameDataLoader.Default.Load(vfs, compilerOptions);
        CompilerExternalContext external = CompilerExternalContext.FromGameData(gameData, compilerOptions.IgnoreCase);
        var compiler = new ErbProjectCompiler(compilerOptions, external);
        return compiler.CompileProject(vfs, throwOnError: true).Module;
    }
}
