using System.Diagnostics;

namespace CommandCenter;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Startet af en portable opdatering: vent, til den gamle version er lukket, før vi tager låsen nedenfor
        var i = Array.IndexOf(args, "--after-update");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(30000); } catch { }

        // Kun ét vindue ad gangen, så to kopier ikke overskriver hinandens data
        using var single = new Mutex(true, @"Local\FileCommandCenter", out var first);
        if (!first)
        {
            MessageBox.Show("File Command Center kører allerede.", "File Command Center", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // Ryd op efter en tidligere portable opdatering (den gamle .exe kunne ikke slettes, mens den kørte)
        if (Environment.ProcessPath is { } exe) try { File.Delete(exe + ".old"); } catch { }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
