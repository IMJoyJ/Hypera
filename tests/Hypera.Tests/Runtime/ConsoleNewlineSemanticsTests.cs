using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Rendering;
using Hypera.Runtime;
using Hypera.Tests.Compiler;
using Xunit;

namespace Hypera.Tests.Runtime;

public sealed class ConsoleNewlineSemanticsTests
{
    private static EmueraEngine CompileAndCreateEngine(
        string erbSource,
        CompilerOptions? compilerOptions = null,
        RuntimeOptions? runtimeOptions = null)
    {
        var vfs = new CompilerMemoryVfs().AddFile("ERB/MAIN.ERB", erbSource);
        CompilerOptions opts = compilerOptions ?? CompilerOptions.Default;
        var compiler = new ErbProjectCompiler(opts);
        CompilationResult res = compiler.CompileProject(vfs, throwOnError: true);
        var engine = new EmueraEngine(res.Module, vfs, runtimeOptions ?? RuntimeOptions.Default)
        {
            DynamicEvaluator = new RuntimeDynamicCompiler(res.Module),
        };
        engine.PushEntryFrame(res.Module.Functions.First(f => f.Name.Equals("MAIN", StringComparison.OrdinalIgnoreCase)));
        return engine;
    }

    private static void StepUntilNotRunning(EmueraEngine engine)
    {
        for (int i = 0; i < 32 && engine.Status is EngineStepStatus.Running or EngineStepStatus.Yielded; i++)
        {
            engine.Step(500_000);
        }
    }

    [Fact]
    public void DuelEndButton_BInput_ThenCustomDrawLine_CommitsPendingLineAndDrawsDividerOnNextLine()
    {
        // Exact reproducer of eraOCG2 DUEL.ERB L376-403 & L539-562:
        // PRINTBUTTON "　[デュエル終了]", 0 -> BINPUT -> CUSTOMDRAWLINE —
        const string erb = """
@MAIN
CUSTOMDRAWLINE —
PRINTBUTTON "　[デュエル終了]", 0
BINPUT
FLAG:0 = RESULT
FLAG:1 = LINEISEMPTY()
FLAG:2 = LINECOUNT
CUSTOMDRAWLINE —
PRINTL 勝利エンディング
CUSTOMDRAWLINE —
RETURN
""";

        var opts = new RuntimeOptions { WindowX = 1230, WindowY = 950, FontSize = 16, LineHeight = 16 };
        using EmueraEngine engine = CompileAndCreateEngine(erb, runtimeOptions: opts);
        StepUntilNotRunning(engine);

        // At BINPUT suspension, pending "[デュエル終了]" line must already be flushed as its own committed line!
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.False(engine.ConsoleBuffer.HasPendingLine);
        Assert.Equal(2, engine.ConsoleBuffer.LineCount);
        Assert.Equal(2, engine.ConsoleBuffer.PhysicalLineCount);
        Assert.Equal("　[デュエル終了]", engine.ConsoleBuffer.PhysicalLines[1].PlainText);

        // Submit button 0 and finish
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 0));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.Completed, engine.Status);

        Assert.Equal(0L, engine.FastVariables!.GetInt("FLAG", 0));
        Assert.Equal(1L, engine.FastVariables.GetInt("FLAG", 1)); // LINEISEMPTY() == 1 after BINPUT
        Assert.Equal(2L, engine.FastVariables.GetInt("FLAG", 2)); // divider + [デュエル終了]

        // 5 logical & 5 physical lines total (zero wrapping stub on CUSTOMDRAWLINE —!)
        Assert.Equal(5, engine.ConsoleBuffer.LineCount);
        Assert.Equal(5, engine.ConsoleBuffer.PhysicalLineCount);
        Assert.Equal("　[デュエル終了]", engine.ConsoleBuffer.PhysicalLines[1].PlainText);
        Assert.True(engine.ConsoleBuffer.PhysicalLines[2].PlainText.All(c => c == '—'));
        Assert.True(engine.ConsoleBuffer.PhysicalLines[2].NoWrap);
        Assert.Equal("勝利エンディング", engine.ConsoleBuffer.PhysicalLines[3].PlainText);
        Assert.True(engine.ConsoleBuffer.PhysicalLines[4].PlainText.All(c => c == '—'));
    }

    [Fact]
    public void WaitAndInputFamily_FlushesPendingLineAndPreservesClearLineCount()
    {
        // Covers COMF413 (PRINT + WAIT + CLEARLINE 1), SHOP_ITEM (PRINT + INPUT),
        // TWAIT, and HTML_PRINT ..., 1 + BINPUT.
        const string erb = """
@MAIN
PRINTL KeepAbove
PRINT ・
WAIT
CLEARLINE 1
PRINT ・・
WAIT
PRINTL AfterWait
PRINT [1] Choice
INPUT
PRINTL AfterInput
PRINT BeforeTWait
TWAIT 10, 0
PRINTL AfterTWait
HTML_PRINT "<button value='7'>HtmlBtn</button>", 1
BINPUT
PRINTL AfterHtmlBInput
RETURN
""";

        using EmueraEngine engine = CompileAndCreateEngine(erb);

        // 1. First WAIT after PRINT ・ -> committed as line 1 (KeepAbove is line 0)
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.False(engine.ConsoleBuffer.HasPendingLine);
        Assert.Equal(["KeepAbove", "・"], engine.ConsoleBuffer.GetAllPlainTextLines());

        // 2. CLEARLINE 1 removes ONLY "・", keeping "KeepAbove", then PRINT ・・ + WAIT commits "・・"
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.Equal(["KeepAbove", "・・"], engine.ConsoleBuffer.GetAllPlainTextLines());

        // 3. Resume -> PRINTL AfterWait, PRINT [1] Choice + INPUT
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.False(engine.ConsoleBuffer.HasPendingLine);

        // 4. Submit 1 -> PRINTL AfterInput, PRINT BeforeTWait + TWAIT
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 1));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.False(engine.ConsoleBuffer.HasPendingLine);

        // 5. Timeout TWAIT -> PRINTL AfterTWait, HTML_PRINT ..., 1 + BINPUT
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IsTimeout: true));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.False(engine.ConsoleBuffer.HasPendingLine);

        // 6. Submit 7 -> PRINTL AfterHtmlBInput -> Completed
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 7));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.Completed, engine.Status);

        string[] expected =
        [
            "KeepAbove",
            "・・",
            "AfterWait",
            "[1] Choice",
            "AfterInput",
            "BeforeTWait",
            "AfterTWait",
            "HtmlBtn",
            "AfterHtmlBInput",
        ];
        Assert.Equal(expected, engine.ConsoleBuffer.GetAllPlainTextLines());
    }

    [Fact]
    public void PrintN_NoNewLineWait_KeepsPendingLineOpenAcrossConsecutiveWaits()
    {
        // Exact reproducer of eraOCG2 0381_おまけ詰めデュエル部分.ERB L588-591
        const string erb = """
@MAIN
PRINTFORMN 「んん…………
PRINTFORMN ちゅ……
PRINTFORMN ぐぱっ…………
PRINTFORMW ちゅぶっ…………」
PRINTL NextLine
RETURN
""";

        using EmueraEngine engine = CompileAndCreateEngine(erb);

        // Wait #1: "「んん…………" stays pending (not flushed to a committed line)
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.True(engine.ConsoleBuffer.HasPendingLine);
        Assert.Equal(0, engine.ConsoleBuffer.LineCount);
        Assert.Equal(["「んん…………"], engine.ConsoleBuffer.GetAllPlainTextLines());

        // Wait #2: appends onto the SAME pending line
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.True(engine.ConsoleBuffer.HasPendingLine);
        Assert.Equal(0, engine.ConsoleBuffer.LineCount);
        Assert.Equal(["「んん…………ちゅ……"], engine.ConsoleBuffer.GetAllPlainTextLines());

        // Wait #3: still same pending line
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.True(engine.ConsoleBuffer.HasPendingLine);
        Assert.Equal(["「んん…………ちゅ……ぐぱっ…………"], engine.ConsoleBuffer.GetAllPlainTextLines());

        // Wait #4 (PRINTFORMW): appends final fragment AND commits the single combined line!
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.False(engine.ConsoleBuffer.HasPendingLine);
        Assert.Equal(1, engine.ConsoleBuffer.LineCount);
        Assert.Equal(["「んん…………ちゅ……ぐぱっ…………ちゅぶっ…………」"], engine.ConsoleBuffer.GetAllPlainTextLines());

        // Complete
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.Completed, engine.Status);
        Assert.Equal(
            ["「んん…………ちゅ……ぐぱっ…………ちゅぶっ…………」", "NextLine"],
            engine.ConsoleBuffer.GetAllPlainTextLines());
    }

    [Fact]
    public void PrintSingle_AndDrawLine_FlushPendingLineAndDisableWrapping()
    {
        const string erb = """
@MAIN
PRINT Prefix
PRINTSINGLE SingleNoWrapLine
PRINTSINGLE 
FONTBOLD
PRINT BeforeRule
DRAWLINE
FONTREGULAR
PRINTL AfterRule
RETURN
""";

        using EmueraEngine engine = CompileAndCreateEngine(erb);
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.Completed, engine.Status);

        Assert.Equal(5, engine.ConsoleBuffer.LineCount);
        Assert.Equal(5, engine.ConsoleBuffer.PhysicalLineCount);
        Assert.Equal("Prefix", engine.ConsoleBuffer.PhysicalLines[0].PlainText);
        Assert.Equal("SingleNoWrapLine", engine.ConsoleBuffer.PhysicalLines[1].PlainText);
        Assert.True(engine.ConsoleBuffer.PhysicalLines[1].NoWrap);
        Assert.Equal("BeforeRule", engine.ConsoleBuffer.PhysicalLines[2].PlainText);
        Assert.True(engine.ConsoleBuffer.PhysicalLines[3].NoWrap);
        Assert.True(engine.ConsoleBuffer.PhysicalLines[3].PlainText.All(c => c == '-'));
        Assert.Equal(FontStyleFlags.Regular, engine.ConsoleBuffer.PhysicalLines[3].Parts[0].FontStyle);
        Assert.Equal("AfterRule", engine.ConsoleBuffer.PhysicalLines[4].PlainText);
    }

    [Fact]
    public void ReuseLastLine_TemporaryLineSemantics_ReplacesOnlyTemporaryLinesAndPreservesPermanentLines()
    {
        const string erb = """
@MAIN
PRINTL Permanent Header
REUSELASTLINE Progress 10\%
WAIT
REUSELASTLINE Progress 50\%
WAIT
PRINTL Final Result
PRINT UnflushedPrefix
REUSELASTLINE TempAfterPrefix
PRINTL DoneAfterTemp
FLAG:0 = LINECOUNT
RETURN
""";

        using EmueraEngine engine = CompileAndCreateEngine(erb);

        // After first REUSELASTLINE + WAIT: "Permanent Header" preserved, "Progress 10%" is temporary!
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.Equal(["Permanent Header", "Progress 10%"], engine.ConsoleBuffer.GetAllPlainTextLines());
        Assert.True(engine.ConsoleBuffer.LastLineIsTemporary);

        // After second REUSELASTLINE + WAIT: "Progress 10%" replaced by "Progress 50%", header intact!
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.Equal(["Permanent Header", "Progress 50%"], engine.ConsoleBuffer.GetAllPlainTextLines());
        Assert.True(engine.ConsoleBuffer.LastLineIsTemporary);

        // Complete: "Final Result" replaces "Progress 50%"; "UnflushedPrefix" is flushed before "TempAfterPrefix";
        // "DoneAfterTemp" replaces "TempAfterPrefix" while keeping "UnflushedPrefix"!
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.Completed, engine.Status);
        Assert.False(engine.ConsoleBuffer.LastLineIsTemporary);
        Assert.Equal(
            ["Permanent Header", "Final Result", "UnflushedPrefix", "DoneAfterTemp"],
            engine.ConsoleBuffer.GetAllPlainTextLines());
        Assert.Equal(4L, engine.FastVariables!.GetInt("FLAG", 0));
    }

    [Fact]
    public void ClearLine_PreservesUnflushedPendingLine_AndAwaitKeepsSameLineIntact()
    {
        const string erb = """
@MAIN
PRINTL Keep
PRINTL Drop
PRINT Partial
CLEARLINE 1
PRINTL Tail
PRINT A
AWAIT 1
PRINT B
PRINTL
RETURN
""";

        using EmueraEngine engine = CompileAndCreateEngine(erb);

        // First stop is AWAIT 1 (Yielded): "A" must remain pending, NOT flushed!
        engine.Step(500_000);
        Assert.Equal(EngineStepStatus.Yielded, engine.Status);
        Assert.True(engine.ConsoleBuffer.HasPendingLine);

        // Continue to completion: "A" + "B" share one line, "Drop" was cleared while "Partial" survived into "PartialTail"!
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.Completed, engine.Status);
        Assert.Equal(["Keep", "PartialTail", "AB"], engine.ConsoleBuffer.GetAllPlainTextLines());
    }
}
