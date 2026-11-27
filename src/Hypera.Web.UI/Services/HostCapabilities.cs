using Microsoft.JSInterop;

namespace Hypera.Web.UI.Services;

/// <summary>
/// Represents a local package file selected directly from the host filesystem (bypassing browser upload buffering).
/// </summary>
public sealed record PickedLocalFile(string FullPath, string FileName, long ByteSize);

/// <summary>
/// Platform abstraction for entering and exiting fullscreen mode.
/// Implemented natively in <c>Hypera.Maui</c> (WinUI 3 <c>AppWindow</c> / Android immersive insets)
/// and via the browser Fullscreen API in <c>Hypera.Server</c>.
/// </summary>
public interface IHostWindowService
{
    bool IsFullscreen { get; }

    event Action<bool>? FullscreenChanged;

    Task<bool> SetFullscreenAsync(bool fullscreen);

    Task<bool> ToggleFullscreenAsync();
}

/// <summary>
/// Platform abstraction for native local file and folder pickers.
/// Enabled in <c>Hypera.Maui</c> so single-user desktop/mobile clients can import large <c>.zip</c>,
/// <c>.hep</c>, or unpacked game folders directly from disk without WebView stream limits.
/// </summary>
public interface ILocalFilePickerService
{
    bool SupportsLocalFilePick { get; }

    bool SupportsLocalFolderPick { get; }

    Task<PickedLocalFile?> PickPackageFileAsync(CancellationToken cancellationToken = default);

    Task<string?> PickGameFolderAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default browser-based fullscreen implementation used by <c>Hypera.Server</c>.
/// </summary>
public sealed class BrowserHostWindowService : IHostWindowService
{
    private readonly IJSRuntime _js;

    public BrowserHostWindowService(IJSRuntime js)
    {
        _js = js;
    }

    public bool IsFullscreen { get; private set; }

    public event Action<bool>? FullscreenChanged;

    public async Task<bool> SetFullscreenAsync(bool fullscreen)
    {
        try
        {
            bool actual = await _js.InvokeAsync<bool>("hyperaPlayer.setDocumentFullscreen", fullscreen);
            UpdateState(actual);
            return actual;
        }
        catch
        {
            // Fall back to CSS-only immersive fullscreen when browser API is blocked or unsupported.
            UpdateState(fullscreen);
            return fullscreen;
        }
    }

    public Task<bool> ToggleFullscreenAsync() => SetFullscreenAsync(!IsFullscreen);

    public void SyncFromBrowser(bool isFullscreen) => UpdateState(isFullscreen);

    private void UpdateState(bool value)
    {
        if (IsFullscreen == value)
        {
            return;
        }

        IsFullscreen = value;
        FullscreenChanged?.Invoke(value);
    }
}

/// <summary>
/// No-op fallback picker used in remote browser (<c>Hypera.Server</c>) sessions where uploads go through <c>MudFileUpload</c>.
/// </summary>
public sealed class NoOpLocalFilePickerService : ILocalFilePickerService
{
    public bool SupportsLocalFilePick => false;

    public bool SupportsLocalFolderPick => false;

    public Task<PickedLocalFile?> PickPackageFileAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<PickedLocalFile?>(null);

    public Task<string?> PickGameFolderAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
