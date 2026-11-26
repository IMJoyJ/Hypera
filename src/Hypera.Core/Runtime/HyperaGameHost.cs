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
/// Structured progress report emitted while <see cref="HyperaGameHost"/> loads a game package or directory.
/// </summary>
public readonly record struct GameLoadProgress(
    int Percent,
    string Stage,
    string? Detail = null);

/// <summary>
/// Read-only compiled game package bundle shareable across concurrent <see cref="HyperaGameHost"/> sessions
/// (amortizing <see cref="BytecodeModule"/>, <see cref="ConfigLoadResult"/>, <see cref="GameData"/>,
/// and <see cref="SymbolTable"/> memory once per game package in multi-user server hosts).
/// </summary>
public sealed class SharedGamePackage
{
    public required BytecodeModule Module { get; init; }
    public required ConfigLoadResult Config { get; init; }
    public required GameData GameData { get; init; }
    public required SymbolTable SharedSymbols { get; init; }
    public bool LoadedFromPrebuiltBytecode { get; init; }
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
    /// Optional pluggable <see cref="IWritableUserStorage"/> backend (e.g. <see cref="InMemoryWritableStorage"/>
    /// for Web/Server IndexedDB sync, or custom quota-enforced storage). Takes precedence over
    /// <see cref="WritableSaveDirectory"/> and <see cref="SaveDirectory"/>.
    /// </summary>
    public IWritableUserStorage? WritableStorage { get; init; }

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

    /// <summary>
    /// Optional progress callback receiving percentage and stage descriptions during load.
    /// </summary>
    public IProgress<GameLoadProgress>? Progress { get; init; }

    /// <summary>
    /// Optional cancellation token observed across loading stages.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Optional preloaded immutable <see cref="SharedGamePackage"/> reused across concurrent sessions
    /// so each session only allocates its own mutable <see cref="VariableStore"/> and <see cref="CharacterStore"/>.
    /// </summary>
    public SharedGamePackage? SharedPackage { get; init; }

    /// <summary>
    /// Optional decoded pixel-buffer byte budget for <see cref="SpriteRegistry.MaxDecodedCacheBytes"/>
    /// (defaults to <see cref="SpriteRegistry.DefaultMaxDecodedCacheBytes"/>, 128 MB).
    /// </summary>
    public long? MaxSpriteCacheBytes { get; init; }
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
        IWritableUserStorage? writableStorage,
        bool loadedFromPrebuiltBytecode)
    {
        Engine = engine;
        FileSystem = fileSystem;
        _ownsVfs = ownsVfs;
        Config = config;
        GameData = gameData;
        DynamicCompiler = dynamicCompiler;
        SaveDirectory = saveDirectory;
        WritableStorage = writableStorage;
        LoadedFromPrebuiltBytecode = loadedFromPrebuiltBytecode;
    }

    public EmueraEngine Engine { get; }
    public IVirtualFileSystem FileSystem { get; }
    public BytecodeModule Module => Engine.Module;
    public ConfigLoadResult Config { get; }
    public GameData GameData { get; }
    public RuntimeDynamicCompiler DynamicCompiler { get; }
    public WritableSaveDirectory? SaveDirectory { get; }
    public IWritableUserStorage? WritableStorage { get; }
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

    /// <summary>
    /// Loads and caches the immutable portions of a game package (<see cref="ConfigLoadResult"/>,
    /// <see cref="GameData"/>, <see cref="BytecodeModule"/>, and lazy <see cref="SymbolTable"/>) once for
    /// sharing across many concurrent sessions via <see cref="HyperaGameLoadOptions.SharedPackage"/>.
    /// </summary>
    public static SharedGamePackage LoadSharedPackage(
        IVirtualFileSystem vfs,
        bool usePrebuiltBytecode = true,
        bool isDebugMode = false,
        CompilerOptions? compilerOptionsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ConfigLoadResult config = ConfigLoader.Default.Load(vfs);
        CompilerOptions compilerOptions = compilerOptionsOverride ?? config.CompilerOptions;
        GameData gameData = CsvGameDataLoader.Default.Load(vfs, compilerOptions);

        BytecodeModule module;
        SymbolTable symbols;
        bool loadedPrebuilt = false;

        if (usePrebuiltBytecode && TryLoadPrebuiltBytecode(vfs, out BytecodeModule? embedded) && embedded is not null)
        {
            module = embedded;
            symbols = RuntimeDynamicCompiler.CreateSharedSymbolTable(module);
            loadedPrebuilt = true;
        }
        else
        {
            CompilerExternalContext external = CompilerExternalContext.FromGameData(gameData, compilerOptions.IgnoreCase) with
            {
                IsDebugMode = isDebugMode,
            };
            var compiler = new ErbProjectCompiler(compilerOptions, external);
            CompilationResult compilation = compiler.CompileProject(vfs, throwOnError: true);
            module = compilation.Module;
            symbols = RuntimeDynamicCompiler.CreateSharedSymbolTable(module);
        }

        return new SharedGamePackage
        {
            Module = module,
            Config = config,
            GameData = gameData,
            SharedSymbols = symbols,
            LoadedFromPrebuiltBytecode = loadedPrebuilt,
        };
    }

    internal static HyperaGameHost LoadFromVfsCore(
        IVirtualFileSystem vfs,
        bool ownsVfs,
        string? defaultSaveRoot,
        HyperaGameLoadOptions? options,
        BytecodeModule? preloadedModule)
    {
        HyperaGameLoadOptions opts = options ?? new HyperaGameLoadOptions();
        IProgress<GameLoadProgress>? progress = opts.Progress;
        CancellationToken ct = opts.CancellationToken;

        static void Report(IProgress<GameLoadProgress>? sink, int percent, string stage, string? detail = null) =>
            sink?.Report(new GameLoadProgress(Math.Clamp(percent, 0, 100), stage, detail));

        ct.ThrowIfCancellationRequested();

        ConfigLoadResult config;
        CompilerOptions compilerOptions;
        RuntimeOptions runtimeOptions;
        GameData gameData;
        BytecodeModule module;
        CompilationResult? compilation = null;
        SymbolTable? sharedSymbols = null;
        bool loadedPrebuilt = false;

        if (opts.SharedPackage is { } shared)
        {
            Report(progress, 44, "Attaching shared game package...");
            config = shared.Config;
            compilerOptions = opts.CompilerOptionsOverride ?? config.CompilerOptions;
            runtimeOptions = opts.RuntimeOptionsOverride ?? config.RuntimeOptions;
            gameData = shared.GameData;
            module = shared.Module;
            sharedSymbols = shared.SharedSymbols;
            loadedPrebuilt = shared.LoadedFromPrebuiltBytecode;
        }
        else
        {
            // 1. Load layered configuration: csv/_default.config -> emuera.config -> csv/_fixed.config + setting.json
            Report(progress, 6, "Loading configuration...", "emuera.config & defaults");
            config = ConfigLoader.Default.Load(vfs);
            compilerOptions = opts.CompilerOptionsOverride ?? config.CompilerOptions;
            runtimeOptions = opts.RuntimeOptionsOverride ?? config.RuntimeOptions;
            ct.ThrowIfCancellationRequested();

            // 2. Load all CSV, ERD, ALS, VarExt, and resources metadata
            Report(progress, 10, "Loading CSV & resource metadata...", "CSV / ERD / resources");
            gameData = CsvGameDataLoader.Default.Load(vfs, compilerOptions);
            ct.ThrowIfCancellationRequested();

            // 3. Resolve BytecodeModule: explicit preloaded -> embedded .heb in VFS -> strict ErbProjectCompiler
            if (preloadedModule is not null)
            {
                Report(progress, 44, "Using preloaded bytecode...");
                module = preloadedModule;
                loadedPrebuilt = true;
            }
            else if (opts.UsePrebuiltBytecode && HasPrebuiltBytecode(vfs))
            {
                Report(progress, 15, "Loading precompiled bytecode...", "game.heb");
                if (TryLoadPrebuiltBytecode(vfs, out BytecodeModule? embedded) && embedded is not null)
                {
                    module = embedded;
                    loadedPrebuilt = true;
                }
                else
                {
                    Report(progress, 15, "Compiling ERB scripts...");
                    CompilerExternalContext external = CompilerExternalContext.FromGameData(gameData, compilerOptions.IgnoreCase) with
                    {
                        IsDebugMode = opts.IsDebugMode,
                    };
                    var compiler = new ErbProjectCompiler(compilerOptions, external);
                    compilation = compiler.CompileProject(vfs, throwOnError: true);
                    module = compilation.Module;
                }
            }
            else
            {
                Report(progress, 15, "Compiling ERB scripts...");
                CompilerExternalContext external = CompilerExternalContext.FromGameData(gameData, compilerOptions.IgnoreCase) with
                {
                    IsDebugMode = opts.IsDebugMode,
                };
                var compiler = new ErbProjectCompiler(compilerOptions, external);
                compilation = compiler.CompileProject(vfs, throwOnError: true);
                module = compilation.Module;
            }
        }

        ct.ThrowIfCancellationRequested();

        // 4. Construct isolated stores & engine
        int totalStaticVars = module.StaticVariables.Length;
        Report(progress, 46, "Allocating runtime variables...", totalStaticVars > 0 ? $"0 / {totalStaticVars:N0} slots" : null);
        var characters = new CharacterStore(module.CompilerOptions, gameData.ReplaceConfig);
        var variables = new VariableStore(
            module,
            characters,
            opts.RandomSeed,
            progress is null
                ? null
                : (done, total) =>
                {
                    int pct = 46 + (int)Math.Round(34.0 * done / Math.Max(1, total));
                    Report(progress, pct, "Allocating runtime variables...", $"{done:N0} / {total:N0} slots");
                },
            ct);
        ct.ThrowIfCancellationRequested();

        Report(progress, 81, "Initializing engine, fonts & sprites...");
        var engine = new EmueraEngine(
            module,
            vfs,
            runtimeOptions,
            variables,
            characters,
            opts.AudioBackend,
            opts.DisplaySink);
        if (opts.MaxSpriteCacheBytes.HasValue)
        {
            engine.Sprites.MaxDecodedCacheBytes = opts.MaxSpriteCacheBytes.Value;
        }

        ct.ThrowIfCancellationRequested();

        // 5. Populate CSV tables, VariableSize overrides, STR.CSV, _Replace.csv, Chara*.csv, ERD, VarExt, and #DIM initializers
        Report(progress, 85, "Applying game data tables...");
        engine.ConfigureConfigValues(config.RawValues);
        engine.ApplyGameData(gameData);
        compilation?.ApplyStaticInitializers(variables);
        ct.ThrowIfCancellationRequested();

        // 6. Wire RuntimeDynamicCompiler (IDynamicEvaluator)
        Report(progress, 88, "Preparing dynamic expression evaluator...");
        var dynamicCompiler = new RuntimeDynamicCompiler(module, sharedSymbols ?? compilation?.Symbols);
        dynamicCompiler.AttachTo(engine);
        ct.ThrowIfCancellationRequested();

        // 7. Wire sandboxed IWritableUserStorage (pluggable host backend; never mutates read-only archives)
        Report(progress, 96, "Configuring save storage...");
        IWritableUserStorage? writableStorage = opts.WritableStorage ?? opts.WritableSaveDirectory;
        if (writableStorage is null)
        {
            string? savePath = !string.IsNullOrWhiteSpace(opts.SaveDirectory)
                ? opts.SaveDirectory
                : defaultSaveRoot;
            if (!string.IsNullOrWhiteSpace(savePath))
            {
                writableStorage = new WritableSaveDirectory(savePath, runtimeOptions.ValidExtensions);
            }
        }

        if (writableStorage is not null)
        {
            engine.ConfigureWritableStorage(writableStorage);
        }

        // 8. Wire optional legacy CALLSHARP plugin host
        if (opts.SharpPluginHost is not null)
        {
            engine.SharpPluginHost = opts.SharpPluginHost;
        }

        // 9. Optionally enter BEGIN TITLE lifecycle
        if (opts.AutoStartSystemFlow)
        {
            Report(progress, 98, "Starting title sequence...");
            engine.StartSystemFlow(SystemPhaseKind.Title);
        }

        Report(progress, 100, "Ready");
        return new HyperaGameHost(
            engine,
            vfs,
            ownsVfs,
            config,
            gameData,
            dynamicCompiler,
            writableStorage as WritableSaveDirectory,
            writableStorage,
            loadedPrebuilt);
    }

    private static bool HasPrebuiltBytecode(IVirtualFileSystem vfs)
    {
        if (vfs.FileExists(HepPackageWriter.DefaultPrebuiltBytecodePath) ||
            vfs.FileExists(LegacyEmbeddedBytecodePath))
        {
            return true;
        }

        return HasHepPrebuiltBytecode(vfs);
    }

    private static bool HasHepPrebuiltBytecode(IVirtualFileSystem vfs)
    {
        if (vfs is OverlayVirtualFileSystem overlay)
        {
            return HasHepPrebuiltBytecode(overlay.Upper) || HasHepPrebuiltBytecode(overlay.Lower);
        }

        return vfs is HepArchiveVfs { HasPrebuiltBytecode: true };
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

        if (TryLoadFromHepIfPrebuilt(vfs, out module))
        {
            return true;
        }

        return false;
    }

    private static bool TryLoadFromHepIfPrebuilt(IVirtualFileSystem vfs, out BytecodeModule? module)
    {
        module = null;
        if (vfs is OverlayVirtualFileSystem overlay)
        {
            return TryLoadFromHepIfPrebuilt(overlay.Upper, out module) ||
                   TryLoadFromHepIfPrebuilt(overlay.Lower, out module);
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
