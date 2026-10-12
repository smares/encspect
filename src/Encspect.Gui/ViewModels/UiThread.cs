using Avalonia.Threading;

namespace Encspect.Gui.ViewModels;

// View models change their state only on the UI thread. Background work hands its results over
// through a post function, which tests replace with a queue they drain themselves.
public static class UiThread
{
    public static Action<Action> Post { get; } = action => Dispatcher.UIThread.Post(action);
}
