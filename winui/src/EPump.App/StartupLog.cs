using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EPump.App;

/// <summary>
/// Breadcrumb and crash log for the one failure mode that is otherwise undiagnosable: the
/// exe is double-clicked and nothing appears. A GUI process has no console, so an exception
/// before the first window is shown simply ends the process in silence.
///
/// Every line goes to <c>%LOCALAPPDATA%\EPump\startup.log</c>, and a fatal error is also put
/// on screen with a raw Win32 message box — which works even when the XAML runtime is the
/// thing that failed to load.
/// </summary>
internal static class StartupLog
{
    private static readonly object Gate = new();
    private static readonly string? LogPath = ResolveLogPath();

    public static string Location => LogPath ?? "(log file unavailable)";

    public static void Write(string message)
    {
        if (LogPath is null) return;

        try
        {
            lock (Gate)
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging must never be the thing that takes the app down.
        }
    }

    private static int _shown;

    /// <summary>
    /// Record an exception and show it to the user, then let the caller decide whether to
    /// continue. One failure surfaces through several layers (control, window, App, the
    /// XAML runtime's own handler), so only the first -- the innermost, most specific one --
    /// gets a message box; the rest are logged only.
    /// </summary>
    public static void Fatal(string context, Exception exception)
    {
        Write($"FATAL: {context}{Environment.NewLine}{exception}");
        if (Interlocked.Exchange(ref _shown, 1) != 0) return;

        var text = new StringBuilder()
            .AppendLine(context)
            .AppendLine()
            .AppendLine(exception.GetType().Name + ": " + exception.Message)
            .AppendLine()
            .AppendLine("Full details were written to:")
            .AppendLine(Location)
            .ToString();

        try
        {
            _ = MessageBoxW(0, text, "ReaXus CP Pump Control — startup error", MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>One-time header so a log from a lab machine explains itself.</summary>
    public static void WriteSessionHeader()
    {
        Write("==================================================================");
        Write($"EPump starting  pid={Environment.ProcessId}");
        Write($"exe      : {Environment.ProcessPath}");
        Write($"cwd      : {Environment.CurrentDirectory}");
        Write($"os       : {Environment.OSVersion} ({RuntimeInformation.OSArchitecture}), {RuntimeInformation.FrameworkDescription}");
        Write($"process  : {RuntimeInformation.ProcessArchitecture}, 64-bit={Environment.Is64BitProcess}");
    }

    private static string? ResolveLogPath()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EPump");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "startup.log");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_SETFOREGROUND = 0x10000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
