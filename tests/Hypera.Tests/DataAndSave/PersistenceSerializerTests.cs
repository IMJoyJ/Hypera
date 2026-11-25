using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Hypera.Bytecode;
using Hypera.Configuration;
using Hypera.Diagnostics;
using Hypera.Persistence;
using Xunit;

namespace Hypera.Tests.DataAndSave;

public sealed class PersistenceSerializerTests
{
    static PersistenceSerializerTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public void BytecodeSerializer_FullModuleRoundtrip_PreservesAllSections()
    {
        var sourceMapBuilder = new SourceMapBuilder();
        int f0 = sourceMapBuilder.GetOrAddFile("erb/System.erb", "System.erb");
        int f1 = sourceMapBuilder.GetOrAddFile("erb/Events.erb", "Events.erb");
        sourceMapBuilder.AddMapping(0, f0, 10);
        sourceMapBuilder.AddMapping(2, f1, 42);

        ImmutableArray<Instruction> instructions =
        [
            Instruction.FromWideImmediate(OpCode.LoadIntImm, 1, 0x0123_4567_89AB_CDEF),
            Instruction.Reg2(OpCode.AddInt, 2, 1, immediate: 5, aux: 3),
            Instruction.Simple(OpCode.ReturnInt, immediate: 0),
        ];

        var original = new BytecodeModule
        {
            Header = BytecodeModuleHeader.Create(3, 1, 2, 2, 1, flags: 0x0003),
            CompilerOptions = new CompilerOptions
            {
                IgnoreCase = false,
                CompatiCALLNAME = true,
                UseScopedVariableInstruction = true,
                ReplaceContinuationBR = "<br>",
                SifBlankLineBehavior = SifBlankLineBehavior.EramakerBlankLineConsumesCondition,
            },
            ReplaceConfig = new ReplaceConfiguration
            {
                MoneyLabel = "G",
                MoneyFirst = false,
                BarChar1 = '=',
                BarChar2 = '_',
                StainDefault = [1L, 2L, 3L],
                PbandDef = 9L,
            },
            Instructions = instructions,
            IntConstants = [42L, -9_000_000_000_000L],
            StringConstants = ["Hello", "日本語テスト"],
            Functions =
            [
                new FunctionDescriptor(
                    FunctionId: 0,
                    Name: "CALC_DAMAGE",
                    EntryPc: 0,
                    InstructionCount: 3,
                    IntRegisterCount: 8,
                    StrRegisterCount: 4,
                    IsMethodFunction: true,
                    MethodReturnKind: VariableValueKind.Int,
                    EventFlags: EventDirectiveFlags.Pri | EventDirectiveFlags.Single,
                    Parameters:
                    [
                        new FunctionParameterDescriptor("ATK", VariableValueKind.Int, false, 0, true, 100L, null),
                        new FunctionParameterDescriptor("TAG", VariableValueKind.Str, true, 1, true, 0L, "DEFAULT"),
                    ],
                    LocalVariables:
                    [
                        new VariableSlotDescriptor(10, "TEMP_ARR", VariableValueKind.Int, VariableScopeKind.FunctionDynamic, [16]),
                    ]),
            ],
            EventGroups =
            [
                new EventFunctionGroup("EVENTFIRST", [new EventHandlerEntry(0, EventDirectiveFlags.Only, 0)]),
            ],
            StaticVariables =
            [
                new VariableSlotDescriptor(0, "CUSTOM_SAVED", VariableValueKind.Int, VariableScopeKind.GlobalDimSavedata, [10, 20], IsSavedata: true),
            ],
            SourceMap = sourceMapBuilder.Build(),
        };

        byte[] bytes = BytecodeSerializer.Serialize(original);
        BytecodeModule loaded = BytecodeSerializer.Deserialize(bytes);

        Assert.True(loaded.Header.IsValid);
        Assert.Equal(original.Header.Flags, loaded.Header.Flags);
        Assert.Equal(original.CompilerOptions, loaded.CompilerOptions);
        Assert.Equal(original.ReplaceConfig.MoneyLabel, loaded.ReplaceConfig.MoneyLabel);
        Assert.Equal(original.ReplaceConfig.MoneyFirst, loaded.ReplaceConfig.MoneyFirst);
        Assert.Equal(original.ReplaceConfig.BarChar1, loaded.ReplaceConfig.BarChar1);
        Assert.Equal(original.ReplaceConfig.StainDefault, loaded.ReplaceConfig.StainDefault);
        Assert.Equal(original.ReplaceConfig.PbandDef, loaded.ReplaceConfig.PbandDef);

        Assert.Equal(original.Instructions.Length, loaded.Instructions.Length);
        Assert.Equal(0x0123_4567_89AB_CDEF, loaded.Instructions[0].WideImmediate);
        Assert.Equal(original.Instructions[1], loaded.Instructions[1]);
        Assert.Equal<long>(original.IntConstants, loaded.IntConstants);
        Assert.Equal<string>(original.StringConstants, loaded.StringConstants);

        FunctionDescriptor fn = Assert.Single(loaded.Functions);
        Assert.Equal("CALC_DAMAGE", fn.Name);
        Assert.Equal(VariableValueKind.Int, fn.MethodReturnKind);
        Assert.Equal(2, fn.Parameters.Length);
        Assert.Equal("DEFAULT", fn.Parameters[1].DefaultStrValue);
        Assert.Single(fn.LocalVariables);
        Assert.Equal<int>([16], fn.LocalVariables[0].Dimensions);

        EventFunctionGroup ev = Assert.Single(loaded.EventGroups);
        Assert.Equal("EVENTFIRST", ev.EventName);
        Assert.Equal(EventDirectiveFlags.Only, ev.OrderedHandlers[0].Flags);

        VariableSlotDescriptor sv = Assert.Single(loaded.StaticVariables);
        Assert.Equal("CUSTOM_SAVED", sv.Name);
        Assert.True(sv.IsSavedata);
        Assert.Equal<int>([10, 20], sv.Dimensions);

        Assert.True(loaded.SourceMap.TryLookup(2, out SourceLocation loc));
        Assert.Equal("erb/Events.erb", loc.VirtualPath);
        Assert.Equal(42, loc.LineNo);
    }

    [Theory]
    [InlineData(EraSaveFormat.Binary)]
    [InlineData(EraSaveFormat.GZipBinary)]
    [InlineData(EraSaveFormat.Text)]
    public void EraSaveSerializer_RoundtripsAllFormats_WithCharactersRleArraysAndVarExt(EraSaveFormat format)
    {
        // Sparse 1D array with large zero runs to exercise Ebdb RLE tokens
        long[] flagArray = new long[200];
        flagArray[0] = 5;           // inline byte (<= 0xCF)
        flagArray[1] = 1500;        // Int16
        flagArray[50] = -123456;    // Int32
        flagArray[120] = 9_876_543_210_123L; // Int64

        long[,] matrix2d = new long[6, 5];
        matrix2d[2, 3] = 777;
        matrix2d[5, 0] = -42;

        string[] saveStr = new string[10];
        Array.Fill(saveStr, string.Empty);
        saveStr[0] = "Chapter 1";
        saveStr[7] = "セーブ文字列\n改行付き";

        var chara = new EraCharacterSaveData
        {
            No = 1,
            IsAssi = 1,
            Name = "アリス",
            CallName = "アリスちゃん",
            NickName = "少女",
            MasterName = "マスター",
        };
        long[] baseArr = new long[20];
        baseArr[0] = 1500;
        baseArr[1] = 950;
        chara.BuiltinVariables["BASE"] = EraSaveValue.FromIntArray(baseArr);
        chara.UserVariables["CUSTOM_CHARA_SCORE"] = EraSaveValue.FromInt(999);

        var dt = new EraDataTableSnapshot
        {
            Columns =
            [
                new EraDataColumnDefinition("Id", "Int64"),
                new EraDataColumnDefinition("Title", "String"),
            ],
            Rows =
            [
                [1L, "Sword"],
                [2L, null],
            ],
        };

        var snapshot = new EraSaveSnapshot
        {
            Header = new EraSaveHeader(
                ScriptUniqueCode: 987654321L,
                ScriptVersion: 204L,
                SaveDataText: "Day 12 - Forest Camp",
                FileType: EraSaveFileType.Normal),
            Characters = [chara],
            Variables = new Dictionary<string, EraSaveValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["DAY"] = EraSaveValue.FromInt(12),
                ["FLAG"] = EraSaveValue.FromIntArray(flagArray),
                ["DA"] = EraSaveValue.FromIntArray2D(matrix2d),
                ["SAVESTR"] = EraSaveValue.FromStrArray(saveStr),
            },
            VarExtVariables = new Dictionary<string, EraSaveValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["QUEST_MAP"] = EraSaveValue.FromMap(new Dictionary<string, string> { ["q1"] = "done", ["q2"] = "active" }),
                ["DIALOG_XML"] = EraSaveValue.FromXml("<root><node id=\"1\">ok</node></root>"),
            },
        };

        if (format != EraSaveFormat.Text)
        {
            long[,,] cube3d = new long[3, 3, 3];
            cube3d[2, 1, 2] = 314159;
            snapshot.Variables["TA"] = EraSaveValue.FromIntArray3D(cube3d);
            snapshot.VarExtVariables["ITEMS_DT"] = EraSaveValue.FromDataTable(dt);
        }

        byte[] serialized = EraSaveSerializer.Save(snapshot, format);

        // Verify exact wire magic for Binary & GZipBinary
        Assert.Equal(format, EraSaveSerializer.DetectFormat(serialized));
        if (format == EraSaveFormat.Binary)
        {
            Assert.Equal(EraSaveSerializer.BinaryHeaderMagic, BinaryPrimitives.ReadUInt64LittleEndian(serialized.AsSpan(0, 8)));
        }
        else if (format == EraSaveFormat.GZipBinary)
        {
            Assert.Equal(EraSaveSerializer.ZipHeaderMagic, BinaryPrimitives.ReadUInt64LittleEndian(serialized.AsSpan(0, 8)));
        }

        // Fast header peek
        EraSaveHeader peeked = EraSaveSerializer.PeekHeader(serialized);
        Assert.Equal(987654321L, peeked.ScriptUniqueCode);
        Assert.Equal(204L, peeked.ScriptVersion);
        Assert.Equal("Day 12 - Forest Camp", peeked.SaveDataText);

        // Full load
        EraSaveSnapshot loaded = EraSaveSerializer.Load(serialized);
        Assert.Equal(987654321L, loaded.Header.ScriptUniqueCode);
        Assert.Equal(204L, loaded.Header.ScriptVersion);
        Assert.Equal("Day 12 - Forest Camp", loaded.Header.SaveDataText);

        EraCharacterSaveData loadedChara = Assert.Single(loaded.Characters);
        Assert.Equal(1L, loadedChara.No);
        Assert.Equal(1L, loadedChara.IsAssi);
        Assert.Equal("アリス", loadedChara.Name);
        Assert.Equal("アリスちゃん", loadedChara.CallName);
        Assert.Equal(1500L, loadedChara.BuiltinVariables["BASE"].AsIntArray()[0]);
        Assert.Equal(999L, loadedChara.UserVariables["CUSTOM_CHARA_SCORE"].AsInt());

        Assert.Equal(12L, loaded.Variables["DAY"].AsInt());
        long[] loadedFlag = loaded.Variables["FLAG"].AsIntArray();
        Assert.Equal(200, loadedFlag.Length);
        Assert.Equal(5L, loadedFlag[0]);
        Assert.Equal(1500L, loadedFlag[1]);
        Assert.Equal(0L, loadedFlag[25]);
        Assert.Equal(-123456L, loadedFlag[50]);
        Assert.Equal(9_876_543_210_123L, loadedFlag[120]);

        long[,] loaded2d = loaded.Variables["DA"].AsIntArray2D();
        Assert.Equal(777L, loaded2d[2, 3]);
        Assert.Equal(-42L, loaded2d[5, 0]);
        Assert.Equal(0L, loaded2d[0, 0]);

        string[] loadedSaveStr = loaded.Variables["SAVESTR"].AsStrArray();
        Assert.Equal("Chapter 1", loadedSaveStr[0]);
        Assert.Equal(string.Empty, loadedSaveStr[3]);
        Assert.Equal("セーブ文字列\n改行付き", loadedSaveStr[7]);

        Dictionary<string, string> loadedMap = loaded.VarExtVariables["QUEST_MAP"].AsMap();
        Assert.Equal("done", loadedMap["q1"]);
        Assert.Equal("active", loadedMap["q2"]);
        Assert.Equal("<root><node id=\"1\">ok</node></root>", loaded.VarExtVariables["DIALOG_XML"].AsXml());

        if (format != EraSaveFormat.Text)
        {
            long[,,] loaded3d = loaded.Variables["TA"].AsIntArray3D();
            Assert.Equal(314159L, loaded3d[2, 1, 2]);
            Assert.Equal(0L, loaded3d[0, 0, 0]);

            EraDataTableSnapshot loadedDt = loaded.VarExtVariables["ITEMS_DT"].AsDataTable();
            Assert.Equal(2, loadedDt.Columns.Count);
            Assert.Equal(2, loadedDt.Rows.Count);
            Assert.Equal(1L, loadedDt.Rows[0][0]);
            Assert.Equal("Sword", loadedDt.Rows[0][1]);
            Assert.Null(loadedDt.Rows[1][1]);
        }
    }

    [Fact]
    public void EraSaveSerializer_GlobalSaveRoundtrip_WorksInTextAndBinary()
    {
        var globalSnapshot = new EraSaveSnapshot
        {
            Header = new EraSaveHeader(1001L, 50L, "GLOBAL", EraSaveFileType.Global),
            Variables = new Dictionary<string, EraSaveValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["GLOBAL"] = EraSaveValue.FromIntArray([10L, 0L, 0L, 99L]),
                ["GLOBALS"] = EraSaveValue.FromStrArray(["UNLOCKED", string.Empty, "TRUE_END"]),
            },
        };

        foreach (EraSaveFormat fmt in new[] { EraSaveFormat.Text, EraSaveFormat.Binary, EraSaveFormat.GZipBinary })
        {
            byte[] bytes = EraSaveSerializer.Save(globalSnapshot, fmt);
            EraSaveSnapshot loaded = EraSaveSerializer.Load(bytes);
            Assert.Equal(EraSaveFileType.Global, loaded.Header.FileType);
            Assert.Equal(99L, loaded.Variables["GLOBAL"].AsIntArray()[3]);
            Assert.Equal("TRUE_END", loaded.Variables["GLOBALS"].AsStrArray()[2]);
        }
    }
}
