namespace Encspect.Gui.Tests;

internal static class Fixtures
{
    public static string Directory { get; } = Path.Combine(FindRepositoryRoot(), "tests", "fixtures");

    public static string Mpeg(string name) => Path.Combine(Directory, "mpeg", name);

    public static string Stats(string name) => Path.Combine(Directory, "stats", name);

    public static string Aac(string name) => Path.Combine(Directory, "aac", name);

    public static string Ogg(string name) => Path.Combine(Directory, "ogg", name);

    private static string FindRepositoryRoot()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "Encspect.slnx")))
            {
                return directory;
            }
        }

        throw new InvalidOperationException("The repository root was not found.");
    }
}

// A temporary folder of test inputs, deleted with everything in it.
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Directory.CreateTempSubdirectory("encspect-gui-").FullName;

    public string this[string relative] => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string Copy(string source, string relative)
    {
        string target = this[relative];
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.Copy(source, target);
        return target;
    }

    public string Write(string relative, byte[] bytes)
    {
        string target = this[relative];
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
        return target;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
