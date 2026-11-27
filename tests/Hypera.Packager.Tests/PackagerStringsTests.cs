using Xunit;

namespace Hypera.Packager.Tests;

public sealed class PackagerStringsTests
{
    [Fact]
    public void DefaultLanguage_IsJapanese()
    {
        Assert.Equal("ja", PackagerStrings.DefaultLanguage);
        Assert.Equal("ja", PackagerStrings.NormalizeLanguage(null));
        Assert.Equal("ja", PackagerStrings.NormalizeLanguage("unknown"));
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public void AllKeysExistAndNonEmptyInSupportedLanguages(string lang)
    {
        foreach (string key in PackagerStrings.AllKeys)
        {
            string value = PackagerStrings.Get(lang, key);
            Assert.False(string.IsNullOrWhiteSpace(value), $"Missing or empty key '{key}' for '{lang}'.");
            Assert.NotEqual(key, value);
        }
    }
}
