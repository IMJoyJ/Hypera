using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Rendering;
using Hypera.Runtime;
using Hypera.Tests.Compiler;
using SkiaSharp;
using Xunit;

namespace Hypera.Tests.Runtime;

public sealed class PerformanceOptimizationsRegressionTests
{
    private static EmueraEngine BuildAndRunScript(string erbSource, int maxSteps = 200)
    {
        var vfs = new CompilerMemoryVfs().AddFile("ERB/MAIN.ERB", erbSource);
        var compiler = new ErbProjectCompiler(CompilerOptions.Default);
        CompilationResult compiled = compiler.CompileProject(vfs, throwOnError: true);
        var engine = new EmueraEngine(compiled.Module, vfs, RuntimeOptions.Default);
        engine.PushEntryFrame(compiled.Module.Functions.First(f => f.Name.Equals("SYSTEM_TITLE", StringComparison.OrdinalIgnoreCase)));
        for (int i = 0; i < maxSteps; i++)
        {
            EngineStepStatus st = engine.Step(100_000);
            if (st is EngineStepStatus.WaitingInput or EngineStepStatus.Completed or EngineStepStatus.Faulted)
            {
                break;
            }
        }

        return engine;
    }

    [Fact]
    public void PooledCallFrames_ResetDynamicLocals_PreserveRecursion_AndClearByRefSlotsSafely()
    {
        string erb = """
@SYSTEM_TITLE
#DIM DYNAMIC ACC
#DIM DYNAMIC TOTAL
#DIM DYNAMIC ARR, 4
ARR:0 = 10
ARR:1 = 20
FOR LOCAL, 0, 5
    TOTAL += DYN_SUM(LOCAL + 1)
NEXT
CALL MUTATE_REF(ARR)
ACC = FACT(5)
PRINTFORML TOTAL={TOTAL} ARR0={ARR:0} ARR1={ARR:1} FACT5={ACC}
QUIT

@DYN_SUM(N)
#FUNCTION
#DIM DYNAMIC N
#DIM DYNAMIC BUF, 16
; If pooled dynamic array wasn't zeroed between calls, BUF:0 would leak from prior call!
BUF:0 += N
BUF:15 += N * 2
RETURNF BUF:0 + BUF:15

@MUTATE_REF(R)
#DIM REF R, 0
R:0 += 5
R:1 += 7
RETURN

@FACT(K)
#FUNCTION
#DIM DYNAMIC K
#DIM DYNAMIC TEMP
SIF K <= 1
    RETURNF 1
TEMP = K * FACT(K - 1)
RETURNF TEMP
""";
        using EmueraEngine engine = BuildAndRunScript(erb);
        Assert.Equal(EngineStepStatus.Completed, engine.Status);
        string text = string.Join("\n", engine.ConsoleBuffer.GetAllPlainTextLines());
        // DYN_SUM(1..5) = 3*(1+2+3+4+5) = 45; ARR0=15, ARR1=27; FACT(5)=120
        Assert.Contains("TOTAL=45 ARR0=15 ARR1=27 FACT5=120", text);
    }

    [Fact]
    public void PerSlotCowAndNoOpGuard_PreserveExactUndoRollbackForCharaVarsAndRefs()
    {
        string erb = """
@SYSTEM_TITLE
ADDVOIDCHARA
ADDVOIDCHARA
CFLAG:0:5 = 100
CFLAG:1:5 = 200
TALENT:1:3 = 7
BASE:1:0 = 1500
INPUT
; No-op write (should not corrupt snapshot) + real write to CFLAG only (TALENT/BASE stay shared!)
CFLAG:0:5 = 100
CFLAG:1:5 = 999
BASE:1:0 = 500
CALL BUMP_BASE_REF(BASE:1:0)
PRINTFORML AFTER={CFLAG:0:5},{CFLAG:1:5},{TALENT:1:3},{BASE:1:0}
INPUT
QUIT

@BUMP_BASE_REF(B)
#DIM REF B, 0
B:0 += 11
RETURN
""";
        using EmueraEngine engine = BuildAndRunScript(erb);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.Equal(1, engine.MaxUndoSteps);

        // First INPUT captures CoW checkpoint at (100, 200, 7, 1500)
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 1));
        while (engine.Step(100_000) == EngineStepStatus.Running) { }
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.Contains("AFTER=100,999,7,511", string.Join("\n", engine.ConsoleBuffer.GetAllPlainTextLines()));

        // Undo MUST restore exact pre-INPUT state (CFLAG:1:5=200, BASE:1:0=1500, TALENT:1:3=7)!
        Assert.True(engine.TryUndo());
        Assert.Equal(100L, engine.FastCharacters!.GetInt1D(0, engine.FastCharacters.CFlagSlotId, 5));
        Assert.Equal(200L, engine.FastCharacters.GetInt1D(1, engine.FastCharacters.CFlagSlotId, 5));
        Assert.Equal(7L, engine.FastCharacters.GetInt1D(1, engine.FastCharacters.TalentSlotId, 3));
        Assert.Equal(1500L, engine.FastCharacters.GetInt1D(1, engine.FastCharacters.BaseSlotId, 0));

        // Mutating again after Undo MUST NOT corrupt anything and must update live state cleanly
        engine.FastCharacters.SetInt1D(1, engine.FastCharacters.CFlagSlotId, 5, 321);
        Assert.Equal(321L, engine.FastCharacters.GetInt1D(1, engine.FastCharacters.CFlagSlotId, 5));
        Assert.Equal(7L, engine.FastCharacters.GetInt1D(1, engine.FastCharacters.TalentSlotId, 3));
    }

    [Fact]
    public void VisualMutationVersionAndZeroCopyCompositor_TrackChangesAndRenderAccurately()
    {
        using var fonts = new FontRegistry();
        using var measurer = new GlyphMeasurer(fonts);
        using var gm = new GraphicsManager(fonts, measurer);
        using var sprites = new SpriteRegistry();
        using var compositor = new SkiaFrameCompositor(fonts, measurer);

        var vp = new LogicalViewport(120, 60, 16, 16);
        var console = new VirtualConsoleBuffer(vp, measurer, sprites, graphics: gm);

        long v0 = console.VisualMutationVersion;
        long g0 = gm.GlobalMutationVersion;
        long s0 = sprites.GlobalMutationVersion;

        gm.Create(1, 40, 20);
        gm.Clear(1, 0xFF33AA55u);
        sprites.CreateSprite("SPR_TEST", 1, gm);
        Assert.True(gm.GlobalMutationVersion > g0);
        Assert.True(sprites.GlobalMutationVersion > s0);

        console.HtmlPrint("<img src='SPR_TEST' width='80px' height='40px'/>");
        long v1 = console.VisualMutationVersion;
        Assert.True(v1 > v0);

        // No-op background color assignment does NOT bump VisualMutationVersion
        console.CurrentBackColorRgba = console.CurrentBackColorRgba;
        Assert.Equal(v1, console.VisualMutationVersion);

        ConsoleScene scene = console.CaptureScene();
        using var bmp = new SKBitmap(new SKImageInfo(120, 60, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            compositor.RenderScene(canvas, 120, 60, scene, gm, sprites);
        }

        // Zero-copy wrapped draw rendered the exact sprite color (0x33, 0xAA, 0x55) at line top (y = 60 - 16 = 44 -> pixel y=50)
        Assert.Equal(new SKColor(0x33, 0xAA, 0x55, 0xFF), bmp.GetPixel(30, 50));
    }
}
