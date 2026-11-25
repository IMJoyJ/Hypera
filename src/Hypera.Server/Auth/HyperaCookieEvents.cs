using System.Security.Claims;
using Hypera.Platform.Domain;
using Hypera.Platform.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Hypera.Server.Auth;

internal static class HyperaCookieEvents
{
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        if (!AuthClaimsFactory.TryGetUserId(context.Principal, out Guid userId))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        IAuthService authService = context.HttpContext.RequestServices.GetRequiredService<IAuthService>();
        AppUser? user = await authService.GetUserByIdAsync(userId, context.HttpContext.RequestAborted);
        if (user is null || user.IsDisabled)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        string? claimRole = context.Principal?.FindFirstValue(ClaimTypes.Role);
        string? claimName = context.Principal?.FindFirstValue(ClaimTypes.Name);
        if (!string.Equals(claimRole, user.Role.ToString(), StringComparison.Ordinal) ||
            !string.Equals(claimName, user.Username, StringComparison.Ordinal))
        {
            context.ReplacePrincipal(AuthClaimsFactory.CreatePrincipal(user));
            context.ShouldRenew = true;
        }
    }
}
