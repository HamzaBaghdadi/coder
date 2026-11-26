using System.Globalization;
using Avalonia.Threading;

namespace Coder.App.Services;

/// <summary>
/// Last line of defense. An unexpected error on the UI thread is written to a log file with its full
/// stack trace and reported to the user, instead of terminating the app and losing unsaved work.
/// This never replaces fixing the bug: every logged error is something to fix.
/// </summary>
public static class CrashReporter
{
    /// <summary>Where logs go; <c>--user-data-dir</c> moves it.</summary>
    public static string LogDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Coder", "logs");

    /// <param name="notify">Shows a short message to the user (e.g. in the status bar).</param>
    public static void Install(Action<string> notify)
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            var logFile = Write(e.Exception, "UI thread");
            e.Handled = true;
            notify($"Something went wrong and was recovered. Details: {logFile}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception, "background task");
            e.SetObserved();
        };

        // Not recoverable, but the log tells us why it happened.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                Write(exception, "fatal");
            }
        };
    }

    /// <summary>Appends the error to today's log and returns the log file path.</summary>
    public static string Write(Exception exception, string context)
    {
        var file = Path.Combine(LogDirectory, $"coder-{DateTime.Now:yyyy-MM-dd}.log");
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(file, string.Create(CultureInfo.InvariantCulture,
                $"[{DateTime.Now:O}] Unhandled exception ({context}){Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}"));
        }
        catch (IOException)
        {
            // Logging must never cause a second failure.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return file;
    }
}
