using Hypera.Configuration;
using Hypera.Localization;
using Hypera.Rendering;
using SkiaSharp;
using Xunit;

namespace Hypera.Tests.Configuration;

public sealed class CompilerOptionsOverrideAndDynamicSpriteVersionTests
{
    [Fact]
    public void CompilerOptionsOverride_AppliesTriState_Merges_AndRoundTripsJson()
    {
        var baseOpts = CompilerOptions.Default;

        var empty = new CompilerOptionsOverride();
        Assert.False(empty.HasAnyOverride);
        Assert.Equal(0, empty.OverrideCount);
        Assert.Null(empty.ToJson());
        Assert.Equal(baseOpts, empty.ApplyTo(baseOpts));

        var first = new CompilerOptionsOverride
        {
            UseNewRandom = true,
            CompatiCallEvent = true,
            IgnoreCase = false,
            SifBlankLineBehavior = SifBlankLineBehavior.EramakerBlankLineConsumesCondition,
        };

        Assert.True(first.HasAnyOverride);
        Assert.Equal(4, first.OverrideCount);

        CompilerOptions applied = first.ApplyTo(baseOpts);
        Assert.True(applied.UseNewRandom);
        Assert.True(applied.CompatiCallEvent);
        Assert.False(applied.IgnoreCase);
        Assert.Equal(SifBlankLineBehavior.EramakerBlankLineConsumesCondition, applied.SifBlankLineBehavior);
        // Unspecified properties inherit baseOpts unchanged
        Assert.Equal(baseOpts.UseERD, applied.UseERD);
        Assert.Equal(baseOpts.IgnoreUncalledFunction, applied.IgnoreUncalledFunction);

        string? json = first.ToJson();
        Assert.NotNull(json);
        CompilerOptionsOverride? parsed = CompilerOptionsOverride.FromJson(json);
        Assert.Equal(first, parsed);
        Assert.Null(CompilerOptionsOverride.FromJson("   "));
        Assert.Null(CompilerOptionsOverride.FromJson("{invalid-json"));
        Assert.Null(CompilerOptionsOverride.FromJson("{}"));

        var overlay = new CompilerOptionsOverride
        {
            UseNewRandom = false,
            VarsizeDimConfig = true,
        };
        CompilerOptionsOverride merged = first.MergeWith(overlay);
        Assert.Equal(false, merged.UseNewRandom);
        Assert.Equal(true, merged.CompatiCallEvent);
        Assert.Equal(true, merged.VarsizeDimConfig);
        Assert.Equal(5, merged.OverrideCount);

        IReadOnlyList<(string Key, string Value)> described = merged.DescribeOverrides();
        Assert.Contains(described, d => d.Key == nameof(CompilerOptionsOverride.UseNewRandom) && d.Value == "SFMT");
        Assert.Contains(described, d => d.Key == nameof(CompilerOptionsOverride.CompatiCallEvent) && d.Value == "ON");
        Assert.Contains(described, d => d.Key == nameof(CompilerOptionsOverride.VarsizeDimConfig) && d.Value == "ON");
    }

    [Fact]
    public void DynamicSpriteAndGraphicsSurface_VersionsTrackMutationsDisposeAndRebind()
    {
        using var fonts = new FontRegistry();
        var measurer = new GlyphMeasurer(fonts);
        using var graphics = new GraphicsManager(fonts, measurer);
        using var sprites = new SpriteRegistry();

        Assert.True(graphics.Create(4021, 32, 32));
        Assert.True(graphics.Create(4022, 32, 32));

        Assert.True(graphics.TryGetSurfaceVersion(4021, out long g4021v1));
        Assert.True(graphics.TryGetSurface(4021, out GraphicsSurface s4021));
        s4021.Clear(new SKColor(255, 255, 255, 32));
        Assert.True(graphics.TryGetSurfaceVersion(4021, out long g4021v2));
        Assert.NotEqual(g4021v1, g4021v2);

        // Bind dynamic sprite TITLE_4020 -> gid 4021 (fade-in frame 0, dark alpha=32)
        Assert.True(sprites.CreateSprite("TITLE_4020", 4021, graphics, 0, 0, 32, 32));
        Assert.True(sprites.TryGetAssetVersion("TITLE_4020", graphics, out long spriteV1, out bool isDynamic1));
        Assert.True(isDynamic1);

        // Mutating backing surface gid 4021 invalidates sprite version
        s4021.FillRectangle(0, 0, 16, 16);
        Assert.True(sprites.TryGetAssetVersion("TITLE_4020", graphics, out long spriteV2, out bool isDynamic2));
        Assert.True(isDynamic2);
        Assert.NotEqual(spriteV1, spriteV2);

        // Rebinding the SAME sprite name TITLE_4020 -> gid 4022 (fade-in frame 1, brighter alpha=255)
        Assert.True(graphics.TryGetSurface(4022, out GraphicsSurface s4022));
        s4022.Clear(new SKColor(255, 255, 255, 255));
        Assert.True(sprites.CreateSprite("TITLE_4020", 4022, graphics, 0, 0, 32, 32));
        Assert.True(sprites.TryGetAssetVersion("TITLE_4020", graphics, out long spriteV3, out bool isDynamic3));
        Assert.True(isDynamic3);
        Assert.NotEqual(spriteV2, spriteV3);

        // Disposing and recreating the same graphics ID produces a distinct InstanceToken version
        Assert.True(graphics.TryGetSurfaceVersion(4022, out long beforeDispose));
        graphics.DisposeSurface(4022);
        Assert.False(graphics.TryGetSurfaceVersion(4022, out _));
        Assert.True(graphics.Create(4022, 32, 32));
        Assert.True(graphics.TryGetSurfaceVersion(4022, out long afterRecreate));
        Assert.NotEqual(beforeDispose, afterRecreate);
    }

    [Fact]
    public void BuiltInLocales_ContainAllNewLocalClientAndCompilerKeys()
    {
        using var localizer = new HyperaLocalizer();
        string[] requiredKeys =
        [
            "nav.library",
            "player.fullscreenTooltip",
            "player.exitFullscreenTooltip",
            "player.scrollbackLines",
            "player.scrollToBottom",
            "player.continue",
            "catalog.libraryTitle",
            "catalog.importGame",
            "catalog.emptyLocalHint",
            "catalog.pickPackageFile",
            "catalog.pickFolder",
            "appDetail.backToLibrary",
            "appDetail.editApp",
            "appDetail.deleteApp",
            "appDetail.deleteVersion",
            "appDetail.tabVersions",
            "appDetail.tabBuilds",
            "appDetail.tabSaves",
            "appDetail.recompile",
            "compiler.title",
            "compiler.opt.UseNewRandom",
            "compiler.opt.CompatiCallEvent",
            "compiler.opt.SifBlankLineBehavior",
            "settings.sectionCompiler",
            "settings.sectionRuntime",
            "settings.maxUndoSteps",
            "settings.autoRollbackOnError",
        ];

        foreach (string locale in new[] { "ja", "zh-CN", "en" })
        {
            foreach (string key in requiredKeys)
            {
                string value = localizer.Get(key, locale);
                Assert.False(string.IsNullOrWhiteSpace(value), $"Missing value for {key} in {locale}");
                Assert.NotEqual(key, value);
            }
        }
    }
}
