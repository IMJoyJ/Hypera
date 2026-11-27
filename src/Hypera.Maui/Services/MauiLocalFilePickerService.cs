using Hypera.Web.UI.Services;

namespace Hypera.Maui.Services;

/// <summary>
/// Native MAUI file and folder picker allowing direct local import of <c>.zip</c>, <c>.hep</c>,
/// and unpacked game directories without WebView upload buffering limits.
/// </summary>
public sealed class MauiLocalFilePickerService : ILocalFilePickerService
{
    public bool SupportsLocalFilePick => true;

    public bool SupportsLocalFolderPick => OperatingSystem.IsWindows();

    public async Task<PickedLocalFile?> PickPackageFileAsync(CancellationToken cancellationToken = default)
    {
        var fileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.WinUI] = new[] { ".zip", ".hep" },
            [DevicePlatform.Android] = new[] { "application/zip", "application/x-zip-compressed", "application/octet-stream", "*/*" },
            [DevicePlatform.iOS] = new[] { "public.zip-archive", "public.data", "public.item" },
            [DevicePlatform.MacCatalyst] = new[] { "public.zip-archive", "public.data", "public.item" },
        });

        FileResult? picked = await MainThread.InvokeOnMainThreadAsync(() =>
            FilePicker.Default.PickAsync(new PickOptions
            {
                FileTypes = fileTypes,
            }));

        if (picked is null)
        {
            return null;
        }

        string fileName = Path.GetFileName(picked.FileName);
        string ext = Path.GetExtension(fileName);
        if (!string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ext, ".hep", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported package file '{fileName}'. Please select a .zip or .hep file.");
        }

        string resolvedPath;
        if (!string.IsNullOrWhiteSpace(picked.FullPath) && File.Exists(picked.FullPath))
        {
            resolvedPath = picked.FullPath;
        }
        else
        {
            string cacheDir = Path.Combine(FileSystem.CacheDirectory, "picked");
            Directory.CreateDirectory(cacheDir);
            resolvedPath = Path.Combine(cacheDir, $"{Guid.NewGuid():N}_{fileName}");
            await using Stream source = await picked.OpenReadAsync();
            await using var destination = new FileStream(
                resolvedPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);
            await source.CopyToAsync(destination, cancellationToken);
        }

        long byteSize = new FileInfo(resolvedPath).Length;
        return new PickedLocalFile(resolvedPath, fileName, byteSize);
    }

    public async Task<string?> PickGameFolderAsync(CancellationToken cancellationToken = default)
    {
#if WINDOWS
        return await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            };
            picker.FileTypeFilter.Add("*");

            Microsoft.Maui.Controls.Window? mauiWindow = Application.Current?.Windows.FirstOrDefault();
            if (mauiWindow?.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
            {
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                if (hwnd != 0)
                {
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                }
            }

            Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        });
#else
        await Task.CompletedTask;
        return null;
#endif
    }
}
