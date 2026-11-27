using Hypera.Localization;
using Hypera.Maui.Auth;
using Hypera.Maui.Services;
using Hypera.Platform;
using Hypera.Platform.Domain;
using Hypera.Web.UI;
using Hypera.Web.UI.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Hypera.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        string dataRoot = Path.Combine(FileSystem.AppDataDirectory, "HyperaData");

        builder.Services.AddHyperaPlatformCore(options =>
        {
            options.HostMode = PlatformHostMode.SingleUserLocalAdmin;
            options.DataRootDirectory = dataRoot;
        });

        builder.Services.AddSingleton(sp =>
        {
            PlatformHostOptions hostOptions = sp.GetRequiredService<PlatformHostOptions>();
            string localesDir = hostOptions.GetLocalesDirectory();
            Directory.CreateDirectory(localesDir);

            var localizer = new HyperaLocalizer(loadBuiltIn: true);
            localizer.LoadExternalDirectory(localesDir, enableWatcher: true);
            return localizer;
        });

        builder.Services.AddSingleton<IHostWindowService, MauiHostWindowService>();
        builder.Services.AddSingleton<ILocalFilePickerService, MauiLocalFilePickerService>();

        builder.Services.AddHyperaWebUi();

        builder.Services.AddAuthorizationCore();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddSingleton<MauiPlatformInitializer>();
        builder.Services.AddScoped<AuthenticationStateProvider, LocalAdminAuthenticationStateProvider>();

        MauiApp app = builder.Build();

        // Start SQLite schema and local-admin provisioning asynchronously on startup;
        // MauiRoot.razor awaits this gate before rendering Hypera.Web.UI.Routes.
        _ = app.Services.GetRequiredService<MauiPlatformInitializer>().EnsureInitializedAsync();

        return app;
    }
}
