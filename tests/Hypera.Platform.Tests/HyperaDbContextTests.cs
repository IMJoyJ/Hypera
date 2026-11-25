using System.Net;
using Hypera.Platform.Data;
using Hypera.Platform.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hypera.Platform.Tests;

public sealed class HyperaDbContextTests
{
    private static (SqliteConnection Connection, DbContextOptions<HyperaDbContext> Options) CreateInMemorySqlite()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        DbContextOptions<HyperaDbContext> options = new DbContextOptionsBuilder<HyperaDbContext>()
            .UseSqlite(connection)
            .Options;
        return (connection, options);
    }

    [Fact]
    public async Task InitializeDatabase_CreatesSchemaAndSeedsDefaultSingletonSettings()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "hypera_plat_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddHyperaPlatformCore(opts =>
            {
                opts.HostMode = PlatformHostMode.SingleUserLocalAdmin;
                opts.DataRootDirectory = tempRoot;
            });

            await using ServiceProvider provider = services.BuildServiceProvider();
            await provider.InitializeHyperaPlatformDatabaseAsync();

            Assert.True(File.Exists(Path.Combine(tempRoot, "hypera.db")));
            Assert.True(Directory.Exists(Path.Combine(tempRoot, "packages")));
            Assert.True(Directory.Exists(Path.Combine(tempRoot, "sources")));
            Assert.True(Directory.Exists(Path.Combine(tempRoot, "locales")));

            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            HyperaDbContext db = scope.ServiceProvider.GetRequiredService<HyperaDbContext>();
            PlatformSettings settings = await db.PlatformSettings.SingleAsync();

            Assert.Equal(PlatformSettings.SingletonId, settings.Id);
            Assert.True(settings.AllowOpenRegistration);
            Assert.Equal(512L * 1024 * 1024, settings.MaxUploadBytes);
            Assert.Equal(PlatformSettings.DefaultMaxStepInstructionsPerTurn, settings.MaxStepInstructionsPerTurn);
            Assert.Equal(PlatformSettings.DefaultSessionIdleTimeoutMinutes, settings.SessionIdleTimeoutMinutes);
            Assert.Equal(PlatformSettings.DefaultMaxSaveQuotaBytesPerApp, settings.MaxSaveQuotaBytesPerApp);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Users_EnforceCaseInsensitiveUniqueUsernameAndDefaultJapaneseLocale()
    {
        (SqliteConnection conn, DbContextOptions<HyperaDbContext> opts) = CreateInMemorySqlite();
        await using (conn)
        await using (var db = new HyperaDbContext(opts))
        {
            await db.Database.EnsureCreatedAsync();

            var admin = new AppUser
            {
                Username = "FirstAdmin",
                PasswordHash = "hash-1",
                Role = UserRole.Admin,
            };
            admin.SetKnownLanguages(["zh-CN", "ja", "ZH-cn"]);

            db.Users.Add(admin);
            await db.SaveChangesAsync();

            AppUser loaded = await db.Users.AsNoTracking().SingleAsync();
            Assert.Equal("ja", loaded.UiLanguage);
            Assert.Equal(VersionResolutionStrategy.KnownLanguagePriority, loaded.VersionStrategy);
            Assert.Equal(["zh-CN", "ja"], loaded.GetKnownLanguages());

            db.Users.Add(new AppUser
            {
                Username = "firstadmin",
                PasswordHash = "hash-2",
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task VersionChainAndLanguageVariants_PreserveActiveBuildDuringPendingReuploadAndEnforceSingleActivePublished()
    {
        (SqliteConnection conn, DbContextOptions<HyperaDbContext> opts) = CreateInMemorySqlite();
        await using (conn)
        await using (var db = new HyperaDbContext(opts))
        {
            await db.Database.EnsureCreatedAsync();

            var author = new AppUser { Username = "creator", PasswordHash = "pw", Role = UserRole.User };
            var reviewer = new AppUser { Username = "mod", PasswordHash = "pw", Role = UserRole.Admin };
            db.Users.AddRange(author, reviewer);

            var app = new GameApp
            {
                Title = "EraSample",
                Summary = "Sample game",
                CreatedByUserId = author.Id,
                IsPublicPool = true,
            };
            db.GameApps.Add(app);

            // Historical v0.9 backfilled at OrderIndex 0, v1.0 at OrderIndex 1
            var v09 = new GameVersion { AppId = app.Id, VersionLabel = "v0.9", OrderIndex = 0, CreatedByUserId = author.Id };
            var v10 = new GameVersion { AppId = app.Id, VersionLabel = "v1.0", OrderIndex = 1, CreatedByUserId = author.Id };
            db.GameVersions.AddRange(v09, v10);

            var zhVariant = new GameLanguageVariant
            {
                VersionId = v10.Id,
                LanguageCode = "zh-CN",
            };
            db.GameLanguageVariants.Add(zhVariant);
            await db.SaveChangesAsync();

            var sub1 = new GameLanguageSubmission
            {
                VariantId = zhVariant.Id,
                VersionId = v10.Id,
                LanguageCode = "zh-CN",
                UploadedByUserId = author.Id,
                AllowSourceDownload = true,
                Status = SubmissionStatus.Published,
                IsActivePublished = true,
                HepStoragePath = "packages/v10-zh-1.hep",
                ReviewedByUserId = reviewer.Id,
                PublishedAtUtc = DateTime.UtcNow,
            };
            db.GameLanguageSubmissions.Add(sub1);
            await db.SaveChangesAsync();

            zhVariant.ActiveSubmissionId = sub1.Id;
            await db.SaveChangesAsync();

            // Re-uploading v1.0 [zh-CN] stays PendingReview while sub1 remains live
            var sub2 = new GameLanguageSubmission
            {
                VariantId = zhVariant.Id,
                VersionId = v10.Id,
                LanguageCode = "zh-CN",
                UploadedByUserId = author.Id,
                Status = SubmissionStatus.PendingReview,
                IsActivePublished = false,
                HepStoragePath = "packages/v10-zh-2.hep",
            };
            db.GameLanguageSubmissions.Add(sub2);
            await db.SaveChangesAsync();

            GameLanguageVariant liveCheck = await db.GameLanguageVariants
                .Include(v => v.ActiveSubmission)
                .SingleAsync(v => v.Id == zhVariant.Id);
            Assert.Equal(sub1.Id, liveCheck.ActiveSubmissionId);
            Assert.Equal("packages/v10-zh-1.hep", liveCheck.ActiveSubmission!.HepStoragePath);

            // Attempting to mark sub2 IsActivePublished = true WITHOUT superseding sub1 violates SQLite filtered unique index
            sub2.IsActivePublished = true;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task BinaryReputationAndLaunchState_PersistCleanlyWithCompositeConstraints()
    {
        (SqliteConnection conn, DbContextOptions<HyperaDbContext> opts) = CreateInMemorySqlite();
        await using (conn)
        await using (var db = new HyperaDbContext(opts))
        {
            await db.Database.EnsureCreatedAsync();

            var user = new AppUser { Username = "player1", PasswordHash = "pw", Role = UserRole.Admin };
            var app = new GameApp { Title = "PluginGame", CreatedByUserId = user.Id };
            var ver = new GameVersion { AppId = app.Id, VersionLabel = "v1.0", OrderIndex = 0, CreatedByUserId = user.Id };
            db.Users.Add(user);
            db.GameApps.Add(app);
            db.GameVersions.Add(ver);

            string sha = new('a', 64);
            db.BinaryHashReputations.Add(new BinaryHashReputation
            {
                Sha256Hex = sha,
                ByteSize = 4096,
                ReputationStatus = BinaryReputationStatus.HighRisk,
                SampleFileName = "suspicious.dll",
                ReasonNote = "Network socket call",
                UpdatedByUserId = user.Id,
            });

            db.UserAppLaunchStates.Add(new UserAppLaunchState
            {
                UserId = user.Id,
                AppId = app.Id,
                LastRunVersionId = ver.Id,
                LastRunLanguageCode = "ja",
                WasFollowingAuto = false,
            });

            await db.SaveChangesAsync();

            // Duplicate (Sha256Hex, ByteSize) in reputation table is rejected
            db.BinaryHashReputations.Add(new BinaryHashReputation
            {
                Sha256Hex = sha.ToUpperInvariant(),
                ByteSize = 4096,
                ReputationStatus = BinaryReputationStatus.Trusted,
                SampleFileName = "other.dll",
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task ServerHost_BootsAndServesHealthzInMultiUserServerMode()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "hypera_srv_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("Hypera:DataRoot", tempRoot);
                });

            using HttpClient client = factory.CreateClient();
            HttpResponseMessage response = await client.GetAsync("/healthz");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            string payload = await response.Content.ReadAsStringAsync();
            Assert.Contains("MultiUserServer", payload, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(tempRoot, "hypera.db")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
