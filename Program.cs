using System.Diagnostics;

namespace DeskMon;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Stay out of the way of whatever the machine is actually doing.
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { /* not fatal */ }

        ApplicationConfiguration.Initialize();

        var cfg = Config.Load();
        using var monitor = new HardwareMonitor(cfg.IntervalMs);

        if (args.Contains("--dump-sensors"))
        {
            monitor.Start();
            Thread.Sleep(4000);
            var outPath = args.SkipWhile(a => a != "--dump-sensors").Skip(1).FirstOrDefault()
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "DeskMon-Sensors.txt");
            File.WriteAllText(outPath, monitor.DumpSensors());
            return;
        }

        if (args.Contains("--dump-snapshot"))
        {
            Snapshot? last = null;
            monitor.Updated += s => last = s;
            monitor.Start();
            Thread.Sleep(6500);
            var outPath = args.SkipWhile(a => a != "--dump-snapshot").Skip(1).FirstOrDefault()
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "DeskMon-Snapshot.txt");
            File.WriteAllText(outPath, last?.Describe() ?? "no snapshot");
            return;
        }

        if (args.Contains("--dump-hwinfo"))
        {
            using var hw = new HwInfoSharedSensors();
            using var vsb = new HwInfoVsbSensors();
            var outPath = args.SkipWhile(a => a != "--dump-hwinfo").Skip(1).FirstOrDefault()
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "HwInfo-Sensors.txt");
            File.WriteAllText(outPath, hw.Dump() + "\n" + vsb.Dump());
            return;
        }

        // One widget only -- a second instance would load the sensor driver twice.
        using var single = new Mutex(true, @"Local\DeskMon.SingleInstance", out bool isFirst);
        if (!isFirst) return;

        using var form = new OverlayForm(cfg, monitor);

        monitor.Start();
        Application.Run(form);
    }
}
