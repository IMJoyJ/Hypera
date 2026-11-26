using Hypera.Compiler;
using Hypera.Diagnostics;
using Hypera.Localization;
using Hypera.Tests.Compiler;
using Xunit;

namespace Hypera.Tests.Localization;

public sealed class LocalizationAndDiagnosticsTests
{
    [Fact]
    public void DefaultLocale_IsJapanese_AndBuiltInPacksAreRegistered()
    {
        using var localizer = new HyperaLocalizer();

        Assert.Equal("ja", localizer.CurrentLocale);
        Assert.True(localizer.HasLocale("ja"));
        Assert.True(localizer.HasLocale("zh-CN"));
        Assert.True(localizer.HasLocale("en"));

        Assert.Equal(3, localizer.AvailableLocales.Count);
        Assert.Equal("ja", localizer.AvailableLocales[0].Code);
        Assert.Equal("日本語", localizer.AvailableLocales[0].NativeName);
        Assert.True(localizer.AvailableLocales[0].IsBuiltIn);

        Assert.Equal("zh-CN", localizer.AvailableLocales[1].Code);
        Assert.Equal("简体中文", localizer.AvailableLocales[1].NativeName);
        Assert.True(localizer.AvailableLocales[1].IsBuiltIn);

        Assert.Equal("en", localizer.AvailableLocales[2].Code);
        Assert.Equal("English", localizer.AvailableLocales[2].NativeName);
        Assert.True(localizer.AvailableLocales[2].IsBuiltIn);

        // Minimalist UI keys exist across all 3 built-in packs
        Assert.Equal("プレイ", localizer.Get("nav.play"));
        Assert.Equal("游玩", localizer.Get("nav.play", "zh-CN"));
        Assert.Equal("Play", localizer.Get("nav.play", "en"));
        Assert.Equal("言語", localizer.Get("settings.language", "ja"));
        Assert.Equal("语言", localizer.Get("settings.language", "zh-CN"));
        Assert.Equal("Language", localizer.Get("settings.language", "en"));
    }

    [Fact]
    public void CompiledDiagnostics_FormatAccuratelyInJaZhCnAndEn()
    {
        var localizer = HyperaLocalizer.Default;

        // 1. Unclosed IF block (ERA1701) + IsMethodSafe violation (ERA1540) + unknown variable (ERA1426)
        string brokenErb = """
            @SYSTEM_TITLE
            LOCAL = BAD_FUNC()
            IF 1
                PRINTL Hello

            @BAD_FUNC
            #FUNCTION
            WAIT
            RETURNF UNKNOWN_VAR_XYZ
            """;

        EmueraCompileException ex = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.Compile(new CompilerMemoryVfs().AddFile("ERB/Broken.erb", brokenErb)));

        Diagnostic unclosedIf = Assert.Single(ex.Diagnostics, d => d.Code == "ERA1701");
        Assert.Contains("SYSTEM_TITLE", unclosedIf.Arguments);

        string ifJa = unclosedIf.FormatMessage(localizer, "ja");
        string ifZh = unclosedIf.FormatMessage(localizer, "zh-CN");
        string ifEn = unclosedIf.FormatMessage(localizer, "en");

        Assert.Contains("@SYSTEM_TITLE", ifJa);
        Assert.Contains("閉じられていません", ifJa);
        Assert.Contains("@SYSTEM_TITLE", ifZh);
        Assert.Contains("未闭合", ifZh);
        Assert.Contains("@SYSTEM_TITLE", ifEn);
        Assert.Contains("Unclosed 'IF' block", ifEn);

        Diagnostic methodUnsafe = Assert.Single(ex.Diagnostics, d => d.Code == "ERA1540");
        Assert.Equal<string>(["WAIT", "BAD_FUNC"], methodUnsafe.Arguments);

        Assert.Contains("WAIT", methodUnsafe.FormatMessage(localizer, "ja"));
        Assert.Contains("@BAD_FUNC", methodUnsafe.FormatMessage(localizer, "ja"));
        Assert.Contains("使用できません", methodUnsafe.FormatMessage(localizer, "ja"));

        Assert.Contains("WAIT", methodUnsafe.FormatMessage(localizer, "zh-CN"));
        Assert.Contains("@BAD_FUNC", methodUnsafe.FormatMessage(localizer, "zh-CN"));
        Assert.Contains("禁止", methodUnsafe.FormatMessage(localizer, "zh-CN"));

        Assert.Contains("WAIT", methodUnsafe.FormatMessage(localizer, "en"));
        Assert.Contains("@BAD_FUNC", methodUnsafe.FormatMessage(localizer, "en"));
        Assert.Contains("forbidden", methodUnsafe.FormatMessage(localizer, "en"));

        Diagnostic unknownVar = Assert.Single(ex.Diagnostics, d => d.Code == "ERA1426");
        Assert.Equal<string>(["UNKNOWN_VAR_XYZ"], unknownVar.Arguments);
        Assert.Contains("UNKNOWN_VAR_XYZ", unknownVar.FormatMessage(localizer, "ja"));
        Assert.Contains("未定義", unknownVar.FormatMessage(localizer, "ja"));
        Assert.Contains("未知的变量", unknownVar.FormatMessage(localizer, "zh-CN"));
        Assert.Contains("Unknown variable", unknownVar.FormatMessage(localizer, "en"));

        // FormatFull includes localized severity label + file/line + code
        string fullJa = methodUnsafe.FormatFull(localizer, "ja");
        string fullZh = methodUnsafe.FormatFull(localizer, "zh-CN");
        string fullEn = methodUnsafe.FormatFull(localizer, "en");
        Assert.Contains("エラー ERA1540:", fullJa);
        Assert.Contains("错误 ERA1540:", fullZh);
        Assert.Contains("error ERA1540:", fullEn);

        // EmueraCompileException.FormatMessage localizes summary
        Assert.Contains("コンパイルに失敗しました", ex.FormatMessage(localizer, "ja"));
        Assert.Contains("编译失败", ex.FormatMessage(localizer, "zh-CN"));
        Assert.Contains("Compilation failed", ex.FormatMessage(localizer, "en"));
    }

    [Fact]
    public void ErhDiagnostics_UseDisambiguatedMessageKeys_AndFormatAcrossLocales()
    {
        var localizer = HyperaLocalizer.Default;

        EmueraCompileException ex = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.Compile(
                new CompilerMemoryVfs()
                    .AddFile("ERB/Header.erh", "#DEFINE BAD_MACRO(X) X + 1\n")
                    .AddFile("ERB/Main.erb", "@SYSTEM_TITLE\nQUIT\n")));

        Diagnostic paramMacro = Assert.Single(ex.Diagnostics, d => d.Code == "ERA1506");
        Assert.Equal("diag.erh.ERA1506", paramMacro.MessageKey);
        Assert.Equal<string>(["BAD_MACRO"], paramMacro.Arguments);

        Assert.Contains("BAD_MACRO", paramMacro.FormatMessage(localizer, "ja"));
        Assert.Contains("サポートされていません", paramMacro.FormatMessage(localizer, "ja"));
        Assert.Contains("不支持带参数的 #DEFINE 宏", paramMacro.FormatMessage(localizer, "zh-CN"));
        Assert.Contains("parameterized #DEFINE macro", paramMacro.FormatMessage(localizer, "en"));
    }

    [Fact]
    public void ExternalDirectory_HotLoadsCommunityKoreanPack_AndFallsBackMissingKeysToJapanese()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hypera_locales_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            using var localizer = new HyperaLocalizer();
            using var reloadSignal = new ManualResetEventSlim(false);
            localizer.LocalesChanged += () => reloadSignal.Set();

            localizer.LoadExternalDirectory(tempDir, enableWatcher: true);
            Assert.False(localizer.HasLocale("ko"));
            reloadSignal.Reset();

            string koJson = """
                {
                  "_meta": {
                    "code": "ko",
                    "nativeName": "한국어",
                    "englishName": "Korean"
                  },
                  "nav.play": "플레이",
                  "diag.ERA1701": "함수 '@{0}'의 'IF' 블록이 닫히지 않았습니다 ('ENDIF' 누락)."
                }
                """;
            File.WriteAllText(Path.Combine(tempDir, "ko.json"), koJson);

            // Wait for FileSystemWatcher to finish loading ko.json (handle Linux inotify Created+Changed split)
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!localizer.HasLocale("ko") && DateTime.UtcNow < deadline)
            {
                reloadSignal.Reset();
                if (!localizer.HasLocale("ko"))
                {
                    TimeSpan remaining = deadline - DateTime.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        reloadSignal.Wait(remaining);
                    }
                }
            }

            if (!localizer.HasLocale("ko"))
            {
                localizer.ReloadExternalDirectory();
            }

            Assert.True(localizer.HasLocale("ko"));
            LocaleInfo? koInfo = localizer.GetLocaleInfo("ko");
            Assert.NotNull(koInfo);
            Assert.Equal("ko", koInfo.Code);
            Assert.Equal("한국어", koInfo.NativeName);
            Assert.Equal("Korean", koInfo.EnglishName);
            Assert.False(koInfo.IsBuiltIn);
            Assert.Contains(localizer.AvailableLocales, l => l.Code == "ko" && l.NativeName == "한국어");

            // Translated key uses ko.json
            Assert.Equal("플레이", localizer.Get("nav.play", "ko"));
            var diag = new Diagnostic("ERA1701", DiagnosticSeverity.Error, "fallback", default, "Test.erb", null, ["MAIN"]);
            Assert.Equal("함수 '@MAIN'의 'IF' 블록이 닫히지 않았습니다 ('ENDIF' 누락).", diag.FormatMessage(localizer, "ko"));

            // Missing key in ko.json falls back to Japanese (default fallback locale!)
            Assert.Equal("設定", localizer.Get("settings.title", "ko"));
            Assert.Equal(localizer.Get("diag.ERA1426", "ja"), localizer.Get("diag.ERA1426", "ko"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ExternalDirectory_OverridesBuiltInKeys_WithoutLosingOtherKeys()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hypera_locales_override_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string zhOverride = """
                {
                  "_meta": {
                    "code": "zh-CN"
                  },
                  "nav.play": "启动游戏"
                }
                """;
            File.WriteAllText(Path.Combine(tempDir, "zh-CN.json"), zhOverride);

            using var localizer = new HyperaLocalizer();
            localizer.LoadExternalDirectory(tempDir, enableWatcher: false);

            Assert.Equal("启动游戏", localizer.Get("nav.play", "zh-CN"));
            Assert.Equal("存档", localizer.Get("nav.saves", "zh-CN"));
            LocaleInfo? zhInfo = localizer.GetLocaleInfo("zh-CN");
            Assert.NotNull(zhInfo);
            Assert.True(zhInfo.IsBuiltIn);
            Assert.Equal("简体中文", zhInfo.NativeName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void LocaleResolution_HandlesRegionTags_AndMalformedTemplatesSafely()
    {
        using var localizer = new HyperaLocalizer();

        Assert.Equal("Play", localizer.Get("nav.play", "en-US"));
        Assert.Equal("游玩", localizer.Get("nav.play", "zh-Hans"));
        Assert.Equal("プレイ", localizer.Get("nav.play", "ja-JP"));
        Assert.Equal("プレイ", localizer.Get("nav.play", "xx-UNKNOWN"));

        // Malformed format string must not throw FormatException
        localizer.LoadJsonOverlay("""{ "_meta": { "code": "broken" }, "diag.ERA1426": "Bad {0 template" }""");
        var diag = new Diagnostic("ERA1426", DiagnosticSeverity.Error, "fallback", default, null, null, ["FOO"]);
        Assert.Equal("Bad {0 template", diag.FormatMessage(localizer, "broken"));

        // Backward-compatible Diagnostic constructor leaves Arguments empty and ToString intact
        var legacy = new Diagnostic("ERA1001", DiagnosticSeverity.Error, "Legacy msg", SourceSpan.SingleLine(1, 2, 3, 4), "a.erb");
        Assert.True(legacy.Arguments.IsEmpty);
        Assert.Equal("a.erb(2,3): error ERA1001: Legacy msg", legacy.ToString());
    }
}
