using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Encspect.Gui.Views;

// Puts text on the clipboard of the window a control is in; a failure of the platform's clipboard
// is reported by its message and never thrown to the UI loop.
internal static class Copying
{
    // Tests set it to see what is copied: the headless platform has no system clipboard to read back, and nothing keeps the text.
    internal static Action<string>? Observer { get; set; }

    public static async Task<string?> SetTextAsync(Control control, string text)
    {
        Observer?.Invoke(text);
        try
        {
            if (TopLevel.GetTopLevel(control)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }

            return null;
        }
        catch (Exception ex) when (UnhandledErrors.IsRecoverable(ex))
        {
            return $"Not copied: {ex.Message}";
        }
    }
}
