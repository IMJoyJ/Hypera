using System.Security.Claims;
using Hypera.Platform.Domain;
using Hypera.Platform.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace Hypera.Server.Auth;

/// <summary>
/// Periodically revalidates active Blazor Server circuits against SQLite so account disablement
/// or role promotions/demotions take effect promptly without requiring a manual page reload.
/// </summary>
public sealed class HyperaRevalidatingAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;

    public HyperaRevalidatingAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory)
        : base(loggerFactory)
    {
        _scopeFactory = scopeFactory;
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(15);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        ClaimsPrincipal principal = authenticationState.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            return true;
        }

        if (!AuthClaimsFactory.TryGetUserId(principal, out Guid userId))
        {
            return false;
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IAuthService authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
        AppUser? user = await authService.GetUserByIdAsync(userId, cancellationToken);
        if (user is null || user.IsDisabled)
        {
            return false;
        }

        string? currentRole = principal.FindFirstValue(ClaimTypes.Role);
        string? currentName = principal.FindFirstValue(ClaimTypes.Name);
        if (!string.Equals(currentRole, user.Role.ToString(), StringComparison.Ordinal) ||
            !string.Equals(currentName, user.Username, StringComparison.Ordinal))
        {
            ClaimsPrincipal updatedPrincipal = AuthClaimsFactory.CreatePrincipal(user);
            SetAuthenticationState(Task.FromResult(new AuthenticationState(updatedPrincipal)));
        }

        return true;
    }
}
