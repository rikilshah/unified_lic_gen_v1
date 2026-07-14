using Microsoft.UI.Xaml;

namespace UnifiedLicGen;

public partial class App : Application
{
    private MainWindow? _window;
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnifiedLicGen",
        "startup.log");

    public App()
    {
        InitializeComponent();
        RequestedTheme = ApplicationTheme.Dark;
        UnhandledException += (_, args) => WriteStartupError("Unhandled XAML exception", args.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            WriteStartupError("Window creation failed", ex);
            throw;
        }
    }

    private static void WriteStartupError(string context, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {context}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch
        {
            // Startup diagnostics must never mask the original exception.
        }
    }
}
