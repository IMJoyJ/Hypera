using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Hypera.Audio;
using Hypera.Bytecode;
using Hypera.Data;
using Hypera.Diagnostics;
using Hypera.Persistence;
using Hypera.Rendering;
using Hypera.Runtime;
using Hypera.VirtualFs;
using MinorShift.Emuera.Runtime.Utils.PluginSystem;
using SkiaSharp;

namespace Hypera.Standalone;

/// <summary>
/// Owns a complete running standalone game session: VFS (with optional external overlay),
/// <see cref="HyperaGameHost"/>, <see cref="CrossPlatformAudioMixer"/> + <see cref="WinMmAudioDriver"/>,
/// optional legacy <c>CALLSHARP</c> <see cref="PluginManager"/>, and Emuera window/icon configuration.
/// </summary>
public sealed class StandaloneGameSession : IDisposable
{
    private readonly List<IDisposable> _ownedResources = [];
    private bool _disposed;

    private StandaloneGameSession(
        StandaloneLaunchTarget target,
        HyperaGameHost host,
        CrossPlatformAudioMixer audioMixer,
        WinMmAudioDriver? audioDriver,
        PluginManager? pluginManager,
        long loadElapsedMs)
    {
        Target = target;
        Host = host;
        AudioMixer = audioMixer;
        AudioDriver = audioDriver;
        PluginManager = pluginManager;
        LoadElapsedMs = loadElapsedMs;
    }

    public StandaloneLaunchTarget Target { get; }
    public HyperaGameHost Host { get; }
    public EmueraEngine Engine => Host.Engine;
    public CrossPlatformAudioMixer AudioMixer { get; }
    public WinMmAudioDriver? AudioDriver { get; }
    public PluginManager? PluginManager { get; }
    public long LoadElapsedMs { get; }

    public static StandaloneGameSession Load(
        StandaloneLaunchTarget target,
        bool isDebugMode,
        bool enableHardwareAudio,
        IProgress<GameLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == StandaloneTargetKind.None)
        {
            throw new InvalidOperationException("No game target was specified or discovered.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string targetName = Path.GetFileName(target.Path);
        progress?.Report(new GameLoadProgress(1, "Opening game package...", string.IsNullOrEmpty(targetName) ? target.Path : targetName));

        var sw = Stopwatch.StartNew();
        var owned = new List<IDisposable>();

        try
        {
            IVirtualFileSystem vfs;
            BytecodeModule? preloadedModule = null;

            switch (target.Kind)
            {
                case StandaloneTargetKind.EmbeddedBundle:
                {
                    BundleTrailer trailer = target.EmbeddedTrailer
                        ?? throw new InvalidOperationException("Missing HYPRBNDL trailer metadata.");
                    if (trailer.PayloadKind == BundlePayloadKind.Hep)
                    {
                        Stream subStream = SingleFileBundleCodec.OpenPayloadStream(target.Path, trailer);
                        owned.Add(subStream);
                        var hepVfs = new HepArchiveVfs(subStream, target.Path, leaveOpen: false);
                        vfs = WrapWithOptionalOverlay(hepVfs, target.OverlayDirectory);
                    }
                    else if (trailer.PayloadKind == BundlePayloadKind.Heb)
                    {
                        progress?.Report(new GameLoadProgress(10, "Loading embedded bytecode...", targetName));
                        using Stream subStream = SingleFileBundleCodec.OpenPayloadStream(target.Path, trailer);
                        preloadedModule = BytecodeSerializer.Deserialize(
                            subStream,
                            progress is null
                                ? null
                                : (read, total) =>
                                {
                                    double frac = total > 0 ? Math.Clamp((double)read / total, 0.0, 1.0) : 1.0;
                                    int pct = 10 + (int)Math.Round(78.0 * frac);
                                    string detail = total >= 1024 * 1024
                                        ? string.Create(CultureInfo.InvariantCulture, $"{targetName} ({read / 1048576.0:0.0} / {total / 1048576.0:0.0} MB)")
                                        : string.Create(CultureInfo.InvariantCulture, $"{targetName} ({read:N0} / {total:N0} B)");
                                    progress.Report(new GameLoadProgress(pct, "Loading embedded bytecode...", detail, read, total));
                                },
                            cancellationToken);
                        string dir = Path.GetDirectoryName(target.Path) ?? target.SaveDirectory;
                        vfs = new PhysicalDirectoryVfs(dir);
                    }
                    else
                    {
                        throw new InvalidDataException($"Unsupported HYPRBNDL payload kind: {trailer.PayloadKind}.");
                    }

                    break;
                }

                case StandaloneTargetKind.HepArchive:
                {
                    var hepVfs = new HepArchiveVfs(target.Path);
                    vfs = WrapWithOptionalOverlay(hepVfs, target.OverlayDirectory);
                    break;
                }

                case StandaloneTargetKind.ZipArchive:
                {
                    var zipVfs = new ZipArchiveVfs(target.Path);
                    vfs = WrapWithOptionalOverlay(zipVfs, target.OverlayDirectory);
                    break;
                }

                case StandaloneTargetKind.HebBytecode:
                {
                    progress?.Report(new GameLoadProgress(10, "Loading precompiled bytecode...", targetName));
                    using (var fs = new FileStream(target.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        preloadedModule = BytecodeSerializer.Deserialize(
                            fs,
                            progress is null
                                ? null
                                : (read, total) =>
                                {
                                    double frac = total > 0 ? Math.Clamp((double)read / total, 0.0, 1.0) : 1.0;
                                    int pct = 10 + (int)Math.Round(78.0 * frac);
                                    string detail = total >= 1024 * 1024
                                        ? string.Create(CultureInfo.InvariantCulture, $"{targetName} ({read / 1048576.0:0.0} / {total / 1048576.0:0.0} MB)")
                                        : string.Create(CultureInfo.InvariantCulture, $"{targetName} ({read:N0} / {total:N0} B)");
                                    progress.Report(new GameLoadProgress(pct, "Loading precompiled bytecode...", detail, read, total));
                                },
                            cancellationToken);
                    }

                    string dir = Path.GetDirectoryName(target.Path) ?? target.SaveDirectory;
                    vfs = new PhysicalDirectoryVfs(dir);
                    break;
                }

                case StandaloneTargetKind.Directory:
                {
                    vfs = new PhysicalDirectoryVfs(target.Path);
                    break;
                }

                default:
                    throw new NotSupportedException($"Unsupported target kind: {target.Kind}.");
            }

            owned.Add(vfs);
            cancellationToken.ThrowIfCancellationRequested();

            var audioMixer = new CrossPlatformAudioMixer(vfs, sampleRate: 48_000);
            owned.Add(audioMixer);

            var loadOptions = new HyperaGameLoadOptions
            {
                SaveDirectory = target.SaveDirectory,
                AudioBackend = audioMixer,
                UsePrebuiltBytecode = true,
                AutoStartSystemFlow = true,
                IsDebugMode = isDebugMode,
                Progress = progress,
                CancellationToken = cancellationToken,
            };

            HyperaGameHost host = HyperaGameHost.LoadFromVfs(
                vfs,
                ownsVfs: false,
                options: loadOptions,
                preloadedModule: preloadedModule);
            owned.Add(host);
            cancellationToken.ThrowIfCancellationRequested();

            // Always capture DEBUGPRINT* entries for the standalone Debug Console even when emuera.config sets UseDebugCommand=NO
            host.Engine.AlwaysCaptureDebugPrint = true;
            host.Engine.RecordDiagnostic(
                "INIT",
                $"Session mounted: kind={target.Kind}, prebuilt={host.LoadedFromPrebuiltBytecode}, target='{target.Path}', overlay='{target.OverlayDirectory ?? "(none)"}', saveDir='{target.SaveDirectory}'",
                includeCallerLocation: false);

            PluginManager? pluginManager = null;
            string pluginsDir = Path.Combine(target.SaveDirectory, "Plugins");
            if (Directory.Exists(pluginsDir))
            {
                pluginManager = new PluginManager(host.Engine)
                {
                    PluginsDirectory = pluginsDir,
                };
                pluginManager.LoadPluginsFromDirectory(pluginsDir);
                host.Engine.SharpPluginHost = pluginManager;
                owned.Add(pluginManager);
            }

            WinMmAudioDriver? audioDriver = enableHardwareAudio ? new WinMmAudioDriver(audioMixer) : null;
            if (audioDriver is not null)
            {
                owned.Add(audioDriver);
            }

            sw.Stop();
            var session = new StandaloneGameSession(target, host, audioMixer, audioDriver, pluginManager, sw.ElapsedMilliseconds);
            session._ownedResources.AddRange(owned);
            return session;
        }
        catch
        {
            for (int i = owned.Count - 1; i >= 0; i--)
            {
                try { owned[i].Dispose(); } catch { }
            }

            throw;
        }
    }

    public string GetEffectiveWindowTitle()
    {
        string? scriptTitle = Engine.FastVariables?.WindowTitle;
        if (!string.IsNullOrWhiteSpace(scriptTitle))
        {
            return scriptTitle;
        }

        GameBaseData gb = Host.GameData.GameBase;
        if (!string.IsNullOrWhiteSpace(gb.Title))
        {
            string ver = !string.IsNullOrWhiteSpace(gb.VersionName)
                ? $" {gb.VersionName.Trim()}"
                : gb.Version > 0
                    ? $" {gb.Version / 1000}.{gb.Version % 1000:000}"
                    : string.Empty;
            return gb.Title.Trim() + ver;
        }

        return "Hypera Standalone";
    }

    public bool GetConfigBool(ConfigCode code, bool fallback) =>
        Host.Config.RawValues.TryGetValue(code, out object? raw) && raw is bool b ? b : fallback;

    public int GetConfigInt(ConfigCode code, int fallback) =>
        Host.Config.RawValues.TryGetValue(code, out object? raw)
            ? raw switch
            {
                int i => i,
                long l => (int)l,
                _ => fallback,
            }
            : fallback;

    public string GetConfigString(ConfigCode code, string fallback = "") =>
        Host.Config.RawValues.TryGetValue(code, out object? raw) && raw is string s ? s : fallback;

    /// <summary>
    /// Decodes the game's configured <c>EMUERAのアイコンのパス</c> (supports WebP, PNG, BMP, GIF, ICO)
    /// from the active VFS into a native WinForms <see cref="Icon"/>.
    /// </summary>
    public Icon? TryLoadWindowIcon()
    {
        string iconPath = GetConfigString(ConfigCode.EmueraIcon).Trim();
        if (string.IsNullOrEmpty(iconPath))
        {
            return null;
        }

        string normalized = VfsPathNormalizer.Normalize(iconPath);
        if (!Host.FileSystem.FileExists(normalized))
        {
            return null;
        }

        try
        {
            if (VfsPathNormalizer.GetExtension(normalized).Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                using Stream icoStream = Host.FileSystem.OpenRead(normalized);
                return new Icon(icoStream);
            }

            using Stream imgStream = Host.FileSystem.OpenRead(normalized);
            IReadOnlyList<DecodedImageFrame> frames = ImageCodecDecoder.DecodeFrames(imgStream);
            if (frames.Count == 0)
            {
                return null;
            }

            try
            {
                return CreateIconFromSkBitmap(frames[0].Bitmap, size: 32);
            }
            finally
            {
                foreach (DecodedImageFrame frame in frames)
                {
                    frame.Bitmap.Dispose();
                }
            }
        }
        catch
        {
            return null;
        }
    }

    public string BuildRuntimeInfoText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Title:              {GetEffectiveWindowTitle()}");
        sb.AppendLine($"Target Kind:        {Target.Kind}");
        sb.AppendLine($"Target Path:        {Target.Path}");
        sb.AppendLine($"Overlay Directory:  {Target.OverlayDirectory ?? "(disabled)"}");
        sb.AppendLine($"Save Directory:     {Target.SaveDirectory}");
        sb.AppendLine($"Load Mode:          {(Host.LoadedFromPrebuiltBytecode ? "Precompiled .heb bytecode" : "Compiled from ERB source")}");
        sb.AppendLine($"Load Time:          {LoadElapsedMs} ms");
        sb.AppendLine($"Functions:          {Host.Module.Functions.Length:N0}");
        sb.AppendLine($"Instructions:       {Host.Module.Instructions.Length:N0}");
        sb.AppendLine($"Static Variables:   {Host.Module.StaticVariables.Length:N0}");
        sb.AppendLine($"Defined Macros:     {Host.Module.DefinedMacros.Length:N0}");
        sb.AppendLine($"Logical Viewport:   {Engine.Viewport.LogicalWidth} x {Engine.Viewport.LogicalHeight} (Font {Engine.Options.FontName} {Engine.Options.FontSize}px / LineH {Engine.Options.LineHeight}px)");
        sb.AppendLine($"Custom VFS Fonts:   {Engine.Fonts.CustomFontCount} ({string.Join(", ", Engine.Fonts.CustomFamilyNames)})");
        sb.AppendLine($"Loaded Sprites:     {Engine.Sprites.Count:N0}");
        sb.AppendLine($"Audio Hardware:     {(AudioDriver?.IsHardwareAttached == true ? "Attached (WinMM 48kHz Stereo)" : "Silent / Headless")}");
        sb.AppendLine($"CALLSHARP Plugins:  {PluginManager?.LoadedAssemblyCount ?? 0} assembly(s)");
        sb.AppendLine($"Console Lines:      {Engine.ConsoleBuffer.LineCount} logical / {Engine.ConsoleBuffer.PhysicalLineCount} physical (MaxLog {Engine.Options.MaxLog})");
        return sb.ToString();
    }

    public static string FormatCompileDiagnostics(EmueraCompileException ex)
    {
        IReadOnlyList<Diagnostic> diagnostics = ex.Diagnostics;
        var ordered = diagnostics
            .OrderByDescending(static d => (int)d.Severity)
            .ThenBy(static d => d.FilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static d => d.Span.StartLine)
            .ThenBy(static d => d.Span.StartColumn)
            .ToList();

        int errorCount = diagnostics.Count(static d => d.Severity == DiagnosticSeverity.Error);
        int warningCount = diagnostics.Count(static d => d.Severity == DiagnosticSeverity.Warning);
        int fileCount = diagnostics
            .Select(static d => !string.IsNullOrEmpty(d.FilePath) ? d.FilePath : $"#{d.Span.FileId}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var sb = new StringBuilder();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Compilation failed: {errorCount} error(s), {warningCount} warning(s) across {fileCount} file(s)."));
        sb.AppendLine(new string('-', 72));
        foreach (Diagnostic d in ordered)
        {
            sb.AppendLine(d.ToString());
        }

        return sb.ToString();
    }

    public static string FormatRuntimeFault(Exception ex, EmueraEngine engine)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Runtime Fault: {ex.GetType().Name}");
        sb.AppendLine(ex.Message);

        IReadOnlyList<SourceLocation> trace = engine.GetSourceStackTrace();
        if (trace.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("ERB Call Stack:");
            foreach (SourceLocation loc in trace)
            {
                sb.AppendLine($"  at {loc.DisplayName}:{loc.LineNo}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("CLR Stack Trace:");
        sb.AppendLine(ex.ToString());
        return sb.ToString();
    }

    private static IVirtualFileSystem WrapWithOptionalOverlay(IVirtualFileSystem lowerVfs, string? overlayDirectory)
    {
        if (string.IsNullOrWhiteSpace(overlayDirectory) || !Directory.Exists(overlayDirectory))
        {
            return lowerVfs;
        }

        try
        {
            var upper = new PhysicalDirectoryVfs(overlayDirectory);
            return new OverlayVirtualFileSystem(upper, lowerVfs, ownsUpper: true, ownsLower: true);
        }
        catch
        {
            return lowerVfs;
        }
    }

    private static Icon? CreateIconFromSkBitmap(SKBitmap source, int size)
    {
        var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var resized = new SKBitmap(info);
        using (var canvas = new SKCanvas(resized))
        {
            canvas.Clear(SKColors.Transparent);
            using SKImage img = SKImage.FromBitmap(source);
            float scale = MathF.Min((float)size / Math.Max(1, source.Width), (float)size / Math.Max(1, source.Height));
            float drawW = source.Width * scale;
            float drawH = source.Height * scale;
            var dst = new SKRect((size - drawW) * 0.5f, (size - drawH) * 0.5f, (size + drawW) * 0.5f, (size + drawH) * 0.5f);
            canvas.DrawImage(img, dst, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int byteCount = size * size * 4;
            byte[] pixels = new byte[byteCount];
            Marshal.Copy(resized.GetPixels(), pixels, 0, byteCount);
            Marshal.Copy(pixels, 0, data.Scan0, byteCount);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            _ = DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int i = _ownedResources.Count - 1; i >= 0; i--)
        {
            try
            {
                _ownedResources[i].Dispose();
            }
            catch
            {
                // Ensure every resource gets a chance to clean up.
            }
        }

        _ownedResources.Clear();
    }
}
