using Hypera.Web.UI.Services;

namespace Hypera.Maui.Services;

/// <summary>
/// Native MAUI fullscreen host controller:
/// WinUI 3 <c>AppWindow.SetPresenter(FullScreen)</c> on Windows and Android edge-to-edge immersive system bars on Android.
/// </summary>
public sealed class MauiHostWindowService : IHostWindowService
{
#if WINDOWS
    private Microsoft.UI.Windowing.AppWindow? _trackedAppWindow;
#endif

    public bool IsFullscreen { get; private set; }

    public event Action<bool>? FullscreenChanged;

    public async Task<bool> SetFullscreenAsync(bool fullscreen)
    {
        bool actual = await MainThread.InvokeOnMainThreadAsync(() => ApplyNativeFullscreen(fullscreen));
        UpdateState(actual);
        return actual;
    }

    public Task<bool> ToggleFullscreenAsync() => SetFullscreenAsync(!IsFullscreen);

    private bool ApplyNativeFullscreen(bool fullscreen)
    {
#if WINDOWS
        Microsoft.UI.Windowing.AppWindow? appWindow = GetWindowsAppWindow();
        if (appWindow is null)
        {
            return fullscreen;
        }

        EnsureWindowsPresenterTracking(appWindow);
        appWindow.SetPresenter(fullscreen
            ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
            : Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);

        return appWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
#elif ANDROID
        Android.App.Activity? activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        Android.Views.Window? window = activity?.Window;
        if (window is null)
        {
            return fullscreen;
        }

        AndroidX.Core.View.WindowCompat.SetDecorFitsSystemWindows(window, !fullscreen);
        Android.Views.View? decorView = window.DecorView;
        AndroidX.Core.View.WindowInsetsControllerCompat? controller = decorView is not null
            ? AndroidX.Core.View.WindowCompat.GetInsetsController(window, decorView)
            : null;
        if (controller is not null)
        {
            if (fullscreen)
            {
                controller.SystemBarsBehavior =
                    AndroidX.Core.View.WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
                controller.Hide(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars());
            }
            else
            {
                controller.Show(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars());
            }
        }

        return fullscreen;
#else
        return fullscreen;
#endif
    }

#if WINDOWS
    private static Microsoft.UI.Windowing.AppWindow? GetWindowsAppWindow()
    {
        Microsoft.Maui.Controls.Window? mauiWindow = Application.Current?.Windows.FirstOrDefault();
        if (mauiWindow?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
        {
            return null;
        }

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
        if (hwnd == 0)
        {
            return null;
        }

        Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
    }

    private void EnsureWindowsPresenterTracking(Microsoft.UI.Windowing.AppWindow appWindow)
    {
        if (ReferenceEquals(_trackedAppWindow, appWindow))
        {
            return;
        }

        if (_trackedAppWindow is not null)
        {
            _trackedAppWindow.Changed -= OnWindowsAppWindowChanged;
        }

        _trackedAppWindow = appWindow;
        _trackedAppWindow.Changed += OnWindowsAppWindowChanged;
    }

    private void OnWindowsAppWindowChanged(
        Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange)
        {
            return;
        }

        bool isFs = sender.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
        UpdateState(isFs);
    }
#endif

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
