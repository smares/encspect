using System.Runtime.InteropServices;

namespace Encspect.Host;

// Tells a regular file from anything else before the file is opened. .NET has no API for a file's
// type on Unix: FileSystemInfo reports a FIFO, a device or a socket like a regular file of length
// 0, and opening a FIFO for reading blocks until a writer appears.
internal static class FileType
{
    // The S_IF* values of System.Native's FileStatus.Mode, the same on every Unix .NET runs on.
    private const int TypeMask = 0xF000;
    private const int Fifo = 0x1000;
    private const int CharacterDevice = 0x2000;
    private const int Directory = 0x4000;
    private const int BlockDevice = 0x6000;
    private const int Regular = 0x8000;
    private const int Socket = 0xC000;

    // Kernel file systems whose files report a size of 0 or a fixed 4096 bytes and whose content
    // is made up when read; some block on read, like tracefs's trace_pipe. Names as DriveInfo
    // gives them on Linux, with the kernel's spelling where the two may differ.
    private static readonly HashSet<string> PseudoFileSystems = new(
        ["proc", "sysfs", "debugfs", "tracefs", "securityfs", "configfs", "cgroup", "cgroupfs", "cgroup2", "cgroup2fs",
         "pstore", "pstorefs", "efivarfs", "bpf", "bpf_fs", "fusectl", "binfmt_misc", "selinux", "selinuxfs", "mqueue"],
        StringComparer.Ordinal);

    private static readonly Lazy<bool> StatWorks = new(CheckStat);

    // What the path names when that is not a regular file, as "a named pipe (FIFO)", or null for a
    // regular file and for a path whose type cannot be told, which opening it then decides.
    public static string? DescribeNonRegular(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        if (StatWorks.Value && Stat(path) is { } mode)
        {
            string? kind = (mode & TypeMask) switch
            {
                Regular => null,
                Fifo => "a named pipe (FIFO)",
                CharacterDevice => "a character device",
                BlockDevice => "a block device",
                Socket => "a socket",
                Directory => "a folder",
                _ => "a special file",
            };
            if (kind is not null)
            {
                return kind;
            }
        }

        return OperatingSystem.IsLinux() && PseudoFileSystem(path) is { } system ? $"a file of the kernel's {system} file system" : null;
    }

    private static int? Stat(string path)
    {
        try
        {
            return SystemNativeStat(path, out FileStatus status) == 0 ? status.Mode : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    // System.Native is the runtime's own shim, present in every Unix build of .NET, framework-
    // dependent, self-contained or single-file. libc's stat is not used because struct stat differs
    // by platform and glibc before 2.33 exports no stat symbol. The shim is internal to .NET, so its
    // answers for a known folder and a known regular file are checked once before it is trusted;
    // without it, a FIFO is still caught after opening as a stream that cannot seek, if it has a
    // writer.
    private static bool CheckStat()
    {
        try
        {
            return Stat(AppContext.BaseDirectory) is { } folder && (folder & TypeMask) == Directory
                && Environment.ProcessPath is { } process && Stat(process) is { } file && (file & TypeMask) == Regular;
        }
        catch (Exception ex) when (ex is MarshalDirectiveException or BadImageFormatException)
        {
            return false;
        }
    }

    // statfs(2) on the path itself, which need not be a mount point.
    private static string? PseudoFileSystem(string path)
    {
        try
        {
            string format = new DriveInfo(path).DriveFormat;
            return PseudoFileSystems.Contains(format) ? format : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat")]
    private static extern int SystemNativeStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out FileStatus status);

    // Only Mode, after a 32-bit Flags field, is read. The size leaves room for the whole native
    // struct (about 130 bytes in .NET 10) should it grow.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct FileStatus
    {
        [FieldOffset(4)]
        public int Mode;
    }
}
