using Avalonia.Threading;

namespace Encspect.Gui.ViewModels;

// View models change their state only on the UI thread. Background work hands its results over
// through a post function, which tests replace with a queue they drain themselves.
public static class UiThread
{
    // Read on the thread that builds the view model. Background work then posts to that
    // dispatcher and never reads Dispatcher.UIThread itself: when nothing holds that slot, as
    // between headless tests, reading it makes the calling pool thread the UI thread.
    public static Action<Action> Post
    {
        get
        {
            Dispatcher dispatcher = Dispatcher.UIThread;
            return action => dispatcher.Post(action);
        }
    }
}
