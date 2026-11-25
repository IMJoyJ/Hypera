using Hypera.Rendering;
using Hypera.Runtime;
using SkiaSharp;
using Xunit;

namespace Hypera.Tests.Rendering;

public sealed class HtmlCompositorAndHitTestTests
{
    [Fact]
    public void HtmlPrint_DivHighDepthAndFontSizeMixedNumGeometry_AreAccurate()
    {
        using var fonts = new FontRegistry();
        using var measurer = new GlyphMeasurer(fonts);

        // FontSize = 20, LineHeight = 26 -> all non-px MixedNums in <div> MUST scale by FontSize (20), NOT LineHeight (26)!
        var style = new HtmlParseStyle("Arial", FontSize: 20, LineHeight: 26, ForeColorRgba: 0xFFFFFFFFu, FocusColorRgba: 0xFFFF00FFu);

        string html =
            "<div xpos='150' ypos='200' width='500' height='300' depth='999' display='absolute' " +
            "margin='50,25px' border='10' padding='50' radius='25' color='#112233' bcolor='#FF0000,#00FF00'>" +
            "<b>Hello</b></div>";

        IReadOnlyList<ConsoleLine> lines = HtmlPrintEngine.ParseToLines(
            html,
            logicalLineId: 1,
            maxWidth: 800,
            buttonGeneration: 1,
            style,
            measurer);

        Assert.Single(lines);
        ConsoleRenderPart divPart = Assert.Single(lines[0].Parts);
        Assert.Equal(ConsolePartKind.Div, divPart.Kind);
        Assert.NotNull(divPart.Div);

        DivBoxStyle div = divPart.Div!;
        // Depth ZIndex=999 decoupled from MaxStructuralNestingDepth (32)!
        Assert.Equal(999, div.Depth);
        Assert.Equal(999, divPart.Depth);
        Assert.Equal(DivDisplayMode.Absolute, div.DisplayMode);

        // 150% of 20 = 30, 200% of 20 = 40, 500% of 20 = 100, 300% of 20 = 60
        Assert.Equal(30f, div.XPos, precision: 3);
        Assert.Equal(40f, div.YPos, precision: 3);
        Assert.Equal(100f, div.Width, precision: 3);
        Assert.Equal(60f, div.Height, precision: 3);

        // 4-side margin='50,25px' -> top=10, right=25, bottom=10, left=25
        Assert.Equal(new BoxEdges(10f, 25f, 10f, 25f), div.Margin);
        Assert.Equal(new BoxEdges(2f, 2f, 2f, 2f), div.Border);   // 10% of 20 = 2
        Assert.Equal(new BoxEdges(10f, 10f, 10f, 10f), div.Padding); // 50% of 20 = 10
        Assert.Equal(new BoxEdges(5f, 5f, 5f, 5f), div.Radius);   // 25% of 20 = 5
        Assert.Equal(0x112233FFu, div.BackgroundColorRgba);
        Assert.Equal(new BoxBorderColors(0xFF0000FFu, 0x00FF00FFu, 0xFF0000FFu, 0x00FF00FFu), div.BorderColors);

        // Bottom-left origin for absolute div on 800x600 viewport:
        // raw top = 600 - ypos(40) - height(60) = 500; after margin(top=10, left=25, right=25, bottom=10):
        // x = 30 + 25 = 55, y = 500 + 10 = 510, w = 100 - 50 = 50, h = 60 - 20 = 40
        LogicalRect outer = ConsoleHitTester.ComputeDivOuterRect(divPart, div, lineTopY: 0f, new LogicalViewport(800, 600, 20, 26));
        Assert.Equal(new LogicalRect(55f, 510f, 50f, 40f), outer);
    }

    [Fact]
    public void HtmlPrint_ButtonBackwardPos_ImgFlipAndSubPixelClamp_AndUtilities()
    {
        using var fonts = new FontRegistry();
        using var measurer = new GlyphMeasurer(fonts);
        var style = new HtmlParseStyle("Arial", FontSize: 20, LineHeight: 20, ForeColorRgba: 0xFFFFFFFFu, FocusColorRgba: 0xFFFF00FFu);

        // Backward pos='100' (20px) after pos='500' (100px) MUST NOT clamp upward via max(cursorX, pos)!
        string html =
            "<button value='1' pos='500'>A</button>" +
            "<button value='2' pos='100'>B</button>" +
            "<img src='bar' width='1' height='-100' ypos='50'/>";

        IReadOnlyList<ConsoleLine> lines = HtmlPrintEngine.ParseToLines(html, 1, 800, 1, style, measurer);
        Assert.Single(lines);
        Assert.Equal(3, lines[0].Parts.Count);

        ConsoleRenderPart btn1 = lines[0].Parts[0];
        ConsoleRenderPart btn2 = lines[0].Parts[1];
        ConsoleRenderPart img = lines[0].Parts[2];

        Assert.Equal(100f, btn1.X, precision: 2);
        Assert.Equal(20f, btn2.X, precision: 2); // Moved backward cleanly!

        // width='1' (1% of 20 = 0.2px) clamped to min 1px; height='-100' flipped vertically with height 20px!
        Assert.Equal(1f, img.Width, precision: 3);
        Assert.Equal(20f, img.Height, precision: 3);
        Assert.False(img.FlipX);
        Assert.True(img.FlipY);
        Assert.Equal(10f, img.YOffset, precision: 3);

        // HTML utility functions
        Assert.Equal("&lt;b&gt;A &amp; B&lt;/b&gt;", HtmlPrintEngine.Escape("<b>A & B</b>"));
        Assert.Equal("A & B", HtmlPrintEngine.ToPlainText("<!-- ignore --><font color='red'>A &amp; B</font>"));
        Assert.Equal(["<font color='red'>", "Hi", "</font>"], HtmlPrintEngine.TagSplit("<font color='red'>Hi</font>"));
        Assert.Equal("<b>Ab</b>", HtmlPrintEngine.Substring("<b>Abcdef</b>", 2));
        Assert.Equal(4, HtmlPrintEngine.GetStringLen("<font color='white'>Abあ</font>", returnPixels: false));
    }

    [Fact]
    public void VirtualConsoleBuffer_WrappedLinesLogicalCount_ClearLineGroupDelete_AndInPlaceMerge()
    {
        using var fonts = new FontRegistry();
        using var measurer = new GlyphMeasurer(fonts);
        var vp = new LogicalViewport(LogicalWidth: 100, LogicalHeight: 200, FontSize: 16, LineHeight: 18);
        var console = new VirtualConsoleBuffer(vp, measurer, defaultFontFamily: "Arial");

        console.PrintLine("Short");
        // Long text wraps across multiple physical lines inside 100px width, yet counts as 1 logical line!
        console.PrintLine("WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW");

        Assert.Equal(2, console.LineCount);
        Assert.True(console.PhysicalLineCount >= 4);
        Assert.Equal("Short", console.HtmlGetPrintedStr(1) != string.Empty ? HtmlPrintEngine.ToPlainText(console.HtmlGetPrintedStr(1)) : "");

        // HTML_POPPRINTINGSTR pops unflushed pending line
        console.Print("Buffered");
        Assert.True(console.HasPendingLine);
        string popped = console.HtmlPopPrintingStr();
        Assert.Contains("Buffered", popped);
        Assert.False(console.HasPendingLine);

        // CLEARLINE 1 deletes ALL physical lines belonging to the last logical line (the wrapped one)
        int removed = console.ClearLine(1);
        Assert.True(removed >= 3);
        Assert.Equal(1, console.LineCount);
        Assert.Equal(1, console.PhysicalLineCount);

        // Next output merges as in-place replacement of that cleared line!
        console.PrintLine("Replaced");
        Assert.Equal(2, console.LineCount);
        Assert.True(console.LastOutputWasInPlaceReplacement);
        Assert.True(console.PhysicalLines[^1].ReplacedInPlace);
    }

    [Fact]
    public void CompositorAndHitTester_ZOrderInterleaving_CbgButtonMapAndImgSrcmHitTest()
    {
        using var fonts = new FontRegistry();
        using var measurer = new GlyphMeasurer(fonts);
        using var gm = new GraphicsManager(fonts, measurer);
        using var sprites = new SpriteRegistry();
        using var compositor = new SkiaFrameCompositor(fonts, measurer);

        var vp = new LogicalViewport(LogicalWidth: 100, LogicalHeight: 100, FontSize: 20, LineHeight: 20);
        var console = new VirtualConsoleBuffer(vp, measurer, sprites, defaultFontFamily: "Arial", backColorRgba: 0x101010FFu);

        // 1. Background image (SETBGIMAGE depth=100, dark gray)
        gm.Create(1, 100, 100);
        gm.Clear(1, 0xFF202020u);
        sprites.CreateSprite("BG_IMG", 1, gm);
        console.SetBackgroundImage("BG_IMG", depth: 100);

        // 2. CBG layers: depth=10 (behind flow lines, red 40x40 at bottom-left x=0,y=0) & depth=-10 (in front of flow lines, blue 20x20 at bottom-left x=0,y=0)
        gm.Create(2, 40, 40);
        gm.Clear(2, 0xFFFF0000u); // red
        console.CbgSetGraphics(2, x: 0, y: 0, zDepth: 10);

        gm.Create(3, 20, 20);
        gm.Clear(3, 0xFF0000FFu); // blue
        console.CbgSetGraphics(3, x: 0, y: 0, zDepth: -10);

        // 3. Flow line at depth=0 with green <shape rect> covering [0..40) at bottom line (y=80..100) AND <button> with <img srcm> at x=50..90
        gm.Create(4, 40, 20);
        gm.Clear(4, 0xFF00FF00u);
        sprites.CreateSprite("BTN_VIS", 4, gm);

        // srcm map: left half = 0x123456 (opaque), right half = 0xABCDEF (opaque)
        gm.Create(5, 40, 20);
        gm.ClearRect(5, 0xFF123456u, 0, 0, 20, 20);
        gm.ClearRect(5, 0xFFABCDEFu, 20, 0, 20, 20);
        sprites.CreateSprite("BTN_MAP", 5, gm);

        console.HtmlPrint(
            "<shape type='rect' param='0px,0px,40px,20px' color='#00FF00'/>" +
            "<button value='42' title='Pick Color' pos='50px'><img src='BTN_VIS' srcm='BTN_MAP' width='40px' height='20px'/></button>");

        // 4. HTML_PRINT_ISLAND yellow square at (5, 5) covering top-left (always above flow/CBG)
        console.HtmlPrintIsland("<shape type='rect' param='5px,5px,10px,10px' color='#FFFF00'/>");

        // 5. CBGSETBMAPG (bottom-left anchored 100x50 map covering logical y=[50..100))
        gm.Create(6, 100, 50);
        gm.Clear(6, 0x00000000u); // transparent -> no hit
        gm.ClearRect(6, 0xFF654321u, 10, 10, 20, 20); // A=255 -> logical x=[10..30), y=[60..80)
        gm.ClearRect(6, 0x80999999u, 40, 10, 20, 20); // A=128 != 255 -> MUST NOT trigger hit!
        console.CbgSetButtonMap(6);

        ConsoleScene scene = console.CaptureScene();

        // Render and verify exact Z-order pixel composition!
        using var target = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target))
        {
            compositor.RenderScene(canvas, 100, 100, scene, gm, sprites);
        }

        // (95, 5) -> SETBGIMAGE (0x202020)
        Assert.Equal(new SKColor(0x20, 0x20, 0x20, 0xFF), target.GetPixel(95, 5));
        // (10, 10) -> HTML_PRINT_ISLAND yellow
        Assert.Equal(new SKColor(0xFF, 0xFF, 0x00, 0xFF), target.GetPixel(10, 10));
        // (25, 65) -> CBG depth=10 red (covers y=[60..100), x=[0..40), above flow line y=80)
        Assert.Equal(new SKColor(0xFF, 0x00, 0x00, 0xFF), target.GetPixel(25, 65));
        // (25, 90) -> flow line depth=0 green overwrites CBG depth=10 red!
        Assert.Equal(new SKColor(0x00, 0xFF, 0x00, 0xFF), target.GetPixel(25, 90));
        // (10, 90) -> CBG depth=-10 blue overwrites flow line depth=0 green!
        Assert.Equal(new SKColor(0x00, 0x00, 0xFF, 0xFF), target.GetPixel(10, 90));

        // Hit-test CBGSETBMAPG opaque pixel -> RESULT:4 == 0x654321
        ConsoleHitTestResult cbgHit = ConsoleHitTester.HitTestLogicalPoint(scene, 15f, 65f, gm, sprites);
        Assert.True(cbgHit.HasCbgButtonMapHit);
        Assert.Equal(0x654321, cbgHit.CbgButtonMapColor);
        InputHitTestSlots cbgSlots = cbgHit.ToInputHitTestSlots();
        Assert.True(cbgSlots.HasHitColor);
        Assert.Equal(0x654321L, cbgSlots.Result4SecondaryColor);

        // Hit-test CBGSETBMAPG semi-transparent pixel (A=128 != 255) -> NO hit!
        ConsoleHitTestResult cbgMiss = ConsoleHitTester.HitTestLogicalPoint(scene, 45f, 65f, gm, sprites);
        Assert.False(cbgMiss.HasCbgButtonMapHit);
        Assert.Equal(-1, cbgMiss.CbgButtonMapColor);

        // Hit-test <button><img srcm='BTN_MAP'/></button> left half (x=55, y=90) -> RESULT:3 == 0x123456, button value 42, tooltip
        ConsoleHitTestResult imgLeftHit = ConsoleHitTester.HitTestLogicalPoint(scene, 55f, 90f, gm, sprites);
        Assert.True(imgLeftHit.HasSrcMapHit);
        Assert.Equal(0x123456, imgLeftHit.SrcMapColor);
        Assert.True(imgLeftHit.HasActiveButton);
        Assert.Equal(42L, imgLeftHit.ActiveButton!.IntValue);
        Assert.Equal("Pick Color", imgLeftHit.TooltipText);
        InputHitTestSlots leftSlots = imgLeftHit.ToInputHitTestSlots();
        Assert.Equal(0x123456L, leftSlots.Result3PrimaryColor);
        Assert.Equal(0x123456L, leftSlots.Result6HoverOrAuxColor);

        // Hit-test right half of <img srcm> (x=75, y=90) -> RESULT:3 == 0xABCDEF
        ConsoleHitTestResult imgRightHit = ConsoleHitTester.HitTestLogicalPoint(scene, 75f, 90f, gm, sprites);
        Assert.Equal(0xABCDEF, imgRightHit.SrcMapColor);
    }

    [Fact]
    public void HitTesterAndCompositor_FlippedImgSrcm_CbgButtonHoverSwap_DivZOrder_AndTooltipOnTop()
    {
        using var fonts = new FontRegistry();
        using var measurer = new GlyphMeasurer(fonts);
        using var gm = new GraphicsManager(fonts, measurer);
        using var sprites = new SpriteRegistry();
        using var compositor = new SkiaFrameCompositor(fonts, measurer);

        var vp = new LogicalViewport(LogicalWidth: 100, LogicalHeight: 100, FontSize: 20, LineHeight: 20);
        var console = new VirtualConsoleBuffer(vp, measurer, sprites, defaultFontFamily: "Arial", backColorRgba: 0x000000FFu);

        // Create normal (gray) and hover (orange) CBG button sprites + BMapG
        gm.Create(1, 20, 20);
        gm.Clear(1, 0xFF808080u);
        sprites.CreateSprite("CBG_N", 1, gm);

        gm.Create(2, 20, 20);
        gm.Clear(2, 0xFFFF8800u);
        sprites.CreateSprite("CBG_H", 2, gm);

        console.CbgSetButtonSprite(buttonValue: 0x112233, normalSprite: "CBG_N", hoverSprite: "CBG_H", x: 0, y: 0, zDepth: -5, tooltip: "CBG Tip");

        gm.Create(3, 100, 100);
        gm.Clear(3, 0x00000000u);
        gm.ClearRect(3, 0xFF112233u, 0, 80, 20, 20);
        console.CbgSetButtonMap(3);

        // Flipped <img width='-40px' srcm='MAP2'> inside an absolute div at (30, 0) size (40, 20) -> y=[80..100)
        gm.Create(4, 40, 20);
        gm.ClearRect(4, 0xFFAA0000u, 0, 0, 20, 20);  // left half in source = 0xAA0000
        gm.ClearRect(4, 0xFF00BB00u, 20, 0, 20, 20); // right half in source = 0x00BB00
        sprites.CreateSprite("MAP2", 4, gm);

        console.HtmlPrint(
            "<div rect='30px,0px,40px,20px' depth='-1' display='absolute' color='#222222'>" +
            "<button value='flipBtn'><img src='MAP2' srcm='MAP2' width='-40px' height='20px'/></button>" +
            "</div>");

        // Tooltip positioned at (5, 5) -> drawn at (17, 23) on top of everything
        console.Tooltip = new TooltipState(
            ActiveText: "Top",
            AnchorX: 5f,
            AnchorY: 5f,
            ForeColorRgba: 0x000000FFu,
            BackColorRgba: 0xFF00FFFFu,
            FontSize: 12);

        ConsoleScene hoveredScene = console.CaptureScene(hoveredCbgButtonValue: 0x112233);
        using var bmp = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            compositor.RenderScene(canvas, 100, 100, hoveredScene, gm, sprites);
        }

        // CBG button hovered swaps to orange (0xFF8800) at (10, 90)
        Assert.Equal(new SKColor(0xFF, 0x88, 0x00, 0xFF), bmp.GetPixel(10, 90));
        // Tooltip background (magenta 0xFF00FF) appears at top-left anchor+offset (20, 26)
        Assert.Equal(new SKColor(0xFF, 0x00, 0xFF, 0xFF), bmp.GetPixel(20, 26));

        // Hit-test CBG button resolves ActiveCbgButton and tooltip
        ConsoleHitTestResult cbgRes = ConsoleHitTester.HitTestLogicalPoint(hoveredScene, 10f, 90f, gm, sprites);
        Assert.Equal(0x112233, cbgRes.CbgButtonMapColor);
        Assert.NotNull(cbgRes.ActiveCbgButton);
        Assert.Equal("CBG Tip", cbgRes.TooltipText);

        // Hit-test horizontally flipped <img width='-40px' srcm='MAP2'> inside absolute div:
        // Left half of rendered rect (x=35, y=90) maps to RIGHT half of source sprite (0x00BB00)!
        ConsoleHitTestResult flippedLeft = ConsoleHitTester.HitTestLogicalPoint(hoveredScene, 35f, 90f, gm, sprites);
        Assert.True(flippedLeft.HasSrcMapHit);
        Assert.Equal(0x00BB00, flippedLeft.SrcMapColor);
        Assert.True(flippedLeft.HasActiveButton);
        Assert.Equal("flipBtn", flippedLeft.ActiveButton!.StringValue);

        // Right half of rendered rect (x=65, y=90) maps to LEFT half of source sprite (0xAA0000)!
        ConsoleHitTestResult flippedRight = ConsoleHitTester.HitTestLogicalPoint(hoveredScene, 65f, 90f, gm, sprites);
        Assert.Equal(0xAA0000, flippedRight.SrcMapColor);
    }

    [Fact]
    public void HtmlPrint_AlignmentNoWrapStringLinesAndPixelLen_WorkAccurately()
    {
        using var fonts = new FontRegistry();
        using var measurer = new GlyphMeasurer(fonts);
        var style = new HtmlParseStyle("Arial", FontSize: 16, LineHeight: 18, ForeColorRgba: 0xFFFFFFFFu, FocusColorRgba: 0xFFFF00FFu);

        IReadOnlyList<ConsoleLine> centered = HtmlPrintEngine.ParseToLines(
            "<p align='center'><nobr>Centered</nobr></p>",
            logicalLineId: 1,
            maxWidth: 400,
            buttonGeneration: 1,
            style,
            measurer);
        Assert.Single(centered);
        Assert.Equal(TextAlignment.Center, centered[0].Alignment);
        Assert.True(centered[0].NoWrap);
        Assert.True(centered[0].Parts[0].X > 100f);

        int pxWidth = HtmlPrintEngine.GetStringLen("<b>Hello</b>", returnPixels: true, measurer, fontSize: 16, fontFamily: "Arial");
        Assert.True(pxWidth > 10);

        int wrappedLines = HtmlPrintEngine.GetStringLines("WWWWWWWWWWWWWWWWWWWW", maxWidthPx: 60, measurer, fontSize: 16, fontFamily: "Arial");
        Assert.True(wrappedLines >= 3);
    }
}
