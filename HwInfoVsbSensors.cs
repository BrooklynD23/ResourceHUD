using System.Globalization;
using Microsoft.Win32;

namespace DeskMon;

/// <summary>
/// Reads HWiNFO's Gadget/VSB registry feed (HKCU/HKLM\Software\HWiNFO64\VSB).
///
/// Unlike shared memory this feed has no Pro time limit, but HWiNFO only publishes
/// the sensor elements ticked under "HWiNFO Gadget". The registry carries no sensor
/// ids, so values are matched by sensor name + reading label + formatted unit.
/// Everything stays NaN when HWiNFO is closed or nothing useful is exported.
/// </summary>
public sealed class HwInfoVsbSensors : IDisposable
{
    private const string KeyPath = @"Software\HWiNFO64\VSB";
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private List<Entry> _entries = new();
    private int _cachedCount = -1;
    private DateTime _lastScan = DateTime.MinValue;
    private bool _scanned;

    public string Name => "HWiNFO Gadget";

    public double CpuTemp { get; private set; } = double.NaN;
    public double CpuPower { get; private set; } = double.NaN;
    public double CpuClock { get; private set; } = double.NaN;
    public double CpuLoad { get; private set; } = double.NaN;

    public double GpuTemp { get; private set; } = double.NaN;
    public double GpuHotspot { get; private set; } = double.NaN;
    public double GpuLoad { get; private set; } = double.NaN;
    public double GpuPower { get; private set; } = double.NaN;
    public double VramUsedGb { get; private set; } = double.NaN;
    public double VramTotalGb { get; private set; } = double.NaN;

    public double RamTemp { get; private set; } = double.NaN;
    public double RamLoad { get; private set; } = double.NaN;

    public string? CpuTempLabel { get; private set; }
    public string? GpuTempLabel { get; private set; }
    public string? GpuHotspotLabel { get; private set; }
    public string? RamTempLabel { get; private set; }

    private sealed class Entry
    {
        public int Index;
        public string Sensor = "";
        public string Label = "";
        public string Value = "";
        public double Raw;
        public bool HasRaw;
        public bool Fahrenheit;
        public bool TempUnit;
        public bool PercentUnit;
        public bool PowerUnit;
        public bool ClockUnit;
        public bool DataUnit;
    }

    public void Update()
    {
        lock (_gate)
        {
            Reset();

            using var key = OpenKey();
            if (key is null) return;

            try
            {
                EnsureScan(key);
                Select();
            }
            catch
            {
                _entries = new List<Entry>();
                _scanned = false;
            }
        }
    }

    private void Reset()
    {
        CpuTemp = CpuPower = CpuClock = CpuLoad = double.NaN;
        GpuTemp = GpuHotspot = GpuLoad = GpuPower = double.NaN;
        VramUsedGb = VramTotalGb = double.NaN;
        RamTemp = RamLoad = double.NaN;
        CpuTempLabel = GpuTempLabel = GpuHotspotLabel = RamTempLabel = null;
    }

    private static RegistryKey? OpenKey()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            RegistryKey? key = null;
            try { key = root.OpenSubKey(KeyPath, RegistryKeyPermissionCheck.ReadSubTree); }
            catch { }

            if (key is { ValueCount: > 0 }) return key;
            key?.Dispose();
        }
        return null;
    }

    private void EnsureScan(RegistryKey key)
    {
        if (_scanned && key.ValueCount == _cachedCount &&
            DateTime.UtcNow - _lastScan < RescanInterval)
            return;

        Scan(key);
    }

    private void Scan(RegistryKey key)
    {
        var entries = new List<Entry>();
        foreach (var name in key.GetValueNames())
        {
            if (!name.StartsWith("Sensor", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(name.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                continue;

            var e = new Entry { Index = idx };
            e.Sensor = AsString(key.GetValue($"Sensor{idx}"));
            e.Label = AsString(key.GetValue($"Label{idx}"));
            e.Value = AsString(key.GetValue($"Value{idx}"));
            e.Raw = ParseRaw(AsString(key.GetValue($"ValueRaw{idx}")), out e.HasRaw);

            e.Fahrenheit = IsFahrenheit(e.Value);
            e.TempUnit = LooksLikeTempUnit(e.Value) || HasTempWord(e.Label);
            e.PercentUnit = e.Value.Contains('%') || e.Value.EndsWith(" percent", StringComparison.OrdinalIgnoreCase);
            e.PowerUnit = e.Value.EndsWith("W", StringComparison.OrdinalIgnoreCase) ||
                          e.Value.Contains(" Watt", StringComparison.OrdinalIgnoreCase);
            e.ClockUnit = e.Value.Contains("MHz", StringComparison.OrdinalIgnoreCase) ||
                          e.Value.Contains("GHz", StringComparison.OrdinalIgnoreCase) ||
                          e.Value.EndsWith("Hz", StringComparison.OrdinalIgnoreCase);
            e.DataUnit = e.Value.Contains("GB", StringComparison.OrdinalIgnoreCase) ||
                         e.Value.Contains("MB", StringComparison.OrdinalIgnoreCase) ||
                         e.Value.Contains("KB", StringComparison.OrdinalIgnoreCase) ||
                         e.Value.Contains("TB", StringComparison.OrdinalIgnoreCase);

            if (e.Label.Length == 0 && e.Sensor.Length == 0) continue;
            entries.Add(e);
        }

        entries.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        _entries = entries;
        _cachedCount = key.ValueCount;
        _lastScan = DateTime.UtcNow;
        _scanned = true;
    }

    private void Select()
    {
        var cpuTemp = Best(_entries, ScoreCpuTemp);
        CpuTemp = TempCelsius(cpuTemp);
        CpuTempLabel = cpuTemp?.Label;

        var gpuTemp = Best(_entries, ScoreGpuTemp);
        GpuTemp = TempCelsius(gpuTemp);
        GpuTempLabel = gpuTemp?.Label;

        var gpuHot = Best(_entries, ScoreGpuHotspot);
        GpuHotspot = TempCelsius(gpuHot);
        GpuHotspotLabel = gpuHot?.Label;

        var ramTemp = Best(_entries, ScoreRamTemp);
        RamTemp = TempCelsius(ramTemp);
        RamTempLabel = ramTemp?.Label;

        CpuLoad = RawOf(Best(_entries, ScoreCpuLoad));
        RamLoad = RawOf(Best(_entries, ScoreRamLoad));
        GpuLoad = RawOf(Best(_entries, ScoreGpuLoad));
        CpuPower = RawOf(Best(_entries, ScoreCpuPower));
        CpuClock = RawOf(Best(_entries, ScoreCpuClock));
        GpuPower = RawOf(Best(_entries, ScoreGpuPower));
        VramUsedGb = DataGbOf(Best(_entries, ScoreVramUsed));
        VramTotalGb = DataGbOf(Best(_entries, ScoreVramTotal));
    }

    private static Entry? Best(List<Entry> entries, Func<Entry, int> score)
    {
        Entry? best = null;
        int bestScore = -1;
        foreach (var e in entries)
        {
            int s = score(e);
            if (s < 0) continue;
            if (best is null || s > bestScore ||
                (s == bestScore && e.HasRaw && (!best.HasRaw || e.Raw > best.Raw)))
            {
                best = e;
                bestScore = s;
            }
        }
        return best;
    }

    private static string AsString(object? value) => value as string ?? "";

    private static double ParseRaw(string text, out bool ok)
    {
        ok = false;
        if (string.IsNullOrWhiteSpace(text)) return double.NaN;

        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out double v) ||
            double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.CurrentCulture, out v))
        {
            ok = true;
            return v;
        }
        return double.NaN;
    }

    private static bool IsFahrenheit(string value) =>
        value.Contains("°F", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith("F", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Fahrenheit", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeTempUnit(string value) =>
        value.Contains('°') ||
        value.EndsWith("C", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith("F", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Celsius", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Fahrenheit", StringComparison.OrdinalIgnoreCase);

    private static bool HasTempWord(string label) =>
        label.Contains("temp", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("tctl", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("tdie", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("junction", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("hot spot", StringComparison.OrdinalIgnoreCase);

    private static string Norm(string s) =>
        s.Trim().ToLowerInvariant();

    private static bool MentionsCpu(string s) =>
        s.Contains("cpu", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("processor", StringComparison.OrdinalIgnoreCase);

    private static bool MentionsGpu(string s) =>
        s.Contains("gpu", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("graphics", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("arc", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("radeon", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("geforce", StringComparison.OrdinalIgnoreCase);

    private static bool MentionsMemory(string s) =>
        s.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("dram", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("ram", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("spd", StringComparison.OrdinalIgnoreCase);

    private static bool IsTempCandidate(Entry e) =>
        (e.TempUnit || HasTempWord(e.Label)) &&
        !e.PercentUnit && !e.PowerUnit && !e.ClockUnit && !e.DataUnit;

    private static bool IsPercentCandidate(Entry e) =>
        e.PercentUnit ||
        e.Label.Contains("usage", StringComparison.OrdinalIgnoreCase) ||
        e.Label.Contains("load", StringComparison.OrdinalIgnoreCase) ||
        e.Label.Contains("utilization", StringComparison.OrdinalIgnoreCase);

    private static bool LooksPerCore(string label)
    {
        if (label.Contains("core #", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("p-core", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("e-core", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("ccd", StringComparison.OrdinalIgnoreCase))
            return true;

        const string core = "core ";
        int i = label.IndexOf(core, StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            int j = i + core.Length;
            if (j < label.Length && char.IsDigit(label[j])) return true;
            i = label.IndexOf(core, i + core.Length, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static int ScoreCpuTemp(Entry e)
    {
        if (!IsTempCandidate(e)) return -1;
        var l = Norm(e.Label);
        var s = Norm(e.Sensor);
        bool cpuCtx = MentionsCpu(s) || MentionsCpu(l) || l.Contains("package") ||
                      l.Contains("tctl") || l.Contains("tdie") || l.Contains("core");
        if (!cpuCtx) return -1;
        if (l.Contains("power") || l.Contains("clock") || l.Contains("usage") ||
            l.Contains("load") || l.Contains("distance") || l.Contains("tjmax") ||
            l.Contains("throttle") || LooksPerCore(l))
            return -1;

        if (l.Contains("package")) return 100;
        if (l.Contains("tctl") || l.Contains("tdie")) return 95;
        if (l.Contains("cpu") && (l.Contains("temp") || l.Contains("temperature"))) return 90;
        if (l == "core temperature" || l == "core temp") return 85;
        if (l.Contains("core average") || l.Contains("core max")) return 60;
        if (s.Contains("cpu") && e.TempUnit) return 30;
        return -1;
    }

    private static int ScoreGpuTemp(Entry e)
    {
        if (!IsTempCandidate(e)) return -1;
        var l = Norm(e.Label);
        bool gpuCtx = MentionsGpu(e.Sensor) || MentionsGpu(l);
        if (!gpuCtx) return -1;
        if (l.Contains("hot") || l.Contains("junction") || l.Contains("hotspot")) return -1;
        if (l.Contains("memory")) return -1;

        if (l == "gpu temperature" || l == "gpu core" || l == "gpu core temp") return 100;
        if (l.Contains("gpu") && (l.Contains("temp") || l.Contains("temperature"))) return 90;
        if (MentionsGpu(e.Sensor) && e.TempUnit && (l.Contains("temperature") || l.Length == 0)) return 50;
        return -1;
    }

    private static int ScoreGpuHotspot(Entry e)
    {
        if (!IsTempCandidate(e)) return -1;
        var l = Norm(e.Label);
        if (!(l.Contains("hot") || l.Contains("junction") || l.Contains("hotspot"))) return -1;
        if (!(MentionsGpu(e.Sensor) || MentionsGpu(l))) return -1;
        return 100;
    }

    private static int ScoreRamTemp(Entry e)
    {
        if (!IsTempCandidate(e)) return -1;
        var l = Norm(e.Label);
        if (!(MentionsMemory(l) || MentionsMemory(e.Sensor))) return -1;
        if (l.Contains("load") || l.Contains("usage") || l.Contains("clock") ||
            l.Contains("voltage") || l.Contains("size") || l.Contains("gpu"))
            return -1;

        if (l.Contains("dram") || l.Contains("memory temperature") || l.Contains("ram temperature")) return 100;
        if (l.Contains("spd") || l.Contains("hub")) return 70;
        if (l.Contains("memory") || l.Contains("ram")) return 60;
        if (MentionsMemory(e.Sensor) && e.TempUnit) return 40;
        return -1;
    }

    private static int ScoreCpuLoad(Entry e)
    {
        if (!IsPercentCandidate(e)) return -1;
        var l = Norm(e.Label);
        var s = Norm(e.Sensor);
        if (!(MentionsCpu(s) || MentionsCpu(l))) return -1;
        if (LooksPerCore(l) || l.Contains("core #")) return -1;
        if (l.Contains("total cpu usage") || l.Contains("cpu total usage")) return 100;
        if (l.Contains("total cpu") || l.Contains("cpu total")) return 95;
        if (l.Contains("cpu usage") || l.Contains("cpu utility")) return 90;
        if (l.Contains("cpu") && (l.Contains("load") || l.Contains("usage") || l.Contains("utilization"))) return 80;
        if (s.Contains("cpu") && e.PercentUnit) return 40;
        return -1;
    }

    private static int ScoreRamLoad(Entry e)
    {
        if (!IsPercentCandidate(e)) return -1;
        var l = Norm(e.Label);
        if (!(l.Contains("physical memory") || l.Contains("memory load") || l.Contains("ram load") ||
              l.Contains("memory usage")))
            return -1;
        if (l.Contains("virtual")) return -1;
        if (l.Contains("physical memory load") || l.Contains("memory load")) return 100;
        if (l.Contains("physical memory")) return 90;
        if (l.Contains("ram")) return 80;
        return 60;
    }

    private static int ScoreGpuLoad(Entry e)
    {
        if (!IsPercentCandidate(e)) return -1;
        var l = Norm(e.Label);
        bool gpuCtx = MentionsGpu(e.Sensor) || MentionsGpu(l);
        if (!gpuCtx) return -1;
        if (l.Contains("memory") || l.Contains("fan") || l.Contains("clock") || l.Contains("power")) return -1;

        if (l.Contains("gpu core load") || l.Contains("gpu usage") || l.Contains("gpu utilization")) return 100;
        if (l.Contains("core load") || l.Contains("usage") || l.Contains("utilization")) return 90;
        if (l.Contains("3d") || l.Contains("compute") || l.Contains("copy") || l.Contains("video")) return 80;
        if (MentionsGpu(e.Sensor) && e.PercentUnit) return 50;
        return -1;
    }

    private static int ScoreCpuPower(Entry e)
    {
        if (!e.PowerUnit) return -1;
        var l = Norm(e.Label);
        var s = Norm(e.Sensor);
        if (!(MentionsCpu(s) || MentionsCpu(l))) return -1;
        if (l.Contains("package power") || l.Contains("cpu ppt") || l.Contains("cpu power")) return 100;
        if (l.Contains("package") || l.Contains("power") || l.Contains("tdp")) return 80;
        if (s.Contains("cpu") && l.Contains("power")) return 60;
        return -1;
    }

    private static int ScoreCpuClock(Entry e)
    {
        if (!e.ClockUnit) return -1;
        var l = Norm(e.Label);
        var s = Norm(e.Sensor);
        if (!(MentionsCpu(s) || MentionsCpu(l))) return -1;
        if (l.Contains("memory") || l.Contains("gpu")) return -1;
        if (l.Contains("average effective clock") || l.Contains("cpu clock")) return 100;
        if (l.Contains("core 0 clock") || l.Contains("core clock")) return 90;
        if (l.Contains("clock") || l.Contains("frequency")) return 70;
        if (s.Contains("cpu") && e.ClockUnit) return 40;
        return -1;
    }

    private static int ScoreGpuPower(Entry e)
    {
        if (!e.PowerUnit) return -1;
        var l = Norm(e.Label);
        if (!(MentionsGpu(e.Sensor) || MentionsGpu(l))) return -1;
        if (l.Contains("gpu power") || l.Contains("gpu package")) return 100;
        if (l.Contains("power") || l.Contains("package")) return 80;
        if (MentionsGpu(e.Sensor) && e.PowerUnit) return 50;
        return -1;
    }

    private static int ScoreVramUsed(Entry e)
    {
        if (!e.DataUnit) return -1;
        var l = Norm(e.Label);
        if (!MentionsGpu(e.Sensor) && !l.Contains("gpu")) return -1;
        if (l.Contains("total") || l.Contains("free")) return -1;
        if (l.Contains("gpu memory allocated") || l.Contains("gpu memory used") ||
            l.Contains("d3d memory dedicated") || l.Contains("vram used")) return 100;
        if (l.Contains("gpu memory") || l.Contains("vram") || l.Contains("dedicated")) return 80;
        if (l.Contains("memory") && MentionsGpu(e.Sensor)) return 50;
        return -1;
    }

    private static int ScoreVramTotal(Entry e)
    {
        if (!e.DataUnit) return -1;
        var l = Norm(e.Label);
        if (!MentionsGpu(e.Sensor) && !l.Contains("gpu")) return -1;
        if (!(l.Contains("total") || l.Contains("size") || l.Contains("capacity"))) return -1;
        if (l.Contains("gpu memory total") || l.Contains("d3d memory dedicated total") ||
            l.Contains("vram total")) return 100;
        if (l.Contains("gpu memory") || l.Contains("vram") || l.Contains("dedicated")) return 80;
        return 50;
    }

    private static double TempCelsius(Entry? e)
    {
        if (e is null || !e.HasRaw) return double.NaN;
        double v = e.Raw;
        if (e.Fahrenheit) v = (v - 32.0) * 5.0 / 9.0;
        if (v <= 0 || v > 150) return double.NaN;
        return v;
    }

    private static double RawOf(Entry? e) => e is { HasRaw: true } ? e.Raw : double.NaN;

    private static double DataGbOf(Entry? e)
    {
        if (e is null || !e.HasRaw) return double.NaN;
        double v = e.Raw;
        var u = e.Value;
        if (u.Contains("TB", StringComparison.OrdinalIgnoreCase)) v *= 1024.0;
        else if (u.Contains("GB", StringComparison.OrdinalIgnoreCase) || u.Contains("GiB", StringComparison.OrdinalIgnoreCase)) { }
        else if (u.Contains("MB", StringComparison.OrdinalIgnoreCase) || u.Contains("MiB", StringComparison.OrdinalIgnoreCase)) v /= 1024.0;
        else if (u.Contains("KB", StringComparison.OrdinalIgnoreCase) || u.Contains("KiB", StringComparison.OrdinalIgnoreCase)) v /= 1024.0 * 1024.0;
        else v /= 1024.0; // HWiNFO memory readings are usually MB.
        return v > 0 ? v : double.NaN;
    }

    /// <summary>One line per matched metric, for the sensor dump.</summary>
    public string Describe()
    {
        lock (_gate)
        {
            if (!_scanned || _entries.Count == 0)
                return "    HWiNFO Gadget     : not available\n";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"    HWiNFO Gadget     : {_entries.Count} readings");
            Append(sb, "CPU temp", CpuTemp, CpuTempLabel);
            Append(sb, "GPU temp", GpuTemp, GpuTempLabel);
            Append(sb, "GPU hot spot", GpuHotspot, GpuHotspotLabel);
            Append(sb, "RAM temp", RamTemp, RamTempLabel);
            return sb.ToString();
        }
    }

    private static void Append(System.Text.StringBuilder sb, string name, double value, string? label)
    {
        if (double.IsNaN(value)) return;
        sb.AppendLine($"        {name,-12} : {value:0.0} C   ({label})");
    }

    /// <summary>Every exported reading plus the metrics DeskMon picked, for diagnostics.</summary>
    public string Dump()
    {
        lock (_gate)
        {
            var sb = new System.Text.StringBuilder();
            using var key = OpenKey();
            if (key is null)
            {
                sb.AppendLine("HWiNFO Gadget registry feed not available.");
                return sb.ToString();
            }

            Scan(key);
            Select();

            sb.AppendLine($"HWiNFO Gadget dump ({_entries.Count} readings)");
            sb.AppendLine($"CPU temp      : {(double.IsNaN(CpuTemp) ? "n/a" : CpuTemp.ToString("0.0"))}  {CpuTempLabel}");
            sb.AppendLine($"GPU temp      : {(double.IsNaN(GpuTemp) ? "n/a" : GpuTemp.ToString("0.0"))}  {GpuTempLabel}");
            sb.AppendLine($"GPU hot spot  : {(double.IsNaN(GpuHotspot) ? "n/a" : GpuHotspot.ToString("0.0"))}  {GpuHotspotLabel}");
            sb.AppendLine($"RAM temp      : {(double.IsNaN(RamTemp) ? "n/a" : RamTemp.ToString("0.0"))}  {RamTempLabel}");
            sb.AppendLine();

            foreach (var e in _entries)
            {
                var raw = e.HasRaw ? e.Raw.ToString("0.###", CultureInfo.InvariantCulture) : "";
                sb.AppendLine($"{e.Index,4}  {e.Sensor,-42} {e.Label,-36} {e.Value,-18} {raw}");
            }
            return sb.ToString();
        }
    }

    public void Dispose()
    {
        lock (_gate) _entries = new List<Entry>();
    }
}
