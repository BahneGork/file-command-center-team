using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace CommandCenter;

record UpdateInfo(string Version, string Notes, string HtmlUrl, string AssetUrl, string AssetName, long AssetSize);

// Checks GitHub Releases for a newer version and downloads/starts the installer.
// Only this layer may talk to the network - the page in the WebView is still locked to talking only to itself.
static class UpdateCheck
{
    // Can be overridden for tests (points at a local mock instead of GitHub)
    // NB: points at the fork repo (team sync), not the stable file-command-center - the two must never
    // share an update channel, so stable users are never offered an experimental build by mistake.
    public static string ApiUrl = "https://api.github.com/repos/BahneGork/file-command-center-team/releases/latest";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    static UpdateCheck()
    {
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FileCommandCenter", CurrentVersion.ToString()));
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        // Test only: point the check at a local mock instead of GitHub
        if (Environment.GetEnvironmentVariable("FCC_UPDATE_API_URL") is { Length: > 0 } testUrl) ApiUrl = testUrl;
    }

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    // The portable build is built with -p:Flavor=portable (see CommandCenter.csproj) and updates itself from the .zip
    public static bool IsPortable => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "Flavor" && a.Value == "portable");

    // Returns null if there is no newer version, or if the check fails (e.g. no internet connection)
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
            if (asset is null) return null; // the release exists, but doesn't have the file this build needs

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

    // Downloads the installer and starts it (shows Windows' own UAC and installer dialog), then the app closes,
    // so the .exe is no longer locked when the installer writes the new files.
    // The portable build instead downloads the .zip and swaps out its own files (see ReplacePortable).
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
            throw new ApiError(L.T("Downloadet fil har forkert størrelse - prøv igen.", "The downloaded file has the wrong size - try again."));
        }
        if (IsPortable) { ReplacePortable(tmp); return tmp; }
        // Test only: skips actually starting the installer, so an automated test never pops up a UAC dialog
        if (Environment.GetEnvironmentVariable("FCC_UPDATE_NO_LAUNCH") == "1") return tmp;
        // UseShellExecute: opens the .msi through Windows' own handler (msiexec), which asks for admin permission itself
        Process.Start(new ProcessStartInfo(tmp) { UseShellExecute = true });
        return tmp;
    }

    // A running .exe can't be overwritten, but it can be renamed: the old one is renamed to .old, the new files are put
    // in place, and the new version is started. It waits until this one has closed, then deletes .old (see Program.cs).
    static void ReplacePortable(string zip)
    {
        var exe = Environment.ProcessPath ?? throw new ApiError(L.T("Kunne ikke finde programfilen.", "Couldn't find the program file."));
        var dir = Path.GetDirectoryName(exe)!;
        var stage = Path.Combine(Path.GetTempPath(), "FileCommandCenter-update-" + Guid.NewGuid().ToString("N"));
        var old = exe + ".old";
        try
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, stage);
            var newExe = Path.Combine(stage, "FileCommandCenter.exe");
            var newWeb = Path.Combine(stage, "web");
            if (!File.Exists(newExe) || !File.Exists(Path.Combine(newWeb, "index.html")))
                throw new ApiError(L.T("Opdateringen mangler filer - prøv igen.", "The update is missing files - try again."));
            try { File.Delete(old); } catch { }   // left over from an earlier update
            try { File.Move(exe, old); }
            catch (Exception ex) { throw new ApiError(L.T($"Kunne ikke opdatere filerne i {dir} ({ex.Message}). Hent den nye .zip fra udgivelsen i stedet.", $"Couldn't update the files in {dir} ({ex.Message}). Download the new .zip from the release instead.")); }
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
                throw new ApiError(L.T($"Kunne ikke opdatere filerne i {dir} ({ex.Message}). Hent den nye .zip fra udgivelsen i stedet.", $"Couldn't update the files in {dir} ({ex.Message}). Download the new .zip from the release instead."));
            }
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { }
            try { File.Delete(zip); } catch { }
        }
        // Test only: don't start the new version (so an automated test doesn't open a window)
        if (Environment.GetEnvironmentVariable("FCC_UPDATE_NO_LAUNCH") == "1") return;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = dir };
        psi.ArgumentList.Add("--after-update");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi);
    }
}
