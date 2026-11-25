using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Runtime;
using Xunit;

namespace Hypera.Tests.Runtime;

public sealed class SubsystemsAndStringOpsTests
{
    [Fact]
    public void SfmtRandom_DeterministicDumpInitRandAndCompatiRand15Bit()
    {
        var rng1 = new SfmtRandom(123456789L);
        var rng2 = new SfmtRandom(123456789L);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(rng1.NextUInt64(), rng2.NextUInt64());
        }

        // DUMPRAND / INITRAND (RANDDATA 625-element snapshot) exact state restoration
        long[] state = new long[SfmtRandom.RandDataLength];
        rng1.DumpRand(state);

        long expectedNext1 = rng1.NextInt64(1_000_000_000L);
        long expectedNext2 = rng1.NextInt64(1_000_000_000L);

        var restored = new SfmtRandom(999L);
        restored.InitRand(state);
        Assert.Equal(expectedNext1, restored.NextInt64(1_000_000_000L));
        Assert.Equal(expectedNext2, restored.NextInt64(1_000_000_000L));

        // CompatiRAND: eramaker 15-bit (NextInt64(32768) % |X|), X == 0 returns 0
        var compatiA = new SfmtRandom(42L);
        var compatiB = new SfmtRandom(42L);
        Assert.Equal(0L, compatiA.NextCompatiRand(0));
        for (int i = 0; i < 50; i++)
        {
            long expected15 = compatiB.NextInt64(32768) % 50000L;
            long actualCompati = compatiA.NextCompatiRand(-50000L);
            Assert.Equal(expected15, actualCompati);
            Assert.InRange(actualCompati, 0L, 32767L);
        }
    }

    [Fact]
    public void EastAsianStringOps_CrossPlatformWidthConversionKanaAndByteSubstrings()
    {
        Assert.Equal("ＡＢＣ１２３　ガパヴ", EastAsianStringOps.ToFull("ABC123 ｶﾞﾊﾟｳﾞ"));
        Assert.Equal("ABC123 ｶﾞﾊﾟ", EastAsianStringOps.ToHalf("ＡＢＣ１２３　ガパ"));

        Assert.Equal("あいうえおゔ", EastAsianStringOps.ToHiragana("アイウエオヴ"));
        Assert.Equal("アイウエオヴ", EastAsianStringOps.ToKatakana("あいうえおゔ"));

        const string mixed = "AB霊夢ｶ";
        Assert.Equal(2 + 4 + 1, EastAsianStringOps.GetStrlenS(mixed, codePage: 932));
        Assert.Equal(5, EastAsianStringOps.GetStrlenSU(mixed));

        Assert.Equal("霊夢", EastAsianStringOps.SubstringS(mixed, 2, 4, codePage: 932));
        Assert.Equal("B霊", EastAsianStringOps.SubstringU(mixed, 1, 2));

        Assert.Equal("[***..]", EastAsianStringOps.BarStr(value: 60, max: 100, length: 5));
        Assert.Equal("$1,234", EastAsianStringOps.MoneyStr(1234, format: "#,0", moneyLabel: "$", moneyFirst: true));
        Assert.Equal("ff", EastAsianStringOps.ConvertRadix(255, 16));

        string[] splitDest = new string[8];
        int parts = EastAsianStringOps.Split("one,two,three", ",", splitDest);
        Assert.Equal(3, parts);
        Assert.Equal("two", splitDest[1]);
        Assert.Equal("one|two|three", EastAsianStringOps.StrJoin(splitDest.AsSpan(0, 3), "|"));
    }

    [Fact]
    public void DataTableStore_CrudFilterSortSelectAndXmlRoundTrip()
    {
        var dt = new DataTableStore();
        Assert.Equal(1L, dt.Create("Items"));
        Assert.Equal(1L, dt.Exists("Items"));
        Assert.Equal(1L, dt.AddColumnByCode("Items", "name", 5));
        Assert.Equal(1L, dt.AddColumnByCode("Items", "power", 3));
        Assert.Equal(1L, dt.AddColumnByCode("Items", "tier", 5));

        long id1 = dt.AddRow("Items", [("name", "Excalibur"), ("power", 95L), ("tier", "SSR")]);
        long id2 = dt.AddRow("Items", [("name", "BronzeSword"), ("power", 20L), ("tier", "R")]);
        long id3 = dt.AddRow("Items", [("name", "Gungnir"), ("power", 88L), ("tier", "SSR")]);
        Assert.True(id1 > 0 && id2 > id1 && id3 > id2);
        Assert.Equal(3L, dt.RowLength("Items"));

        long[] resultSpan = new long[16];
        long matched = dt.Select("Items", "tier = 'SSR' and power >= 80", "power ASC", resultSpan, writeCountAtIndexZero: true);
        Assert.Equal(2L, matched);
        Assert.Equal(2L, resultSpan[0]);
        Assert.Equal(id3, resultSpan[1]);
        Assert.Equal(id1, resultSpan[2]);

        Assert.Equal("Gungnir", dt.GetCellStr("Items", id3, "name", asId: true));
        Assert.Equal(95L, dt.GetCellInt("Items", id1, "power", asId: true));

        string dataXml = dt.ToXml("Items", out string schemaXml);
        var dt2 = new DataTableStore();
        Assert.Equal(1L, dt2.FromXml("RestoredItems", schemaXml, dataXml));
        Assert.Equal(3L, dt2.RowLength("RestoredItems"));
        Assert.Equal("Excalibur", dt2.GetCellStr("RestoredItems", id1, "name", asId: true));
    }

    [Fact]
    public void XmlStoreAndVmXmlOp_XPathQueriesMutationsAndInlineRefXml()
    {
        var xmls = new XmlStore();
        Assert.Equal(1L, xmls.CreateDocument("1", "<party><member id='10'><name>Reimu</name></member></party>"));
        Assert.Equal(1L, xmls.Exists("1"));

        string[] outValues = new string[8];
        Assert.Equal(1L, xmls.Get("1", isDocumentKey: true, "/party/member/name", outValues, outputStyle: 0));
        Assert.Equal("Reimu", outValues[0]);

        Assert.Equal(1L, xmls.AddNode("1", isDocumentKey: true, "/party", "<member id='20'><name>Marisa</name></member>", out _, method: 0, setAllNodes: false));
        Assert.Equal(1L, xmls.AddAttribute("1", isDocumentKey: true, "/party/member[@id='20']", "role", "mage", out _, method: 0, setAllNodes: false));
        Assert.Equal(2L, xmls.Get("1", isDocumentKey: true, "/party/member/name", outValues, outputStyle: 0));
        Assert.Equal("Marisa", outValues[1]);

        var mb = new TestModuleBuilder();
        int xmlVarSlot = mb.AddStaticVar(1300, "INLINE_XML", VariableValueKind.Str, VariableScopeKind.GlobalDim, 1);
        int xpathConst = mb.AddStrConst("/root/val");
        int newValConst = mb.AddStrConst("UpdatedValue");

        mb.AddFunction("MUTATE_INLINE_XML", b =>
        {
            long packedRef = BytecodeAbi.PackByRefTarget(xmlVarSlot, VariableSlotOrigin.ModuleStatic);
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: packedRef));
            b.Emit(Instruction.Reg1(OpCode.LoadStrConst, a: 1, immediate: xpathConst));
            b.Emit(Instruction.Reg1(OpCode.LoadStrConst, a: 2, immediate: newValConst));
            ushort tags = BytecodeAbi.PackBuiltinArgTags([CallArgTag.ByRef, CallArgTag.Str, CallArgTag.Str]);
            b.Emit(Instruction.Reg4(OpCode.XmlOp, a: 3, b: 0, c: 0, d: 3, immediate: (int)XmlSubOp.Set, aux: tags));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 3));
        });

        using EmueraEngine engine = mb.BuildEngine();
        engine.FastVariables!.SetStr("INLINE_XML", "<root><val>Initial</val></root>", 0);
        engine.PushEntryFrame(engine.Module.Functions[0]);
        Assert.Equal(EngineStepStatus.Completed, engine.Step());
        Assert.Equal(1L, engine.FastVariables.Result);
        Assert.Contains("UpdatedValue", engine.FastVariables.GetStr("INLINE_XML", 0));
    }

    [Fact]
    public void MapStoreAndRegexCache_AllMapOpsAndRegexGroupCapture()
    {
        var maps = new MapStore();
        Assert.Equal(1L, maps.Create("cfg"));
        Assert.Equal(1L, maps.Exists("cfg"));
        Assert.Equal(1L, maps.Set("cfg", "theme", "dark"));
        Assert.Equal(1L, maps.Set("cfg", "lang", "ja-JP"));
        Assert.Equal(2L, maps.Size("cfg"));
        Assert.Equal(1L, maps.Has("cfg", "theme"));
        Assert.Equal("dark", maps.Get("cfg", "theme"));

        string xml = maps.ToXml("cfg");
        var maps2 = new MapStore();
        Assert.Equal(1L, maps2.FromXml("cfg_copy", xml));
        Assert.Equal("ja-JP", maps2.Get("cfg_copy", "lang"));
        Assert.Equal(1L, maps2.Remove("cfg_copy", "lang"));
        Assert.Equal(1L, maps2.Size("cfg_copy"));

        var regexes = new RegexCache();
        string[] groups = new string[8];
        int matchCount = regexes.Match("ID:101-A, ID:202-B", @"ID:(\d+)-([A-Z])", out int groupCount, groups);
        Assert.Equal(2, matchCount);
        Assert.Equal(3, groupCount);
        Assert.Equal("ID:101-A", groups[0]);
        Assert.Equal("101", groups[1]);
        Assert.Equal("A", groups[2]);
        Assert.Equal("202", groups[4]);
    }
}
