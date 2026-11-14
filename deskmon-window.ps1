param([int]$ProcessId)

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DeskMonWinEnum
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static List<string> ForPid(int pid)
    {
        var list = new List<string>();
        EnumWindows((h, l) =>
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if ((int)p != pid) return true;
            var cls = new StringBuilder(256); GetClassName(h, cls, 256);
            var txt = new StringBuilder(256); GetWindowText(h, txt, 256);
            RECT r; GetWindowRect(h, out r);
            int ex = GetWindowLong(h, -20);
            bool vis = IsWindowVisible(h);
            bool topmost = (ex & 0x8) != 0;
            bool tool = (ex & 0x80) != 0;
            bool noact = (ex & unchecked((int)0x08000000)) != 0;
            list.Add(string.Format("hwnd={0} vis={1} rect=({2},{3})-({4},{5}) cls={6} title={7} ex=0x{8:X8} topmost={9} tool={10} noact={11}", h, vis, r.Left, r.Top, r.Right, r.Bottom, cls, txt, ex, topmost, tool, noact));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@

[DeskMonWinEnum]::ForPid($ProcessId) | ForEach-Object { $_ }
