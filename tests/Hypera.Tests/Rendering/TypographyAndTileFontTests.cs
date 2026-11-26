using System.Buffers.Binary;
using System.Text;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.Rendering;
using Hypera.Runtime;
using Hypera.Tests.Compiler;
using Hypera.VirtualFs;
using SkiaSharp;
using Xunit;

namespace Hypera.Tests.Rendering;

public sealed class TypographyAndTileFontTests
{
    [Fact]
    public void FontRegistry_VfsCustomFontIsStrictlyInstanceScoped_NeverLeaksGlobally()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hypera_font_iso_{Guid.NewGuid():N}");
        string fontDir = Path.Combine(tempDir, "font");
        Directory.CreateDirectory(fontDir);

        try
        {
            byte[] ttfBytes = MinimalSquareTileTtfBuilder.Build("HyperaNesTile");
            File.WriteAllBytes(Path.Combine(fontDir, "HyperaNesTile.ttf"), ttfBytes);

            using var vfs = new PhysicalDirectoryVfs(tempDir);
            using var registryA = new FontRegistry();
            using var registryB = new FontRegistry();

            int loaded = registryA.RegisterFromVfs(vfs);
            Assert.Equal(1, loaded);
            Assert.True(registryA.HasFont("HyperaNesTile"));
            Assert.True(registryA.IsCustomTileFont("HyperaNesTile"));

            // Second independent instance MUST NOT see registryA's loaded font!
            Assert.False(registryB.HasFont("HyperaNesTile"));
            Assert.False(registryB.IsCustomTileFont("HyperaNesTile"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CustomTileFont_RendersAdjacentBlockGlyphsWithZeroPixelSeamsAndZeroAntiAliasBleed()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hypera_tile_seam_{Guid.NewGuid():N}");
        string fontDir = Path.Combine(tempDir, "font");
        Directory.CreateDirectory(fontDir);

        try
        {
            // Real TrueType font whose glyph 'A' (U+0041) & '█' (U+2588) is a full-em solid square [0..1000]x[0..1000]
            byte[] ttfBytes = MinimalSquareTileTtfBuilder.Build("NesBlockTile");
            File.WriteAllBytes(Path.Combine(fontDir, "NesBlockTile.ttf"), ttfBytes);

            using var vfs = new PhysicalDirectoryVfs(tempDir);
            using var fonts = new FontRegistry();
            fonts.RegisterFromVfs(vfs);
            using var measurer = new GlyphMeasurer(fonts);

            const int fontSize = 16;
            // Custom tile font MUST enforce LineHeight == FontSize (0px vertical seam)
            int lineHeight = measurer.GetEffectiveLineHeight("NesBlockTile", fontSize, configuredLineHeight: 19);
            Assert.Equal(fontSize, lineHeight);

            float width3 = measurer.MeasureTextWidth("███", fontSize, "NesBlockTile");
            Assert.Equal(48f, width3, precision: 2);

            using var bmp = new SKBitmap(new SKImageInfo(64, 48, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.Black);
                // Draw 2 stacked rows of 3 solid 16x16 tiles starting at (4, 4) -> covers exact [4..52) x [4..36)
                measurer.DrawText(canvas, "███", 4f, 4f, fontSize, "NesBlockTile", FontStyleFlags.Regular, SKColors.White);
                measurer.DrawText(canvas, "███", 4f, 4f + lineHeight, fontSize, "NesBlockTile", FontStyleFlags.Regular, SKColors.White);
            }

            // Every single pixel inside the 48x32 tiled region MUST be pure white (0px gaps horizontally or vertically!)
            for (int y = 4; y < 36; y++)
            {
                for (int x = 4; x < 52; x++)
                {
                    SKColor px = bmp.GetPixel(x, y);
                    Assert.Equal(SKColors.White, px);
                }
            }

            // Border pixels immediately outside [4..52) x [4..36) MUST be pure black (0px anti-alias bleed / overlap!)
            Assert.Equal(SKColors.Black, bmp.GetPixel(3, 10));
            Assert.Equal(SKColors.Black, bmp.GetPixel(52, 10));
            Assert.Equal(SKColors.Black, bmp.GetPixel(10, 3));
            Assert.Equal(SKColors.Black, bmp.GetPixel(10, 36));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void StandardFont_AlignsBoxDrawingAndGeometricSymbolsToFullWidthSpaceAdvance()
    {
        using var fonts = new FontRegistry();
        using var measurer = new GlyphMeasurer(fonts);

        float fullWidth = measurer.MeasureFullWidthAdvance(18f, fontFamily: null);
        Assert.True(fullWidth > 0f);

        string[] symbols = ["─", "│", "┌", "■", "○", "―", "∥", "★", "☆"];
        foreach (string sym in symbols)
        {
            float w = measurer.MeasureTextWidth(sym, 18f, fontFamily: null, samplingMode: PixelSamplingMode.SmoothAntialiased);
            Assert.Equal(fullWidth, w, precision: 3);
        }

        float rowWidth = measurer.MeasureTextWidth("┌──┐", 18f, fontFamily: null, samplingMode: PixelSamplingMode.SmoothAntialiased);
        Assert.Equal(fullWidth * 4f, rowWidth, precision: 2);
    }

    [Fact]
    public void DisplayTextProjector_ExpandsTabsStripsControlsAndPreservesOriginalString()
    {
        string raw = "A\tB\u0003C\u0007D\u0085E\\F";

        // Default TEXTRENDERER semantics: GDI TextRenderer without ExpandTabs treats '\t' as 0px (stripped on display)
        string textRendererProjected = DisplayTextProjector.ProjectForDisplay(raw, replaceBackslashWithYen: false);
        Assert.Equal("ABCDE\\F", textRendererProjected);

        string textRendererYen = DisplayTextProjector.ProjectForDisplay(raw, replaceBackslashWithYen: true);
        Assert.Equal("ABCDE¥F", textRendererYen);

        // Legacy GRAPHICS semantics: StringMeasure replaces '\t' with 8 spaces
        string graphicsProjected = DisplayTextProjector.ProjectForDisplay(raw, replaceBackslashWithYen: false, expandTabs: true);
        Assert.Equal("A        BCDE\\F", graphicsProjected);

        string graphicsYen = DisplayTextProjector.ProjectForDisplay(raw, replaceBackslashWithYen: true, expandTabs: true);
        Assert.Equal("A        BCDE¥F", graphicsYen);

        // Original script string is untouched
        Assert.Contains('\u0003', raw);
        Assert.Contains('\t', raw);
    }

    [Fact]
    public void VirtualConsoleBuffer_TrailingTabsUnderTextRendererDoNotShiftOrWrapColumns()
    {
        using var registry = new FontRegistry();
        using var measurer = new GlyphMeasurer(registry);
        var viewport = new LogicalViewport(1230, 950, 16, 16);
        var buffer = new VirtualConsoleBuffer(viewport, measurer, defaultFontFamily: "ＭＳ ゴシック")
        {
            ExpandTabs = false,
            ButtonWrap = true,
        };

        // Row 3 (no trailing tabs)
        buffer.PrintPlain(new string(' ', 40));
        buffer.PrintPlain("|");
        buffer.HtmlPrint("<button value='2'><font color='#B26132'>[ 3]　L キスキル      </font></button>", noNewLine: true);
        buffer.PrintPlain("   |　|");
        buffer.HtmlPrint("<button value='106'><font color='#8E4D28'>LWリィラ        </font></button>", noNewLine: true);
        buffer.PrintPlain("[x 3]　　　");
        buffer.HtmlPrint("<button value='107'><font color='#33909C'>L☆Tエントランス</font></button>", noNewLine: true);
        buffer.PrintPlain("[x 3]　　　");
        buffer.HtmlPrint("<button value='108'><font color='#BF3180'>E★Tチャレンジ  </font></button>", noNewLine: true);
        buffer.PrintPlain("[x 3]　　|　|");
        buffer.NewLine();

        // Row 4 (two accidental trailing tabs after 40 spaces, matching 01.DUEL_DECK.ERB:512)
        buffer.Print(new string(' ', 40) + "\t\t");
        buffer.PrintPlain("|");
        buffer.HtmlPrint("<button value='3'><font color='#B26132'>[ 4]　L リィラ        </font></button>", noNewLine: true);
        buffer.Print("   |－|");
        buffer.HtmlPrint("<button value='109'><font color='#EF3EA0'>E★Wチャレンジ  </font></button>", noNewLine: true);
        buffer.Print("[x 3]　　　");
        buffer.HtmlPrint("<button value='110'><font color='#C0C0C0'>                </font></button>", noNewLine: true);
        buffer.Print("[x 0]　　　");
        buffer.HtmlPrint("<button value='111'><font color='#C0C0C0'>                </font></button>", noNewLine: true);
        buffer.Print("[x 0]　　|－|");
        buffer.NewLine();

        Assert.Equal(2, buffer.PhysicalLines.Count);
        ConsoleLine row3 = buffer.PhysicalLines[0];
        ConsoleLine row4 = buffer.PhysicalLines[1];
        Assert.True(row3.IsLogicalLine);
        Assert.True(row4.IsLogicalLine);
        Assert.Equal(row3.ContentWidth, row4.ContentWidth, precision: 2);

        ConsoleRenderPart row3Pipe = Assert.Single(row3.Parts, static p => p.Text == "|");
        ConsoleRenderPart row4Pipe = Assert.Single(row4.Parts, static p => p.Text == "|");
        Assert.Equal(row3Pipe.X, row4Pipe.X, precision: 2);

        // Under legacy GRAPHICS mode, tabs expand to 8 spaces each
        buffer.ExpandTabs = true;
        buffer.Print("A\tB");
        buffer.NewLine();
        ConsoleLine graphicsLine = buffer.PhysicalLines[^1];
        Assert.Equal("A        B", graphicsLine.PlainText);
    }

    [Fact]
    public void EmueraEngine_WiresTextDrawingModeAndButtonWrapFromRuntimeOptions()
    {
        string source =
            "@SYSTEM_TITLE\n" +
            "PRINTFORM %\"\",10,LEFT%\t\t\n" +
            "PRINTFORML |%GETCONFIGS(\"描画インターフェース\")%\n" +
            "QUIT\n";

        var vfs = new CompilerMemoryVfs();
        vfs.AddFile("ERB/MAIN.ERB", source);
        CompilationResult compiled = CompilerTestHarness.Compile(vfs, throwOnError: true);

        // TEXTRENDERER (default) + ButtonWrap = true
        using var textRendererEngine = new EmueraEngine(
            compiled.Module,
            vfs,
            new RuntimeOptions { TextDrawingMode = TextDrawingMode.TEXTRENDERER, ButtonWrap = true });
        Assert.False(textRendererEngine.ConsoleBuffer.ExpandTabs);
        Assert.True(textRendererEngine.ConsoleBuffer.ButtonWrap);
        textRendererEngine.StartSystemFlow();
        textRendererEngine.Step(10_000);
        Assert.Equal("          |TEXTRENDERER", textRendererEngine.ConsoleBuffer.PhysicalLines[0].PlainText);

        // GRAPHICS mode expands the two tabs to 16 spaces
        using var graphicsEngine = new EmueraEngine(
            compiled.Module,
            vfs,
            new RuntimeOptions { TextDrawingMode = TextDrawingMode.GRAPHICS, ButtonWrap = false });
        Assert.True(graphicsEngine.ConsoleBuffer.ExpandTabs);
        Assert.False(graphicsEngine.ConsoleBuffer.ButtonWrap);
        graphicsEngine.StartSystemFlow();
        graphicsEngine.Step(10_000);
        Assert.Equal(new string(' ', 26) + "|GRAPHICS", graphicsEngine.ConsoleBuffer.PhysicalLines[0].PlainText);
    }

    /// <summary>
    /// Builds a valid, standards-compliant minimal TrueType (.ttf) binary in memory
    /// containing full-em square block glyphs (0..1000 x 0..1000, ascent=1000, descent=0, advanceWidth=1000)
    /// for ASCII 'A' (U+0041) and Full Block '█' (U+2588).
    /// </summary>
    internal static class MinimalSquareTileTtfBuilder
    {
        public static byte[] Build(string familyName)
        {
            const ushort upem = 1000;
            // Glyph 0 (.notdef): empty; Glyph 1 (solid full-em square 0..1000 x 0..1000)
            byte[] glyph1 = BuildSquareGlyph(0, 0, 1000, 1000);

            var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["OS/2"] = BuildOs2(upem),
                ["cmap"] = BuildCmapFormat4([(0x0041, 1), (0x2588, 1)]),
                ["glyf"] = glyph1,
                ["head"] = BuildHead(upem),
                ["hhea"] = BuildHhea(upem, numHMetrics: 2),
                ["hmtx"] = BuildHmtx(upem),
                ["loca"] = BuildLocaShort(glyph1.Length),
                ["maxp"] = BuildMaxp(numGlyphs: 2),
                ["name"] = BuildName(familyName),
                ["post"] = BuildPost(),
            };

            return AssembleSfnt(tables);
        }

        private static byte[] BuildSquareGlyph(short xMin, short yMin, short xMax, short yMax)
        {
            // Simple glyph: 1 contour, 4 on-curve points: (0,0) -> (0,1000) -> (1000,1000) -> (1000,0) (clockwise outer)
            using var ms = new MemoryStream();
            WriteInt16BE(ms, 1);    // numberOfContours
            WriteInt16BE(ms, xMin);
            WriteInt16BE(ms, yMin);
            WriteInt16BE(ms, xMax);
            WriteInt16BE(ms, yMax);
            WriteUInt16BE(ms, 3);   // endPtsOfContours[0] = 3
            WriteUInt16BE(ms, 0);   // instructionLength = 0
            // Flags (4 points): bit0 = onCurve (0x01), x/y 16-bit deltas (no short/same bits)
            ms.WriteByte(0x01);
            ms.WriteByte(0x01);
            ms.WriteByte(0x01);
            ms.WriteByte(0x01);
            // X deltas: 0, 0, +1000, 0
            WriteInt16BE(ms, 0);
            WriteInt16BE(ms, 0);
            WriteInt16BE(ms, 1000);
            WriteInt16BE(ms, 0);
            // Y deltas: 0, +1000, 0, -1000
            WriteInt16BE(ms, 0);
            WriteInt16BE(ms, 1000);
            WriteInt16BE(ms, 0);
            WriteInt16BE(ms, -1000);

            // Pad to 4-byte boundary so loca short offsets (div 2) are exact
            while ((ms.Length % 4) != 0) ms.WriteByte(0);
            return ms.ToArray();
        }

        private static byte[] BuildHead(ushort upem)
        {
            byte[] b = new byte[54];
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0, 4), 0x00010000u); // version 1.0
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4, 4), 0x00010000u); // fontRevision
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12, 4), 0x5F0F3CF5u); // magicNumber
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(16, 2), (ushort)0x000B); // flags
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(18, 2), upem);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(36, 2), 0);    // xMin
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(38, 2), 0);    // yMin
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(40, 2), 1000); // xMax
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(42, 2), 1000); // yMax
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(46, 2), 8);   // lowestRecPPEM
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(48, 2), 2);    // fontDirectionHint
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(50, 2), 0);    // indexToLocFormat = short
            return b;
        }

        private static byte[] BuildHhea(ushort upem, ushort numHMetrics)
        {
            byte[] b = new byte[36];
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0, 4), 0x00010000u);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(4, 2), (short)upem); // Ascender = 1000
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(6, 2), 0);           // Descender = 0
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(8, 2), 0);           // LineGap = 0
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(10, 2), upem);      // advanceWidthMax
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(16, 2), (short)upem); // xMaxExtent
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(18, 2), 1);          // caretSlopeRise
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(34, 2), numHMetrics);
            return b;
        }

        private static byte[] BuildMaxp(ushort numGlyphs)
        {
            byte[] b = new byte[32];
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0, 4), 0x00010000u);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4, 2), numGlyphs);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6, 2), 4); // maxPoints
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8, 2), 1); // maxContours
            return b;
        }

        private static byte[] BuildOs2(ushort upem)
        {
            byte[] b = new byte[78];
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0, 2), 1);      // version 1
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(2, 2), (short)upem); // xAvgCharWidth
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4, 2), 400);    // usWeightClass
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6, 2), 5);      // usWidthClass
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(62, 2), 0x0040); // fsSelection = REGULAR
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(64, 2), 0x0041); // usFirstCharIndex
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(66, 2), 0x2588); // usLastCharIndex
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(68, 2), (short)upem); // sTypoAscender
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(70, 2), 0);           // sTypoDescender
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(72, 2), 0);           // sTypoLineGap
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(74, 2), upem);       // usWinAscent
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(76, 2), 0);          // usWinDescent
            return b;
        }

        private static byte[] BuildHmtx(ushort advance)
        {
            byte[] b = new byte[8];
            // glyph 0
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0, 2), advance);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(2, 2), 0);
            // glyph 1
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4, 2), advance);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(6, 2), 0);
            return b;
        }

        private static byte[] BuildLocaShort(int glyph1Len)
        {
            // 3 entries (numGlyphs + 1): glyph 0 at [0..0], glyph 1 at [0..glyph1Len]
            byte[] b = new byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0, 2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2, 2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4, 2), (ushort)(glyph1Len / 2));
            return b;
        }

        private static byte[] BuildCmapFormat4((ushort Code, ushort Gid)[] mappings)
        {
            // Segments: one per code point + terminal 0xFFFF
            int segCount = mappings.Length + 1;
            ushort segCountX2 = (ushort)(segCount * 2);
            int subtableLen = 14 + (segCount * 8) + 2;

            using var ms = new MemoryStream();
            // cmap header
            WriteUInt16BE(ms, 0); // version
            WriteUInt16BE(ms, 1); // numTables
            WriteUInt16BE(ms, 3); // platformID = Windows
            WriteUInt16BE(ms, 1); // encodingID = Unicode BMP
            WriteUInt32BE(ms, 12); // offset to subtable

            // Format 4 subtable
            WriteUInt16BE(ms, 4);
            WriteUInt16BE(ms, (ushort)subtableLen);
            WriteUInt16BE(ms, 0);
            WriteUInt16BE(ms, segCountX2);
            WriteUInt16BE(ms, 4); // searchRange
            WriteUInt16BE(ms, 1); // entrySelector
            WriteUInt16BE(ms, (ushort)(segCountX2 - 4)); // rangeShift

            // endCode
            foreach (var (code, _) in mappings) WriteUInt16BE(ms, code);
            WriteUInt16BE(ms, 0xFFFF);
            WriteUInt16BE(ms, 0); // reservedPad

            // startCode
            foreach (var (code, _) in mappings) WriteUInt16BE(ms, code);
            WriteUInt16BE(ms, 0xFFFF);

            // idDelta
            foreach (var (code, gid) in mappings) WriteInt16BE(ms, unchecked((short)(gid - code)));
            WriteInt16BE(ms, 1);

            // idRangeOffset
            for (int i = 0; i < segCount; i++) WriteUInt16BE(ms, 0);

            return ms.ToArray();
        }

        private static byte[] BuildName(string familyName)
        {
            (ushort NameId, string Value)[] entries =
            [
                (1, familyName),           // Font Family
                (2, "Regular"),            // Subfamily
                (4, familyName),           // Full Name
                (6, familyName.Replace(" ", string.Empty)), // PostScript name
            ];

            using var strBuf = new MemoryStream();
            var records = new List<(ushort NameId, ushort Length, ushort Offset)>();
            foreach (var (id, val) in entries)
            {
                byte[] utf16be = Encoding.BigEndianUnicode.GetBytes(val);
                records.Add((id, (ushort)utf16be.Length, (ushort)strBuf.Length));
                strBuf.Write(utf16be);
            }

            ushort stringOffset = (ushort)(6 + (records.Count * 12));
            using var ms = new MemoryStream();
            WriteUInt16BE(ms, 0); // format
            WriteUInt16BE(ms, (ushort)records.Count);
            WriteUInt16BE(ms, stringOffset);
            foreach (var (id, len, off) in records)
            {
                WriteUInt16BE(ms, 3);      // platformID = Windows
                WriteUInt16BE(ms, 1);      // encodingID = Unicode BMP
                WriteUInt16BE(ms, 0x0409); // languageID = en-US
                WriteUInt16BE(ms, id);
                WriteUInt16BE(ms, len);
                WriteUInt16BE(ms, off);
            }

            ms.Write(strBuf.ToArray());
            return ms.ToArray();
        }

        private static byte[] BuildPost()
        {
            byte[] b = new byte[32];
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0, 4), 0x00030000u); // format 3.0
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12, 4), 1u);         // isFixedPitch = 1
            return b;
        }

        private static byte[] AssembleSfnt(SortedDictionary<string, byte[]> tables)
        {
            ushort numTables = (ushort)tables.Count;
            ushort searchRange = 1;
            ushort entrySelector = 0;
            while ((searchRange * 2) <= numTables)
            {
                searchRange *= 2;
                entrySelector++;
            }

            searchRange *= 16;
            ushort rangeShift = (ushort)((numTables * 16) - searchRange);

            int headerSize = 12 + (numTables * 16);
            int currentOffset = headerSize;
            var records = new List<(string Tag, uint Checksum, uint Offset, uint Length, byte[] Data)>();

            foreach (KeyValuePair<string, byte[]> kv in tables)
            {
                uint csum = ComputeTableChecksum(kv.Value);
                records.Add((kv.Key, csum, (uint)currentOffset, (uint)kv.Value.Length, kv.Value));
                currentOffset += (kv.Value.Length + 3) & ~3;
            }

            byte[] sfnt = new byte[currentOffset];
            BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(0, 4), 0x00010000u);
            BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(4, 2), numTables);
            BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(6, 2), searchRange);
            BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(8, 2), entrySelector);
            BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(10, 2), rangeShift);

            int recOff = 12;
            int headOffset = -1;
            foreach (var rec in records)
            {
                Encoding.ASCII.GetBytes(rec.Tag, sfnt.AsSpan(recOff, 4));
                BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(recOff + 4, 4), rec.Checksum);
                BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(recOff + 8, 4), rec.Offset);
                BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(recOff + 12, 4), rec.Length);
                recOff += 16;

                Buffer.BlockCopy(rec.Data, 0, sfnt, (int)rec.Offset, rec.Data.Length);
                if (rec.Tag == "head")
                {
                    headOffset = (int)rec.Offset;
                }
            }

            if (headOffset >= 0)
            {
                uint wholeChecksum = ComputeTableChecksum(sfnt);
                BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(headOffset + 8, 4), unchecked(0xB1B0AFBAu - wholeChecksum));
            }

            return sfnt;
        }

        private static uint ComputeTableChecksum(byte[] data)
        {
            uint sum = 0;
            int padded = (data.Length + 3) & ~3;
            for (int i = 0; i < padded; i += 4)
            {
                uint v = 0;
                for (int j = 0; j < 4; j++)
                {
                    v <<= 8;
                    if (i + j < data.Length)
                    {
                        v |= data[i + j];
                    }
                }

                sum = unchecked(sum + v);
            }

            return sum;
        }

        private static void WriteUInt16BE(Stream s, ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, v);
            s.Write(b);
        }

        private static void WriteInt16BE(Stream s, short v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(b, v);
            s.Write(b);
        }

        private static void WriteUInt32BE(Stream s, uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            s.Write(b);
        }
    }
}
