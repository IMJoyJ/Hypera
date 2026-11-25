using Hypera.Localization;
using Hypera.Web.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor;
using MudBlazor.Services;

namespace Hypera.Web.UI;

public static class WebUiServiceCollectionExtensions
{
    public static IServiceCollection AddHyperaWebUi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(HyperaLocalizer.Default);
        services.AddScoped<UiSessionState>();
        services.AddMudServices(config =>
        {
            config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
            config.SnackbarConfiguration.PreventDuplicates = true;
            config.SnackbarConfiguration.VisibleStateDuration = 3500;
            config.SnackbarConfiguration.ShowTransitionDuration = 150;
            config.SnackbarConfiguration.HideTransitionDuration = 150;
        });

        return services;
    }
}
