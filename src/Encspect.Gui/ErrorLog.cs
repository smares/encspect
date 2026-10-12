namespace Encspect.Gui;

// Errors the GUI shows or recovers from are also written to stderr, with their stack trace, so a
// report from a user or a CI log carries what the window only summarizes.
public static class ErrorLog
{
    public static void Write(string context, Exception exception)
    {
        try
        {
            Console.Error.WriteLine($"encspect-gui: {context}: {exception}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}
