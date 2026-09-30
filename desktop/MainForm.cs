using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CommandCenter;

sealed class MainForm : Form
{
    // Siden vises fra en intern adresse, der peger på mappen "web" ved siden af programmet. Der åbnes ingen netværksport.
    const string Host = "commandcenter.local";
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    TaskCompletionSource? flushed;
    bool closing, closeNow;

    public MainForm()
    {
        Text = "File Command Center";
        Size = new Size(1400, 900);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(web);
        Load += async (_, _) => await Init();
        FormClosing += OnClosing;
    }

    async Task Init()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Store.Dir, "WebView2"));
            await web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show("Microsoft Edge WebView2 Runtime mangler på denne pc, så dashboardet kan ikke vises.\nInstallér 'WebView2 Runtime' fra Microsoft.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            closeNow = true; Close();
            return;
        }

        var c = web.CoreWebView2;
        c.Settings.AreDevToolsEnabled = false;
        c.Settings.IsStatusBarEnabled = false;
        c.Settings.IsGeneralAutofillEnabled = false;
        c.Settings.IsPasswordAutosaveEnabled = false;
        c.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Deny);

        // Siden må kun tale med sig selv: alt andet blokeres
        c.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        c.WebResourceRequested += (_, e) =>
        {
            if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u) && (u.Host == Host || u.Scheme is "data" or "blob")) return;
            e.Response = c.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        };
        c.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host == Host) return;
            e.Cancel = true;
            // Excel-link til en fil på nettet (siden lader som et klik på ms-excel:...)
            if (e.Uri.StartsWith("ms-excel:ofe|u|https://", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("ms-excel:ofe|u|http://", StringComparison.OrdinalIgnoreCase))
                Launch(e.Uri);
        };
        c.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") Launch(e.Uri);
        };
        c.WebMessageReceived += OnMessage;
        c.Navigate($"https://{Host}/index.html");
    }

    static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var src) || src.Host != Host) return;
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var msg = doc.RootElement;
        var id = msg.GetProperty("id").GetInt32();
        var path = msg.GetProperty("path").GetString() ?? "";
        if (path == "/flushed") { flushed?.TrySetResult(); return; }

        // Downloadfremgang sendes løbende som en "event"-besked, ikke som svar på et bestemt kald
        Action<long, long>? onProgress = null;
        if (path == "/api/update/install")
        {
            var lastPct = -1;
            onProgress = (received, total) =>
            {
                var pct = total > 0 ? (int)(received * 100 / total) : 0;
                if (pct == lastPct) return;
                lastPct = pct;
                web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { @event = "update-progress", data = new { received, total, pct } }));
            };
        }

        string reply; var ok = true;
        try { reply = $"{{\"id\":{id},\"ok\":true,\"data\":{await Api.Handle(path, msg.GetProperty("body"), this, onProgress)}}}"; }
        catch (Exception ex) { ok = false; reply = $"{{\"id\":{id},\"ok\":false,\"error\":{JsonSerializer.Serialize(ex.Message)}}}"; }
        if (id > 0) web.CoreWebView2.PostWebMessageAsJson(reply);

        // Installeren er startet i en separat proces; luk os selv ned (med normal gem-før-luk), så vores
        // .exe ikke længere er låst, når installeren skal skrive de nye filer. Kort pause, så siden når at
        // vise "Opdaterer…" først.
        if (ok && path == "/api/update/install")
        {
            await Task.Delay(2000);
            Close();
        }
    }

    // Lukker først, når siden har gemt (ellers kan de sidste ændringer gå tabt)
    async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closeNow || web.CoreWebView2 == null) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        flushed = new();
        try
        {
            await web.CoreWebView2.ExecuteScriptAsync("Promise.resolve(flushSave()).finally(()=>chrome.webview.postMessage({id:-1,path:'/flushed',body:null}))");
            await Task.WhenAny(flushed.Task, Task.Delay(3000));
        }
        catch { }
        closeNow = true;
        Close();
    }
}
