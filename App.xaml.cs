using Microsoft.UI.Xaml;

namespace GetSoundBoard;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        RequestedTheme = ApplicationTheme.Dark;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
        if (_window is MainWindow main &&
            Environment.GetCommandLineArgs().Any(argument =>
                string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase)))
            main.StartInTray();
    }
}
