using System.Text;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.Diagnostics;
using Hypera.Rendering;
using Hypera.Runtime;
using Hypera.VirtualFs;

namespace Hypera.Tests.Compiler;

internal sealed class CompilerMemoryVfs : IVirtualFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public string RootDescription => "memory://compiler-tests";

    public CompilerMemoryVfs AddFile(string virtualPath, string utf8Content)
    {
        _files[Normalize(virtualPath)] = Encoding.UTF8.GetBytes(utf8Content);
        return this;
    }

    public bool FileExists(string virtualPath) => _files.ContainsKey(Normalize(virtualPath));

    public bool DirectoryExists(string virtualPath)
    {
        string prefix = Normalize(virtualPath).TrimEnd('/');
        if (prefix.Length == 0)
        {
            return true;
        }

        prefix += "/";
        foreach (string key in _files.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public Stream OpenRead(string virtualPath) => new MemoryStream(ReadAllBytes(virtualPath), writable: false);

    public byte[] ReadAllBytes(string virtualPath) =>
        _files.TryGetValue(Normalize(virtualPath), out byte[]? bytes)
            ? bytes
            : throw new FileNotFoundException($"Missing VFS file: {virtualPath}");

    public string ReadAllText(string virtualPath, Encoding? overrideEncoding = null) =>
        (overrideEncoding ?? Encoding.UTF8).GetString(ReadAllBytes(virtualPath));

    public IEnumerable<string> EnumerateFiles(
        string directoryVirtualPath = "",
        string searchPattern = "*",
        bool recursive = true)
    {
        string prefix = Normalize(directoryVirtualPath).Trim('/');
        string extFilter = searchPattern.StartsWith("*.", StringComparison.Ordinal)
            ? searchPattern[1..]
            : string.Empty;

        foreach (string key in _files.Keys)
        {
            if (prefix.Length > 0 && !key.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!recursive)
            {
                string rel = prefix.Length > 0 ? key[(prefix.Length + 1)..] : key;
                if (rel.Contains('/'))
                {
                    continue;
                }
            }

            if (extFilter.Length > 0 && !key.EndsWith(extFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return key;
        }
    }

    public void Dispose()
    {
    }

    private static string Normalize(string path) =>
        (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
}

internal sealed record ExecutedRun(
    CompilationResult Compilation,
    EmueraEngine Engine,
    VariableStore Variables,
    RuntimeDynamicCompiler DynamicCompiler,
    string PrintedOutput,
    IReadOnlyList<string> PrintedLines)
{
    public long GetInt(string varName, params long[] indices) =>
        Variables.TryResolveSlot(varName, out int slot)
            ? Variables.GetInt(slot, indices)
            : throw new KeyNotFoundException($"Slot '{varName}' not found.");

    public string GetStr(string varName, params long[] indices) =>
        Variables.TryResolveSlot(varName, out int slot)
            ? Variables.GetStr(slot, indices)
            : throw new KeyNotFoundException($"Slot '{varName}' not found.");
}

internal static class CompilerTestHarness
{
    static CompilerTestHarness()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static CompilationResult Compile(
        CompilerMemoryVfs vfs,
        CompilerOptions? options = null,
        CompilerExternalContext? external = null,
        bool throwOnError = true)
    {
        var compiler = new ErbProjectCompiler(options, external);
        return compiler.CompileProject(vfs, throwOnError);
    }

    public static CompilationResult CompileSingleErb(
        string erbSource,
        string? erhSource = null,
        CompilerOptions? options = null,
        CompilerExternalContext? external = null,
        bool throwOnError = true)
    {
        var vfs = new CompilerMemoryVfs();
        if (erhSource is not null)
        {
            vfs.AddFile("ERH/HEADER.ERH", erhSource);
        }

        vfs.AddFile("ERB/MAIN.ERB", erbSource);
        return Compile(vfs, options, external, throwOnError);
    }

    public static ExecutedRun CompileAndRun(
        string erbSource,
        string entryFunctionName = "MAIN",
        string? erhSource = null,
        CompilerOptions? options = null,
        CompilerExternalContext? external = null,
        RuntimeOptions? runtimeOptions = null,
        GameData? gameData = null)
    {
        var vfs = new CompilerMemoryVfs();
        if (erhSource is not null)
        {
            vfs.AddFile("ERH/HEADER.ERH", erhSource);
        }

        vfs.AddFile("ERB/MAIN.ERB", erbSource);
        return CompileAndRunVfs(vfs, entryFunctionName, options, external, runtimeOptions, gameData);
    }

    public static ExecutedRun CompileAndRunVfs(
        CompilerMemoryVfs vfs,
        string entryFunctionName = "MAIN",
        CompilerOptions? options = null,
        CompilerExternalContext? external = null,
        RuntimeOptions? runtimeOptions = null,
        GameData? gameData = null)
    {
        CompilerOptions effectiveOptions = options ?? CompilerOptions.Default;
        if (options is null && !SymbolTable.IsSystemFunctionName(entryFunctionName, effectiveOptions.IgnoreCase))
        {
            effectiveOptions = effectiveOptions with { IgnoreUncalledFunction = false };
        }

        CompilationResult res = Compile(vfs, effectiveOptions, external, throwOnError: true);
        var engine = new EmueraEngine(res.Module, vfs, runtimeOptions);
        var vars = (VariableStore)engine.Variables!;
        if (gameData is not null)
        {
            engine.ApplyGameData(gameData);
        }

        res.ApplyStaticInitializers(vars);
        var dynCompiler = new RuntimeDynamicCompiler(res.Module, res.Symbols).AttachTo(engine);

        FunctionDescriptor entry = res.Module.Functions.First(f =>
            f.Name.Equals(entryFunctionName, StringComparison.OrdinalIgnoreCase));
        engine.PushEntryFrame(entry);

        EngineStepStatus status = engine.Step(500_000);
        if (status == EngineStepStatus.Faulted && engine.FaultException is not null)
        {
            throw engine.FaultException;
        }

        DisplaySnapshot snap = engine.CaptureSnapshot();
        var fullSb = new StringBuilder();
        var lines = new List<string>();
        var lineSb = new StringBuilder();

        foreach (DisplayOperation op in snap.Operations)
        {
            switch (op.Kind)
            {
                case DisplayOperationKind.PrintTextSpan:
                case DisplayOperationKind.PrintButtonSpan:
                    fullSb.Append(op.Text);
                    lineSb.Append(op.Text);
                    break;
                case DisplayOperationKind.NewLine:
                    fullSb.Append('\n');
                    lines.Add(lineSb.ToString());
                    lineSb.Clear();
                    break;
            }
        }

        if (lineSb.Length > 0)
        {
            lines.Add(lineSb.ToString());
        }

        return new ExecutedRun(res, engine, vars, dynCompiler, fullSb.ToString(), lines);
    }
}
