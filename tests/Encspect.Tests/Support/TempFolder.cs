using System.Diagnostics;

namespace Encspect.Tests;

// A temporary folder of test inputs. FIFOs and names that are not valid UTF-8 are made with the
// system's tools, since .NET cannot make them, so tests that need them run on Linux and macOS only.
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("encspect-").FullName;

    public string this[string relative] => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string Copy(string source, string relative)
    {
        string target = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.Copy(source, target);
        return target;
    }

    public string MakeFifo(string relative)
    {
        string fifo = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fifo)!);
        Assert.True(Exec("mkfifo", fifo), "mkfifo failed");
        return fifo;
    }

    // Runs a program and tells whether it exited with 0.
    public static bool Exec(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.Delete(Path, recursive: true);
            return;
        }

        // Unlike Directory.Delete, rm removes locked folders after the chmod and names that are not
        // valid UTF-8.
        Exec("chmod", "-R", "u+rwx", Path);
        Exec("rm", "-rf", Path);
    }
}
