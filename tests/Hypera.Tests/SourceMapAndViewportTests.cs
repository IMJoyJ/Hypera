using Hypera.Bytecode;
using Hypera.Diagnostics;
using Hypera.Rendering;
using Hypera.Runtime;
using Hypera.VirtualFs;
using Xunit;

namespace Hypera.Tests;

public sealed class SourceMapAndViewportTests
{
    [Fact]
    public void SourceMap_BinarySearchResolvesExactAndInteriorProgramCounters()
    {
        var builder = new SourceMapBuilder();
        int sysFile = builder.GetOrAddFile("ERB/SYSTEM.ERB");
        int shopFile = builder.GetOrAddFile("ERB/SHOP.ERB");

        builder.AddMapping(pcOffset: 0, fileId: sysFile, lineNo: 10);
        builder.AddMapping(pcOffset: 1, fileId: sysFile, lineNo: 10); // deduplicated run
        builder.AddMapping(pcOffset: 4, fileId: sysFile, lineNo: 15);
        builder.AddMapping(pcOffset: 12, fileId: shopFile, lineNo: 42);

        SourceMap map = builder.Build();
        Assert.Equal(2, map.Files.Count);
        Assert.Equal(3, map.Runs.Count);

        Assert.False(map.TryLookup(-1, out _));

        Assert.True(map.TryLookup(0, out SourceLocation loc0));
        Assert.Equal("ERB/SYSTEM.ERB", loc0.VirtualPath);
        Assert.Equal(10, loc0.LineNo);

        Assert.True(map.TryLookup(3, out SourceLocation loc3));
        Assert.Equal(10, loc3.LineNo);

        Assert.True(map.TryLookup(7, out SourceLocation loc7));
        Assert.Equal("SYSTEM.ERB", loc7.DisplayName);
        Assert.Equal(15, loc7.LineNo);

        Assert.True(map.TryLookup(99, out SourceLocation loc99));
        Assert.Equal("ERB/SHOP.ERB", loc99.VirtualPath);
        Assert.Equal(42, loc99.LineNo);
    }

    [Fact]
    public void ViewportTransform_ComputesLetterboxAndMapsCoordinatesAccurately()
    {
        // 800x600 (4:3) centered inside 1920x1080 (16:9): scale = 1080/600 = 1.8, width = 1440, pillarbox X offset = 240
        var transform = new ViewportTransform(logicalWidth: 800, logicalHeight: 600, screenWidth: 1920, screenHeight: 1080);

        Assert.Equal(1.8f, transform.Letterbox.Scale, precision: 4);
        Assert.Equal(240f, transform.Letterbox.OffsetX, precision: 3);
        Assert.Equal(0f, transform.Letterbox.OffsetY, precision: 3);
        Assert.Equal(1440f, transform.Letterbox.Width, precision: 3);
        Assert.Equal(1080f, transform.Letterbox.Height, precision: 3);

        // Left pillarbox bar (x = 100) is outside logical viewport
        LogicalPoint outsideLeft = transform.ScreenToLogical(100f, 540f, out bool insideLeft);
        Assert.False(insideLeft);
        Assert.True(outsideLeft.X < 0f);

        // Exact center of 1920x1080 maps to (400, 300) in logical 800x600
        LogicalPoint center = transform.ScreenToLogical(960f, 540f, out bool insideCenter);
        Assert.True(insideCenter);
        Assert.Equal(400f, center.X, precision: 3);
        Assert.Equal(300f, center.Y, precision: 3);

        // Round-trip Logical -> Screen -> Logical
        LogicalPoint screenPt = transform.LogicalToScreen(125f, 450f);
        LogicalPoint roundTrip = transform.ScreenToLogical(screenPt.X, screenPt.Y, out bool insideRt);
        Assert.True(insideRt);
        Assert.Equal(125f, roundTrip.X, precision: 3);
        Assert.Equal(450f, roundTrip.Y, precision: 3);
    }

    [Fact]
    public void Engine_StacklessWaitInputSuspendsAndResumesCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hypera_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var vfs = new PhysicalDirectoryVfs(tempDir);
            var mapBuilder = new SourceMapBuilder();
            int fid = mapBuilder.GetOrAddFile("ERB/TITLE.ERB");
            mapBuilder.AddMapping(0, fid, 1);
            mapBuilder.AddMapping(1, fid, 2);

            var fn = new FunctionDescriptor(
                FunctionId: 0,
                Name: "SYSTEM_TITLE",
                EntryPc: 0,
                InstructionCount: 2,
                IntRegisterCount: 2,
                StrRegisterCount: 1,
                IsMethodFunction: false,
                MethodReturnKind: null,
                EventFlags: EventDirectiveFlags.None,
                Parameters: [],
                LocalVariables: []);

            var module = new BytecodeModule
            {
                Header = BytecodeModuleHeader.Create(2, 1, 0, 0, 0),
                Instructions =
                [
                    Instruction.Simple(OpCode.WaitInput, aux: (ushort)InputRequestKind.IntValue),
                    Instruction.Simple(OpCode.Halt),
                ],
                Functions = [fn],
                SourceMap = mapBuilder.Build(),
            };

            using var engine = new EmueraEngine(module, vfs);
            engine.PushEntryFrame(fn);

            Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
            Assert.NotNull(engine.PendingInputRequest);
            Assert.Single(engine.GetSourceStackTrace());
            Assert.Equal(1, engine.GetSourceStackTrace()[0].LineNo);

            engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 100));
            Assert.Equal(EngineStepStatus.Completed, engine.Step());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
