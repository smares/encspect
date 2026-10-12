using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Encspect.Host;

// Opens a path for analysis only when it is a regular file. .NET has no public API for a file's
// type on Unix: FileSystemInfo reports a FIFO, a device, a socket and a procfs file alike as a
// regular file of length 0, and opening a FIFO for reading blocks until a writer appears. So the
// file is opened with POSIX open(2) and O_NONBLOCK, which returns at once for a FIFO, and what the
// open file does tells the rest: a FIFO or a terminal cannot seek, and a device or a kernel file
// reports a size of 0 yet yields data. A device that yields nothing, such as /dev/null, reads like
// an empty file, so only then is the file's type asked of the system: statx on Linux and fstat on
// macOS, looked up as open(2) is.
public static class FileType
{
    private static readonly Lazy<OpenFunction?> NonBlockingOpen = new(() => Find<OpenFunction>("open"));
    private static readonly Lazy<StatxFunction?> Statx = new(() => OperatingSystem.IsLinux() ? Find<StatxFunction>("statx") : null);

    // macOS on x64 keeps the fstat of 32-bit inodes under the plain name and the 64-bit one under
    // fstat$INODE64; on arm64 only the 64-bit one exists, as fstat. st_mode is at 4 in both.
    private static readonly Lazy<FstatFunction?> Fstat = new(() =>
        OperatingSystem.IsMacOS() ? Find<FstatFunction>(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "fstat$INODE64" : "fstat") : null);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    private delegate int OpenFunction([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    private delegate int StatxFunction(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    private delegate int FstatFunction(int descriptor, [Out] byte[] buffer);

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
                : stream.Length > 0 ? null
                : YieldsData(stream) ? "a device or kernel file that reports a size of 0 but yields data"
                : IsDevice(stream.SafeFileHandle) ? "a device"
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

    // Whether the system says the open file is a character or block device; false where it cannot
    // say. S_IFMT from sys/stat.h, the same on Linux and macOS.
    private static bool IsDevice(SafeFileHandle handle)
    {
        const int TypeMask = 0xF000;
        const int CharacterDevice = 0x2000;
        const int BlockDevice = 0x6000;
        if (Mode(handle) is not { } mode)
        {
            return false;
        }

        return (mode & TypeMask) is CharacterDevice or BlockDevice;
    }

    private static int? Mode(SafeFileHandle handle)
    {
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            int descriptor = (int)handle.DangerousGetHandle();
            byte[] buffer = new byte[256];

            // struct statx from the kernel's include/uapi/linux/stat.h, the same on every
            // architecture: stx_mask at 0, stx_mode at 28, 256 bytes in all.
            if (Statx.Value is { } statx)
            {
                const int EmptyPath = 0x1000;
                const uint TypeField = 0x1;
                return statx(descriptor, string.Empty, EmptyPath, TypeField, buffer) == 0 && (BitConverter.ToUInt32(buffer, 0) & TypeField) != 0
                    ? BitConverter.ToUInt16(buffer, 28)
                    : null;
            }

            // struct stat with 64-bit inodes from macOS's sys/stat.h: st_mode at 4, 144 bytes.
            if (Fstat.Value is { } fstat)
            {
                return fstat(descriptor, buffer) == 0 ? BitConverter.ToUInt16(buffer, 4) : null;
            }

            return null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or MarshalDirectiveException)
        {
            return null;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
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

    // A function of libc, looked up in the running process, which has loaded the C library on
    // every Unix .NET runs on, whatever the library's file is called (glibc, musl, macOS's
    // libSystem).
    private static T? Find<T>(string name)
        where T : Delegate
    {
        if (OperatingSystem.IsWindows() || Flags() is null)
        {
            return null;
        }

        return NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), name, out IntPtr address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;
    }
}
