using LibreHardwareMonitor.Hardware;

namespace DeskMon;

/// <summary>One poll's worth of numbers. NaN means "sensor not available".</summary>
public sealed class Snapshot
{
    public double CpuLoad = double.NaN;
    public double CpuTemp = double.NaN;
    public double CpuPower = double.NaN;
    public double CpuClock = double.NaN;

    public double GpuLoad = double.NaN;
    public double GpuTemp = double.NaN;
    public double GpuHotspot = double.NaN;
    public double GpuPower = double.NaN;
    public double VramUsedGb = double.NaN;
    public double VramTotalGb = double.NaN;

    public double RamLoad = double.NaN;
    public double RamUsedGb = double.NaN;
    public double RamTotalGb = double.NaN;

    public string? CpuName;
    public string? GpuName;

    /// <summary>
    /// Non-null when this snapshot is a placeholder for a failed poll. Carried on the
    /// snapshot rather than raised as a separate event, because a separate event races
    /// this one through the message queue and loses.
    /// </summary>
    public string? Error;

    /// <summary>
    /// True when a Tctl/Tdie sensor exists but reads exactly 0 -- i.e. the sensor is
    /// there but nothing can talk to ring 0, so the value is meaningless. Lets the UI
    /// distinguish "no CPU temp because PawnIO is missing" from "no sensor at all".
    /// </summary>
    public bool CpuTempNeedsDriver;

    /// <summary>Where CpuTemp came from, so the UI can say so.</summary>
    public CpuTempSource CpuTempFrom = CpuTempSource.None;

    /// <summary>Which external tool supplied it, when CpuTempFrom is External.</summary>
    public string? CpuTempSourceName;
}

public enum CpuTempSource
{
    None,
    /// <summary>Read directly through the sensor library (needs PawnIO on Ryzen).</summary>
    Direct,
    /// <summary>Borrowed from a monitoring tool that already has a driver loaded.</summary>
    External,
}

/// <summary>
/// Polls CPU / GPU / memory sensors on a background thread.
/// Only those three subsystems are opened -- motherboard, storage, network and PSU
/// polling are the expensive ones and we never touch them.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly CpuTempChain _external = new();
    private readonly List<IHardware> _flat = new();
    private Thread? _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private volatile int _intervalMs;
    private volatile bool _opened;

    public event Action<Snapshot>? Updated;
    public event Action<string>? Failed;

    public HardwareMonitor(int intervalMs)
    {
        // Through the property, not the field: config.json is hand-editable, and
        // Wait(0) turns the poll loop into a hot spin while Wait(<-1) throws.
        IntervalMs = intervalMs;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsControllerEnabled = false,
            IsPsuEnabled = false,
            IsBatteryEnabled = false,
        };
    }

    public int IntervalMs
    {
        get => _intervalMs;
        set => _intervalMs = Math.Clamp(value, 250, 60_000);
    }

    public void Start()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "DeskMon.Sensors",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    private void Loop()
    {
        try
        {
            _computer.Open();
            _opened = true;
            Flatten(_computer.Hardware);
        }
        catch (Exception ex)
        {
            SafeRaiseFailed(ex.Message);
            return;
        }

        while (!_stop.IsSet)
        {
            Snapshot snap;
            try
            {
                snap = Poll();
            }
            catch (Exception ex)
            {
                SafeRaiseFailed(ex.Message);
                snap = new Snapshot { Error = ex.Message };
            }

            // A handler that throws here would kill the poll thread and silently
            // freeze the widget, so it is contained.
            try { Updated?.Invoke(snap); }
            catch { /* UI is going away */ }

            _stop.Wait(_intervalMs);
        }
    }

    private void SafeRaiseFailed(string message)
    {
        try { Failed?.Invoke(message); } catch { }
    }

    private void Flatten(IEnumerable<IHardware> items)
    {
        foreach (var hw in items)
        {
            _flat.Add(hw);
            if (hw.SubHardware.Length > 0) Flatten(hw.SubHardware);
        }
    }

    /// <summary>
    /// A sensor that physically cannot read zero. Ryzen Tctl and package power both
    /// report exactly 0 when there is no ring-0 path, which would otherwise render
    /// as a perfectly plausible "0°".
    /// </summary>
    private static bool Positive(float v) => v > 0f && !float.IsNaN(v);

    private Snapshot Poll()
    {
        var s = new Snapshot();

        foreach (var hw in _flat)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                case HardwareType.Memory:
                case HardwareType.GpuAmd:
                case HardwareType.GpuNvidia:
                case HardwareType.GpuIntel:
                    break;
                default:
                    continue;
            }

            hw.Update();

            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    s.CpuName ??= hw.Name;
                    ReadCpu(hw, s);
                    break;

                case HardwareType.Memory:
                    ReadMemory(hw, s);
                    break;

                case HardwareType.GpuAmd:
                case HardwareType.GpuNvidia:
                case HardwareType.GpuIntel:
                    s.GpuName ??= hw.Name;
                    ReadGpu(hw, s);
                    break;
            }
        }

        if (!double.IsNaN(s.CpuTemp))
        {
            s.CpuTempFrom = CpuTempSource.Direct;
        }
        else
        {
            // No ring-0 path of our own. If Afterburner is running it already has
            // one, so borrow the number it publishes rather than loading a driver.
            _external.Update();

            if (!double.IsNaN(_external.CpuTemp))
            {
                s.CpuTemp = _external.CpuTemp;
                s.CpuTempFrom = CpuTempSource.External;
                s.CpuTempSourceName = _external.SourceName;
                s.CpuTempNeedsDriver = false;
            }

            // Each reading is toggled independently in these tools' settings, so a
            // missing temperature says nothing about power or clock. Update() leaves
            // anything unreadable as NaN, making these no-ops when nothing is running.
            if (double.IsNaN(s.CpuPower)) s.CpuPower = _external.CpuPower;
            if (double.IsNaN(s.CpuClock)) s.CpuClock = _external.CpuClock;
        }

        return s;
    }

    private static void ReadCpu(IHardware hw, Snapshot s)
    {
        foreach (var sensor in hw.Sensors)
        {
            if (sensor.Value is not float v) continue;
            var n = sensor.Name;

            switch (sensor.SensorType)
            {
                case SensorType.Load when n == "CPU Total":
                    s.CpuLoad = v;      // 0 is a legitimate load
                    break;

                // Ryzen exposes "Core (Tctl/Tdie)"; Intel uses "CPU Package".
                case SensorType.Temperature when n.StartsWith("Core (Tctl", StringComparison.Ordinal)
                                               || n == "CPU Package":
                    if (Positive(v)) s.CpuTemp = v;
                    else s.CpuTempNeedsDriver = true;
                    break;

                case SensorType.Power when n == "Package":
                    if (Positive(v)) s.CpuPower = v;
                    break;

                case SensorType.Clock when n == "Cores (Average)" || n == "Core #1":
                    if (Positive(v) && double.IsNaN(s.CpuClock)) s.CpuClock = v;
                    break;
            }
        }
    }

    private static void ReadGpu(IHardware hw, Snapshot s)
    {
        foreach (var sensor in hw.Sensors)
        {
            if (sensor.Value is not float v) continue;
            var n = sensor.Name;

            switch (sensor.SensorType)
            {
                case SensorType.Load when n == "GPU Core":
                    s.GpuLoad = v;
                    break;

                case SensorType.Temperature:
                    if (n == "GPU Core") { if (Positive(v)) s.GpuTemp = v; }
                    else if (n == "GPU Hot Spot") { if (Positive(v)) s.GpuHotspot = v; }
                    break;

                // AMD reports total board power as "GPU Package"; NVIDIA as "GPU Power".
                case SensorType.Power when n is "GPU Package" or "GPU Power":
                    if (Positive(v)) s.GpuPower = v;
                    break;

                // SmallData is in MB. The vendor "GPU Memory *" pair comes from ADL and
                // is only populated when ADL2_Adapter_MemoryInfoX4_Get resolves; the
                // "D3D Dedicated Memory *" pair is recomputed from D3DKMT every update.
                // Prefer the vendor value, but fall back so the pair never goes half-missing.
                case SensorType.SmallData when n is "GPU Memory Used" or "D3D Dedicated Memory Used":
                    if (double.IsNaN(s.VramUsedGb) || n == "GPU Memory Used")
                        s.VramUsedGb = v / 1024.0;
                    break;

                case SensorType.SmallData when n is "GPU Memory Total" or "D3D Dedicated Memory Total":
                    if (Positive(v) && (double.IsNaN(s.VramTotalGb) || n == "GPU Memory Total"))
                        s.VramTotalGb = v / 1024.0;
                    break;
            }
        }
    }

    private static void ReadMemory(IHardware hw, Snapshot s)
    {
        // LHM exposes two Memory devices: "Total Memory" (physical RAM) and
        // "Virtual Memory" (the commit charge). Only the physical one is wanted --
        // relying on enumeration order to pick would be a coin flip.
        if (hw.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) return;

        double used = double.NaN, avail = double.NaN;

        foreach (var sensor in hw.Sensors)
        {
            if (sensor.Value is not float v) continue;

            switch (sensor.SensorType)
            {
                case SensorType.Load when sensor.Name == "Memory":
                    s.RamLoad = v;
                    break;
                case SensorType.Data when sensor.Name == "Memory Used":
                    used = v;
                    break;
                case SensorType.Data when sensor.Name == "Memory Available":
                    avail = v;
                    break;
            }
        }

        if (!double.IsNaN(used))
        {
            s.RamUsedGb = used;                                   // already GB
            if (!double.IsNaN(avail)) s.RamTotalGb = used + avail;
        }
    }

    /// <summary>
    /// PawnIO is the signed, HVCI-compatible kernel driver that replaced WinRing0.
    /// Without it, Ryzen Tctl/Tdie cannot be read at all -- not even elevated.
    /// </summary>
    public static bool IsPawnIOInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            if (key is not null) return true;
        }
        catch { }

        try
        {
            var sys = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "PawnIO.sys");
            if (File.Exists(sys)) return true;
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Every sensor the library can see, for checking what this machine actually
    /// exposes (and what the picker logic above should be keying off).
    /// </summary>
    public string DumpSensors()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("DeskMon sensor dump");
        sb.AppendLine($"LHM version : {typeof(Computer).Assembly.GetName().Version}");
        sb.AppendLine($"Elevated    : {IsElevated()}");
        sb.AppendLine($"PawnIO      : {(IsPawnIOInstalled() ? "installed" : "not installed")}");
        sb.AppendLine("External CPU temperature sources:");
        sb.Append(_external.Describe());
        sb.AppendLine($"Opened      : {_opened}");
        sb.AppendLine();

        if (!_opened)
        {
            sb.AppendLine("Sensor library is not open yet.");
            return sb.ToString();
        }

        foreach (var hw in _flat.ToArray())
        {
            sb.AppendLine($"[{hw.HardwareType}] {hw.Name}");
            foreach (var s in hw.Sensors.OrderBy(x => x.SensorType).ThenBy(x => x.Name))
            {
                var v = s.Value.HasValue ? s.Value.Value.ToString("0.###") : "null";
                sb.AppendLine($"    {s.SensorType,-12} {s.Name,-34} = {v}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _stop.Set();

        // Only tear the library down once the poll thread has actually stopped.
        // Closing underneath a live Update() would race the sensor backend, and
        // disposing _stop while the thread is still in Wait() throws in that thread.
        bool stopped = _thread is null || _thread.Join(3000);

        if (stopped)
        {
            if (_opened)
            {
                try { _computer.Close(); } catch { /* backend may already be gone */ }
            }
            _external.Dispose();
            _stop.Dispose();
        }
        // If the thread is wedged, leak both rather than crash on the way out --
        // the process is exiting anyway and the thread is a background thread.
    }
}
