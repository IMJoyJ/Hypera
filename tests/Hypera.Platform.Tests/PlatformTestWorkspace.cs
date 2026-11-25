using System.IO.Compression;
using System.Text;
using Hypera.Platform.Domain;
using Hypera.Platform.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Hypera.Platform.Tests;

internal sealed class PlatformTestWorkspace : IAsyncDisposable
{
    public PlatformTestWorkspace(
        string dataRoot,
        ServiceProvider provider)
    {
        DataRoot = dataRoot;
        Services = provider;
    }

    public string DataRoot { get; }

    public ServiceProvider Services { get; }

    public IAuthService Auth => Services.GetRequiredService<IAuthService>();

    public IAppCatalogService Catalog => Services.GetRequiredService<IAppCatalogService>();

    public IVersionResolutionService VersionResolution => Services.GetRequiredService<IVersionResolutionService>();

    public IPackagePipelineService Pipeline => Services.GetRequiredService<IPackagePipelineService>();

    public IBinaryReputationService Reputation => Services.GetRequiredService<IBinaryReputationService>();

    public IUserSaveService Saves => Services.GetRequiredService<IUserSaveService>();

    public RuntimeSessionManager SessionManager => Services.GetRequiredService<RuntimeSessionManager>();

    public IRuntimeSessionManager Sessions => Services.GetRequiredService<IRuntimeSessionManager>();

    public static async Task<PlatformTestWorkspace> CreateAsync(
        PlatformHostMode hostMode = PlatformHostMode.MultiUserServer,
        Action<PlatformHostOptions>? configure = null)
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "hypera_psvc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var services = new ServiceCollection();
        services.AddHyperaPlatformCore(opts =>
        {
            opts.HostMode = hostMode;
            opts.DataRootDirectory = tempRoot;
            configure?.Invoke(opts);
        });

        ServiceProvider provider = services.BuildServiceProvider();
        await provider.InitializeHyperaPlatformDatabaseAsync();
        return new PlatformTestWorkspace(tempRoot, provider);
    }

    public static MemoryStream CreateZipStream(
        IReadOnlyDictionary<string, string>? textEntries = null,
        IReadOnlyDictionary<string, byte[]>? binaryEntries = null)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (textEntries is not null)
            {
                foreach ((string path, string content) in textEntries)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                    using Stream s = entry.Open();
                    byte[] utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(content);
                    s.Write(utf8, 0, utf8.Length);
                }
            }

            if (binaryEntries is not null)
            {
                foreach ((string path, byte[] data) in binaryEntries)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                    using Stream s = entry.Open();
                    s.Write(data, 0, data.Length);
                }
            }
        }

        ms.Position = 0;
        return ms;
    }

    public static MemoryStream CreateMinimalPlayableGameZip(
        string greetingLine = "HELLO HYPERA",
        IReadOnlyDictionary<string, byte[]>? extraBinaries = null)
    {
        var files = new Dictionary<string, string>
        {
            ["CSV/GameBase.csv"] = "タイトル,TestEraGame\nバージョン,1000\n",
            ["ERB/SYSTEM.ERB"] =
                $$"""
                @SYSTEM_TITLE
                PRINTL {{greetingLine}}
                PRINTBUTTON "[0] Start", 0
                PRINTL
                INPUT
                IF RESULT == 0
                    PRINTL STARTED_OK
                    QUIT
                ENDIF
                RESTART
                """,
        };

        return CreateZipStream(files, extraBinaries);
    }

    public async Task<(GameVersion Version, GameLanguageSubmission Submission)> UploadAndPublishAsync(
        Guid uploaderId,
        Guid adminId,
        Guid appId,
        string versionLabel,
        string languageCode,
        Stream zipStream,
        VersionInsertPosition position = VersionInsertPosition.NewestHead,
        Guid? targetVersionId = null,
        bool allowSourceDownload = true)
    {
        GameVersion version = await Catalog.InsertVersionAsync(
            uploaderId,
            appId,
            versionLabel,
            changelog: $"Release {versionLabel}",
            position: position,
            targetVersionId: targetVersionId);

        GameLanguageSubmission sub = await Pipeline.SubmitZipPackageAsync(new PackageUploadRequest(
            VersionId: version.Id,
            LanguageCode: languageCode,
            UploadedByUserId: uploaderId,
            AllowSourceDownload: allowSourceDownload,
            ZipArchiveStream: zipStream));

        sub = await Pipeline.WaitForCompilationAsync(sub.Id);
        if (sub.Status == SubmissionStatus.PendingReview)
        {
            sub = await Pipeline.ApproveSubmissionAsync(adminId, sub.Id);
        }

        return (version, sub);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(DataRoot))
                {
                    Directory.Delete(DataRoot, recursive: true);
                }

                break;
            }
            catch (IOException)
            {
                await Task.Delay(50);
            }
        }
    }
}
