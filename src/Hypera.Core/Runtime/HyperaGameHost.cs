using Hypera.Audio;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.Persistence;
using Hypera.Rendering;
using Hypera.VirtualFs;

namespace Hypera.Runtime;

/// <summary>
/// Typed value passed across the optional legacy <c>CALLSHARP</c> plugin bridge.
/// </summary>
public readonly record struct SharpPluginArgument(bool IsString, long IntValue, string StrValue);

/// <summary>
/// Optional plugin dispatch contract so hosts (such as <c>Hypera.LegacyPluginShim.PluginManager</c>)
/// can handle <c>CALLSHARP</c> instructions without introducing a hard dependency in <c>Hypera.Core</c>.
/// </summary>
public interface ISharpPluginHost
{
    void Invoke(string methodName, SharpPluginArgument[] arguments);
}

/// <summary>
/// Configuration options for turnkey game startup via <see cref="HyperaGameHost"/> or <see cref="EmueraEngine"/>.
/// </summary>
public sealed class HyperaGameLoadOptions
{
    /// <summary>
    /// Optional host directory path used for runtime <c>.sav</c>, <c>global.sav</c>, <c>dat/</c>,
    /// and <c>SAVETEXT</c>/<c>LOADTEXT</c> persistence. When null on a physical game directory load,
    /// defaults to the game directory itself; on read-only archives (<c>.hep</c>/<c>.zip</c>),
    /// save files remain in-memory unless a writable directory is explicitly provided.
    /// </summary>
    public string? SaveDirectory { get; init; }

    /// <summary>
    /// Optional pre-constructed <see cref="WritableSaveDirectory"/> (takes precedence over <see cref="SaveDirectory"/>).
    /// </summary>
    public WritableSaveDirectory? WritableSaveDirectory { get; init; }

    /// <summary>
    /// Optional override for <see cref="CompilerOptions"/> loaded by <see cref="ConfigLoader"/>.
    /// </summary>
    public CompilerOptions? CompilerOptionsOverride { get; init; }

    /// <summary>
    /// Optional override for <see cref="RuntimeOptions"/> loaded by <see cref="ConfigLoader"/>.
    /// </summary>
    public RuntimeOptions? RuntimeOptionsOverride { get; init; }

    /// <summary>
    /// Optional cross-platform audio backend (e.g. <c>Hypera.Audio.CrossPlatformAudioMixer</c>).
    /// </summary>
    public IAudioBackend? AudioBackend { get; init; }

    /// <summary>
    /// Optional display snapshot sink for headless/Web/CloudEmuera hosts.
    /// </summary>
    public IDisplaySnapshotSink? DisplaySink { get; init; }

    /// <summary>
    /// Optional legacy <c>CALLSHARP</c> plugin host (e.g. <c>PluginManager</c>).
    /// </summary>
    public ISharpPluginHost? SharpPluginHost { get; init; }

    /// <summary>
    /// When true (default), loads precompiled embedded <c>.heb</c> bytecode from <c>.hep</c> archives if present.
    /// </summary>
    public bool UsePrebuiltBytecode { get; init; } = true;

    /// <summary>
    /// When true, automatically calls <see cref="EmueraEngine.StartSystemFlow"/> (<c>BEGIN TITLE</c>) upon load.
    /// </summary>
    public bool AutoStartSystemFlow { get; init; }

    /// <summary>
    /// Optional deterministic RNG seed for reproducible runs and tests.
    /// </summary>
    public long? RandomSeed { get; init; }

    /// <summary>
    /// When true, enables <c>[IF_DEBUG]</c> and <c>;!;</c> debug compilation directives.
    /// </summary>
    public bool IsDebugMode { get; init; }
}

/// <summary>
/// High-level turnkey loader that wires the complete Hypera startup pipeline in a single call:
/// VFS mount (<see cref="PhysicalDirectoryVfs"/>, <see cref="HepArchiveVfs"/>, <see cref="ZipArchiveVfs"/>) ->
/// <see cref="ConfigLoader"/> -> <see cref="CsvGameDataLoader"/> -> prebuilt <c>.heb</c> or <see cref="ErbProjectCompiler"/> ->
/// fully populated <see cref="EmueraEngine"/> with <see cref="RuntimeDynamicCompiler"/>, <see cref="WritableSaveDirectory"/>,
/// audio backend, and optional <see cref="ISharpPluginHost"/>.
/// </summary>
public sealed class HyperaGameHost : IDisposable
{
    public const string LegacyEmbeddedBytecodePath = "__hypera/module.heb";

    private readonly bool _ownsVfs;
    private bool _disposed;

    private HyperaGameHost(
        EmueraEngine engine,
        IVirtualFileSystem fileSystem,
        bool ownsVfs,
        ConfigLoadResult config,
        GameData gameData,
        RuntimeDynamicCompiler dynamicCompiler,
        WritableSaveDirectory? saveDirectory,
        bool loadedFromPrebuiltBytecode)
    {
        Engine = engine;
        FileSystem = fileSystem;
        _ownsVfs = ownsVfs;
        Config = config;
        GameData = gameData;
        DynamicCompiler = dynamicCompiler;
        SaveDirectory = saveDirectory;
        LoadedFromPrebuiltBytecode = loadedFromPrebuiltBytecode;
    }

    public EmueraEngine Engine { get; }
    public IVirtualFileSystem FileSystem { get; }
    public BytecodeModule Module => Engine.Module;
    public ConfigLoadResult Config { get; }
    public GameData GameData { get; }
    public RuntimeDynamicCompiler DynamicCompiler { get; }
    public WritableSaveDirectory? SaveDirectory { get; }
    public bool LoadedFromPrebuiltBytecode { get; }

    /// <summary>
    /// Auto-detects and loads a raw game directory, <c>.hep</c> package, <c>.zip</c> archive, or standalone <c>.heb</c> file.
    /// </summary>
    public static HyperaGameHost Load(string path, HyperaGameLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);

        if (Directory.Exists(fullPath))
        {
            return LoadFromDirectory(fullPath, options);
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Game directory or package not found: '{fullPath}'.", fullPath);
        }

        string ext = Path.GetExtension(fullPath);
        if (ext.Equals(".hep", StringComparison.OrdinalIgnoreCase))
        {
            return LoadFromHepArchive(fullPath, options);
        }

        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return LoadFromZipArchive(fullPath, options);
        }

        if (ext.Equals(".heb", StringComparison.OrdinalIgnoreCase))
        {
            return LoadFromHebFile(fullPath, options);
        }

        throw new NotSupportedException($"Unsupported Hypera game target extension '{ext}' for '{fullPath}'.");
    }

    public static HyperaGameHost LoadFromDirectory(string gameDirectory, HyperaGameLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        string fullDir = Path.GetFullPath(gameDirectory);
        var vfs = new PhysicalDirectoryVfs(fullDir);
        try
        {
            return LoadFromVfsCore(vfs, ownsVfs: true, defaultSaveRoot: fullDir, options, preloadedModule: null);
        }
        catch
        {
            vfs.Dispose();
            throw;
        }
    }

    public static HyperaGameHost LoadFromHepArchive(string hepFilePath, HyperaGameLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hepFilePath);
        var vfs = new HepArchiveVfs(hepFilePath);
        try
        {
            return LoadFromVfsCore(vfs, ownsVfs: true, defaultSaveRoot: null, options, preloadedModule: null);
        }
        catch
        {
            vfs.Dispose();
            throw;
        }
    }

    public static HyperaGameHost LoadFromHepStream(
        Stream hepStream,
        string rootDescription = "memory.hep",
        bool leaveOpen = false,
        HyperaGameLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(hepStream);
        var vfs = new HepArchiveVfs(hepStream, rootDescription, leaveOpen);
        try
        {
            return LoadFromVfsCore(vfs, ownsVfs: true, defaultSaveRoot: null, options, preloadedModule: null);
        }
        catch
        {
            vfs.Dispose();
            throw;
        }
    }

    public static HyperaGameHost LoadFromZipArchive(string zipFilePath, HyperaGameLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipFilePath);
        var vfs = new ZipArchiveVfs(zipFilePath);
        try
        {
            return LoadFromVfsCore(vfs, ownsVfs: true, defaultSaveRoot: null, options, preloadedModule: null);
        }
        catch
        {
            vfs.Dispose();
            throw;
        }
    }

    public static HyperaGameHost LoadFromHebFile(string hebFilePath, HyperaGameLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hebFilePath);
        string fullHeb = Path.GetFullPath(hebFilePath);
        BytecodeModule module;
        using (var fs = new FileStream(fullHeb, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            module = BytecodeSerializer.Deserialize(fs);
        }

        string parentDir = Path.GetDirectoryName(fullHeb) ?? Environment.CurrentDirectory;
        var vfs = new PhysicalDirectoryVfs(parentDir);
        try
        {
            return LoadFromVfsCore(vfs, ownsVfs: true, defaultSaveRoot: parentDir, options, preloadedModule: module);
        }
        catch
        {
            vfs.Dispose();
            throw;
        }
    }

    public static HyperaGameHost LoadFromVfs(
        IVirtualFileSystem vfs,
        bool ownsVfs = false,
        HyperaGameLoadOptions? options = null,
        BytecodeModule? preloadedModule = null)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        string? defaultSaveRoot = vfs is PhysicalDirectoryVfs && Directory.Exists(vfs.RootDescription)
            ? vfs.RootDescription
            : null;
        return LoadFromVfsCore(vfs, ownsVfs, defaultSaveRoot, options, preloadedModule);
    }

    internal static HyperaGameHost LoadFromVfsCore(
        IVirtualFileSystem vfs,
        bool ownsVfs,
        string? defaultSaveRoot,
        HyperaGameLoadOptions? options,
        BytecodeModule? preloadedModule)
    {
        HyperaGameLoadOptions opts = options ?? new HyperaGameLoadOptions();

        // 1. Load layered configuration: csv/_default.config -> emuera.config -> csv/_fixed.config + setting.json
        ConfigLoadResult config = ConfigLoader.Default.Load(vfs);
        CompilerOptions compilerOptions = opts.CompilerOptionsOverride ?? config.CompilerOptions;
        RuntimeOptions runtimeOptions = opts.RuntimeOptionsOverride ?? config.RuntimeOptions;

        // 2. Load all CSV, ERD, ALS, VarExt, and resources metadata
        GameData gameData = CsvGameDataLoader.Default.Load(vfs, compilerOptions);

        // 3. Resolve BytecodeModule: explicit preloaded -> embedded .heb in VFS -> strict ErbProjectCompiler
        BytecodeModule module;
        CompilationResult? compilation = null;
        bool loadedPrebuilt = false;

        if (preloadedModule is not null)
        {
            module = preloadedModule;
            loadedPrebuilt = true;
        }
        else if (opts.UsePrebuiltBytecode && TryLoadPrebuiltBytecode(vfs, out BytecodeModule? embedded) && embedded is not null)
        {
            module = embedded;
            loadedPrebuilt = true;
        }
        else
        {
            CompilerExternalContext external = CompilerExternalContext.FromGameData(gameData, compilerOptions.IgnoreCase) with
            {
                IsDebugMode = opts.IsDebugMode,
            };
            var compiler = new ErbProjectCompiler(compilerOptions, external);
            compilation = compiler.CompileProject(vfs, throwOnError: true);
            module = compilation.Module;
        }

        // 4. Construct isolated stores & engine
        var characters = new CharacterStore(module.CompilerOptions, gameData.ReplaceConfig);
        var variables = new VariableStore(module, characters, opts.RandomSeed);
        var engine = new EmueraEngine(
            module,
            vfs,
            runtimeOptions,
            variables,
            characters,
            opts.AudioBackend,
            opts.DisplaySink);

        // 5. Populate CSV tables, VariableSize overrides, STR.CSV, _Replace.csv, Chara*.csv, ERD, VarExt, and #DIM initializers
        engine.ApplyGameData(gameData);
        compilation?.ApplyStaticInitializers(variables);

        // 6. Wire RuntimeDynamicCompiler (IDynamicEvaluator)
        var dynamicCompiler = new RuntimeDynamicCompiler(module, compilation?.Symbols);
        dynamicCompiler.AttachTo(engine);

        // 7. Wire sandboxed WritableSaveDirectory (never mutating read-only archives)
        WritableSaveDirectory? saveDir = opts.WritableSaveDirectory;
        if (saveDir is null)
        {
            string? savePath = !string.IsNullOrWhiteSpace(opts.SaveDirectory)
                ? opts.SaveDirectory
                : defaultSaveRoot;
            if (!string.IsNullOrWhiteSpace(savePath))
            {
                saveDir = new WritableSaveDirectory(savePath, runtimeOptions.ValidExtensions);
            }
        }

        if (saveDir is not null)
        {
            engine.ConfigureSaveDirectory(saveDir);
        }

        // 8. Wire optional legacy CALLSHARP plugin host
        if (opts.SharpPluginHost is not null)
        {
            engine.SharpPluginHost = opts.SharpPluginHost;
        }

        // 9. Optionally enter BEGIN TITLE lifecycle
        if (opts.AutoStartSystemFlow)
        {
            engine.StartSystemFlow(SystemPhaseKind.Title);
        }

        return new HyperaGameHost(
            engine,
            vfs,
            ownsVfs,
            config,
            gameData,
            dynamicCompiler,
            saveDir,
            loadedPrebuilt);
    }

    internal static bool TryLoadPrebuiltBytecode(IVirtualFileSystem vfs, out BytecodeModule? module)
    {
        module = null;

        if (vfs.FileExists(HepPackageWriter.DefaultPrebuiltBytecodePath))
        {
            using Stream s = vfs.OpenRead(HepPackageWriter.DefaultPrebuiltBytecodePath);
            module = BytecodeSerializer.Deserialize(s);
            return true;
        }

        if (vfs.FileExists(LegacyEmbeddedBytecodePath))
        {
            using Stream s = vfs.OpenRead(LegacyEmbeddedBytecodePath);
            module = BytecodeSerializer.Deserialize(s);
            return true;
        }

        if (vfs is HepArchiveVfs { HasPrebuiltBytecode: true })
        {
            string? firstHeb = vfs.EnumerateFiles(recursive: true)
                .FirstOrDefault(static p => VfsPathNormalizer.GetExtension(p).Equals(".heb", StringComparison.OrdinalIgnoreCase));
            if (firstHeb is not null)
            {
                using Stream s = vfs.OpenRead(firstHeb);
                module = BytecodeSerializer.Deserialize(s);
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Engine.Dispose();
        if (_ownsVfs)
        {
            FileSystem.Dispose();
        }
    }
}
