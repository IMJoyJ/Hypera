using System.Security.Claims;
using Hypera.Maui.Services;
using Hypera.Platform.Domain;
using Hypera.Platform.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Hypera.Maui.Auth;

/// <summary>
/// Provides an always-authenticated local <see cref="UserRole.Admin"/> principal for
/// <see cref="PlatformHostMode.SingleUserLocalAdmin"/> MAUI Blazor Hybrid sessions.
/// </summary>
public sealed class LocalAdminAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MauiPlatformInitializer _initializer;

    public LocalAdminAuthenticationStateProvider(
        IServiceScopeFactory scopeFactory,
        MauiPlatformInitializer initializer)
    {
        _scopeFactory = scopeFactory;
        _initializer = initializer;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        await _initializer.EnsureInitializedAsync().ConfigureAwait(false);

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IAuthService authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
        AppUser localAdmin = await authService.GetOrEnsureLocalAdminAsync().ConfigureAwait(false);

        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, localAdmin.Id.ToString("D")),
            new(ClaimTypes.Name, localAdmin.Username),
            new(ClaimTypes.Role, UserRole.Admin.ToString()),
        ];

        var identity = new ClaimsIdentity(claims, authenticationType: "LocalAdmin");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }
}
