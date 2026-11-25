using System.Security.Claims;
using Hypera.Platform.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Hypera.Server.Auth;

internal static class AuthClaimsFactory
{
    public static ClaimsPrincipal CreatePrincipal(
        AppUser user,
        string authenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme)
    {
        ArgumentNullException.ThrowIfNull(user);

        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString()),
        ];

        var identity = new ClaimsIdentity(claims, authenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    public static bool TryGetUserId(ClaimsPrincipal? principal, out Guid userId)
    {
        userId = Guid.Empty;
        string? raw = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        return !string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw, out userId);
    }

    public static string SanitizeLocalReturnUrl(string? returnUrl, string fallback = "/")
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return fallback;
        }

        string trimmed = returnUrl.Trim();
        if (trimmed.StartsWith('/') && !trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith("/\\", StringComparison.Ordinal))
        {
            return trimmed;
        }

        return fallback;
    }
}
