using System.Diagnostics;

namespace DeskMon;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One widget only -- a second instance would load the sensor driver twice.
        using var single = new Mutex(true, @"Local\DeskMon.SingleInstance", out bool isFirst);
        if (!isFirst) return;

        // Stay out of the way of whatever the machine is actually doing.
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { /* not fatal */ }

        ApplicationConfiguration.Initialize();

        var cfg = Config.Load();
        using var monitor = new HardwareMonitor(cfg.IntervalMs);
        using var form = new OverlayForm(cfg, monitor);

        monitor.Start();
        Application.Run(form);
    }
}
