using System.Runtime.InteropServices;
using System.Text;

namespace CommandCenter;

// Brings windows to the front (Windows otherwise doesn't let a program bring other programs' windows forward)
static class Fg
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);

    // Forces a window to the front: attaches to the active window's thread, puts the window on top and gives it focus
    public static void Force(IntPtr h)
    {
        // Press and release F24 first: that makes Windows let us change the foreground window
        keybd_event(0x87, 0, 0, UIntPtr.Zero); keybd_event(0x87, 0, 2, UIntPtr.Zero);
        if (IsIconic(h)) ShowWindow(h, 9);
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint me = GetCurrentThreadId();
        bool attached = false;
        if (fgThread != 0 && fgThread != me) attached = AttachThreadInput(me, fgThread, true);
        SetWindowPos(h, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
        SetWindowPos(h, (IntPtr)(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        SwitchToThisWindow(h, true);
        if (attached) AttachThreadInput(me, fgThread, false);
    }

    // Finds a File Explorer window by its title (the folder's name)
    public static IntPtr FindExplorerByTitle(string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            if (cls.ToString() != "CabinetWClass") return true;
            var t = new StringBuilder(512);
            GetWindowText(h, t, 512);
            if (string.Equals(t.ToString(), title, StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
