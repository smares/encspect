using System.Text.Json;
using System.Text.Json.Serialization;

namespace Encspect.Gui.Views;

// Where a window was, in screen pixels for the position and device-independent units for the size.
// The position is null where it was never known, as in a first session on Wayland, which does not let
// a program place its windows; older settings files hold numbers there.
public sealed record WindowPlacement(int? X, int? Y, double Width, double Height, bool IsMaximized);

// The GUI's settings, as JSON in the user's application data folder. Settings that cannot be read
// are ignored, and a failure to write them is not an error worth interrupting anyone for.
public sealed class Settings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromHours(1);

    public WindowPlacement? DetailWindow { get; set; }

    // Whether the detail window's pane of background articles was open, and how wide it was.
    public bool IsTopicPaneOpen { get; set; }

    public double? TopicPaneWidth { get; set; }

    // The share of the detail window's height that the graphs and findings take below the tree and hex view, as the splitter left it.
    public double? GraphPaneShare { get; set; }

    [JsonIgnore]
    public string? FilePath { get; private set; }

    // %APPDATA% on Windows, $XDG_CONFIG_HOME or ~/.config on Linux; on macOS .NET maps
    // ApplicationData to ~/Library/Application Support (NSApplicationSupportDirectory).
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Encspect", "settings.json");

    public static Settings Load(string? path)
    {
        Settings? settings = null;
        if (path is not null)
        {
            try
            {
                settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
            }
        }

        settings ??= new Settings();
        settings.FilePath = path;
        return settings;
    }

    public void Save()
    {
        if (FilePath is null)
        {
            return;
        }

        // Written next to the file and moved over it, so two instances closing at once cannot leave
        // a torn file: the last move wins, and each file is whole.
        string temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            File.Move(temporary, FilePath, overwrite: true);
            SweepStaleTemporaries();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // A temporary file survives only a process that ended between writing and moving it. One an hour
    // old is no other instance's in-flight write.
    private void SweepStaleTemporaries()
    {
        try
        {
            string directory = Path.GetDirectoryName(FilePath)!;
            DateTime cutoff = DateTime.UtcNow - StaleTemporaryAge;
            foreach (string file in Directory.EnumerateFiles(directory, Path.GetFileName(FilePath) + ".*.tmp"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
