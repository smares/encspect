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

    public Action<Action> Post => _queue.Enqueue;

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
