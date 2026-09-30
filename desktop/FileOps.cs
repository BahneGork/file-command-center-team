using System.Diagnostics;

namespace CommandCenter;

static class FileOps
{
    public static readonly string[] Exts = { ".xlsx", ".xlsm", ".xlsb", ".xls", ".csv" };
    static readonly string[] ExcelExts = { ".xlsx", ".xlsm", ".xlsb", ".xls", ".xltx", ".xltm", ".csv" };
    // Filtyper der aldrig åbnes via dashboardet (programmer/scripts)
    static readonly string[] Blocked = { ".exe", ".bat", ".cmd", ".com", ".scr", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".msi", ".msp", ".hta", ".reg", ".lnk", ".jar", ".dll", ".cpl", ".pif", ".url", ".appref-ms" };
    const int MaxPreviewBytes = 50 * 1024 * 1024;

    static string Ext(string p) => Path.GetExtension(p).ToLowerInvariant();
    static readonly EnumerationOptions Visible = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };

    // ------------------------------------------------------------ åbn
    public static void Open(string p, bool reveal)
    {
        var ext = Ext(p);
        var isDir = Directory.Exists(p);
        if (!isDir && Blocked.Contains(ext)) throw new ApiError($"Filtypen {ext} åbnes ikke fra dashboardet");
        if (!isDir && !File.Exists(p)) throw new ApiError("Filen findes ikke. Er netværksdrevet forbundet, eller er filen flyttet?");

        var psi = new ProcessStartInfo { UseShellExecute = true };
        if (reveal)
        {
            psi.FileName = "explorer.exe";
            psi.Arguments = isDir ? $"\"{p}\"" : $"/select,\"{p}\"";
        }
        else psi.FileName = p;
        Process.Start(psi);

        if (reveal) return;
        if (isDir) BringFolderToFront(p);
        else if (ExcelExts.Contains(ext)) _ = BringExcelToFront();
    }

    // Henter Excel-vinduet frem (en baggrundsproces må ellers ikke selv få vinduer forrest)
    static async Task BringExcelToFront()
    {
        bool done = false;
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500);
            try
            {
                var xl = Process.GetProcessesByName("excel")
                    .Where(p => p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0)
                    .OrderByDescending(p => p.StartTime).FirstOrDefault();
                if (xl != null)
                {
                    Fg.Force(xl.MainWindowHandle);
                    if (done) return;
                    done = true;
                }
            }
            catch { return; }
        }
    }

    // Henter en Stifinder-mappe frem, når den er åbnet (Shell.Application kræver en STA-tråd)
    static void BringFolderToFront(string p)
    {
        var t = new Thread(() =>
        {
            var target = p.TrimEnd('\\');
            var leaf = Path.GetFileName(target);
            for (int i = 0; i < 8; i++)
            {
                Thread.Sleep(400);
                try
                {
                    var hwnd = IntPtr.Zero;
                    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                    foreach (dynamic w in shell.Windows())
                    {
                        try
                        {
                            string? wp = w.Document.Folder.Self.Path;
                            if (wp != null && wp.TrimEnd('\\').Equals(target, StringComparison.OrdinalIgnoreCase)) { hwnd = new IntPtr(Convert.ToInt64(w.HWND)); break; }
                        }
                        catch { }
                    }
                    if (hwnd == IntPtr.Zero && leaf.Length > 0) hwnd = Fg.FindExplorerByTitle(leaf);
                    if (hwnd != IntPtr.Zero) { Fg.Force(hwnd); return; }
                }
                catch { return; }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    // ------------------------------------------------------------ tjek / scan / mappebrowser
    static object Probe(string p)
    {
        try
        {
            if (Directory.Exists(p)) return new { exists = true, isDir = true, size = (long?)null, modified = Directory.GetLastWriteTimeUtc(p).ToString("o") };
            if (File.Exists(p)) { var i = new FileInfo(p); return new { exists = true, isDir = false, size = (long?)i.Length, modified = i.LastWriteTimeUtc.ToString("o") }; }
        }
        catch { }
        return new { exists = false };
    }

    // Tjekker parallelt med 6 sek. samlet frist, så et dødt netværksdrev ikke fryser alt
    public static async Task<Dictionary<string, object>> Check(IEnumerable<string> paths)
    {
        var jobs = paths.Distinct().Select(p => (p, t: Task.Run(() => Probe(p)))).ToList();
        var deadline = Task.Delay(6000);
        var res = new Dictionary<string, object>();
        foreach (var (p, t) in jobs)
            res[p] = await Task.WhenAny(t, deadline) == t ? t.Result : new { exists = (bool?)null, timeout = true };
        return res;
    }

    public static object Scan(string folder, bool recurse)
    {
        if (!Directory.Exists(folder)) throw new ApiError("Mappen findes ikke eller kan ikke nås");
        var opt = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = Visible.AttributesToSkip, RecurseSubdirectories = recurse };
        var list = new DirectoryInfo(folder).EnumerateFiles("*", opt)
            .Where(f => !f.Name.StartsWith("~$"))
            .Take(501).Select(f => new { path = f.FullName, name = Path.GetFileNameWithoutExtension(f.Name) }).ToList();
        var truncated = list.Count > 500;
        return new { files = truncated ? list.Take(500).ToList() : list, truncated };
    }

    // Leder efter bestemte fil- eller mappenavne under en mappe (til at genfinde flyttede filer).
    // Stopper efter 200.000 poster eller 15 sek., så et stort netværksdrev ikke låser appen.
    public static object Find(string folder, IEnumerable<string> names)
    {
        if (!Directory.Exists(folder)) throw new ApiError("Mappen findes ikke eller kan ikke nås");
        var want = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var matches = new Dictionary<string, List<string>>();
        var opt = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = Visible.AttributesToSkip, RecurseSubdirectories = true };
        var sw = Stopwatch.StartNew();
        var seen = 0;
        var truncated = false;
        foreach (var i in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", opt))
        {
            if (++seen > 200000 || sw.ElapsedMilliseconds > 15000) { truncated = true; break; }
            if (!want.Contains(i.Name)) continue;
            var key = i.Name.ToLowerInvariant();
            if (!matches.TryGetValue(key, out var list)) matches[key] = list = [];
            if (list.Count < 5) list.Add(i.FullName);
        }
        return new { matches, truncated };
    }

    static List<object> Places()
    {
        var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cands = new List<(string name, string path)>
        {
            ("Skrivebord", Path.Combine(up, "Desktop")), ("Dokumenter", Path.Combine(up, "Documents")), ("Overførsler", Path.Combine(up, "Downloads")),
        };
        foreach (var v in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
        {
            var pp = Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrEmpty(pp)) cands.Add((Path.GetFileName(pp.TrimEnd('\\')), pp));
        }
        var seen = new HashSet<string>();
        var places = new List<object>();
        foreach (var (name, path) in cands)
            if (seen.Add(path.TrimEnd('\\').ToLowerInvariant()) && Directory.Exists(path)) places.Add(new { name, path });
        return places;
    }

    public static object ListDir(string path, bool all)
    {
        if (string.IsNullOrEmpty(path))
            return new { path = "", parent = (string?)null, entries = DriveInfo.GetDrives().Select(d => (object)new { name = d.Name, path = d.Name, type = "drive" }).ToList(), places = Places() };
        if (!Directory.Exists(path)) throw new ApiError("Mappen findes ikke eller kan ikke nås");

        var entries = new List<object>();
        var items = new DirectoryInfo(path).EnumerateFileSystemInfos("*", Visible)
            .OrderBy(i => i is not DirectoryInfo).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).Take(2000);
        foreach (var i in items)
        {
            if (i is DirectoryInfo) entries.Add(new { name = i.Name, path = i.FullName, type = "dir" });
            else if (!i.Name.StartsWith("~$") && (all || Exts.Contains(i.Extension.ToLowerInvariant())))
                entries.Add(new { name = i.Name, path = i.FullName, type = "file", size = ((FileInfo)i).Length, modified = i.LastWriteTimeUtc.ToString("o") });
        }
        var parent = Path.GetDirectoryName(path.Length > 3 ? path.TrimEnd('\\') : path);
        if (string.IsNullOrEmpty(parent)) parent = "";
        return new { path, parent, entries };
    }

    // ------------------------------------------------------------ læs (til forhåndsvisningsruden; siden afgør selv, om den kan vise indholdet)
    public static object ReadFile(string p, int max)
    {
        if (!File.Exists(p)) throw new ApiError("Filen findes ikke. Er netværksdrevet forbundet, eller er filen flyttet?");
        // FileShare.ReadWrite: filen kan godt være åben i Excel
        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long size = fs.Length;
        long want = max > 0 ? Math.Min(max, size) : size;
        if (want > MaxPreviewBytes) throw new ApiError("Filen er for stor til forhåndsvisning");
        var buf = new byte[want];
        fs.ReadExactly(buf, 0, buf.Length);
        return new { b64 = Convert.ToBase64String(buf), size };
    }
}
