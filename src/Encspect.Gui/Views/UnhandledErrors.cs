using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Encspect.Gui.Views;

// The last resort for exceptions nothing else caught; each is written to stderr first.
//
// On the UI thread, an error the system raised for one operation, such as a file or the clipboard
// refusing access, leaves the state as it was: it shows in the main window's status bar and the app
// carries on. Any other exception may have left a view model half changed, so every window is
// disabled and the error is shown until it is acknowledged, then the app quits; a second one while
// that message shows ends the app at once. An exception on another thread ends the process whatever
// is done, so that thread waits while the UI thread shows the error.
public static class UnhandledErrors
{
    private static readonly TimeSpan ShowTimeout = TimeSpan.FromSeconds(2);

    private static int _isFatalShown;

    // For the process, before the app starts.
    public static void InstallForProcess()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => OnOtherThread(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Write("unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    // For the desktop app's UI thread; tests leave it out, so their exceptions still fail them.
    public static void InstallForUiThread() =>
        Dispatcher.UIThread.UnhandledException += (_, e) => e.Handled = Handle(e.Exception, MainWindowOfApp());

    // Whether the exception leaves the app's state sound: an error of the system for one
    // operation, not a defect of the app.
    public static bool IsRecoverable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Exception inner = Unwrap(exception);
        return inner is IOException or UnauthorizedAccessException or (ExternalException and not SEHException);
    }

    // Handles an exception that reached the UI thread's loop; true when the app carries on, for
    // the moment (a fatal error's message) or for good (a recoverable one).
    internal static bool Handle(Exception exception, MainWindow? main)
    {
        ErrorLog.Write("unhandled exception on the UI thread", exception);
        if (IsRecoverable(exception) && main is not null)
        {
            main.Model.ShowError(Unwrap(exception).Message);
            return true;
        }

        return ShowFatal(exception, main, quitWhenClosed: true, closed: null);
    }

    private static void OnOtherThread(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        ErrorLog.Write("unhandled exception", exception);
        if (Dispatcher.UIThread.CheckAccess() || Application.Current is null)
        {
            return;
        }

        using var shown = new ManualResetEventSlim();
        using var closed = new ManualResetEventSlim();
        Dispatcher.UIThread.Post(() =>
        {
            if (ShowFatal(exception, MainWindowOfApp(), quitWhenClosed: false, closed.Set))
            {
                shown.Set();
            }
        });

        if (shown.Wait(ShowTimeout))
        {
            closed.Wait();
        }
    }

    private static bool ShowFatal(Exception exception, MainWindow? main, bool quitWhenClosed, Action? closed)
    {
        if (Interlocked.Exchange(ref _isFatalShown, 1) == 1)
        {
            return false;
        }

        try
        {
            IClassicDesktopStyleApplicationLifetime? desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            foreach (Window window in desktop?.Windows ?? [])
            {
                window.IsEnabled = false;
            }

            var message = new ErrorWindow("Encspect has to quit because of an unexpected error.", Unwrap(exception), "Quit");
            message.Closed += (_, _) =>
            {
                Volatile.Write(ref _isFatalShown, 0);
                closed?.Invoke();
                if (quitWhenClosed)
                {
                    desktop?.Shutdown(1);
                }
            };

            if (main is { IsVisible: true })
            {
                _ = message.ShowDialog(main);
            }
            else
            {
                message.Show();
            }

            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("the error could not be shown", ex);
            return false;
        }
    }

    private static MainWindow? MainWindowOfApp() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow main } ? main : null;

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } || exception is AggregateException { InnerExceptions.Count: 1 })
        {
            exception = exception is AggregateException aggregate ? aggregate.InnerExceptions[0] : exception.InnerException!;
        }

        return exception;
    }
}
