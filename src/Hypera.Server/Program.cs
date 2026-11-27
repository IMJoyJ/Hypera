using Hypera.Localization;
using Hypera.Platform;
using Hypera.Platform.Domain;
using Hypera.Server.Auth;
using Hypera.Server.Components;
using Hypera.Server.Endpoints;
using Hypera.Server.Services;
using Hypera.Web.UI;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http.Features;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

bool hasExplicitUrls =
    !string.IsNullOrWhiteSpace(builder.Configuration["urls"]) ||
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) ||
    builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any();

if (!hasExplicitUrls)
{
    builder.WebHost.UseUrls("http://0.0.0.0:5080");
}

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = PlatformSettings.DefaultMaxUploadBytes;
});

builder.Services.Configure<FormOptions>(formOptions =>
{
    formOptions.MultipartBodyLengthLimit = PlatformSettings.DefaultMaxUploadBytes;
    formOptions.ValueLengthLimit = int.MaxValue;
});

builder.Services.AddHyperaPlatformCore((sp, options) =>
{
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    IWebHostEnvironment env = sp.GetRequiredService<IWebHostEnvironment>();

    string configuredDataRoot = config["Hypera:DataRoot"]
        ?? Path.Combine(env.ContentRootPath, "data");

    if (!Path.IsPathRooted(configuredDataRoot))
    {
        configuredDataRoot = Path.Combine(env.ContentRootPath, configuredDataRoot);
    }

    options.HostMode = PlatformHostMode.MultiUserServer;
    options.DataRootDirectory = configuredDataRoot;
    options.MaxUndoSteps = Math.Max(0, config.GetValue("Hypera:MaxUndoSteps", 1));
    options.AutoRollbackOnError = config.GetValue("Hypera:AutoRollbackOnError", true);
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

builder.Services.AddHyperaWebUi();

int cookieExpirationDays = Math.Clamp(
    builder.Configuration.GetValue("Hypera:AuthCookieExpirationDays", 14),
    1,
    365);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Hypera.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(cookieExpirationDays);
        options.LoginPath = "/login";
        options.LogoutPath = "/api/auth/logout";
        options.AccessDeniedPath = "/login";
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }

                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            },
            OnValidatePrincipal = HyperaCookieEvents.ValidatePrincipalAsync,
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, HyperaRevalidatingAuthenticationStateProvider>();

long maxSignalRMessageBytes = Math.Max(
    1024L * 1024L,
    builder.Configuration.GetValue("Hypera:SignalRMaxReceiveMessageBytes", 64L * 1024L * 1024L));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(hubOptions =>
    {
        hubOptions.MaximumReceiveMessageSize = maxSignalRMessageBytes;
    });

builder.Services.AddHostedService<RuntimeSessionEvictionHostedService>();

WebApplication app = builder.Build();

await app.Services.InitializeHyperaPlatformDatabaseAsync();

if (!string.IsNullOrWhiteSpace(app.Environment.WebRootPath) &&
    Directory.Exists(app.Environment.WebRootPath))
{
    app.UseStaticFiles();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapGet("/healthz", (PlatformHostOptions opts) => Results.Ok(new
{
    status = "ok",
    hostMode = opts.HostMode.ToString(),
})).AllowAnonymous();

app.MapAuthEndpoints();
app.MapDownloadEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Routes).Assembly);

app.Run();

public partial class Program;
