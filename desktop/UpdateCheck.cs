using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace CommandCenter;

record UpdateInfo(string Version, string Notes, string HtmlUrl, string AssetUrl, string AssetName, long AssetSize);

// Tjekker GitHub Releases for en nyere version og henter/starter installeren.
// Kun det her lag må tale med nettet - siden i WebView'en er stadig spærret til kun at tale med sig selv.
static class UpdateCheck
{
    // Kan overstyres til test (peger på en lokal mock i stedet for GitHub)
    // OBS: peger på fork-repoet (team-sync), ikke det stabile file-command-center - de to må aldrig
    // dele opdateringskanal, så stabile brugere ikke tilbydes en eksperimentel build ved en fejl.
    public static string ApiUrl = "https://api.github.com/repos/BahneGork/file-command-center-team/releases/latest";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    static UpdateCheck()
    {
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FileCommandCenter", CurrentVersion.ToString()));
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        // Kun til test: peg tjekket på en lokal mock i stedet for GitHub
        if (Environment.GetEnvironmentVariable("FCC_UPDATE_API_URL") is { Length: > 0 } testUrl) ApiUrl = testUrl;
    }

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    // Den portable udgave er bygget med -p:Flavor=portable (se CommandCenter.csproj) og opdaterer sig selv fra .zip'en
    public static bool IsPortable => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "Flavor" && a.Value == "portable");

    // Returnerer null, hvis der ikke er en nyere version, eller hvis tjekket fejler (fx ingen internetforbindelse)
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var res = await Http.GetAsync(ApiUrl);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync());
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            if (latest.CompareTo(CurrentVersion) <= 0) return null;

            var want = IsPortable ? "-portable-win-x64.zip" : ".msi";
            JsonElement? asset = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if ((a.GetProperty("name").GetString() ?? "").EndsWith(want, StringComparison.OrdinalIgnoreCase)) { asset = a; break; }
            if (asset is null) return null; // release findes, men har ikke den fil, denne udgave skal bruge

            var a2 = asset.Value;
            return new UpdateInfo(
                Version: tag,
                Notes: root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                HtmlUrl: root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
                AssetUrl: a2.GetProperty("browser_download_url").GetString()!,
                AssetName: a2.GetProperty("name").GetString()!,
                AssetSize: a2.GetProperty("size").GetInt64());
        }
        catch { return null; }
    }

    // Henter installeren og starter den (viser Windows' egen UAC- og installer-dialog), og afslutter så appen,
    // så .exe-filen ikke længere er låst, når installeren skal skrive de nye filer.
    // Den portable udgave henter i stedet .zip'en og skifter sine egne filer ud (se ReplacePortable).
    public static async Task<string> DownloadAndLaunchInstallerAsync(UpdateInfo info, Action<long, long> onProgress)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"FileCommandCenter-{info.Version}{Path.GetExtension(info.AssetName)}");
        using (var res = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? info.AssetSize;
            await using var src = await res.Content.ReadAsStreamAsync();
            await using var dst = File.Create(tmp);
            var buf = new byte[81920];
            long received = 0;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                received += n;
                onProgress(received, total);
            }
        }
        if (info.AssetSize > 0 && new FileInfo(tmp).Length != info.AssetSize)
        {
            File.Delete(tmp);
            throw new ApiError("Downloadet fil har forkert størrelse - prøv igen.");
        }
        if (IsPortable) { ReplacePortable(tmp); return tmp; }
        // Kun til test: springer den rigtige installer-start over, så en automatiseret test aldrig popper en UAC-dialog
        if (Environment.GetEnvironmentVariable("FCC_UPDATE_NO_LAUNCH") == "1") return tmp;
        // UseShellExecute: åbner .msi'en via Windows' egen handler (msiexec), som selv beder om admin-tilladelse
        Process.Start(new ProcessStartInfo(tmp) { UseShellExecute = true });
        return tmp;
    }

    // En kørende .exe kan ikke overskrives, men godt omdøbes: den gamle omdøbes til .old, de nye filer lægges på plads,
    // og den nye version startes. Den venter, til denne er lukket, og sletter så .old (se Program.cs).
    static void ReplacePortable(string zip)
    {
        var exe = Environment.ProcessPath ?? throw new ApiError("Kunne ikke finde programfilen.");
        var dir = Path.GetDirectoryName(exe)!;
        var stage = Path.Combine(Path.GetTempPath(), "FileCommandCenter-update-" + Guid.NewGuid().ToString("N"));
        var old = exe + ".old";
        try
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, stage);
            var newExe = Path.Combine(stage, "FileCommandCenter.exe");
            var newWeb = Path.Combine(stage, "web");
            if (!File.Exists(newExe) || !File.Exists(Path.Combine(newWeb, "index.html")))
                throw new ApiError("Opdateringen mangler filer - prøv igen.");
            try { File.Delete(old); } catch { }   // rest fra en tidligere opdatering
            try { File.Move(exe, old); }
            catch (Exception ex) { throw new ApiError($"Kunne ikke opdatere filerne i {dir} ({ex.Message}). Hent den nye .zip fra udgivelsen i stedet."); }
            try
            {
                File.Copy(newExe, exe);
                foreach (var f in Directory.EnumerateFiles(newWeb, "*", SearchOption.AllDirectories))
                {
                    var dst = Path.Combine(dir, "web", Path.GetRelativePath(newWeb, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(f, dst, true);
                }
            }
            catch (Exception ex)
            {
                try { File.Delete(exe); File.Move(old, exe); } catch { }
                throw new ApiError($"Kunne ikke opdatere filerne i {dir} ({ex.Message}). Hent den nye .zip fra udgivelsen i stedet.");
            }
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { }
            try { File.Delete(zip); } catch { }
        }
        // Kun til test: start ikke den nye version (så en automatiseret test ikke åbner et vindue)
        if (Environment.GetEnvironmentVariable("FCC_UPDATE_NO_LAUNCH") == "1") return;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = dir };
        psi.ArgumentList.Add("--after-update");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi);
    }
}
