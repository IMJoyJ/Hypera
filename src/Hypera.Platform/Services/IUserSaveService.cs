using Hypera.VirtualFs;

namespace Hypera.Platform.Services;

public sealed record UserSaveFileEntry(
    string RelativePath,
    long ByteSize,
    DateTime LastModifiedUtc);

/// <summary>
/// Manages per-user, per-app sandboxed save directories (<see cref="WritableSaveDirectory"/>) shared seamlessly
/// across all versions and language variants of the same <c>GameApp</c>.
/// </summary>
public interface IUserSaveService
{
    WritableSaveDirectory GetOrCreateSaveDirectory(
        Guid userId,
        Guid appId,
        IEnumerable<string>? validTextExtensions = null);

    Task<IReadOnlyList<UserSaveFileEntry>> ListSaveFilesAsync(
        Guid userId,
        Guid appId,
        CancellationToken cancellationToken = default);

    Task<UserSaveFileEntry> ImportSaveFileAsync(
        Guid userId,
        Guid appId,
        string relativePath,
        Stream contentStream,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenSaveFileStreamAsync(
        Guid userId,
        Guid appId,
        string relativePath,
        CancellationToken cancellationToken = default);

    Task ExportSavesZipAsync(
        Guid userId,
        Guid appId,
        Stream destinationZipStream,
        CancellationToken cancellationToken = default);

    Task ImportSavesZipAsync(
        Guid userId,
        Guid appId,
        Stream sourceZipStream,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default);

    Task DeleteSaveFileAsync(
        Guid userId,
        Guid appId,
        string relativePath,
        CancellationToken cancellationToken = default);

    Task ClearAllSavesAsync(
        Guid userId,
        Guid appId,
        CancellationToken cancellationToken = default);
}
