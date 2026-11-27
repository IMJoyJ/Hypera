using System.Text;
using Hypera.Bytecode;
using Hypera.Configuration;
using Hypera.Rendering;
using Hypera.Runtime;
using Hypera.VirtualFs;
using SkiaSharp;

namespace Hypera.Cli;

/// <summary>
/// Interactive ANSI 24-bit TrueColor terminal host and headless Skia PNG frame dumper.
/// Supports colors, alignments, [Button] highlights, line/key/timed prompts, and <c>--headless-frames</c>.
/// </summary>
public sealed class AnsiConsoleHost : IDisposable
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly SkiaFrameCompositor _compositor = new();
    private readonly string? _headlessFramesDir;
    private readonly Queue<string> _scriptedInputs;
    private readonly bool _enableAnsi;
    private long _lastRenderedFrameSeq = -1;
    private int _renderedOperationCount;
    private int _dumpedFrameCount;
    private bool _disposed;

    public AnsiConsoleHost(
        TextReader? input = null,
        TextWriter? output = null,
        string? headlessFramesDir = null,
        IEnumerable<string>? scriptedInputs = null,
        bool enableAnsi = true)
    {
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
        _headlessFramesDir = headlessFramesDir;
        _scriptedInputs = scriptedInputs is not null ? new Queue<string>(scriptedInputs) : new Queue<string>();
        _enableAnsi = enableAnsi;

        if (!string.IsNullOrWhiteSpace(_headlessFramesDir))
        {
            Directory.CreateDirectory(_headlessFramesDir);
        }
    }

    public int DumpedFrameCount => _dumpedFrameCount;

    public int RunHost(HyperaGameHost gameHost)
    {
        ArgumentNullException.ThrowIfNull(gameHost);
        return RunEngine(gameHost.Engine);
    }

    public int RunModule(BytecodeModule module, IVirtualFileSystem vfs, RuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(vfs);

        using HyperaGameHost gameHost = HyperaGameHost.LoadFromVfs(
            vfs,
            ownsVfs: false,
            new HyperaGameLoadOptions { RuntimeOptionsOverride = options },
            preloadedModule: module);
        return RunEngine(gameHost.Engine);
    }

    public int RunEngine(EmueraEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        _renderedOperationCount = 0;
        _lastRenderedFrameSeq = -1;

        if (!engine.SystemFlow.IsActive && engine.CallStack.Count == 0)
        {
            FunctionDescriptor? entry =
                engine.Module.Functions.FirstOrDefault(f => f.Name.Equals("SYSTEM_TITLE", StringComparison.OrdinalIgnoreCase)) ??
                engine.Module.Functions.FirstOrDefault(f => f.Name.Equals("EVENTFIRST", StringComparison.OrdinalIgnoreCase)) ??
                engine.Module.Functions.FirstOrDefault();

            if (entry is not null)
            {
                engine.PushEntryFrame(entry);
            }
        }

        while (true)
        {
            EngineStepStatus status = engine.Step();
            DisplaySnapshot snapshot = engine.CaptureSnapshot();

            if (snapshot.FrameSequence != _lastRenderedFrameSeq)
            {
                _lastRenderedFrameSeq = snapshot.FrameSequence;
                RenderSnapshotIncrementalToAnsi(snapshot);
                DumpHeadlessFrameIfConfigured(engine, snapshot);
            }

            if (status == EngineStepStatus.WaitingInput && engine.PendingInputRequest is { } req)
            {
                InputResponse response = ReadInputResponse(req);
                engine.SubmitInput(response);
                continue;
            }

            if (status == EngineStepStatus.Faulted)
            {
                _output.WriteLine($"[hypera] Engine faulted: {engine.FaultException?.Message}");
                return 1;
            }

            if (status == EngineStepStatus.Completed || (!engine.SystemFlow.IsActive && engine.CallStack.Count == 0))
            {
                break;
            }
        }

        return 0;
    }

    public void RenderSnapshotToAnsi(DisplaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RenderOperationsToAnsi(snapshot, startIndex: 0);
    }

    private void RenderSnapshotIncrementalToAnsi(DisplaySnapshot snapshot)
    {
        if (snapshot.Operations.Count < _renderedOperationCount)
        {
            _renderedOperationCount = 0;
        }

        RenderOperationsToAnsi(snapshot, _renderedOperationCount);
        _renderedOperationCount = snapshot.Operations.Count;
    }

    private void RenderOperationsToAnsi(DisplaySnapshot snapshot, int startIndex)
    {
        var sb = new StringBuilder();

        for (int i = startIndex; i < snapshot.Operations.Count; i++)
        {
            DisplayOperation op = snapshot.Operations[i];
            switch (op.Kind)
            {
                case DisplayOperationKind.ClearAll:
                    if (_enableAnsi)
                    {
                        sb.Append("\x1b[2J\x1b[H");
                    }

                    break;

                case DisplayOperationKind.NewLine:
                    sb.AppendLine();
                    break;

                case DisplayOperationKind.DrawLineRule:
                    string rule = string.IsNullOrEmpty(op.Text) ? new string('-', 60) : op.Text;
                    AppendColored(sb, rule, op.ColorRgba, snapshot.BackgroundColorRgba, bold: false);
                    sb.AppendLine();
                    break;

                case DisplayOperationKind.PrintTextSpan:
                    if (!string.IsNullOrEmpty(op.Text))
                    {
                        string aligned = FormatAlignedText(op.Text, op.Bounds, snapshot.Viewport.LogicalWidth);
                        AppendColored(sb, aligned, op.ColorRgba, snapshot.BackgroundColorRgba, bold: false);
                    }

                    break;

                case DisplayOperationKind.PrintButtonSpan:
                    string label = string.IsNullOrEmpty(op.Text)
                        ? $"[{op.ButtonIntValue}]"
                        : (op.Text.StartsWith('[') && op.Text.EndsWith(']') ? op.Text : $"[{op.Text}]");
                    uint btnColor = op.ColorRgba == 0 ? 0xFFFF00FFu : op.ColorRgba;
                    AppendColored(sb, label, btnColor, snapshot.BackgroundColorRgba, bold: true);
                    break;
            }
        }

        if (sb.Length > 0)
        {
            _output.Write(sb.ToString());
            _output.Flush();
        }
    }

    public string? DumpHeadlessFrameIfConfigured(DisplaySnapshot snapshot) =>
        DumpHeadlessFrameIfConfigured(engine: null, snapshot);

    public string? DumpHeadlessFrameIfConfigured(EmueraEngine? engine, DisplaySnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(_headlessFramesDir))
        {
            return null;
        }

        int width = Math.Max(1, snapshot.Viewport.LogicalWidth);
        int height = Math.Max(1, snapshot.Viewport.LogicalHeight);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

        if (engine is not null)
        {
            ConsoleScene scene = engine.ConsoleBuffer.CaptureScene();
            if (scene.VisibleLines.Count > 0 ||
                scene.CbgLayers.Count > 0 ||
                scene.IslandLayers.Count > 0 ||
                scene.BackgroundImages.Count > 0)
            {
                using var engineCompositor = new SkiaFrameCompositor(engine.Fonts, engine.Glyphs);
                engineCompositor.RenderScene(surface.Canvas, width, height, scene, engine.Graphics, engine.Sprites);
            }
            else
            {
                _compositor.RenderSnapshot(surface.Canvas, width, height, snapshot);
            }
        }
        else
        {
            _compositor.RenderSnapshot(surface.Canvas, width, height, snapshot);
        }

        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        _dumpedFrameCount++;
        string filePath = Path.Combine(_headlessFramesDir, $"frame_{_dumpedFrameCount:D6}.png");
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        png.SaveTo(fs);
        return filePath;
    }

    private InputResponse ReadInputResponse(InputRequest req)
    {
        if (_scriptedInputs.Count > 0)
        {
            string scripted = _scriptedInputs.Dequeue();
            return BuildResponseFromLine(req, scripted);
        }

        if (req.TimeoutMilliseconds.HasValue)
        {
            if (req.TimeoutMilliseconds.Value <= 1L &&
                !req.DisplayTime &&
                req.Kind is InputRequestKind.IntValue or InputRequestKind.StringValue or InputRequestKind.OneInt or InputRequestKind.OneString)
            {
                return new InputResponse(req.GenerationId, IsTimeout: true);
            }

            return ReadTimedInput(req);
        }

        switch (req.Kind)
        {
            case InputRequestKind.TimedWait:
                return ReadTimedInput(req);

            case InputRequestKind.AnyKey:
            case InputRequestKind.OneInt:
            case InputRequestKind.OneString:
                if (ReferenceEquals(_input, Console.In) && !Console.IsInputRedirected)
                {
                    ConsoleKeyInfo keyInfo = Console.ReadKey(intercept: true);
                    string s = keyInfo.KeyChar == '\0' ? string.Empty : keyInfo.KeyChar.ToString();
                    return BuildResponseFromLine(req, s);
                }

                string? line = _input.ReadLine();
                return line is null
                    ? new InputResponse(req.GenerationId, IsTimeout: true)
                    : BuildResponseFromLine(req, line);

            default:
                while (true)
                {
                    string? raw = _input.ReadLine();
                    if (raw is null)
                    {
                        return new InputResponse(req.GenerationId, IsTimeout: true);
                    }

                    if (req.Kind == InputRequestKind.IntValue)
                    {
                        if (long.TryParse(raw.Trim(), out long val))
                        {
                            return new InputResponse(req.GenerationId, IntValue: val);
                        }

                        _output.WriteLine("[hypera] Please enter a valid integer.");
                        continue;
                    }

                    return new InputResponse(req.GenerationId, StringValue: raw);
                }
        }
    }

    private InputResponse ReadTimedInput(InputRequest req)
    {
        int timeoutMs = (int)Math.Clamp(req.TimeoutMilliseconds ?? 1000, 1, int.MaxValue);
        if (ReferenceEquals(_input, Console.In) && Console.IsInputRedirected)
        {
            string? redirected = _input.ReadLine();
            return redirected is null
                ? new InputResponse(req.GenerationId, IsTimeout: true)
                : BuildResponseFromLine(req, redirected);
        }

        Task<string?> readTask = Task.Run(() => _input.ReadLine());
        if (readTask.Wait(timeoutMs))
        {
            string? val = readTask.Result;
            return val is null
                ? new InputResponse(req.GenerationId, IsTimeout: true)
                : BuildResponseFromLine(req, val);
        }

        return new InputResponse(req.GenerationId, IsTimeout: true);
    }

    private static InputResponse BuildResponseFromLine(InputRequest req, string text)
    {
        if (req.Kind is InputRequestKind.OneInt or InputRequestKind.IntValue)
        {
            if (req.Kind == InputRequestKind.OneInt && text.Length > 1)
            {
                text = text[..1];
            }

            long.TryParse(text.Trim(), out long parsed);
            return new InputResponse(req.GenerationId, IntValue: parsed);
        }

        if (req.Kind == InputRequestKind.OneString && text.Length > 1)
        {
            text = text[..1];
        }

        return new InputResponse(req.GenerationId, StringValue: text);
    }

    private void AppendColored(StringBuilder sb, string text, uint fgRgba, uint bgRgba, bool bold)
    {
        if (!_enableAnsi)
        {
            sb.Append(text);
            return;
        }

        byte fr = (byte)((fgRgba >> 24) & 0xFF);
        byte fg = (byte)((fgRgba >> 16) & 0xFF);
        byte fb = (byte)((fgRgba >> 8) & 0xFF);
        byte br = (byte)((bgRgba >> 24) & 0xFF);
        byte bg = (byte)((bgRgba >> 16) & 0xFF);
        byte bb = (byte)((bgRgba >> 8) & 0xFF);

        sb.Append("\x1b[");
        if (bold)
        {
            sb.Append("1;4;");
        }

        sb.Append($"38;2;{fr};{fg};{fb};48;2;{br};{bg};{bb}m");
        sb.Append(text);
        sb.Append("\x1b[0m");
    }

    private static string FormatAlignedText(string text, LogicalRect bounds, int logicalWidth)
    {
        if (bounds.Width <= 0 || bounds.X <= 0)
        {
            return text;
        }

        int approxCols = Math.Max(1, logicalWidth / 10);
        int targetCol = (int)Math.Round((bounds.X / Math.Max(1, logicalWidth)) * approxCols);
        return targetCol > 0 ? new string(' ', Math.Min(targetCol, 80)) + text : text;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _compositor.Dispose();
    }
}
