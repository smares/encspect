using System.Text;

namespace Encspect.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Reports are written as UTF-8 bytes; a Windows console shows them, and the file names on
        // stderr, in its own code page unless told otherwise.
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
            // No console to set, as when both streams are redirected.
        }

        using Stream stdout = Console.OpenStandardOutput();
        return CommandLine.Run(args, stdout, Console.Error);
    }
}
