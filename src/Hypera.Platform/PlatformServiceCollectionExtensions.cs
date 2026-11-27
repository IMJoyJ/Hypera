using Hypera.Platform.Data;
using Hypera.Platform.Domain;
using Hypera.Platform.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hypera.Platform;

/// <summary>
/// Runtime storage and host-mode configuration for <c>Hypera.Platform</c>.
/// </summary>
public sealed class PlatformHostOptions
{
    public const string LocalAdminDefaultUsername = "local-admin";

    public PlatformHostMode HostMode { get; set; } = PlatformHostMode.MultiUserServer;

    public string DataRootDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>
    /// Optional explicit SQLite connection string override (e.g. in-memory SQLite in automated tests).
    /// When null or whitespace, defaults to <c>{DataRootDirectory}/hypera.db</c> with foreign keys enabled.
    /// </summary>
    public string? SqliteConnectionStringOverride { get; set; }

    /// <summary>
    /// When false (default in <see cref="PlatformHostMode.MultiUserServer"/>), blocks <c>CALLSHARP</c> plugin execution.
    /// When true on server, allows <c>CALLSHARP</c> only if every binary in the submission is <see cref="BinaryReputationStatus.Trusted"/>.
    /// In <see cref="PlatformHostMode.SingleUserLocalAdmin"/>, plugins are always permitted.
    /// </summary>
    public bool AllowTrustedSharpPluginsOnServer { get; set; }

    /// <summary>
    /// Per-turn wall-clock timeout guard protecting the runtime host against pathological slow/infinite loops.
    /// </summary>
    public TimeSpan StepWallClockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum number of <c>INPUT*</c> undo checkpoints retained per active session (default: 1; 0 disables undo).
    /// </summary>
    public int MaxUndoSteps { get; set; } = 1;

    /// <summary>
    /// When true (default), runtime faults or turn timeouts automatically roll back to the latest <c>INPUT*</c>
    /// checkpoint while preserving the diagnostic message for user display.
    /// </summary>
    public bool AutoRollbackOnError { get; set; } = true;

    public string GetResolvedDataRoot() => Path.GetFullPath(DataRootDirectory);

    public string GetDatabaseFilePath() => Path.Combine(GetResolvedDataRoot(), "hypera.db");

    public string GetPackagesDirectory() => Path.Combine(GetResolvedDataRoot(), "packages");

    public string GetSourcesDirectory() => Path.Combine(GetResolvedDataRoot(), "sources");

    public string GetLocalesDirectory() => Path.Combine(GetResolvedDataRoot(), "locales");

    public string GetUserAppSaveDirectory(Guid userId, Guid appId) =>
        Path.Combine(GetResolvedDataRoot(), "saves", userId.ToString("N"), appId.ToString("N"));

    public string BuildSqliteConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(SqliteConnectionStringOverride))
        {
            return SqliteConnectionStringOverride;
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = GetDatabaseFilePath(),
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        return builder.ToString();
    }
}

public static class PlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PlatformHostOptions"/>, <see cref="IDbContextFactory{TContext}"/>, and scoped
    /// <see cref="HyperaDbContext"/> backed by SQLite.
    /// </summary>
    public static IServiceCollection AddHyperaPlatformCore(
        this IServiceCollection services,
        Action<PlatformHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddHyperaPlatformCore((_, opts) => configure?.Invoke(opts));
    }

    /// <summary>
    /// Registers <see cref="PlatformHostOptions"/> resolved lazily from <see cref="IServiceProvider"/>
    /// (allowing <c>WebApplicationFactory</c> configuration overrides to take effect cleanly).
    /// </summary>
    public static IServiceCollection AddHyperaPlatformCore(
        this IServiceCollection services,
        Action<IServiceProvider, PlatformHostOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton(sp =>
        {
            var options = new PlatformHostOptions();
            configure(sp, options);
            return options;
        });

        services.AddDbContextFactory<HyperaDbContext>((sp, db) =>
        {
            PlatformHostOptions options = sp.GetRequiredService<PlatformHostOptions>();
            if (string.IsNullOrWhiteSpace(options.SqliteConnectionStringOverride))
            {
                Directory.CreateDirectory(options.GetResolvedDataRoot());
            }

            db.UseSqlite(options.BuildSqliteConnectionString());
        });

        services.AddScoped<HyperaDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<HyperaDbContext>>().CreateDbContext());

        services.TryAddSingleton<AuthService>();
        services.TryAddSingleton<IAuthService>(sp => sp.GetRequiredService<AuthService>());

        services.TryAddSingleton<BinaryReputationService>();
        services.TryAddSingleton<IBinaryReputationService>(sp => sp.GetRequiredService<BinaryReputationService>());

        services.TryAddSingleton<PackagePipelineService>();
        services.TryAddSingleton<IPackagePipelineService>(sp => sp.GetRequiredService<PackagePipelineService>());

        services.TryAddSingleton<AppCatalogService>();
        services.TryAddSingleton<IAppCatalogService>(sp => sp.GetRequiredService<AppCatalogService>());

        services.TryAddSingleton<VersionResolutionService>();
        services.TryAddSingleton<IVersionResolutionService>(sp => sp.GetRequiredService<VersionResolutionService>());

        services.TryAddSingleton<UserSaveService>();
        services.TryAddSingleton<IUserSaveService>(sp => sp.GetRequiredService<UserSaveService>());

        services.TryAddSingleton<RuntimeSessionManager>();
        services.TryAddSingleton<IRuntimeSessionManager>(sp => sp.GetRequiredService<RuntimeSessionManager>());

        return services;
    }

    /// <summary>
    /// Ensures runtime storage directories and the SQLite schema exist, enabling WAL mode for file-backed databases.
    /// </summary>
    public static async Task InitializeHyperaPlatformDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        PlatformHostOptions options = services.GetRequiredService<PlatformHostOptions>();
        if (string.IsNullOrWhiteSpace(options.SqliteConnectionStringOverride))
        {
            Directory.CreateDirectory(options.GetResolvedDataRoot());
            Directory.CreateDirectory(options.GetPackagesDirectory());
            Directory.CreateDirectory(options.GetSourcesDirectory());
            Directory.CreateDirectory(options.GetLocalesDirectory());
        }

        IDbContextFactory<HyperaDbContext> factory = services.GetRequiredService<IDbContextFactory<HyperaDbContext>>();
        await using HyperaDbContext db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(options.SqliteConnectionStringOverride))
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        }

        // Eagerly activate the singleton parallel compilation worker pool in both Hypera.Server and Hypera.Maui,
        // and resume any submissions that were still compiling before an unexpected shutdown.
        PackagePipelineService? pipeline = services.GetService<PackagePipelineService>();
        if (pipeline is not null)
        {
            await pipeline.ResumePendingCompilationsAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
