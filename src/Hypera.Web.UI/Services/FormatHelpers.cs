using System.Globalization;
using Hypera.Localization;
using Hypera.Platform.Domain;
using MudBlazor;

namespace Hypera.Web.UI.Services;

public static class FormatHelpers
{
    public static readonly string[] CommonLanguageCodes =
    [
        "ja",
        "zh-CN",
        "zh-TW",
        "en",
        "ko",
        "ru",
        "es",
        "fr",
        "de",
    ];

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int idx = 0;
        while (value >= 1024.0 && idx < units.Length - 1)
        {
            value /= 1024.0;
            idx++;
        }

        return idx == 0
            ? $"{bytes} B"
            : $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[idx]}";
    }

    public static string FormatUtc(DateTime utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string FormatRgbaCss(uint rgba)
    {
        byte r = (byte)((rgba >> 24) & 0xFF);
        byte g = (byte)((rgba >> 16) & 0xFF);
        byte b = (byte)((rgba >> 8) & 0xFF);
        byte a = (byte)(rgba & 0xFF);
        return a == 255
            ? $"rgb({r},{g},{b})"
            : $"rgba({r},{g},{b},{(a / 255.0).ToString("0.###", CultureInfo.InvariantCulture)})";
    }

    public static string GetLanguageLabel(string code, HyperaLocalizer localizer)
    {
        LocaleInfo? info = localizer.GetLocaleInfo(code);
        if (info is not null)
        {
            return $"{info.NativeName} ({info.Code})";
        }

        return code switch
        {
            "zh-TW" => "繁體中文 (zh-TW)",
            "ko" => "한국어 (ko)",
            "ru" => "Русский (ru)",
            "es" => "Español (es)",
            "fr" => "Français (fr)",
            "de" => "Deutsch (de)",
            _ => code,
        };
    }

    public static Color GetSubmissionStatusColor(SubmissionStatus status) => status switch
    {
        SubmissionStatus.Compiling => Color.Info,
        SubmissionStatus.CompileFailed => Color.Error,
        SubmissionStatus.PendingReview => Color.Warning,
        SubmissionStatus.Published => Color.Success,
        SubmissionStatus.Superseded => Color.Default,
        SubmissionStatus.Rejected => Color.Error,
        _ => Color.Default,
    };

    public static string GetSubmissionStatusText(SubmissionStatus status, UiSessionState ui) => status switch
    {
        SubmissionStatus.Compiling => ui.T("appDetail.statusCompiling"),
        SubmissionStatus.CompileFailed => ui.T("appDetail.statusCompileFailed"),
        SubmissionStatus.PendingReview => ui.T("appDetail.statusPendingReview"),
        SubmissionStatus.Published => ui.T("appDetail.statusPublished"),
        SubmissionStatus.Superseded => ui.T("appDetail.statusSuperseded"),
        SubmissionStatus.Rejected => ui.T("appDetail.statusRejected"),
        _ => status.ToString(),
    };

    public static Color GetReputationColor(BinaryReputationStatus status) => status switch
    {
        BinaryReputationStatus.Trusted => Color.Success,
        BinaryReputationStatus.Unknown => Color.Warning,
        BinaryReputationStatus.HighRisk => Color.Error,
        _ => Color.Default,
    };

    public static string GetReputationText(BinaryReputationStatus status, UiSessionState ui) => status switch
    {
        BinaryReputationStatus.Trusted => ui.T("admin.binaryTrusted"),
        BinaryReputationStatus.Unknown => ui.T("admin.binaryUnknown"),
        BinaryReputationStatus.HighRisk => ui.T("admin.binaryHighRisk"),
        _ => status.ToString(),
    };

    public static string MapAuthError(string? errorCode, UiSessionState ui)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return string.Empty;
        }

        return errorCode.Trim().ToLowerInvariant() switch
        {
            "invalid_credentials" or "invalidcredentials" => ui.T("auth.invalidCredentials"),
            "account_disabled" or "user_disabled" or "disabled" => ui.T("auth.accountDisabled"),
            "registration_closed" or "registration_disabled" => ui.T("auth.registrationClosed"),
            "username_taken" or "duplicate_username" or "username_exists" => ui.T("auth.usernameTaken"),
            "invalid_username" or "username_required" => ui.T("auth.usernameRequired"),
            "invalid_password" or "password_required" or "password_too_short" => ui.T("auth.passwordTooShort"),
            _ => errorCode,
        };
    }
}
