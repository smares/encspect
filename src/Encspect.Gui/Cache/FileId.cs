using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Encspect.Gui.Cache;

// What identifies a file on its volume beyond its path, so a file replaced by another under the
// same name, size and time is told apart: the volume serial number and file index on Windows, the
// device and inode on Linux and macOS. .NET has no public API for either, so they are read from an
// open handle through the operating system's own functions, never through private .NET APIs:
// GetFileInformationByHandle from kernel32 on Windows, statx on Linux and fstat on macOS, both
// looked up in the C library the process has loaded, as FileType finds open(2). Elsewhere (FreeBSD
// and other systems .NET may run on) there is no ID, and the cache relies on the other parts of
// its key.
public readonly record struct FileId(ulong Volume, ulong File)
{
    private static readonly Lazy<StatxFunction?> Statx = new(() => Find<StatxFunction>("statx"));

    // macOS on x64 keeps the old fstat, whose struct has a 32-bit inode, under the plain name and
    // the 64-bit one under fstat$INODE64; on arm64 only the 64-bit one exists, as fstat.
    private static readonly Lazy<FstatFunction?> Fstat = new(() =>
        Find<FstatFunction>(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "fstat$INODE64" : "fstat"));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    private delegate int StatxFunction(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    private delegate int FstatFunction(int descriptor, [Out] byte[] buffer);

    // Null where the system offers no ID or the call fails.
    public static FileId? Of(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            if (OperatingSystem.IsWindows())
            {
                return Windows(handle);
            }

            int descriptor = (int)handle.DangerousGetHandle();
            return OperatingSystem.IsLinux() ? Linux(descriptor)
                : OperatingSystem.IsMacOS() ? MacOs(descriptor)
                : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or MarshalDirectiveException)
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

    private static FileId? Windows(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out ByHandleFileInformation information)
            ? new FileId(information.VolumeSerialNumber, ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow)
            : null;

    // struct statx from the kernel's include/uapi/linux/stat.h, the same on every architecture:
    // stx_mask at 0, stx_ino at 32, stx_dev_major and stx_dev_minor at 136 and 140, 256 bytes in
    // all. The device is put together as glibc's makedev does, so it reads as st_dev does.
    private static FileId? Linux(int descriptor)
    {
        const int EmptyPath = 0x1000;
        const uint InodeMask = 0x100;
        if (Statx.Value is not { } statx)
        {
            return null;
        }

        byte[] buffer = new byte[256];
        if (statx(descriptor, string.Empty, EmptyPath, InodeMask, buffer) != 0 || (BitConverter.ToUInt32(buffer, 0) & InodeMask) == 0)
        {
            return null;
        }

        ulong major = BitConverter.ToUInt32(buffer, 136);
        ulong minor = BitConverter.ToUInt32(buffer, 140);
        ulong device = ((major & 0xFFFFF000) << 32) | ((major & 0xFFF) << 8) | ((minor & 0xFFFFFF00) << 12) | (minor & 0xFF);
        return new FileId(device, BitConverter.ToUInt64(buffer, 32));
    }

    // struct stat with 64-bit inodes from macOS's sys/stat.h, the same on x64 and arm64: st_dev,
    // a 32-bit dev_t, at 0 and st_ino at 8, 144 bytes in all.
    private static FileId? MacOs(int descriptor)
    {
        if (Fstat.Value is not { } fstat)
        {
            return null;
        }

        byte[] buffer = new byte[256];
        return fstat(descriptor, buffer) == 0 ? new FileId(BitConverter.ToUInt32(buffer, 0), BitConverter.ToUInt64(buffer, 8)) : null;
    }

    private static T? Find<T>(string name)
        where T : Delegate
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        return NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), name, out IntPtr address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    // BY_HANDLE_FILE_INFORMATION; each FILETIME is two 32-bit halves, so nothing is padded.
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
