using Hypera.Platform.Domain;
using Hypera.Platform.Services;

namespace Hypera.Server.Services;

/// <summary>
/// Periodically evicts idle <see cref="IRuntimeSessionManager"/> sessions based on
/// <see cref="PlatformSettings.SessionIdleTimeoutMinutes"/>.
/// </summary>
public sealed class RuntimeSessionEvictionHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RuntimeSessionEvictionHostedService> _logger;
    private readonly TimeSpan _pollInterval;

    public RuntimeSessionEvictionHostedService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<RuntimeSessionEvictionHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        int seconds = configuration.GetValue("Hypera:SessionEvictionIntervalSeconds", 60);
        _pollInterval = TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 3600));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                IAuthService authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
                IRuntimeSessionManager sessionManager = scope.ServiceProvider.GetRequiredService<IRuntimeSessionManager>();

                PlatformSettings settings = await authService.GetSettingsAsync(stoppingToken);
                int timeoutMinutes = Math.Max(1, settings.SessionIdleTimeoutMinutes);
                int evicted = await sessionManager.EvictIdleSessionsAsync(
                    TimeSpan.FromMinutes(timeoutMinutes),
                    stoppingToken);

                if (evicted > 0)
                {
                    _logger.LogInformation(
                        "Evicted {EvictedCount} idle runtime session(s) exceeding {TimeoutMinutes} minute(s).",
                        evicted,
                        timeoutMinutes);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during periodic runtime session idle eviction.");
            }
        }
    }
}
