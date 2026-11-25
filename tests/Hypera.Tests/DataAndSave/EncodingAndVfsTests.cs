using System.IO.Compression;
using System.Text;
using Hypera.VirtualFs;
using Xunit;

namespace Hypera.Tests.DataAndSave;

public sealed class EncodingAndVfsTests
{
    static EncodingAndVfsTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public void EncodingDetector_DetectsBomsAndStrictUtf8()
    {
        var detector = EncodingDetector.Default;

        byte[] utf8Bom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("こんにちは世界")];
        Assert.Equal("こんにちは世界", detector.Decode(utf8Bom, out var utf8BomRes));
        Assert.Equal(DetectedEncodingKind.Utf8Bom, utf8BomRes.Kind);
        Assert.Equal(3, utf8BomRes.BomLength);

        byte[] utf16Le = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("テスト")];
        Assert.Equal("テスト", detector.Decode(utf16Le, out var utf16LeRes));
        Assert.Equal(DetectedEncodingKind.Utf16LeBom, utf16LeRes.Kind);

        byte[] utf16Be = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("テスト")];
        Assert.Equal("テスト", detector.Decode(utf16Be, out var utf16BeRes));
        Assert.Equal(DetectedEncodingKind.Utf16BeBom, utf16BeRes.Kind);

        byte[] utf8NoBom = Encoding.UTF8.GetBytes("PRINTL こんにちは你好");
        Assert.Equal("PRINTL こんにちは你好", detector.Decode(utf8NoBom, out var utf8Res));
        Assert.Equal(DetectedEncodingKind.Utf8, utf8Res.Kind);
    }

    [Fact]
    public void EncodingDetector_DistinguishesShiftJisAndGb18030()
    {
        var detector = EncodingDetector.Default;

        string jpSample = "; 日本語のシフトJISスクリプト\r\nPRINTL 最初からはじめる\r\nPRINTL ロードしてはじめる\r\n";
        byte[] sjisBytes = Encoding.GetEncoding(932).GetBytes(jpSample);
        string decodedJp = detector.Decode(sjisBytes, out var jpRes);
        Assert.Equal(DetectedEncodingKind.ShiftJisCp932, jpRes.Kind);
        Assert.Equal(jpSample, decodedJp);

        string zhSample = "; 简体中文脚本测试，包含常见汉字和标点符号。\r\nPRINTL 开始新游戏\r\nPRINTL 读取存档进度\r\n";
        byte[] gbBytes = Encoding.GetEncoding("GB18030").GetBytes(zhSample);
        string decodedZh = detector.Decode(gbBytes, out var zhRes);
        Assert.Equal(DetectedEncodingKind.Gb18030, zhRes.Kind);
        Assert.Equal(zhSample, decodedZh);
    }

    [Fact]
    public void HepArchiveVfs_PackAndReadRoundtrip_SupportsCompressionPrebuiltBytecodeAndCrcCheck()
    {
        string repeatedScript = string.Concat(Enumerable.Repeat("PRINTL Hello Hypera Engine\r\n", 100));
        byte[] scriptBytes = Encoding.UTF8.GetBytes(repeatedScript);
        byte[] fakeHeb = [0x48, 0x45, 0x42, 0x43, 1, 0, 0, 0];

        using var ms = new MemoryStream();
        ArchivePackageHeader header = HepPackageWriter.PackEntries(
            [
                ("ERB\\System\\Title.ERB", scriptBytes),
                ("CSV/GameBase.csv", Encoding.UTF8.GetBytes("タイトル,TestGame\n")),
                ("game.heb", fakeHeb),
            ],
            ms,
            compress: true,
            includesPrebuiltBytecode: true);

        Assert.True(header.IsValid);
        Assert.Equal(3u, header.EntryCount);
        Assert.True(header.Flags.HasFlag(ArchivePackageFlags.DeflateCompressed));
        Assert.True(header.Flags.HasFlag(ArchivePackageFlags.IncludesPrebuiltBytecode));

        ms.Position = 0;
        using (var vfs = new HepArchiveVfs(ms, "test.hep", leaveOpen: true))
        {
            Assert.True(vfs.HasPrebuiltBytecode);
            Assert.True(vfs.FileExists("erb/system/title.erb"));
            Assert.True(vfs.DirectoryExists("ERB"));
            Assert.True(vfs.DirectoryExists("erb/system"));
            Assert.Equal(repeatedScript, vfs.ReadAllText("ERB/System/Title.ERB"));
            Assert.Equal(fakeHeb, vfs.ReadAllBytes("game.heb"));

            Assert.True(vfs.TryGetDescriptor("erb/system/title.erb", out var desc));
            Assert.True(desc.IsCompressed);
            Assert.True(desc.StoredByteLength < desc.UncompressedByteLength);

            string[] erbFiles = vfs.EnumerateFiles("erb", "*.erb", recursive: true).ToArray();
            Assert.Single(erbFiles);
            Assert.Equal("ERB/System/Title.ERB", erbFiles[0]);
        }

        // Corrupt a payload byte and confirm CRC32 mismatch throws InvalidDataException
        byte[] corrupted = ms.ToArray();
        corrupted[ArchivePackageHeader.SizeInBytes + 2] ^= 0xFF;
        using var corruptedMs = new MemoryStream(corrupted, writable: false);
        using var corruptedVfs = new HepArchiveVfs(corruptedMs, "corrupted.hep");
        Assert.Throws<InvalidDataException>(() => corruptedVfs.ReadAllBytes("erb/system/title.erb"));
    }

    [Fact]
    public void ZipArchiveVfs_ReadAndEnumerateRoundtrip()
    {
        using var zipStream = new MemoryStream();
        using (var writer = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry e1 = writer.CreateEntry("CSV/Abl.csv");
            using (var sw = new StreamWriter(e1.Open(), Encoding.UTF8))
            {
                sw.Write("0,従順\n1,欲望\n");
            }

            ZipArchiveEntry e2 = writer.CreateEntry("ERB/Main.erb");
            using (var sw = new StreamWriter(e2.Open(), Encoding.UTF8))
            {
                sw.Write("@SYSTEM_TITLE\nPRINTL OK\n");
            }
        }

        zipStream.Position = 0;
        using var vfs = new ZipArchiveVfs(zipStream, "game.zip", leaveOpen: true);
        Assert.Equal(2, vfs.EntryCount);
        Assert.True(vfs.DirectoryExists("csv"));
        Assert.True(vfs.FileExists("csv\\abl.csv"));
        Assert.Contains("従順", vfs.ReadAllText("CSV/Abl.csv"));
        Assert.Single(vfs.EnumerateFiles("erb", "*.erb", recursive: false));
    }

    [Fact]
    public void WritableSaveDirectory_EnforcesSandboxAndValidExtensionWhitelist()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hypera_savetest_{Guid.NewGuid():N}");
        try
        {
            var saveDir = new WritableSaveDirectory(tempDir, validTextExtensions: ["txt", ".log"]);

            Assert.Equal("sav/save03.sav", saveDir.GetSaveSlotVirtualPath(3, useSaveFolder: true));
            Assert.Equal("save03.sav", saveDir.GetSaveSlotVirtualPath(3, useSaveFolder: false));
            Assert.Equal("sav/global.sav", saveDir.GetGlobalSaveVirtualPath(useSaveFolder: true));
            Assert.Equal("dat/var_score.dat", saveDir.GetDatVirtualPath("score", isCharaVar: false));
            Assert.Equal("dat/chara_hero.dat", saveDir.GetDatVirtualPath("hero", isCharaVar: true));

            saveDir.SaveText("notes/journal.txt", "Hello SaveText!");
            Assert.True(saveDir.FileExists("notes/journal.txt"));
            Assert.Equal("Hello SaveText!", saveDir.LoadText("notes/journal.txt"));

            // Disallowed extension
            Assert.Throws<UnauthorizedAccessException>(() => saveDir.SaveText("evil.exe", "bad"));
            Assert.Throws<UnauthorizedAccessException>(() => saveDir.LoadText("evil.exe"));

            // Path traversal escape
            Assert.Throws<InvalidOperationException>(() => saveDir.ResolveConfinedPath("../outside.txt"));
            Assert.Throws<UnauthorizedAccessException>(() => saveDir.ResolveConfinedPath(Path.Combine(tempDir, "abs.txt")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
