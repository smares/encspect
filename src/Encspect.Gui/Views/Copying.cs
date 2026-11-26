using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Encspect.Gui.Views;

// Puts text on the clipboard of the window a control is in; a failure of the platform's clipboard
// is reported by its message and never thrown to the UI loop.
internal static class Copying
{
    // Text put on the clipboard last, for tests: the headless platform has no system clipboard to read back reliably.
    internal static string? Last { get; private set; }

    public static async Task<string?> SetTextAsync(Control control, string text)
    {
        Last = text;
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
