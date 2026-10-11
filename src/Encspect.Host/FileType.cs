using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Encspect.Host;

// Opens a path for analysis only when it is a regular file. .NET has no public API for a file's
// type on Unix: FileSystemInfo reports a FIFO, a device, a socket and a procfs file alike as a
// regular file of length 0, and opening a FIFO for reading blocks until a writer appears. So the
// file is opened with POSIX open(2) and O_NONBLOCK, which returns at once for a FIFO, and what the
// open file does tells the rest: a FIFO or a terminal cannot seek, and a device or a kernel file
// reports a size of 0 yet yields data.
internal static class FileType
{
    private static readonly Lazy<OpenFunction?> NonBlockingOpen = new(FindOpen);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    private delegate int OpenFunction([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    // The open file, or null and what the path names when that is not a regular file. Throws
    // IOException or UnauthorizedAccessException when the file cannot be opened.
    public static FileStream? Open(string path, out string? notRegular)
    {
        notRegular = null;
        if (Directory.Exists(path))
        {
            notRegular = "a folder";
            return null;
        }

        FileStream? stream = OpenNonBlocking(path, out notRegular);
        if (notRegular is not null)
        {
            return null;
        }

        stream ??= new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            notRegular = !stream.CanSeek ? "a named pipe, socket or terminal"
                : stream.Length == 0 && YieldsData(stream) ? "a device or kernel file that reports a size of 0 but yields data"
                : null;
        }
        catch (IOException)
        {
            notRegular = "a file that cannot be read like a regular file";
        }

        if (notRegular is null)
        {
            return stream;
        }

        stream.Dispose();
        return null;
    }

    private static bool YieldsData(FileStream stream)
    {
        Span<byte> buffer = stackalloc byte[1];
        int read = stream.Read(buffer);
        stream.Position = 0;
        return read > 0;
    }

    // Null without a stream where open(2) is not available, so the caller opens the file as .NET
    // does.
    private static FileStream? OpenNonBlocking(string path, out string? notRegular)
    {
        notRegular = null;
        if (NonBlockingOpen.Value is not { } open || Flags() is not { } flags)
        {
            return null;
        }

        int descriptor = open(path, flags);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            string message = $"{Marshal.GetPInvokeErrorMessage(error)}: {path}";

            // The error numbers below are the same on Linux, macOS and FreeBSD. ENXIO: a socket, or
            // a device without a driver behind it.
            if (error == 6)
            {
                notRegular = "a socket or a device that cannot be opened";
                return null;
            }

            throw error switch
            {
                // ENOENT and ENOTDIR.
                2 or 20 => new FileNotFoundException(message, path),
                // EACCES and EPERM.
                13 or 1 => new UnauthorizedAccessException(message),
                _ => new IOException(message, error),
            };
        }

        return new FileStream(new SafeFileHandle(descriptor, ownsHandle: true), FileAccess.Read);
    }

    // O_RDONLY (0) | O_NONBLOCK | O_CLOEXEC, from each system's fcntl.h. O_NONBLOCK does not
    // change how a regular file is read.
    private static int? Flags() =>
        OperatingSystem.IsLinux() ? 0x800 | 0x80000
        : OperatingSystem.IsMacOS() ? 0x4 | 0x1000000
        : OperatingSystem.IsFreeBSD() ? 0x4 | 0x100000
        : null;

    // libc's open, looked up in the running process, which has loaded the C library on every Unix
    // .NET runs on, whatever the library's file is called (glibc, musl, macOS's libSystem).
    private static OpenFunction? FindOpen()
    {
        if (OperatingSystem.IsWindows() || Flags() is null)
        {
            return null;
        }

        return NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), "open", out IntPtr address)
            ? Marshal.GetDelegateForFunctionPointer<OpenFunction>(address)
            : null;
    }
}
