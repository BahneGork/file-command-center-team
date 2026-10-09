using System.Text;
using System.Text.Json;

namespace CommandCenter;

// Stores the dashboard's data in %LOCALAPPDATA%\FileCommandCenter (one set per Windows user)
static class Store
{
    static readonly UTF8Encoding Utf8 = new(false);
    // Test only: point the data folder at an isolated test folder instead of the real %LOCALAPPDATA%.
    // .NET's Environment.SpecialFolder is resolved through Windows' Known Folder API, not the
    // %LOCALAPPDATA% environment variable - setting that variable for the process (e.g. via ProcessStartInfo) changes nothing.
    public static readonly string Dir = Environment.GetEnvironmentVariable("FCC_DATA_DIR") is { Length: > 0 } testDir
        ? testDir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileCommandCenter");
    static string DataFile => Path.Combine(Dir, "data.json");
    static string BackupDir => Path.Combine(Dir, "backups");

    public static string? Load() => File.Exists(DataFile) ? File.ReadAllText(DataFile, Utf8) : null;

    // The language chosen on the page (settings.lang), or null if nothing is saved yet
    public static string? SavedLang()
    {
        try
        {
            if (Load() is not { } json) return null;
            using var d = JsonDocument.Parse(json);
            return d.RootElement.TryGetProperty("settings", out var st) && st.ValueKind == JsonValueKind.Object
                && st.TryGetProperty("lang", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        }
        catch { return null; }
    }

    public static void Save(string json)
    {
        Directory.CreateDirectory(Dir);
        if (File.Exists(DataFile))
        {
            Directory.CreateDirectory(BackupDir);
            var bak = Path.Combine(BackupDir, "data-" + DateTime.Now.ToString("yyyyMMdd") + ".json");
            if (!File.Exists(bak))
            {
                File.Copy(DataFile, bak);
                Prune("data-2*.json", 30);
            }
            // Extra safety: if a save has FEWER files than before, a copy of what was there is saved first
            try
            {
                if (CountFiles(File.ReadAllText(DataFile, Utf8)) > CountFiles(json))
                {
                    File.Copy(DataFile, Path.Combine(BackupDir, "gemt-foer-sletning-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"), true);
                    Prune("gemt-foer-sletning-*.json", 20);
                }
            }
            catch { }
        }
        var tmp = DataFile + ".tmp";
        File.WriteAllText(tmp, json, Utf8);
        File.Move(tmp, DataFile, true);
    }

    static int CountFiles(string json)
    {
        using var d = JsonDocument.Parse(json);
        return d.RootElement.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array ? f.GetArrayLength() : 0;
    }

    static void Prune(string pattern, int keep)
    {
        foreach (var f in new DirectoryInfo(BackupDir).GetFiles(pattern).OrderByDescending(f => f.Name).Skip(keep))
            f.Delete();
    }

    // Only paths that are on the dashboard may be opened/read
    public static bool IsRegistered(string p)
    {
        var json = Load();
        if (json == null) return false;
        using var d = JsonDocument.Parse(json);
        if (!d.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return false;
        foreach (var f in files.EnumerateArray())
            if (f.TryGetProperty("path", out var fp) && fp.ValueKind == JsonValueKind.String
                && string.Equals(fp.GetString(), p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
