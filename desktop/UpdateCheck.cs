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

            JsonElement? msiAsset = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if ((a.GetProperty("name").GetString() ?? "").EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) { msiAsset = a; break; }
            if (msiAsset is null) return null; // release findes, men har ingen installer vedhæftet endnu

            var a2 = msiAsset.Value;
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
    public static async Task<string> DownloadAndLaunchInstallerAsync(UpdateInfo info, Action<long, long> onProgress)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"FileCommandCenter-{info.Version}.msi");
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
        // Kun til test: springer den rigtige installer-start over, så en automatiseret test aldrig popper en UAC-dialog
        if (Environment.GetEnvironmentVariable("FCC_UPDATE_NO_LAUNCH") == "1") return tmp;
        // UseShellExecute: åbner .msi'en via Windows' egen handler (msiexec), som selv beder om admin-tilladelse
        Process.Start(new ProcessStartInfo(tmp) { UseShellExecute = true });
        return tmp;
    }
}
