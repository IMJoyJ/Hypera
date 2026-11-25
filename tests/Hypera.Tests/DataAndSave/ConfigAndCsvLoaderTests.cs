using System.Text;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.VirtualFs;
using Xunit;

namespace Hypera.Tests.DataAndSave;

public sealed class ConfigAndCsvLoaderTests
{
    [Fact]
    public void ConfigLoader_ThreeTierCascade_RespectsFixedLocksAndMultilingualKeys()
    {
        string defaultConfig = """
            ; Tier 1: Japanese canonical keys
            ウィンドウ幅:900
            ウィンドウ高さ:640
            フォントサイズ:20
            セーブデータをバイナリ形式で保存する:YES
            ERD機能を利用する:YES
            内部で使用する東アジア言語:JAPANESE
            """;

        string userConfig = """
            ; Tier 2: Chinese aliases & English labels overriding defaults
            窗口宽度:1280
            Window height:720
            字体大小:22
            使用SP角色:YES
            Compress save data:YES
            内部使用东亚语言:CHINESE_HANS
            文字色:255,128,64
            """;

        string fixedConfig = """
            ; Tier 3: Fixed overrides that lock WindowX and CompatiCALLNAME
            ウィンドウ幅:1024
            CALLNAME空字符串时代入NAME:YES
            """;

        string settingJson = """
            {
              "UseScopedVariableInstruction": true,
              "UseNewRandom": true,
              "UseButtonFocusBackgroundColor": true,
              "ValidExtension": ["txt", "csv", "json"]
            }
            """;

        ConfigLoadResult result = ConfigLoader.Default.LoadFromText(
            defaultConfig,
            userConfig,
            fixedConfig,
            settingJson);

        // Fixed overrides win over user config
        Assert.Equal(1024, result.RuntimeOptions.WindowX);
        Assert.Contains(ConfigCode.WindowX, result.FixedKeys);
        Assert.True(result.CompilerOptions.CompatiCALLNAME);
        Assert.Contains(ConfigCode.CompatiCALLNAME, result.FixedKeys);

        // User config overrides default config
        Assert.Equal(720, result.RuntimeOptions.WindowY);
        Assert.Equal(22, result.RuntimeOptions.FontSize);
        Assert.True(result.CompilerOptions.CompatiSPChara);
        Assert.True(result.RuntimeOptions.ZipSaveData);
        Assert.Equal(936, result.RuntimeOptions.LanguageCodePage);
        Assert.Equal(0xFF8040FFu, result.RuntimeOptions.ForeColorRgba);

        // Default config preserved where not overridden
        Assert.True(result.RuntimeOptions.SystemSaveInBinary);
        Assert.True(result.CompilerOptions.UseERD);

        // setting.json flags applied
        Assert.True(result.CompilerOptions.UseScopedVariableInstruction);
        Assert.True(result.CompilerOptions.UseNewRandom);
        Assert.True(result.CompilerOptions.UseButtonFocusBackgroundColor);
        Assert.Equal(["txt", "csv", "json"], result.RuntimeOptions.ValidExtensions);

        // Subsequent user override cannot change locked FixedKeys, but can change unlocked keys
        ConfigLoadResult afterOverride = ConfigLoader.Default.ApplyUserOverride(
            result,
            "Window width:1920\nWindow height:1080\n");

        Assert.Equal(1024, afterOverride.RuntimeOptions.WindowX);
        Assert.Equal(1080, afterOverride.RuntimeOptions.WindowY);
    }

    [Fact]
    public void CsvGameDataLoader_ParsesAllCsvErdAlsVarExtAndResources()
    {
        using var hepStream = new MemoryStream();
        HepPackageWriter.PackEntries(
            [
                ("csv/GameBase.csv", Utf8("""
                    コード,123456
                    バージョン,105
                    バージョン名,v1.0.5
                    バージョン違い認める,100
                    タイトル,Hypera Sample Era
                    作者,Hypera Team
                    製作年,2026
                    最初からいるキャラ,1
                    """)),
                ("csv/_Replace.csv", Utf8("""
                    お金の単位,円
                    単位の位置,後
                    BAR文字1,#
                    BAR文字2,-
                    汚れの初期値,0/1/2/4/8
                    EXPLVの初期値,0/10/50/200
                    PBANDの初期値,7
                    """)),
                ("csv/_Rename.csv", Utf8("""
                    FLAG:100,[[HERO_GOLD]]
                    CFLAG:0,[[MOOD]]
                    """)),
                ("csv/VariableSize.csv", Utf8("""
                    ABL,50
                    ITEM,200
                    CDFLAG,10,20
                    """)),
                ("csv/Abl.csv", Utf8("""
                    0,従順
                    1,欲望
                    99,OutOfBoundsIgnored
                    """)),
                ("csv/Talent.csv", Utf8("""
                    10,勇敢
                    20,器用
                    """)),
                ("csv/Item.csv", Utf8("""
                    0,薬草,500
                    1,鉄の剣,1500
                    """)),
                ("csv/Base.csv", Utf8("""
                    0,体力
                    1,気力
                    """)),
                ("csv/Chara1.csv", Utf8("""
                    番号,1
                    名前,アリス
                    呼び名,
                    あだ名,不思議な少女
                    基礎,体力,1500
                    基礎,1,1000
                    能力,従順,3
                    素質,勇敢
                    相性,2,120
                    CSTR,0,初期紹介文
                    """)),
                ("csv/Chara99_SP.csv", Utf8("""
                    番号,99
                    名前,SPアリス
                    呼び名,SP
                    SP,1
                    """)),
                ("erb/CustomFlags@0.erd", Utf8("""
                    0,QUEST_STARTED
                    5,BOSS_DEFEATED
                    """)),
                ("erb/Aliases.als", Utf8("""
                    HP,BASE:0
                    MP:BASE:1
                    """)),
                ("csv/VarExtMap.csv", Utf8("""
                    SAVE_DICT,MAP,SAVEDATA
                    GLOBAL_XML,XML,GLOBAL
                    ITEM_DB,DT,SAVEDATA
                    """)),
                ("resources/sprites.csv", Utf8("""
                    HERO_IDLE,hero.png,0,0,64,64,2,4
                    HERO_WALK,ANIME,64,64
                    HERO_WALK,hero.png,0,64,64,64,0,0,120
                    HERO_WALK,hero.png,64,64,64,64,0,0,120
                    """)),
            ],
            hepStream);

        hepStream.Position = 0;
        using var vfs = new HepArchiveVfs(hepStream, "sample.hep");

        var options = new CompilerOptions
        {
            UseRenameFile = true,
            UseReplaceFile = true,
            UseERD = true,
            CompatiCALLNAME = true,
            CompatiSPChara = true,
        };

        GameData data = CsvGameDataLoader.Default.Load(vfs, options);

        // GameBase
        Assert.Equal(123456L, data.GameBase.Code);
        Assert.Equal(105L, data.GameBase.Version);
        Assert.Equal("v1.0.5", data.GameBase.VersionName);
        Assert.Equal("Hypera Sample Era", data.GameBase.Title);
        Assert.Equal(1L, data.GameBase.DefaultChara);

        // _Replace.csv
        Assert.Equal("円", data.ReplaceConfig.MoneyLabel);
        Assert.False(data.ReplaceConfig.MoneyFirst);
        Assert.Equal('#', data.ReplaceConfig.BarChar1);
        Assert.Equal('-', data.ReplaceConfig.BarChar2);
        Assert.Equal([0L, 1L, 2L, 4L, 8L], data.ReplaceConfig.StainDefault);
        Assert.Equal(7L, data.ReplaceConfig.PbandDef);

        // _Rename.csv
        Assert.Equal("FLAG:100", data.RenameDictionary["[[HERO_GOLD]]"]);
        Assert.Equal("CFLAG:0", data.RenameDictionary["[[MOOD]]"]);

        // VariableSize + NameTables + bounds clipping
        Assert.Equal(50, data.NameTables["ABLNAME"].Length);
        Assert.Equal("従順", data.NameTables["ABLNAME"][0]);
        Assert.False(data.NameTables["ABLNAME"].TryGetIndex("OutOfBoundsIgnored", out _));
        Assert.Equal(500L, data.ItemPrices[0]);
        Assert.Equal(1500L, data.ItemPrices[1]);
        Assert.Equal(10, data.GetVariableSize("CDFLAG", 0));
        Assert.Equal(20, data.GetVariableSize("CDFLAG", 1));

        // Chara1.csv (CompatiCALLNAME fallback + named index resolution)
        Assert.True(data.Characters.TryGetValue(1, out CharacterTemplate? alice));
        Assert.Equal("アリス", alice.Name);
        Assert.Equal("アリス", alice.CallName); // fallback from NAME
        Assert.Equal("不思議な少女", alice.NickName);
        Assert.Equal(1500L, alice.Base[0]);
        Assert.Equal(1500L, alice.MaxBase[0]);
        Assert.Equal(1000L, alice.Base[1]);
        Assert.Equal(3L, alice.Abl[0]);
        Assert.Equal(1L, alice.Talent[10]);
        Assert.Equal(120L, alice.Relation[2]);
        Assert.Equal("初期紹介文", alice.CStr[0]);

        // SP Chara
        Assert.True(data.SpCharacters.ContainsKey(99));

        // ERD
        ErdDefinition erd = Assert.Single(data.ErdDefinitions);
        Assert.Equal("CustomFlags", erd.VariableName);
        Assert.Equal(0, erd.DimensionIndex);
        Assert.True(erd.TryGetIndex("BOSS_DEFEATED", out int bossIdx));
        Assert.Equal(5, bossIdx);

        // ALS
        Assert.Equal("BASE:0", data.Aliases["HP"]);
        Assert.Equal("BASE:1", data.Aliases["MP"]);

        // VarExt
        Assert.Equal(3, data.VarExtDefinitions.Count);
        Assert.Contains(data.VarExtDefinitions, v => v.Name == "SAVE_DICT" && v.Kind == VarExtKind.Map && v.IsSavedata);
        Assert.Contains(data.VarExtDefinitions, v => v.Name == "GLOBAL_XML" && v.Kind == VarExtKind.Xml && v.IsGlobal);
        Assert.Contains(data.VarExtDefinitions, v => v.Name == "ITEM_DB" && v.Kind == VarExtKind.DataTable && v.IsSavedata);

        // Resources (static sprite + ANIME)
        Assert.True(data.Resources.Sprites.TryGetValue("HERO_IDLE", out SpriteDescriptor? idle));
        Assert.Equal("resources/hero.png", idle.SourceImagePath);
        Assert.Equal(64, idle.Width);
        Assert.Equal(2, idle.OffsetX);

        Assert.True(data.Resources.AnimeSprites.TryGetValue("HERO_WALK", out AnimeSpriteDescriptor? walk));
        Assert.Equal(2, walk.Frames.Count);
        Assert.Equal(120, walk.Frames[0].DelayMs);
        Assert.Equal(64, walk.Frames[1].X);
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
