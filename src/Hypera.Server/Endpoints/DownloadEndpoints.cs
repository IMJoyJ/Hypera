using Hypera.Platform.Services;
using Hypera.Server.Auth;

namespace Hypera.Server.Endpoints;

internal static class DownloadEndpoints
{
    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/downloads/source/{submissionId:guid}", HandleDownloadSourceAsync)
            .RequireAuthorization();

        endpoints.MapGet("/api/downloads/saves/{appId:guid}/zip", HandleDownloadSavesZipAsync)
            .RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> HandleDownloadSourceAsync(
        Guid submissionId,
        HttpContext httpContext,
        IPackagePipelineService packagePipeline,
        CancellationToken cancellationToken)
    {
        if (!AuthClaimsFactory.TryGetUserId(httpContext.User, out Guid userId))
        {
            return Results.Unauthorized();
        }

        try
        {
            Stream zipStream = await packagePipeline.OpenSourceZipStreamAsync(
                userId,
                submissionId,
                cancellationToken);

            return Results.File(
                zipStream,
                contentType: "application/zip",
                fileDownloadName: $"source-{submissionId:N}.zip",
                enableRangeProcessing: true);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> HandleDownloadSavesZipAsync(
        Guid appId,
        HttpContext httpContext,
        IUserSaveService saveService,
        CancellationToken cancellationToken)
    {
        if (!AuthClaimsFactory.TryGetUserId(httpContext.User, out Guid userId))
        {
            return Results.Unauthorized();
        }

        string tempZipPath = Path.Combine(Path.GetTempPath(), $"hypera-saves-{Guid.NewGuid():N}.zip");
        FileStream? exportStream = null;

        try
        {
            exportStream = new FileStream(
                tempZipPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.DeleteOnClose | FileOptions.Asynchronous);

            await saveService.ExportSavesZipAsync(userId, appId, exportStream, cancellationToken);
            exportStream.Position = 0;

            FileStream responseStream = exportStream;
            exportStream = null;

            return Results.File(
                responseStream,
                contentType: "application/zip",
                fileDownloadName: $"saves-{appId:N}.zip",
                enableRangeProcessing: true);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException)
        {
            return Results.NotFound();
        }
        finally
        {
            if (exportStream is not null)
            {
                await exportStream.DisposeAsync();
            }
        }
    }
}
