using Avalonia.Threading;

namespace Encspect.Gui.Tests;

// Runs the UI thread's queue from a headless test until a condition holds.
internal static class UiPump
{
    public static void Until(Func<bool> condition, int timeoutMilliseconds = 20000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMilliseconds)
            {
                throw new TimeoutException("The condition did not hold in time.");
            }

            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();
    }
}

// A UI thread for view-model tests without Avalonia: posts queue up until the test runs them.
internal sealed class TestDispatcher
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _withheld = new();

    // Decides for each posted action whether it is withheld: never run, so that a test can have the
    // UI thread miss a post it would otherwise get.
    public Func<Action, bool>? Withhold { get; init; }

    public int WithheldCount => _withheld.Count;

    public Action<Action> Post => action =>
    {
        if (Withhold?.Invoke(action) == true)
        {
            _withheld.Enqueue(action);
        }
        else
        {
            _queue.Enqueue(action);
        }
    };

    public void RunPending()
    {
        while (_queue.TryDequeue(out Action? action))
        {
            action();
        }
    }

    public void Until(Func<bool> condition, int timeoutMilliseconds = 20000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        RunPending();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMilliseconds)
            {
                throw new TimeoutException("The condition did not hold in time.");
            }

            Thread.Sleep(5);
            RunPending();
        }
    }
}
