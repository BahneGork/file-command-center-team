using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommandCenter;

sealed class ApiError(string message) : Exception(message);

// De samme "ruter" som PowerShell-hjælperen havde, men kaldt via beskeder fra siden (ingen netværksport)
static class Api
{
    static string Str(JsonElement b, string k) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    static bool Bool(JsonElement b, string k) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static int Int(JsonElement b, string k) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    static string Json(object o) => JsonSerializer.Serialize(o);

    // "teamFolder" sendes med fra siden ved hvert kald, der skal kunne åbne en team-fil - C#-laget
    // har ingen egen gemt tilstand, S.team.folder i data.json er den ene sandhed om, hvilken mappe der bruges.
    static void RequireRegistered(string p, string? teamFolder)
    {
        if (!Store.IsRegistered(p) && !TeamStore.IsRegistered(teamFolder, p))
            throw new ApiError("Stien er ikke registreret i dashboardet");
    }

    // Returnerer svaret som JSON-tekst. onProgress bruges kun af /api/update/install til at rapportere downloadfremgang undervejs.
    public static async Task<string> Handle(string path, JsonElement body, IWin32Window owner, Action<long, long>? onProgress = null)
    {
        switch (path)
        {
            case "/api/ping":
                return "{\"ok\":true}";

            case "/api/state":
                if (body.ValueKind == JsonValueKind.Object) { Store.Save(body.GetRawText()); return "{\"ok\":true}"; }
                return Store.Load() ?? "null";

            case "/api/open":
            {
                var p = Str(body, "path");
                RequireRegistered(p, Str(body, "teamFolder"));
                FileOps.Open(p, Bool(body, "reveal"));
                return "{\"ok\":true}";
            }

            case "/api/file":
            {
                var p = Str(body, "path");
                RequireRegistered(p, Str(body, "teamFolder"));
                return Json(await Task.Run(() => FileOps.ReadFile(p, Int(body, "max"))));
            }

            case "/api/check":
            {
                var paths = body.TryGetProperty("paths", out var arr) && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!) : [];
                return Json(new { results = await FileOps.Check(paths) });
            }

            case "/api/ls":
                return Json(await Task.Run(() => FileOps.ListDir(Str(body, "path"), Bool(body, "all"))));

            case "/api/scan":
                return Json(await Task.Run(() => FileOps.Scan(Str(body, "folder"), Bool(body, "recurse"))));

            case "/api/pick":
            {
                using var dlg = new OpenFileDialog
                {
                    Multiselect = Bool(body, "multi"),
                    Title = "Vælg filer til dashboardet",
                    Filter = "Alle filer|*.*|Excel og CSV|*.xlsx;*.xlsm;*.xlsb;*.xls;*.csv",
                };
                return Json(new { paths = dlg.ShowDialog(owner) == DialogResult.OK ? dlg.FileNames : [] });
            }

            case "/api/pickfolder":
            {
                using var dlg = new FolderBrowserDialog { Description = "Vælg en mappe" };
                return Json(new { path = dlg.ShowDialog(owner) == DialogResult.OK ? dlg.SelectedPath : null });
            }

            case "/api/update/check":
            {
                var info = await UpdateCheck.CheckAsync();
                return info is null
                    ? "{\"available\":false}"
                    : Json(new { available = true, version = info.Version, notes = info.Notes, url = info.HtmlUrl, size = info.AssetSize });
            }

            case "/api/update/install":
            {
                var info = await UpdateCheck.CheckAsync();
                if (info is null) throw new ApiError("Ingen opdatering fundet - prøv at tjekke igen.");
                await UpdateCheck.DownloadAndLaunchInstallerAsync(info, (received, total) => onProgress?.Invoke(received, total));
                return "{\"ok\":true}";
            }

            case "/api/whoami":
            {
                var dom = Environment.UserDomainName;
                var name = !string.IsNullOrEmpty(dom) && !string.Equals(dom, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                    ? dom + "\\" + Environment.UserName : Environment.UserName;
                return Json(new { name });
            }

            case "/api/team/connect":
            {
                var folder = Str(body, "folder");
                if (string.IsNullOrWhiteSpace(folder)) throw new ApiError("Vælg en mappe");
                if (!Directory.Exists(folder)) throw new ApiError("Mappen findes ikke eller kan ikke nås");
                var empty = new JsonObject { ["categories"] = new JsonArray(), ["files"] = new JsonArray() };
                var merged = await Task.Run(() => TeamStore.Sync(folder, empty));
                return merged.ToJsonString();
            }

            case "/api/team/sync":
            {
                var folder = Str(body, "folder");
                if (string.IsNullOrWhiteSpace(folder)) throw new ApiError("Intet team-mappe valgt");
                var local = body.TryGetProperty("local", out var l) ? JsonNode.Parse(l.GetRawText()) as JsonObject : null;
                local ??= new JsonObject { ["categories"] = new JsonArray(), ["files"] = new JsonArray() };
                var merged = await Task.Run(() => TeamStore.Sync(folder, local));
                return merged.ToJsonString();
            }

            case "/api/team/export":
            {
                var folder = Str(body, "folder");
                if (string.IsNullOrWhiteSpace(folder)) throw new ApiError("Intet team-mappe valgt");
                return TeamStore.Load(folder) ?? "{\"rev\":0,\"categories\":[],\"files\":[]}";
            }

            case "/api/team/restore":
            {
                var folder = Str(body, "folder");
                if (string.IsNullOrWhiteSpace(folder)) throw new ApiError("Intet team-mappe valgt");
                var snapshot = body.TryGetProperty("snapshot", out var sn) ? JsonNode.Parse(sn.GetRawText()) as JsonObject : null;
                if (snapshot == null) throw new ApiError("Ugyldig fil");
                var merged = await Task.Run(() => TeamStore.Restore(folder, snapshot));
                return merged.ToJsonString();
            }

            default:
                throw new ApiError("Ikke fundet");
        }
    }
}
