using Microsoft.AspNetCore.Components.WebView;

namespace Hypera.Maui;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
        blazorWebView.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
    }

    private static void OnBlazorWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
#if ANDROID
        if (e.WebView is Android.Webkit.WebView androidWebView)
        {
            Android.Webkit.WebSettings settings = androidWebView.Settings;
            settings.MediaPlaybackRequiresUserGesture = false;

            // Prevent Android OS dark mode from applying algorithmic darkening / inversion to game sprites and canvas.
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                settings.AlgorithmicDarkeningAllowed = false;
            }
            else if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
#pragma warning disable CA1422
                settings.ForceDark = Android.Webkit.ForceDarkMode.Off;
#pragma warning restore CA1422
            }
        }
#endif
    }
}
