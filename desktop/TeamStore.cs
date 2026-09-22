using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommandCenter;

// Team-delt fildatabase: flere skrivere, samme mappe (et netværksdrev, en OneDrive/SharePoint-synkroniseret
// mappe, eller hvad brugeren nu peger på - appen er ligeglad med, hvad der holder mappen synkroniseret).
// I modsætning til Store.cs (én bruger, hele filen overskrives ved hver gemning) skal denne håndtere flere
// samtidige skrivere: hver post har sit eget "senest ændret"-tidsstempel, og en slettet post bliver en
// gravsten i stedet for at forsvinde, så en computer, der endnu ikke har set slettelsen, ikke genopliver den.
static class TeamStore
{
    static readonly UTF8Encoding Utf8 = new(false);
    const string FileName = "team-data.json";
    const int MaxRetries = 5;

    static string DataFile(string folder) => Path.Combine(folder, FileName);
    static string BackupDir(string folder) => Path.Combine(folder, "backups");

    // Læser den delte fil rå (bruges til eksport og som fald-tilbage, hvis mappen ikke kan nås)
    public static string? Load(string folder)
    {
        var p = DataFile(folder);
        return File.Exists(p) ? File.ReadAllText(p, Utf8) : null;
    }

    // Er stien registreret i den delte database? Fejler lukket (false), hvis mappen mangler, ikke kan
    // læses, eller filen er ugyldig - et midlertidigt utilgængeligt drev skal ikke vælte personlige åbninger.
    public static bool IsRegistered(string? folder, string p)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        try
        {
            var json = Load(folder);
            if (json == null) return false;
            using var d = JsonDocument.Parse(json);
            if (!d.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return false;
            foreach (var f in files.EnumerateArray())
            {
                var deleted = f.TryGetProperty("deleted", out var del) && del.ValueKind == JsonValueKind.True;
                if (!deleted && f.TryGetProperty("path", out var fp) && fp.ValueKind == JsonValueKind.String
                    && string.Equals(fp.GetString(), p, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    static JsonObject EmptyDoc() => new() { ["rev"] = 0L, ["categories"] = new JsonArray(), ["files"] = new JsonArray() };

    // JsonValue.GetValue&lt;long&gt;() kaster, hvis tallet blev bygget som en rå C#-int (fx i EmptyDoc) i stedet for
    // parset fra JSON-tekst - de to former opfører sig forskelligt. TryGetValue med begge typer er robust over for begge.
    static long RevOf(JsonObject doc) =>
        doc["rev"] is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : 0) : 0;

    static JsonObject ReadDoc(string folder)
    {
        var json = Load(folder);
        if (json == null) return EmptyDoc();
        try { return JsonNode.Parse(json) as JsonObject ?? EmptyDoc(); }
        catch { return EmptyDoc(); }   // ugyldig fil: opfør dig som om den var tom, i stedet for at vælte synkroniseringen
    }

    // Slår to lister af poster sammen (hubs eller filer): for hvert id vinder den nyeste "modifiedAt".
    // Id'er, der kun findes på den ene side, tages altid med - intet forsvinder tavst.
    static JsonArray MergeEntries(JsonArray a, JsonArray b)
    {
        var byId = new Dictionary<string, JsonObject>();
        void Add(JsonArray arr)
        {
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                var id = o["id"]?.GetValue<string>();
                if (id == null) continue;
                if (!byId.TryGetValue(id, out var existing) || Newer(o, existing)) byId[id] = o;
            }
        }
        Add(a); Add(b);
        var outArr = new JsonArray();
        foreach (var o in byId.Values) outArr.Add(o.DeepClone());
        return outArr;
    }

    static bool Newer(JsonObject a, JsonObject b)
    {
        var ta = a["modifiedAt"]?.GetValue<string>() ?? "";
        var tb = b["modifiedAt"]?.GetValue<string>() ?? "";
        return string.CompareOrdinal(ta, tb) > 0;   // ISO-tidsstempler sorterer korrekt som tekst
    }

    // Slår klientens lokale tilstand sammen med den delte fil og skriver resultatet tilbage.
    // Prøver igen, hvis en anden computer nåede at skrive, imens vi arbejdede (optimistisk låsning via "rev").
    public static JsonObject Sync(string folder, JsonObject local)
    {
        Directory.CreateDirectory(folder);
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var remote = ReadDoc(folder);
            var remoteRev = RevOf(remote);
            var merged = new JsonObject
            {
                ["rev"] = remoteRev + 1,
                ["categories"] = MergeEntries(AsArray(remote["categories"]), AsArray(local["categories"])),
                ["files"] = MergeEntries(AsArray(remote["files"]), AsArray(local["files"])),
            };
            try { if (TryWrite(folder, merged, remoteRev)) return merged; }
            catch (IOException) { }   // delt drev kan drille et øjeblik; prøv igen
            Thread.Sleep(150 * (attempt + 1));
        }
        throw new ApiError("Kunne ikke synkronisere team-databasen - prøv igen om lidt.");
    }

    // Erstatter HELE den delte database med et gendannet øjebliksbillede ("Gendan team-database fra fil…").
    // Går stadig igennem rev-tjekket, så det ikke usynligt overskriver ændringer, andre har lavet i mellemtiden.
    public static JsonObject Restore(string folder, JsonObject snapshot)
    {
        Directory.CreateDirectory(folder);
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var remote = ReadDoc(folder);
            var remoteRev = RevOf(remote);
            var merged = new JsonObject
            {
                ["rev"] = remoteRev + 1,
                ["categories"] = AsArray(snapshot["categories"]).DeepClone(),
                ["files"] = AsArray(snapshot["files"]).DeepClone(),
            };
            try { if (TryWrite(folder, merged, remoteRev)) return merged; }
            catch (IOException) { }
            Thread.Sleep(150 * (attempt + 1));
        }
        throw new ApiError("Kunne ikke gendanne team-databasen - prøv igen om lidt.");
    }

    static JsonArray AsArray(JsonNode? n) => n as JsonArray ?? new JsonArray();

    // Skriver kun, hvis "rev" i filen stadig er den, vi læste ud fra (ingen anden nåede at skrive imens).
    static bool TryWrite(string folder, JsonObject merged, long expectedPrevRev)
    {
        var path = DataFile(folder);
        if (File.Exists(path))
        {
            var currentRev = RevOf(ReadDoc(folder));
            if (currentRev != expectedPrevRev) return false;   // en anden skrev imens - prøv igen med friske data
            Backup(folder, path);
        }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, merged.ToJsonString(), Utf8);
        File.Move(tmp, path, true);
        return true;
    }

    // Daglig sikkerhedskopi i den delte mappe selv, så alle med adgang til mappen kan se/gendanne den -
    // et ekstra sikkerhedsnet oven på, hvad selve delingsmekanismen allerede tilbyder (fx OneDrives egen historik).
    static void Backup(string folder, string dataFile)
    {
        try
        {
            var dir = BackupDir(folder);
            Directory.CreateDirectory(dir);
            var bak = Path.Combine(dir, "team-data-" + DateTime.Now.ToString("yyyyMMdd") + ".json");
            if (!File.Exists(bak))
            {
                File.Copy(dataFile, bak);
                foreach (var f in new DirectoryInfo(dir).GetFiles("team-data-2*.json").OrderByDescending(f => f.Name).Skip(30))
                    f.Delete();
            }
        }
        catch { }   // backup er et sikkerhedsnet, ikke noget der må vælte en synkronisering
    }
}
