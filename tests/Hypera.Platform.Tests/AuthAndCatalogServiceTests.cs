using Hypera.Platform.Data;
using Hypera.Platform.Domain;
using Hypera.Platform.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hypera.Platform.Tests;

public sealed class AuthAndCatalogServiceTests
{
    [Fact]
    public async Task FirstUserAutoAdmin_PromotesEvenWhenRegistrationClosed_AndEnforcesRegistrationToggleAndLastAdminGuard()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync();

        // Force AllowOpenRegistration = false in DB BEFORE any user exists: first user MUST still become Admin!
        await using (AsyncServiceScope scope = ws.Services.CreateAsyncScope())
        {
            HyperaDbContext db = scope.ServiceProvider.GetRequiredService<HyperaDbContext>();
            PlatformSettings seeded = await db.PlatformSettings.SingleAsync();
            seeded.AllowOpenRegistration = false;
            await db.SaveChangesAsync();
        }

        Assert.False(await ws.Auth.HasAnyUsersAsync());

        AuthResult first = await ws.Auth.RegisterAsync("founder", "CorrectHorse1!", "zh-CN");
        Assert.True(first.Succeeded);
        Assert.NotNull(first.User);
        Assert.Equal(UserRole.Admin, first.User!.Role);
        Assert.Equal("zh-CN", first.User.UiLanguage);
        Assert.True(await ws.Auth.HasAnyUsersAsync());

        // Second user is blocked because AllowOpenRegistration is currently false
        AuthResult blockedSecond = await ws.Auth.RegisterAsync("alice", "Password123!");
        Assert.False(blockedSecond.Succeeded);
        Assert.Equal("registration_closed", blockedSecond.ErrorCode);

        // Re-enable open registration via Admin
        PlatformSettings current = await ws.Auth.GetSettingsAsync();
        current.AllowOpenRegistration = true;
        await ws.Auth.UpdateSettingsAsync(first.User.Id, current);

        AuthResult second = await ws.Auth.RegisterAsync("alice", "Password123!");
        Assert.True(second.Succeeded);
        Assert.Equal(UserRole.User, second.User!.Role);

        // Case-insensitive duplicate username rejected
        AuthResult dup = await ws.Auth.RegisterAsync("ALICE", "Password123!");
        Assert.False(dup.Succeeded);
        Assert.Equal("username_taken", dup.ErrorCode);

        // PBKDF2 authentication & disabled user rejection
        Assert.True((await ws.Auth.AuthenticateAsync("alice", "Password123!")).Succeeded);
        Assert.False((await ws.Auth.AuthenticateAsync("alice", "WrongPass!")).Succeeded);

        await ws.Auth.SetUserDisabledAsync(first.User.Id, second.User.Id, isDisabled: true);
        AuthResult disabledLogin = await ws.Auth.AuthenticateAsync("alice", "Password123!");
        Assert.False(disabledLogin.Succeeded);
        Assert.Equal("user_disabled", disabledLogin.ErrorCode);

        // Non-admin cannot manage settings or roles
        await ws.Auth.SetUserDisabledAsync(first.User.Id, second.User.Id, isDisabled: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => ws.Auth.SetUserRoleAsync(second.User.Id, second.User.Id, UserRole.Admin));

        // Sole active Admin cannot demote or disable themselves!
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ws.Auth.SetUserRoleAsync(first.User.Id, first.User.Id, UserRole.User));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ws.Auth.SetUserDisabledAsync(first.User.Id, first.User.Id, isDisabled: true));

        // Once a second active Admin exists, the first Admin can be demoted cleanly
        await ws.Auth.SetUserRoleAsync(first.User.Id, second.User.Id, UserRole.Admin);
        await ws.Auth.SetUserRoleAsync(second.User.Id, first.User.Id, UserRole.User);
        AppUser? updatedFirst = await ws.Auth.GetUserByIdAsync(first.User.Id);
        Assert.Equal(UserRole.User, updatedFirst!.Role);
    }

    [Fact]
    public async Task ConcurrentRegistrationBurst_AssignsExactlyOneAutoAdmin()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync();

        Task<AuthResult>[] tasks = Enumerable.Range(1, 8)
            .Select(i => ws.Auth.RegisterAsync($"racer_{i}", "SecretPass99!"))
            .ToArray();

        AuthResult[] results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.Single(results, r => r.User!.Role == UserRole.Admin);
        Assert.Equal(7, results.Count(r => r.User!.Role == UserRole.User));
    }

    [Fact]
    public async Task LocalSingleUserMode_GetOrEnsureLocalAdmin_IsIdempotentAdmin()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync(PlatformHostMode.SingleUserLocalAdmin);

        AppUser local1 = await ws.Auth.GetOrEnsureLocalAdminAsync();
        AppUser local2 = await ws.Auth.GetOrEnsureLocalAdminAsync();

        Assert.Equal(local1.Id, local2.Id);
        Assert.Equal(UserRole.Admin, local1.Role);
        Assert.Equal(PlatformHostOptions.LocalAdminDefaultUsername, local1.Username);
    }

    [Fact]
    public async Task CatalogVisibility_HistoricalVersionInsertion_Reordering_AndForkWithZip_WorkEndToEnd()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync();

        AppUser admin = (await ws.Auth.RegisterAsync("admin", "AdminPass1!")).User!;
        AppUser author = (await ws.Auth.RegisterAsync("author", "AuthorPass1!")).User!;
        AppUser bystander = (await ws.Auth.RegisterAsync("bystander", "Bystander1!")).User!;

        // 1. Unapproved app is visible ONLY to creator and Admin
        GameApp app = await ws.Catalog.CreateAppAsync(author.Id, "ChronicleEra", "Original game");
        Assert.False(app.IsPublicPool);

        Assert.Contains(await ws.Catalog.ListVisibleAppsAsync(author.Id), a => a.Id == app.Id);
        Assert.Contains(await ws.Catalog.ListVisibleAppsAsync(admin.Id), a => a.Id == app.Id);
        Assert.DoesNotContain(await ws.Catalog.ListVisibleAppsAsync(bystander.Id), a => a.Id == app.Id);
        Assert.DoesNotContain(await ws.Catalog.ListVisibleAppsAsync(null), a => a.Id == app.Id);
        Assert.Null(await ws.Catalog.GetAppDetailAsync(app.Id, bystander.Id));

        // 2. Historical insertion (NewestHead, BeforeTarget, AfterTarget) maintains dense 0..N-1 OrderIndex
        GameVersion v10 = await ws.Catalog.InsertVersionAsync(author.Id, app.Id, "v1.0", position: VersionInsertPosition.NewestHead);
        GameVersion v20 = await ws.Catalog.InsertVersionAsync(author.Id, app.Id, "v2.0", position: VersionInsertPosition.NewestHead);
        GameVersion v05 = await ws.Catalog.InsertVersionAsync(author.Id, app.Id, "v0.5", position: VersionInsertPosition.BeforeTarget, targetVersionId: v10.Id);
        GameVersion v15 = await ws.Catalog.InsertVersionAsync(author.Id, app.Id, "v1.5", position: VersionInsertPosition.AfterTarget, targetVersionId: v10.Id);

        GameApp detail = (await ws.Catalog.GetAppDetailAsync(app.Id, author.Id))!;
        string[] orderedLabels = detail.Versions.OrderBy(v => v.OrderIndex).Select(v => $"{v.OrderIndex}:{v.VersionLabel}").ToArray();
        Assert.Equal(["0:v0.5", "1:v1.0", "2:v1.5", "3:v2.0"], orderedLabels);

        // 3. ReorderVersionsAsync reassigns dense 0..N-1 cleanly
        await ws.Catalog.ReorderVersionsAsync(admin.Id, app.Id, [v10.Id, v05.Id, v20.Id, v15.Id]);
        GameApp reordered = (await ws.Catalog.GetAppDetailAsync(app.Id, admin.Id))!;
        Assert.Equal(
            ["0:v1.0", "1:v0.5", "2:v2.0", "3:v1.5"],
            reordered.Versions.OrderBy(v => v.OrderIndex).Select(v => $"{v.OrderIndex}:{v.VersionLabel}").ToArray());

        // Restore chronological order
        await ws.Catalog.ReorderVersionsAsync(admin.Id, app.Id, [v05.Id, v10.Id, v15.Id, v20.Id]);

        // 4. Publish v1.0 [ja] so app enters public pool
        using MemoryStream baseZip = PlatformTestWorkspace.CreateMinimalPlayableGameZip("ORIGINAL V1");
        GameLanguageSubmission pubSub = await ws.Pipeline.SubmitZipPackageAsync(
            new PackageUploadRequest(v10.Id, "ja", author.Id, AllowSourceDownload: true, baseZip));
        pubSub = await ws.Pipeline.WaitForCompilationAsync(pubSub.Id);
        Assert.Equal(SubmissionStatus.PendingReview, pubSub.Status);
        await ws.Pipeline.ApproveSubmissionAsync(admin.Id, pubSub.Id);

        // Now visible to everyone, and creator CANNOT unlist/delete once IsPublicPool == true!
        Assert.Contains(await ws.Catalog.ListVisibleAppsAsync(bystander.Id), a => a.Id == app.Id);
        Assert.Contains(await ws.Catalog.ListVisibleAppsAsync(null), a => a.Id == app.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => ws.Catalog.UpdateAppMetadataAsync(author.Id, app.Id, "ChronicleEra", "Updated", isUnlistedOrDeleted: true));

        // Admin CAN unlist and re-list
        await ws.Catalog.UpdateAppMetadataAsync(admin.Id, app.Id, "ChronicleEra", "Updated", isUnlistedOrDeleted: true);
        Assert.DoesNotContain(await ws.Catalog.ListVisibleAppsAsync(bystander.Id), a => a.Id == app.Id);
        Assert.Contains(await ws.Catalog.ListVisibleAppsAsync(admin.Id, includeUnlisted: true), a => a.Id == app.Id);
        await ws.Catalog.UpdateAppMetadataAsync(admin.Id, app.Id, "ChronicleEra", "Restored", isUnlistedOrDeleted: false);

        // 5. Fork-with-zip: synchronous invalid/empty zip rolls back orphan GameApp; valid zip creates pending fork!
        using var emptyZip = new MemoryStream([]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ws.Catalog.ForkAppWithPackageAsync(
            bystander.Id,
            app.Id,
            v10.Id,
            "BrokenFork",
            "Should rollback",
            "v1.0-fork",
            "zh-CN",
            allowSourceDownload: true,
            zipArchiveStream: emptyZip));
        Assert.DoesNotContain(await ws.Catalog.ListVisibleAppsAsync(admin.Id, includeUnlisted: true), a => a.Title == "BrokenFork");

        using MemoryStream forkZip = PlatformTestWorkspace.CreateMinimalPlayableGameZip("FORKED EDITION");
        ForkAppResult forkResult = await ws.Catalog.ForkAppWithPackageAsync(
            bystander.Id,
            app.Id,
            v10.Id,
            "ChronicleEra Plus",
            "Expanded fork",
            "v1.0-plus",
            "zh-CN",
            allowSourceDownload: false,
            zipArchiveStream: forkZip);

        Assert.Equal(app.Id, forkResult.App.ForkedFromAppId);
        Assert.Equal(v10.Id, forkResult.App.ForkedFromVersionId);
        Assert.False(forkResult.App.IsPublicPool);

        GameLanguageSubmission forkCompiled = await ws.Pipeline.WaitForCompilationAsync(forkResult.InitialSubmission.Id);
        Assert.Equal(SubmissionStatus.PendingReview, forkCompiled.Status);
        Assert.DoesNotContain(await ws.Catalog.ListVisibleAppsAsync(author.Id), a => a.Id == forkResult.App.Id);

        await ws.Pipeline.ApproveSubmissionAsync(admin.Id, forkCompiled.Id);
        Assert.Contains(await ws.Catalog.ListVisibleAppsAsync(null), a => a.Id == forkResult.App.Id);
    }
}
