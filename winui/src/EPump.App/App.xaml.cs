using Microsoft.UI.Xaml;

namespace EPump.App;

/// <summary>
/// The XAML application. Equivalent of the NI6451 application's App: construct the main window
/// and show it. The process entry point itself lives in <see cref="Program"/>.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        StartupLog.Write("App ctor: InitializeComponent");
        InitializeComponent();

        // XAML-thread exceptions do not reach AppDomain.UnhandledException reliably; this is
        // the hook that sees a failure inside a handler or during a layout pass.
        UnhandledException += (_, e) =>
        {
            StartupLog.Fatal("Unhandled exception in the UI", e.Exception);
            // Leave e.Handled false: continuing after an unknown UI failure risks a silent,
            // half-working pump controller, which is worse than an honest exit.
        };

        StartupLog.Write("App ctor: done");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupLog.Write("OnLaunched: constructing MainWindow");
        try
        {
            _window = new MainWindow();
            StartupLog.Write("OnLaunched: activating MainWindow");
            _window.Activate();
            StartupLog.Write("OnLaunched: window activated");
        }
        catch (Exception e)
        {
            StartupLog.Fatal("The main window could not be created.", e);
            throw;
        }
    }
}
