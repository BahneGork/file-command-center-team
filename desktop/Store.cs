using System.Text;
using System.Text.Json;

namespace CommandCenter;

// Gemmer dashboardets data i %LOCALAPPDATA%\FileCommandCenter (ét sæt pr. Windows-bruger)
static class Store
{
    static readonly UTF8Encoding Utf8 = new(false);
    // Kun til test: peg data-mappen på en isoleret testmappe i stedet for den rigtige %LOCALAPPDATA%.
    // .NET's Environment.SpecialFolder løses via Windows' Known Folder-API, ikke miljøvariablen
    // %LOCALAPPDATA% - at sætte den variabel på procesniveau (fx via ProcessStartInfo) ændrer intet.
    public static readonly string Dir = Environment.GetEnvironmentVariable("FCC_DATA_DIR") is { Length: > 0 } testDir
        ? testDir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileCommandCenter");
    static string DataFile => Path.Combine(Dir, "data.json");
    static string BackupDir => Path.Combine(Dir, "backups");

    public static string? Load() => File.Exists(DataFile) ? File.ReadAllText(DataFile, Utf8) : null;

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
            // Ekstra sikring: hvis der gemmes FÆRRE filer end før, gemmes først en kopi af det, der var
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

    // Kun stier, der står på dashboardet, må åbnes/læses
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
