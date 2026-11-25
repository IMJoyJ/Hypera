namespace Hypera.Localization;

/// <summary>
/// Describes a registered language pack (built-in or hot-loaded from an external locales directory).
/// </summary>
public sealed record LocaleInfo(
    string Code,
    string NativeName,
    string EnglishName,
    bool IsBuiltIn);
