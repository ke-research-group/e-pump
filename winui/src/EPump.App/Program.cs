using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace EPump.App;

/// <summary>
/// Hand-written entry point in place of the one the Windows App SDK generates, so that the
/// whole startup path — COM wrappers, the XAML application, the first window — sits inside a
/// try/catch that can log and display whatever went wrong. The generated Main cannot do that,
/// and without it a startup failure is a process that exits with no window and no message.
///
/// The project sets <c>WindowsAppSDKSelfContained</c>, so no runtime bootstrap call is needed
/// here: the App SDK binaries ship next to the exe and are loaded directly.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        StartupLog.WriteSessionHeader();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            StartupLog.Fatal("Unhandled exception (AppDomain)", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            StartupLog.Write("Unobserved task exception: " + e.Exception);
            e.SetObserved();
        };

        try
        {
            StartupLog.Write("Initialising COM wrappers");
            WinRT.ComWrappersSupport.InitializeComWrappers();

            StartupLog.Write("Starting the XAML application");
            Application.Start(callbackParams =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);

                StartupLog.Write("Constructing App");
                new App();
            });

            StartupLog.Write("Application.Start returned; exiting normally");
            return 0;
        }
        catch (Exception e)
        {
            StartupLog.Fatal("The application failed to start.", e);
            return 1;
        }
    }
}
