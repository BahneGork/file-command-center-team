using System.Diagnostics;

namespace CommandCenter;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Started by a portable update: wait until the old version has closed before taking the lock below
        var i = Array.IndexOf(args, "--after-update");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(30000); } catch { }

        // Only one window at a time, so two copies don't overwrite each other's data
        using var single = new Mutex(true, @"Local\FileCommandCenter", out var first);
        if (!first)
        {
            MessageBox.Show(L.T("File Command Center kører allerede.", "File Command Center is already running."), "File Command Center", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // Clean up after an earlier portable update (the old .exe could not be deleted while it was running)
        if (Environment.ProcessPath is { } exe) try { File.Delete(exe + ".old"); } catch { }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
