using System.Diagnostics;

namespace WmaDecodeProbe;

public static class Versions
{
    public static string? Of(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                var inSystem = Path.Combine(Environment.SystemDirectory, Path.GetFileName(path));
                if (!File.Exists(inSystem))
                {
                    return null;
                }

                path = inSystem;
            }

            return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The modules of this process that belong to Media Foundation, the ASF source and the WMA decoders.
    public static List<Dictionary<string, string?>> LoadedModules()
    {
        var list = new List<Dictionary<string, string?>>();
        foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
        {
            string name = m.ModuleName.ToLowerInvariant();
            if (name.StartsWith("wm") || name.StartsWith("mf") || name.StartsWith("msaud") || name.Contains("asf") || name.Contains("dmo") || name.StartsWith("msaudio"))
            {
                list.Add(new Dictionary<string, string?> { ["name"] = m.ModuleName, ["path"] = m.FileName, ["version"] = m.FileVersionInfo.FileVersion });
            }
        }

        return list;
    }
}
