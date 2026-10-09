using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommandCenter;

// Team-shared file database: several writers, one folder (a network drive, a OneDrive/SharePoint-synced
// folder, or whatever the user points at - the app doesn't care what keeps the folder in sync).
// Unlike Store.cs (one user, the whole file is overwritten on every save) this one has to handle several
// writers at once: each entry has its own "last modified" timestamp, and a deleted entry becomes a
// tombstone instead of disappearing, so a computer that hasn't seen the deletion yet doesn't bring it back.
static class TeamStore
{
    static readonly UTF8Encoding Utf8 = new(false);
    const string FileName = "team-data.json";
    const int MaxRetries = 5;

    static string DataFile(string folder) => Path.Combine(folder, FileName);
    static string BackupDir(string folder) => Path.Combine(folder, "backups");

    // Reads the shared file raw (used for export and as a fallback if the folder can't be reached)
    public static string? Load(string folder)
    {
        var p = DataFile(folder);
        return File.Exists(p) ? File.ReadAllText(p, Utf8) : null;
    }

    // Is the path registered in the shared database? Fails closed (false) if the folder is missing, can't be
    // read, or the file is invalid - a drive that is briefly unavailable must not break opening personal files.
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

    // JsonValue.GetValue&lt;long&gt;() throws if the number was built as a raw C# int (e.g. in EmptyDoc) instead of
    // parsed from JSON text - the two forms behave differently. TryGetValue with both types handles either.
    static long RevOf(JsonObject doc) =>
        doc["rev"] is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : 0) : 0;

    static JsonObject ReadDoc(string folder)
    {
        var json = Load(folder);
        if (json == null) return EmptyDoc();
        try { return JsonNode.Parse(json) as JsonObject ?? EmptyDoc(); }
        catch { return EmptyDoc(); }   // invalid file: act as if it were empty, instead of breaking the sync
    }

    // Merges two lists of entries (hubs or files): for each id the newest "modifiedAt" wins.
    // Ids that only exist on one side are always kept - nothing disappears silently.
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
        return string.CompareOrdinal(ta, tb) > 0;   // ISO timestamps sort correctly as text
    }

    // Merges the client's local state with the shared file and writes the result back.
    // Tries again if another computer managed to write while we were working (optimistic locking via "rev").
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
            catch (IOException) { }   // a shared drive can act up for a moment; try again
            Thread.Sleep(150 * (attempt + 1));
        }
        throw new ApiError(L.T("Kunne ikke synkronisere team-databasen - prøv igen om lidt.", "Couldn't sync the team database - try again in a moment."));
    }

    // Replaces the WHOLE shared database with a restored snapshot ("Restore team database from file…").
    // Still goes through the rev check, so it doesn't silently overwrite changes others have made in the meantime.
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
        throw new ApiError(L.T("Kunne ikke gendanne team-databasen - prøv igen om lidt.", "Couldn't restore the team database - try again in a moment."));
    }

    static JsonArray AsArray(JsonNode? n) => n as JsonArray ?? new JsonArray();

    // Only writes if "rev" in the file is still the one we read from (nobody else managed to write in the meantime).
    static bool TryWrite(string folder, JsonObject merged, long expectedPrevRev)
    {
        var path = DataFile(folder);
        if (File.Exists(path))
        {
            var currentRev = RevOf(ReadDoc(folder));
            if (currentRev != expectedPrevRev) return false;   // someone else wrote in the meantime - try again with fresh data
            Backup(folder, path);
        }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, merged.ToJsonString(), Utf8);
        File.Move(tmp, path, true);
        return true;
    }

    // Daily backup in the shared folder itself, so everyone with access to the folder can see/restore it -
    // an extra safety net on top of what the sharing mechanism already offers (e.g. OneDrive's own history).
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
        catch { }   // the backup is a safety net, not something that may break a sync
    }
}
