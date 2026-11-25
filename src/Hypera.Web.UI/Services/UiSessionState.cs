using System.Security.Claims;
using Hypera.Localization;
using Hypera.Platform;
using Hypera.Platform.Domain;
using Hypera.Platform.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Hypera.Web.UI.Services;

/// <summary>
/// Per-circuit UI state managing current user resolution across both MultiUserServer and SingleUserLocalAdmin,
/// circuit-scoped UI language (defaulting to Japanese "ja"), dark/light theme, and live locale pack updates.
/// </summary>
public sealed class UiSessionState : IDisposable
{
    private readonly HyperaLocalizer _localizer;
    private readonly PlatformHostOptions _hostOptions;
    private readonly IAuthService _authService;
    private readonly AuthenticationStateProvider? _authStateProvider;
    private bool _disposed;
    private bool _userLanguageSynced;

    public UiSessionState(
        HyperaLocalizer localizer,
        PlatformHostOptions hostOptions,
        IAuthService authService,
        IServiceProvider services)
    {
        _localizer = localizer;
        _hostOptions = hostOptions;
        _authService = authService;
        _authStateProvider = services.GetService<AuthenticationStateProvider>();
        _localizer.LocalesChanged += OnLocalesChanged;
    }

    public event Action? Changed;

    public HyperaLocalizer Localizer => _localizer;

    public PlatformHostMode HostMode => _hostOptions.HostMode;

    public bool IsLocalSingleUser => _hostOptions.HostMode == PlatformHostMode.SingleUserLocalAdmin;

    public bool IsMultiUserServer => _hostOptions.HostMode == PlatformHostMode.MultiUserServer;

    public bool IsDarkMode { get; private set; } = true;

    public string UiLanguage { get; private set; } = HyperaLocalizer.DefaultLocaleCode;

    public AppUser? CurrentUser { get; private set; }

    public bool IsAuthenticated => CurrentUser is not null && !CurrentUser.IsDisabled;

    public bool IsAdmin => IsAuthenticated && CurrentUser!.Role == UserRole.Admin;

    public bool ShowAdminNav => IsMultiUserServer && IsAdmin;

    public bool IsInitialized { get; private set; }

    public IReadOnlyList<LocaleInfo> AvailableLocales => _localizer.AvailableLocales;

    public string T(string key) => _localizer.Get(key, UiLanguage);

    public string T(string key, params string[] args) =>
        args.Length == 0
            ? _localizer.Get(key, UiLanguage)
            : _localizer.Format(key, UiLanguage, null, args);

    public async Task InitializeAsync(bool forceRefreshUser = false, CancellationToken cancellationToken = default)
    {
        if (IsInitialized && !forceRefreshUser)
        {
            return;
        }

        if (IsLocalSingleUser)
        {
            CurrentUser = await _authService.GetOrEnsureLocalAdminAsync(cancellationToken);
        }
        else if (_authStateProvider is not null)
        {
            AuthenticationState state = await _authStateProvider.GetAuthenticationStateAsync();
            CurrentUser = await ResolveUserFromPrincipalAsync(state.User, cancellationToken);
        }
        else
        {
            CurrentUser = null;
        }

        if (!_userLanguageSynced && CurrentUser is not null && !string.IsNullOrWhiteSpace(CurrentUser.UiLanguage))
        {
            UiLanguage = CurrentUser.UiLanguage;
            _userLanguageSynced = true;
        }

        IsInitialized = true;
        NotifyChanged();
    }

    public async Task RefreshUserAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(forceRefreshUser: true, cancellationToken);
    }

    public async Task SetUiLanguageAsync(string localeCode, bool persistToProfile = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localeCode))
        {
            return;
        }

        string normalized = localeCode.Trim();
        if (string.Equals(UiLanguage, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        UiLanguage = normalized;
        _userLanguageSynced = true;
        NotifyChanged();

        if (persistToProfile && CurrentUser is not null)
        {
            try
            {
                CurrentUser = await _authService.UpdateUserPreferencesAsync(
                    CurrentUser.Id,
                    new UserPreferencesUpdate(
                        UiLanguage: normalized,
                        VersionStrategy: CurrentUser.VersionStrategy,
                        KnownLanguages: CurrentUser.GetKnownLanguages()),
                    cancellationToken);
                NotifyChanged();
            }
            catch
            {
                // Keep in-memory UI language switch even if persistence fails transiently
            }
        }
    }

    public void SetDarkMode(bool isDark)
    {
        if (IsDarkMode == isDark)
        {
            return;
        }

        IsDarkMode = isDark;
        NotifyChanged();
    }

    public void ToggleDarkMode() => SetDarkMode(!IsDarkMode);

    private async Task<AppUser?> ResolveUserFromPrincipalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        string? idClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(idClaim, out Guid userId))
        {
            return null;
        }

        AppUser? user = await _authService.GetUserByIdAsync(userId, cancellationToken);
        if (user is null || user.IsDisabled)
        {
            return null;
        }

        return user;
    }

    private void OnLocalesChanged()
    {
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _localizer.LocalesChanged -= OnLocalesChanged;
    }
}
