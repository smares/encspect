// Open question 1 and 9 of docs/design/official-decoder-crosschecks.md: which audio decoders this Windows image has.
// MFTEnumEx over the audio decoder category with and without the field-of-use and hardware flags, DMOEnum, and
// CoCreateInstance of the decoders the design names. Nothing is decoded.
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WmaDecodeProbe;

public static class Inventory
{
    private static readonly Guid AudioDecoderMft = new("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7");
    private static readonly Guid AudioDecoderDmo = new("57f2db8b-e6bb-4513-9d43-dcd2a6593125");
    private static Guid friendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    private static Guid inputTypes = new("4276c9b1-759d-4bf3-9cd0-0d723d138f96");
    private static Guid transformClsid = new("6821c42b-65a4-4e82-99bc-9a88205ecd0c");

    public static void Run(Dictionary<string, object?> record)
    {
        void Set(string key, object? value)
        {
            lock (SourceReaderProbe.Gate)
            {
                record[key] = value;
            }
        }

        Set("startupHresult", Format.Hr(Native.MFStartup(Native.MfVersion, 0)));
        // 0x3F: sync, async, hardware, field of use, local and transcode only. 0x43F adds the untrusted Store MFTs (0x400), which a Store-packaged
        // decoder such as a Dolby extension needs. 0: the flags an application gets by default (the API reads them as 0x51).
        Set("mftAll", Enumerate(0x3F));
        Set("mftWithStore", Enumerate(0x43F));
        Set("mftDefault", Enumerate(0));
        Set("dmo", EnumerateDmo());
        var created = new List<Dictionary<string, object?>>();
        Set("createInstance", created);
        foreach (var (name, clsid) in new[]
        {
            ("CLSID_CMSDDPlusDecMFT (Dolby Digital and Digital Plus decoder)", "177c0afe-900b-48d4-9e4c-57add250b3d4"),
            ("CLSID_CMSAACDecMFT (AAC decoder)", "32d186a7-218f-4c75-8876-dd77273a8999"),
        })
        {
            var entry = new Dictionary<string, object?> { ["name"] = name, ["clsid"] = clsid };
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid(clsid), true)!;
                var instance = Activator.CreateInstance(type)!;
                entry["created"] = true;
                entry["transform"] = instance is IMFTransform;
                if (instance is IMFTransform transform)
                {
                    var subtypes = new List<string>();
                    for (uint i = 0; i < 64; i++)
                    {
                        if (transform.GetInputAvailableType(0, i, out var type2) != 0 || type2 == null)
                        {
                            break;
                        }

                        subtypes.Add(DescribeSubtype(type2));
                        Marshal.ReleaseComObject(type2);
                    }

                    entry["inputTypesOffered"] = subtypes;
                }

                Marshal.ReleaseComObject(instance);
            }
            catch (Exception e)
            {
                entry["created"] = false;
                entry["error"] = e.GetType().Name + ": " + e.Message;
                entry["hresult"] = Format.Hr(e.HResult);
            }

            lock (SourceReaderProbe.Gate)
            {
                created.Add(entry);
            }
        }

        var dlls = new SortedDictionary<string, string?>();
        foreach (var d in new[] { "MSAudDecMFT.dll", "Msauddecmft.dll", "mfmp4srcsnk.dll", "mfsrcsnk.dll", "mfmpeg4.dll", "mfaudiocnv.dll", "mfcore.dll", "mfplat.dll", "mfreadwrite.dll", "mf.dll", "wmadmod.dll", "wmvdecod.dll" })
        {
            dlls[d] = Versions.Of(Path.Combine(Environment.SystemDirectory, d));
        }

        Set("dlls", dlls);
        Set("os", Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", null)?.ToString());
        Set("build", Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", null)?.ToString());
        Set("ubr", Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null)?.ToString());
        Set("installationType", Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "InstallationType", null)?.ToString());
        Set("architecture", RuntimeInformation.OSArchitecture.ToString());
        Native.MFShutdown();
    }

    private static string DescribeSubtype(IMFMediaType type)
    {
        if (Native.MFCreateWaveFormatExFromMFMediaType(type, out var pointer, out uint size, 0) < 0 || pointer == IntPtr.Zero)
        {
            return "no WAVEFORMATEX";
        }

        try
        {
            var bytes = new byte[Math.Min(size, 64u)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return "tag 0x" + (bytes.Length >= 2 ? BitConverter.ToUInt16(bytes, 0) : 0).ToString("X4");
        }
        finally
        {
            Native.CoTaskMemFree(pointer);
        }
    }

    internal static string? ServerOf(Guid clsid)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{clsid}}}\InprocServer32");
            return key?.GetValue("")?.ToString() is { } path ? Environment.ExpandEnvironmentVariables(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<Dictionary<string, object?>> Enumerate(uint flags)
    {
        var list = new List<Dictionary<string, object?>>();
        int hr = Native.MFTEnumEx(AudioDecoderMft, flags, IntPtr.Zero, IntPtr.Zero, out var array, out uint count);
        if (hr < 0)
        {
            list.Add(new Dictionary<string, object?> { ["error"] = Format.Hr(hr) });
            return list;
        }

        for (uint i = 0; i < count; i++)
        {
            var pointer = Marshal.ReadIntPtr(array, (int)i * IntPtr.Size);
            var entry = new Dictionary<string, object?>();
            try
            {
                var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pointer);
                if (activate.GetAllocatedString(ref friendlyName, out var text, out _) >= 0 && text != IntPtr.Zero)
                {
                    entry["name"] = Marshal.PtrToStringUni(text);
                    Native.CoTaskMemFree(text);
                }

                if (activate.GetGUID(ref transformClsid, out var clsid) >= 0)
                {
                    entry["clsid"] = clsid.ToString();
                    var dll = ServerOf(clsid);
                    entry["dll"] = dll;
                    entry["dllVersion"] = Versions.Of(dll);
                }

                if (activate.GetBlobSize(ref inputTypes, out uint size) >= 0 && size >= 32)
                {
                    var buffer = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (activate.GetBlob(ref inputTypes, buffer, size, out _) >= 0)
                        {
                            var subtypes = new List<string>();
                            for (int k = 0; k + 32 <= size; k += 32)
                            {
                                var bytes = new byte[16];
                                Marshal.Copy(buffer + k + 16, bytes, 0, 16);
                                subtypes.Add(new Guid(bytes).ToString());
                            }

                            entry["inputSubtypes"] = subtypes;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }

                Marshal.ReleaseComObject(activate);
            }
            catch (Exception e)
            {
                entry["error"] = e.GetType().Name + ": " + e.Message;
            }

            Marshal.Release(pointer);
            list.Add(entry);
        }

        if (array != IntPtr.Zero)
        {
            Native.CoTaskMemFree(array);
        }

        return list;
    }

    private static List<Dictionary<string, object?>> EnumerateDmo()
    {
        var list = new List<Dictionary<string, object?>>();
        var category = AudioDecoderDmo;
        int hr = Native.DMOEnum(ref category, 1, 0, Array.Empty<DmoPartialType>(), 0, IntPtr.Zero, out var enumerator);
        if (hr < 0 || enumerator == null)
        {
            list.Add(new Dictionary<string, object?> { ["error"] = Format.Hr(hr) });
            return list;
        }

        while (true)
        {
            var clsids = new Guid[16];
            var names = new IntPtr[16];
            if (enumerator.Next(16, clsids, names, out uint fetched) < 0 || fetched == 0)
            {
                break;
            }

            for (int i = 0; i < fetched; i++)
            {
                var dll = ServerOf(clsids[i]);
                list.Add(new Dictionary<string, object?>
                {
                    ["clsid"] = clsids[i].ToString(),
                    ["name"] = names[i] == IntPtr.Zero ? null : Marshal.PtrToStringUni(names[i]),
                    ["dll"] = dll,
                    ["dllVersion"] = Versions.Of(dll),
                });
                if (names[i] != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(names[i]);
                }
            }
        }

        Marshal.ReleaseComObject(enumerator);
        return list;
    }
}
