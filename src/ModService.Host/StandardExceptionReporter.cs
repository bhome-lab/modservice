using System.Text;
using System.Windows.Forms;
using Serilog;

namespace ModService.Host;

internal static class StandardExceptionReporter
{
    private static int _installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
        {
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) =>
        {
            Report("Unhandled WinForms exception", eventArgs.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                Report("Unhandled AppDomain exception", exception);
                return;
            }

            Report("Unhandled AppDomain exception", new InvalidOperationException(
                $"Unhandled non-exception object: {eventArgs.ExceptionObject}"));
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Report("Unobserved task exception", eventArgs.Exception);
            eventArgs.SetObserved();
        };
    }

    public static void Report(string title, Exception exception)
    {
        var message = new StringBuilder()
            .Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] ")
            .Append(title).AppendLine()
            .AppendLine(exception.ToString())
            .ToString();

        try
        {
            Log.Error(exception, "{ExceptionTitle}", title);
        }
        catch
        {
        }

        try
        {
            Console.Error.WriteLine(message);
            Console.Error.Flush();
        }
        catch
        {
        }
    }
}
