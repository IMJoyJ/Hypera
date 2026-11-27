namespace Hypera.Maui;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage())
        {
            Title = "Hypera",
        };

#if WINDOWS || MACCATALYST
        window.Width = 1280;
        window.Height = 820;
        window.MinimumWidth = 720;
        window.MinimumHeight = 520;
#endif

        return window;
    }
}
