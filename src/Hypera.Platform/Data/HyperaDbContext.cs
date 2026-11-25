using Hypera.Platform.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hypera.Platform.Data;

/// <summary>
/// Authoritative EF Core SQLite database context for <c>Hypera.Platform</c>.
/// Shared across <c>Hypera.Server</c> (<see cref="PlatformHostMode.MultiUserServer"/>) and
/// <c>Hypera.Maui</c> (<see cref="PlatformHostMode.SingleUserLocalAdmin"/>).
/// </summary>
public sealed class HyperaDbContext : DbContext
{
    public HyperaDbContext(DbContextOptions<HyperaDbContext> options)
        : base(options)
    {
    }

    public DbSet<AppUser> Users => Set<AppUser>();

    public DbSet<PlatformSettings> PlatformSettings => Set<PlatformSettings>();

    public DbSet<GameApp> GameApps => Set<GameApp>();

    public DbSet<GameVersion> GameVersions => Set<GameVersion>();

    public DbSet<GameLanguageVariant> GameLanguageVariants => Set<GameLanguageVariant>();

    public DbSet<GameLanguageSubmission> GameLanguageSubmissions => Set<GameLanguageSubmission>();

    public DbSet<SubmissionBinaryEntry> SubmissionBinaryEntries => Set<SubmissionBinaryEntry>();

    public DbSet<BinaryHashReputation> BinaryHashReputations => Set<BinaryHashReputation>();

    public DbSet<UserAppLaunchState> UserAppLaunchStates => Set<UserAppLaunchState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>(b =>
        {
            b.ToTable("Users");
            b.HasKey(u => u.Id);
            b.Property(u => u.Username)
                .IsRequired()
                .HasMaxLength(64)
                .UseCollation("NOCASE");
            b.HasIndex(u => u.Username).IsUnique();
            b.Property(u => u.PasswordHash).IsRequired().HasMaxLength(512);
            b.Property(u => u.UiLanguage).IsRequired().HasMaxLength(32).HasDefaultValue(AppUser.DefaultLanguageCode);
            b.Property(u => u.KnownLanguagesJson).IsRequired().HasMaxLength(1024).HasDefaultValue(AppUser.DefaultKnownLanguagesJson);
        });

        modelBuilder.Entity<PlatformSettings>(b =>
        {
            b.ToTable("PlatformSettings");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).ValueGeneratedNever();
            b.HasData(new Domain.PlatformSettings
            {
                Id = Domain.PlatformSettings.SingletonId,
                AllowOpenRegistration = true,
                MaxUploadBytes = Domain.PlatformSettings.DefaultMaxUploadBytes,
                MaxStepInstructionsPerTurn = Domain.PlatformSettings.DefaultMaxStepInstructionsPerTurn,
                SessionIdleTimeoutMinutes = Domain.PlatformSettings.DefaultSessionIdleTimeoutMinutes,
                MaxSaveQuotaBytesPerApp = Domain.PlatformSettings.DefaultMaxSaveQuotaBytesPerApp,
                UpdatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
        });

        modelBuilder.Entity<GameApp>(b =>
        {
            b.ToTable("GameApps");
            b.HasKey(a => a.Id);
            b.Property(a => a.Title).IsRequired().HasMaxLength(200);
            b.Property(a => a.Summary).IsRequired().HasMaxLength(4000);
            b.HasIndex(a => new { a.IsPublicPool, a.IsUnlistedOrDeleted, a.UpdatedAtUtc });
            b.HasIndex(a => a.CreatedByUserId);

            b.HasOne(a => a.CreatedByUser)
                .WithMany(u => u.CreatedApps)
                .HasForeignKey(a => a.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasOne(a => a.ForkedFromApp)
                .WithMany()
                .HasForeignKey(a => a.ForkedFromAppId)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasOne(a => a.ForkedFromVersion)
                .WithMany()
                .HasForeignKey(a => a.ForkedFromVersionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<GameVersion>(b =>
        {
            b.ToTable("GameVersions");
            b.HasKey(v => v.Id);
            b.Property(v => v.VersionLabel).IsRequired().HasMaxLength(64);
            b.Property(v => v.Changelog).IsRequired().HasMaxLength(8000);
            b.HasIndex(v => new { v.AppId, v.OrderIndex });
            b.HasIndex(v => new { v.AppId, v.VersionLabel });

            b.HasOne(v => v.App)
                .WithMany(a => a.Versions)
                .HasForeignKey(v => v.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(v => v.CreatedByUser)
                .WithMany()
                .HasForeignKey(v => v.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<GameLanguageVariant>(b =>
        {
            b.ToTable("GameLanguageVariants");
            b.HasKey(v => v.Id);
            b.Property(v => v.LanguageCode)
                .IsRequired()
                .HasMaxLength(32)
                .UseCollation("NOCASE");
            b.HasIndex(v => new { v.VersionId, v.LanguageCode }).IsUnique();

            b.HasOne(v => v.Version)
                .WithMany(ver => ver.Variants)
                .HasForeignKey(v => v.VersionId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(v => v.ActiveSubmission)
                .WithOne()
                .HasForeignKey<GameLanguageVariant>(v => v.ActiveSubmissionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<GameLanguageSubmission>(b =>
        {
            b.ToTable("GameLanguageSubmissions");
            b.HasKey(s => s.Id);
            b.Property(s => s.LanguageCode)
                .IsRequired()
                .HasMaxLength(32)
                .UseCollation("NOCASE");
            b.Property(s => s.HepStoragePath).HasMaxLength(512);
            b.Property(s => s.SourceZipStoragePath).HasMaxLength(512);
            b.Property(s => s.RejectionReason).HasMaxLength(2000);

            b.HasIndex(s => new { s.Status, s.CreatedAtUtc });
            b.HasIndex(s => new { s.VersionId, s.LanguageCode })
                .IsUnique()
                .HasFilter("\"IsActivePublished\" = 1");

            b.HasOne(s => s.Variant)
                .WithMany(v => v.Submissions)
                .HasForeignKey(s => s.VariantId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(s => s.Version)
                .WithMany(v => v.Submissions)
                .HasForeignKey(s => s.VersionId)
                .OnDelete(DeleteBehavior.NoAction);

            b.HasOne(s => s.UploadedByUser)
                .WithMany()
                .HasForeignKey(s => s.UploadedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasOne(s => s.ReviewedByUser)
                .WithMany()
                .HasForeignKey(s => s.ReviewedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SubmissionBinaryEntry>(b =>
        {
            b.ToTable("SubmissionBinaryEntries");
            b.HasKey(e => e.Id);
            b.Property(e => e.RelativePath).IsRequired().HasMaxLength(512);
            b.Property(e => e.Sha256Hex)
                .IsRequired()
                .HasMaxLength(64)
                .UseCollation("NOCASE");

            b.HasIndex(e => e.SubmissionId);
            b.HasIndex(e => new { e.Sha256Hex, e.ByteSize });

            b.HasOne(e => e.Submission)
                .WithMany(s => s.BinaryEntries)
                .HasForeignKey(e => e.SubmissionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BinaryHashReputation>(b =>
        {
            b.ToTable("BinaryHashReputations");
            b.HasKey(r => r.Id);
            b.Property(r => r.Sha256Hex)
                .IsRequired()
                .HasMaxLength(64)
                .UseCollation("NOCASE");
            b.Property(r => r.SampleFileName).IsRequired().HasMaxLength(260);
            b.Property(r => r.ReasonNote).IsRequired().HasMaxLength(2000);

            b.HasIndex(r => new { r.Sha256Hex, r.ByteSize }).IsUnique();

            b.HasOne(r => r.UpdatedByUser)
                .WithMany()
                .HasForeignKey(r => r.UpdatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<UserAppLaunchState>(b =>
        {
            b.ToTable("UserAppLaunchStates");
            b.HasKey(ls => new { ls.UserId, ls.AppId });
            b.Property(ls => ls.LastRunLanguageCode).HasMaxLength(32);

            b.HasOne(ls => ls.User)
                .WithMany(u => u.LaunchStates)
                .HasForeignKey(ls => ls.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(ls => ls.App)
                .WithMany()
                .HasForeignKey(ls => ls.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(ls => ls.LastRunVersion)
                .WithMany()
                .HasForeignKey(ls => ls.LastRunVersionId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
