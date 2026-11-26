using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;
using Hypera.Diagnostics;
using Hypera.Rendering;
using Hypera.Runtime;
using SkiaSharp;

namespace Hypera.Standalone;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StandaloneLaunchOptions options;
        try
        {
            options = StandaloneLaunchResolver.Resolve(Environment.ProcessPath, AppContext.BaseDirectory, args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Hypera.Standalone] Launch error: {ex.Message}");
            if (!args.Any(static a => a.Equals("--headless-smoke", StringComparison.OrdinalIgnoreCase)))
            {
                ApplicationConfiguration.Initialize();
                MessageBox.Show(ex.Message, "Hypera Standalone", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return 2;
        }

        if (!string.IsNullOrWhiteSpace(options.HeadlessSmokePngPath))
        {
            return RunHeadlessSmoke(options);
        }

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var mainForm = new MainForm(options);
        Application.Run(mainForm);
        return 0;
    }

    /// <summary>
    /// Deterministic non-GUI verification entrypoint: mounts the resolved target through the exact same
    /// <see cref="StandaloneGameSession"/> and <see cref="SkiaFrameCompositor"/> pipeline used by the desktop window,
    /// drives startup &amp; optional scripted inputs, renders the resulting <see cref="ConsoleScene"/> to PNG,
    /// and prints machine-readable telemetry to stdout.
    /// </summary>
    private static int RunHeadlessSmoke(StandaloneLaunchOptions options)
    {
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
            // Ignore when stdout is not attached.
        }

        if (options.Target.Kind == StandaloneTargetKind.None)
        {
            Console.Error.WriteLine("[Hypera.Standalone] No target found for --headless-smoke.");
            return 2;
        }

        var totalSw = Stopwatch.StartNew();
        try
        {
            using StandaloneGameSession session = StandaloneGameSession.Load(
                options.Target,
                isDebugMode: options.IsDebugMode,
                enableHardwareAudio: false);

            EmueraEngine engine = session.Engine;
            var inputQueue = new Queue<string>(options.ScriptedInputs);
            int steps = 0;
            int timedWaitsAdvanced = 0;

            while (steps < options.MaxHeadlessSteps)
            {
                EngineStepStatus status = engine.Step();
                steps++;

                if (status == EngineStepStatus.Faulted)
                {
                    Console.Error.WriteLine(StandaloneGameSession.FormatRuntimeFault(
                        engine.FaultException ?? new InvalidOperationException("Unknown fault."),
                        engine));
                    return 1;
                }

                if (status == EngineStepStatus.Completed)
                {
                    break;
                }

                if (status == EngineStepStatus.WaitingInput && engine.PendingInputRequest is { } req)
                {
                    if (req.Kind == InputRequestKind.TimedWait && options.AutoAdvanceTimedWaits && timedWaitsAdvanced < 64)
                    {
                        timedWaitsAdvanced++;
                        engine.SubmitInput(new InputResponse(req.GenerationId, IsTimeout: true));
                        continue;
                    }

                    if (inputQueue.Count > 0)
                    {
                        string next = inputQueue.Dequeue();
                        if (req.Kind is InputRequestKind.IntValue or InputRequestKind.OneInt &&
                            long.TryParse(next.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long intVal))
                        {
                            engine.SubmitInput(new InputResponse(req.GenerationId, IntValue: intVal));
                        }
                        else
                        {
                            engine.SubmitInput(new InputResponse(req.GenerationId, StringValue: next));
                        }

                        continue;
                    }

                    break;
                }
            }

            // Render actual ConsoleScene frame via the exact Skia compositor
            ConsoleScene scene = engine.ConsoleBuffer.CaptureScene(elapsedAnimationMs: totalSw.ElapsedMilliseconds);
            int width = Math.Max(1, scene.Viewport.LogicalWidth);
            int height = Math.Max(1, scene.Viewport.LogicalHeight);

            string pngPath = options.HeadlessSmokePngPath!;
            string? pngDir = Path.GetDirectoryName(pngPath);
            if (!string.IsNullOrEmpty(pngDir))
            {
                Directory.CreateDirectory(pngDir);
            }

            var imgInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(imgInfo);
            using var canvas = new SKCanvas(bitmap);
            using var compositor = new SkiaFrameCompositor(engine.Fonts, engine.Glyphs);
            compositor.RenderScene(canvas, width, height, scene, engine.Graphics, engine.Sprites);

            byte[] pngBytes = ImageCodecDecoder.EncodeToPng(bitmap);
            File.WriteAllBytes(pngPath, pngBytes);

            int nonBackgroundPixels = CountNonBackgroundPixels(bitmap, scene.BackgroundColorRgba);
            bool hasIcon;
            using (var icon = session.TryLoadWindowIcon())
            {
                hasIcon = icon is not null;
            }

            totalSw.Stop();
            IReadOnlyList<string> allLines = engine.ConsoleBuffer.GetAllPlainTextLines();
            Console.WriteLine($"SMOKE_OK TargetKind={options.Target.Kind} Prebuilt={session.Host.LoadedFromPrebuiltBytecode} LoadMs={session.LoadElapsedMs} TotalMs={totalSw.ElapsedMilliseconds}");
            Console.WriteLine($"TITLE={session.GetEffectiveWindowTitle()}");
            Console.WriteLine($"STATUS={engine.Status} PendingKind={engine.PendingInputRequest?.Kind.ToString() ?? "None"} Steps={steps} TimedWaits={timedWaitsAdvanced}");
            Console.WriteLine($"MODULE Functions={session.Host.Module.Functions.Length} Instructions={session.Host.Module.Instructions.Length} CustomFonts={engine.Fonts.CustomFontCount} Sprites={engine.Sprites.Count} IconLoaded={hasIcon}");
            Console.WriteLine($"FRAME Path={pngPath} Size={width}x{height} PngBytes={pngBytes.Length} NonBgPixels={nonBackgroundPixels}");
            Console.WriteLine($"LINES Total={allLines.Count}");
            foreach (string line in allLines.TakeLast(15))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    Console.WriteLine($"  | {line}");
                }
            }

            return 0;
        }
        catch (EmueraCompileException compileEx)
        {
            Console.Error.WriteLine(StandaloneGameSession.FormatCompileDiagnostics(compileEx));
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Hypera.Standalone] Unhandled smoke error: {ex}");
            return 1;
        }
    }

    private static int CountNonBackgroundPixels(SKBitmap bitmap, uint bgRgba)
    {
        SKColor bg = RenderingColor.FromRgba(bgRgba);
        int count = 0;
        int stepY = Math.Max(1, bitmap.Height / 200);
        int stepX = Math.Max(1, bitmap.Width / 200);
        for (int y = 0; y < bitmap.Height; y += stepY)
        {
            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                SKColor c = bitmap.GetPixel(x, y);
                if (c.Red != bg.Red || c.Green != bg.Green || c.Blue != bg.Blue)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
