using System.Text;
using Hypera.Rendering;
using Hypera.VirtualFs;
using SkiaSharp;
using Xunit;

namespace Hypera.Tests.Rendering;

public sealed class WebpAndGraphicsTests
{
    [Fact]
    public void WebpCodec_StaticAndMultiFrameAnimatedWebp_DecodesFramesDurationsAndBlending()
    {
        // 1. Static WebP round-trip
        using var staticBmp = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul));
        staticBmp.Erase(new SKColor(0x20, 0x80, 0xE0, 0xFF));
        byte[] staticWebp = ImageCodecDecoder.EncodeToWebp(staticBmp, quality: 100);

        using (var ms = new MemoryStream(staticWebp))
        {
            IReadOnlyList<DecodedImageFrame> decodedStatic = SkiaFrameCompositor.DecodeFrames(ms);
            Assert.Single(decodedStatic);
            Assert.Equal(16, decodedStatic[0].Bitmap.Width);
            Assert.Equal(16, decodedStatic[0].Bitmap.Height);
            SKColor px = decodedStatic[0].Bitmap.GetPixel(8, 8);
            Assert.Equal(0x20, px.Red);
            Assert.Equal(0x80, px.Green);
            Assert.Equal(0xE0, px.Blue);
            Assert.Equal(0xFF, px.Alpha);
            decodedStatic[0].Bitmap.Dispose();
        }

        // 2. Multi-frame animated WebP with 3 frames, distinct durations, and delta alpha-blending over prior frame
        using var frame0 = new SKBitmap(new SKImageInfo(24, 24, SKColorType.Bgra8888, SKAlphaType.Premul));
        frame0.Erase(new SKColor(0xFF, 0x00, 0x00, 0xFF)); // solid red base

        using var frame1 = new SKBitmap(new SKImageInfo(24, 24, SKColorType.Bgra8888, SKAlphaType.Premul));
        frame1.Erase(SKColors.Transparent);
        using (var c1 = new SKCanvas(frame1))
        using (var p1 = new SKPaint { Color = new SKColor(0x00, 0xFF, 0x00, 0xFF), Style = SKPaintStyle.Fill })
        {
            c1.DrawRect(12, 0, 12, 24, p1); // right half green, left half transparent (blends onto frame0 red!)
        }

        using var frame2 = new SKBitmap(new SKImageInfo(24, 24, SKColorType.Bgra8888, SKAlphaType.Premul));
        frame2.Erase(new SKColor(0x00, 0x00, 0xFF, 0xFF)); // solid blue overwrite

        byte[] animWebp = ImageCodecDecoder.EncodeAnimatedWebp(
            [
                new DecodedImageFrame(frame0, 45),
                new DecodedImageFrame(frame1, 90),
                new DecodedImageFrame(frame2, 135),
            ],
            quality: 100,
            frameOptions:
            [
                new AnimatedWebpFrameOptions(0, 0, AlphaBlend: false, DisposeToBackground: false),
                new AnimatedWebpFrameOptions(0, 0, AlphaBlend: true, DisposeToBackground: false),
                new AnimatedWebpFrameOptions(0, 0, AlphaBlend: false, DisposeToBackground: true),
            ]);

        using var animStream = new MemoryStream(animWebp);
        IReadOnlyList<DecodedImageFrame> decodedAnim = SkiaFrameCompositor.DecodeFrames(animStream);
        try
        {
            Assert.Equal(3, decodedAnim.Count);
            Assert.Equal(45, decodedAnim[0].DurationMs);
            Assert.Equal(90, decodedAnim[1].DurationMs);
            Assert.Equal(135, decodedAnim[2].DurationMs);

            // Frame 0: red everywhere
            SKColor f0 = decodedAnim[0].Bitmap.GetPixel(4, 12);
            Assert.Equal(255, f0.Red);
            Assert.Equal(0, f0.Green);

            // Frame 1: left half retained red from frame 0 via RequiredFrame blending, right half is green!
            SKColor f1Left = decodedAnim[1].Bitmap.GetPixel(4, 12);
            SKColor f1Right = decodedAnim[1].Bitmap.GetPixel(18, 12);
            Assert.Equal(255, f1Left.Red);
            Assert.Equal(0, f1Left.Green);
            Assert.Equal(0, f1Right.Red);
            Assert.Equal(255, f1Right.Green);

            // Frame 2: solid blue
            SKColor f2 = decodedAnim[2].Bitmap.GetPixel(4, 12);
            Assert.Equal(255, f2.Blue);
            Assert.Equal(0, f2.Red);
        }
        finally
        {
            foreach (DecodedImageFrame f in decodedAnim)
            {
                f.Bitmap.Dispose();
            }
        }
    }

    [Fact]
    public void GraphicsManager_CreateClearMaskRotateColorMatrixAndArrayRoundTrip_ArePixelAccurate()
    {
        using var fonts = new FontRegistry();
        using var measurer = new GlyphMeasurer(fonts);
        using var gm = new GraphicsManager(fonts, measurer);

        Assert.True(gm.Create(1, 20, 20));
        Assert.True(gm.IsCreated(1));
        Assert.Equal(20, gm.GetWidth(1));
        Assert.Equal(20, gm.GetHeight(1));

        // GCLEAR full & rect
        gm.Clear(1, 0xFF102030u);
        Assert.Equal(0xFF102030L, gm.GetColor(1, 0, 0));
        gm.ClearRect(1, 0xFF405060u, 5, 5, 10, 10);
        Assert.Equal(0xFF405060L, gm.GetColor(1, 8, 8));
        Assert.Equal(0xFF102030L, gm.GetColor(1, 2, 2));

        // GSETCOLOR / GGETCOLOR
        Assert.True(gm.SetColor(1, 0xFFAABBCCu, 3, 4));
        Assert.Equal(0xFFAABBCCL, gm.GetColor(1, 3, 4));

        // GDRAWGWITHMASK: B-channel mask blend (B=0 untouched, B=255 full src, B=127 ~50% blend)
        Assert.True(gm.Create(10, 4, 1)); // dst: solid red (255, 0, 0)
        gm.Clear(10, 0xFFFF0000u);

        Assert.True(gm.Create(11, 4, 1)); // src: solid blue (0, 0, 255)
        gm.Clear(11, 0xFF0000FFu);

        Assert.True(gm.Create(12, 4, 1)); // mask: B=0, B=255, B=127, B=0
        gm.SetColor(12, 0xFFFF0000u, 0, 0); // B=0 (even though R=255!)
        gm.SetColor(12, 0xFF0000FFu, 1, 0); // B=255
        gm.SetColor(12, 0xFF00007Fu, 2, 0); // B=127 -> m=128/256 = exact 50%
        gm.SetColor(12, 0xFF00FF00u, 3, 0); // B=0 (G=255 ignored)

        Assert.True(gm.DrawGraphicsWithMask(10, 11, 12, 0, 0));
        Assert.Equal(0xFFFF0000L, gm.GetColor(10, 0, 0)); // B=0 -> red preserved
        Assert.Equal(0xFF0000FFL, gm.GetColor(10, 1, 0)); // B=255 -> blue copied
        uint blended = (uint)gm.GetColor(10, 2, 0);
        Assert.InRange((blended >> 16) & 0xFF, 126u, 129u); // R ~ 127
        Assert.InRange(blended & 0xFF, 126u, 129u);         // B ~ 127
        Assert.Equal(0xFFFF0000L, gm.GetColor(10, 3, 0)); // B=0 -> red preserved

        // GDRAWG with 5x5 Emuera ColorMatrix swapping Red -> Green (256 == 1.0)
        Assert.True(gm.Create(20, 4, 4));
        gm.Clear(20, 0xFFFF0000u); // pure red
        Assert.True(gm.Create(21, 4, 4));
        gm.Clear(21, 0x00000000u);

        long[,] swapRToG = new long[5, 5];
        swapRToG[0, 1] = 256; // input R -> output G
        swapRToG[3, 3] = 256; // input A -> output A
        Assert.True(gm.DrawGraphics(21, 20, 0, 0, 4, 4, 0, 0, 4, 4, swapRToG, PixelSamplingMode.CrispNearestNeighbor));
        Assert.Equal(0xFF00FF00L, gm.GetColor(21, 2, 2)); // pure green!

        // GDRAWGWITHROTATE (90 deg clockwise around center (10, 10) on 20x20 surface)
        Assert.True(gm.Create(30, 20, 20));
        gm.Clear(30, 0x00000000u);
        gm.SetBrush(30, 0xFF00FF00u);
        gm.FillRectangle(30, 14, 8, 4, 4); // block at right of center: center (16, 10)

        Assert.True(gm.Create(31, 20, 20));
        gm.Clear(31, 0x00000000u);
        Assert.True(gm.DrawGraphicsWithRotate(31, 30, 90f, 10f, 10f));
        // Rotating (16, 10) by +90 deg CW around (10, 10) lands at (10, 16)
        Assert.Equal(0xFF00FF00L, gm.GetColor(31, 10, 16));
        Assert.Equal(0x00000000L, gm.GetColor(31, 16, 10));

        // GROTATE (in-place +90 deg CW again around (10, 10): (10, 16) -> (4, 10))
        Assert.True(gm.Rotate(31, 90f, 10f, 10f));
        Assert.Equal(0xFF00FF00L, gm.GetColor(31, 4, 10));
        Assert.Equal(0x00000000L, gm.GetColor(31, 10, 16));

        // GTOARRAY / GFROMARRAY & GSAVE / GLOAD
        long[,] arr = gm.ToArray(31)!;
        Assert.Equal(0xFF00FF00L, arr[4, 10]);
        Assert.True(gm.Create(32, 20, 20));
        Assert.True(gm.FromArray(32, arr));
        Assert.Equal(0xFF00FF00L, gm.GetColor(32, 4, 10));

        Assert.True(gm.Save(32, 7));
        Assert.True(gm.Load(33, 7));
        Assert.Equal(0xFF00FF00L, gm.GetColor(33, 4, 10));

        // GDRAWTEXT + GDASHSTYLE + GSETFONT safe cloning
        Assert.True(gm.Create(40, 120, 40));
        gm.Clear(40, 0xFF000000u);
        gm.SetBrush(40, 0xFFFFFFFFu);
        gm.SetPen(40, 0xFFFF0000u, 2f);
        gm.SetDashStyle(40, GraphicsDashStyle.Dash, GraphicsDashCap.Flat);
        gm.SetFont(40, "Arial", 20f, FontStyleFlags.Bold);
        Assert.Equal("Arial", gm.GetFont(40));
        Assert.Equal(20, gm.GetFontSize(40));
        Assert.Equal(FontStyleFlags.Bold, gm.GetFontStyle(40));
        Assert.True(gm.DrawText(40, "Hi", 4, 4));
        SKSize textSize = gm.GetTextSize("Hi", "Arial", 20f, FontStyleFlags.Bold);
        Assert.True(textSize.Width > 0f);
        Assert.True(textSize.Height >= 20f);

        Assert.True(gm.DisposeSurface(1));
        Assert.False(gm.IsCreated(1));
    }

    [Fact]
    public void SpriteRegistry_LoadsResourcesCsvStaticAndAnimatedWebp_AndSupportsSpriteGetColor()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hypera_sprite_test_{Guid.NewGuid():N}");
        string resDir = Path.Combine(tempDir, "resources");
        Directory.CreateDirectory(resDir);

        try
        {
            // Write static PNG sprite sheet (16x8: left 8x8 red, right 8x8 cyan)
            using (var sheet = new SKBitmap(new SKImageInfo(16, 8, SKColorType.Bgra8888, SKAlphaType.Premul)))
            using (var c = new SKCanvas(sheet))
            {
                c.Clear(new SKColor(0xFF, 0x00, 0x00, 0xFF));
                using var cyanPaint = new SKPaint { Color = new SKColor(0x00, 0xFF, 0xFF, 0xFF) };
                c.DrawRect(8, 0, 8, 8, cyanPaint);
                File.WriteAllBytes(Path.Combine(resDir, "sheet.png"), ImageCodecDecoder.EncodeToPng(sheet));
            }

            // Write 2-frame animated WebP (frame0 = yellow 50ms, frame1 = magenta 50ms)
            using (var f0 = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul)))
            using (var f1 = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul)))
            {
                f0.Erase(new SKColor(0xFF, 0xFF, 0x00, 0xFF));
                f1.Erase(new SKColor(0xFF, 0x00, 0xFF, 0xFF));
                byte[] webp = ImageCodecDecoder.EncodeAnimatedWebp([new(f0, 50), new(f1, 50)]);
                File.WriteAllBytes(Path.Combine(resDir, "anim.webp"), webp);
            }

            string csv = """
                ; Sprite definitions
                HERO_RED,sheet.png,0,0,8,8,0,0
                HERO_CYAN,sheet.png,8,0,8,8,2,3
                SPARKLE,anim.webp,0,0,8,8,0,0
                """;
            File.WriteAllText(Path.Combine(resDir, "sprites.csv"), csv, Encoding.UTF8);

            using var vfs = new PhysicalDirectoryVfs(tempDir);
            using var fonts = new FontRegistry();
            using var measurer = new GlyphMeasurer(fonts);
            using var gm = new GraphicsManager(fonts, measurer);
            using var sprites = new SpriteRegistry();

            int loaded = sprites.LoadResourcesFromVfs(vfs);
            Assert.Equal(3, loaded);
            Assert.True(sprites.Exists("HERO_RED"));
            Assert.True(sprites.Exists("hero_cyan")); // case-insensitive
            Assert.Equal(2, sprites.GetPosX("HERO_CYAN"));
            Assert.Equal(3, sprites.GetPosY("HERO_CYAN"));

            Assert.Equal(0xFFFF0000u, sprites.GetColor("HERO_RED", 4, 4));
            Assert.Equal(0xFF00FFFFu, sprites.GetColor("HERO_CYAN", 4, 4));

            // Animated WebP frame switching by elapsed time
            Assert.Equal(0xFFFFFF00u, sprites.GetColor("SPARKLE", 4, 4, elapsedMs: 10));
            Assert.Equal(0xFFFF00FFu, sprites.GetColor("SPARKLE", 4, 4, elapsedMs: 60));
            Assert.Equal(0xFFFFFF00u, sprites.GetColor("SPARKLE", 4, 4, elapsedMs: 110));

            // GDRAWSPRITE into offscreen graphics surface
            gm.Create(50, 16, 16);
            gm.Clear(50, 0x00000000u);
            Assert.True(gm.DrawSprite(50, sprites, "HERO_CYAN", destX: 4, destY: 4));
            Assert.Equal(0xFF00FFFFL, gm.GetColor(50, 6, 6));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
