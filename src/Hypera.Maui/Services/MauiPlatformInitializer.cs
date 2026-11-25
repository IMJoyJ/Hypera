using Hypera.Platform;
using Hypera.Platform.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Hypera.Maui.Services;

/// <summary>
/// Thread-safe asynchronous startup gate ensuring SQLite schema, storage directories,
/// and the implicit local administrator account are initialized before any Razor page queries data.
/// </summary>
public sealed class MauiPlatformInitializer
{
    private readonly IServiceProvider _services;
    private readonly Lock _sync = new();
    private Task? _initTask;

    public MauiPlatformInitializer(IServiceProvider services)
    {
        _services = services;
    }

    public Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        Task currentTask;
        lock (_sync)
        {
            if (_initTask is null || _initTask.IsFaulted || _initTask.IsCanceled)
            {
                _initTask = InitializeCoreAsync();
            }

            currentTask = _initTask;
        }

        return cancellationToken.CanBeCanceled
            ? currentTask.WaitAsync(cancellationToken)
            : currentTask;
    }

    private async Task InitializeCoreAsync()
    {
        await _services.InitializeHyperaPlatformDatabaseAsync().ConfigureAwait(false);

        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        IAuthService authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
        await authService.GetOrEnsureLocalAdminAsync().ConfigureAwait(false);
    }
}
