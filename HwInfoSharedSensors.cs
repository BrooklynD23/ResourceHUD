using System.Globalization;
using System.Text;

namespace DeskMon;

/// <summary>
/// Reads every sensor HWiNFO publishes through shared memory (Global\HWiNFO_SENS_SM2).
///
/// This is the broad fallback used when the VSB registry feed is unavailable: the
/// shared section carries all monitored readings, so GPU/RAM temperatures can be
/// selected by sensor name + reading label + unit. The free HWiNFO build disables
/// shared memory after 12 hours, so this is best-effort; values stay NaN when the
/// section is absent, stale, or the hardware exposes nothing.
/// </summary>
public sealed class HwInfoSharedSensors : IDisposable
{
    private const uint Signature = 0x53695748; // 'HWiS'

    private const int HdrSignature = 0;
    private const int HdrPollTime = 12;
    private const int HdrOffsetOfSensorSection = 20;
    private const int HdrSizeOfSensorElement = 24;
    private const int HdrNumSensorElements = 28;
    private const int HdrOffsetOfReadingSection = 32;
    private const int HdrSizeOfReadingElement = 36;
    private const int HdrNumReadingElements = 40;

    private const int SeId = 0;
    private const int SeInst = 4;
    private const int SeNameOrig = 8;
    private const int SeNameUser = 136;

    private const int RdType = 0;
    private const int RdSensorIndex = 4;
    private const int RdReadingId = 8;
    private const int RdLabelOrig = 12;
    private const int RdLabelUser = 140;
    private const int RdUnit = 268;
    private const int RdValue = 284;
    private const int LabelLen = 128;
    private const int UnitLen = 16;

    private const uint TypeTemp = 1;
    private const uint TypePower = 5;
    private const uint TypeClock = 6;
    private const uint TypeUsage = 7;
    private const uint TypeOther = 8;

    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly NamedSection _section;
    private readonly byte[] _scratch = new byte[LabelLen];

    public string Name => "HWiNFO shared";

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

    /// <summary>True when any CPU thermal-throttling / thermal-event flag is asserted.</summary>
    public bool CpuThrottling { get; private set; }

    public string? CpuTempLabel { get; private set; }
    public string? GpuTempLabel { get; private set; }
    public string? GpuHotspotLabel { get; private set; }
    public string? RamTempLabel { get; private set; }

    private sealed class Entry
    {
        public uint Type;
        public string Sensor = "";
        public string Label = "";
        public string Unit = "";
        public double Value;
        public bool TempUnit;
        public bool PercentUnit;
        public bool PowerUnit;
        public bool ClockUnit;
        public bool DataUnit;
        public bool Fahrenheit;
        public bool Kelvin;
    }

    public HwInfoSharedSensors()
        : this(new[] { @"Global\HWiNFO_SENS_SM2", "HWiNFO_SENS_SM2" })
    {
    }

    private HwInfoSharedSensors(string[] names) => _section = new NamedSection(names);

    public void Update()
    {
        Reset();

        try
        {
            if (!_section.Attach()) return;
            var acc = _section.Accessor!;

            if (acc.ReadUInt32(HdrSignature) != Signature) { _section.Detach(); return; }

            long pollTime = acc.ReadInt64(HdrPollTime);
            uint sOff = acc.ReadUInt32(HdrOffsetOfSensorSection);
            uint sSize = acc.ReadUInt32(HdrSizeOfSensorElement);
            uint sCount = acc.ReadUInt32(HdrNumSensorElements);
            uint rOff = acc.ReadUInt32(HdrOffsetOfReadingSection);
            uint rSize = acc.ReadUInt32(HdrSizeOfReadingElement);
            uint rCount = acc.ReadUInt32(HdrNumReadingElements);

            if (sSize < SeNameUser + LabelLen || rSize < RdValue + 8 ||
                sCount == 0 || sCount > 100_000 || rCount == 0 || rCount > 100_000 ||
                !_section.Fits(sOff, sCount, sSize) || !_section.Fits(rOff, rCount, rSize))
            {
                _section.Detach();
                return;
            }

            if (pollTime > 0)
            {
                var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(pollTime);
                if (age > MaxAge || age < -MaxAge) { _section.Detach(); return; }
            }

            var sensors = new string[sCount];
            for (uint i = 0; i < sCount; i++)
            {
                long b = sOff + (long)i * sSize;
                uint id = acc.ReadUInt32(b + SeId);
                uint inst = acc.ReadUInt32(b + SeInst);
                var orig = _section.ReadAnsi(b + SeNameOrig, LabelLen, _scratch);
                var user = _section.ReadAnsi(b + SeNameUser, LabelLen, _scratch);
                sensors[i] = $"{id:X8}_{inst} {(user.Length > 0 ? user : orig)}";
            }

            var entries = new List<Entry>(Math.Min((int)rCount, 4096));
            for (uint i = 0; i < rCount && entries.Count < 8192; i++)
            {
                long b = rOff + (long)i * rSize;
                uint type = acc.ReadUInt32(b + RdType);
                if (type is not (TypeTemp or TypePower or TypeClock or TypeUsage or TypeOther)) continue;

                uint sIdx = acc.ReadUInt32(b + RdSensorIndex);
                var e = new Entry
                {
                    Type = type,
                    Sensor = sIdx < sensors.Length ? sensors[sIdx] : $"#{sIdx}",
                    Label = _section.ReadAnsi(b + RdLabelOrig, LabelLen, _scratch),
                    Unit = _section.ReadAnsi(b + RdUnit, UnitLen, _scratch),
                    Value = acc.ReadDouble(b + RdValue),
                };
                if (e.Label.Length == 0)
                    e.Label = _section.ReadAnsi(b + RdLabelUser, LabelLen, _scratch);
                if (string.IsNullOrWhiteSpace(e.Label) && string.IsNullOrWhiteSpace(e.Sensor)) continue;

                e.TempUnit = e.Unit.Contains('°') || e.Unit.Contains("C", StringComparison.OrdinalIgnoreCase) ||
                             e.Unit.Contains("F", StringComparison.OrdinalIgnoreCase) ||
                             e.Unit.Contains("K", StringComparison.OrdinalIgnoreCase);
                e.PercentUnit = e.Unit.Contains('%') || e.Label.Contains('%') ||
                                e.Label.Contains("load", StringComparison.OrdinalIgnoreCase) ||
                                e.Label.Contains("usage", StringComparison.OrdinalIgnoreCase) ||
                                e.Label.Contains("utilization", StringComparison.OrdinalIgnoreCase);
                e.PowerUnit = e.Unit.EndsWith("W", StringComparison.OrdinalIgnoreCase) ||
                              e.Label.Contains("power", StringComparison.OrdinalIgnoreCase) ||
                              e.Label.Contains("PPT", StringComparison.OrdinalIgnoreCase);
                e.ClockUnit = e.Unit.Contains("MHz", StringComparison.OrdinalIgnoreCase) ||
                              e.Unit.Contains("GHz", StringComparison.OrdinalIgnoreCase) ||
                              e.Unit.EndsWith("Hz", StringComparison.OrdinalIgnoreCase) ||
                              e.Label.Contains("clock", StringComparison.OrdinalIgnoreCase) ||
                              e.Label.Contains("frequency", StringComparison.OrdinalIgnoreCase);
                e.DataUnit = e.Unit.Contains("MB", StringComparison.OrdinalIgnoreCase) ||
                             e.Unit.Contains("GB", StringComparison.OrdinalIgnoreCase) ||
                             e.Unit.Contains("KB", StringComparison.OrdinalIgnoreCase) ||
                             e.Unit.Contains("TB", StringComparison.OrdinalIgnoreCase) ||
                             e.Label.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
                             e.Label.Contains("VRAM", StringComparison.OrdinalIgnoreCase) ||
                             e.Label.Contains("Dedicated", StringComparison.OrdinalIgnoreCase);
                e.Fahrenheit = e.Unit.Contains('F') || e.Label.Contains("Fahrenheit", StringComparison.OrdinalIgnoreCase);
                e.Kelvin = e.Unit.Contains('K') && !e.Unit.Contains("KB", StringComparison.OrdinalIgnoreCase) ||
                           e.Label.Contains("Kelvin", StringComparison.OrdinalIgnoreCase);

                entries.Add(e);
            }

            Select(entries);
        }
        catch
        {
            _section.Detach();
        }
    }

    private void Reset()
    {
        CpuTemp = CpuPower = CpuClock = CpuLoad = double.NaN;
        GpuTemp = GpuHotspot = GpuLoad = GpuPower = double.NaN;
        VramUsedGb = VramTotalGb = double.NaN;
        RamTemp = RamLoad = double.NaN;
        CpuThrottling = false;
        CpuTempLabel = GpuTempLabel = GpuHotspotLabel = RamTempLabel = null;
    }

    private void Select(List<Entry> entries)
    {
        var cpuTemp = Best(entries, ScoreCpuTemp);
        CpuTemp = TempCelsius(cpuTemp);
        CpuTempLabel = cpuTemp?.Label;

        var gpuTemp = Best(entries, ScoreGpuTemp);
        GpuTemp = TempCelsius(gpuTemp);
        GpuTempLabel = gpuTemp?.Label;

        var gpuHot = Best(entries, ScoreGpuHotspot);
        GpuHotspot = TempCelsius(gpuHot);
        GpuHotspotLabel = gpuHot?.Label;

        var ramTemp = Best(entries, ScoreRamTemp);
        RamTemp = TempCelsius(ramTemp);
        RamTempLabel = ramTemp?.Label;

        CpuLoad = RawOf(Best(entries, ScoreCpuLoad));
        RamLoad = RawOf(Best(entries, ScoreRamLoad));
        GpuLoad = RawOf(Best(entries, ScoreGpuLoad));
        CpuPower = RawOf(Best(entries, ScoreCpuPower));
        CpuClock = RawOf(Best(entries, ScoreCpuClock));
        GpuPower = RawOf(Best(entries, ScoreGpuPower));
        VramUsedGb = DataGbOf(Best(entries, ScoreVramUsed));
        VramTotalGb = DataGbOf(Best(entries, ScoreVramTotal));
        CpuThrottling = entries.Any(IsThrottleReading);
    }

    private static bool IsThrottleReading(Entry e)
    {
        if (e.Value < 0.5) return false;
        var l = Norm(e.Label);
        if (!(l.Contains("throttl") || l.Contains("thermal event"))) return false;
        // Per-core DTS flags latch/power-state noise on some CPUs; trust package/ring or thermal-event only.
        if (LooksPerCore(l)) return false;
        return MentionsCpu(e.Sensor) || MentionsCpu(l) || e.Sensor.Contains("DTS", StringComparison.OrdinalIgnoreCase);
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
                (s == bestScore && e.Value > best.Value))
            {
                best = e;
                bestScore = s;
            }
        }
        return best;
    }

    private static string Norm(string s) => s.Trim().ToLowerInvariant();

    private static bool MentionsCpu(string s) =>
        s.Contains("cpu", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("processor", StringComparison.OrdinalIgnoreCase);

    private static bool MentionsGpu(string s) =>
        s.Contains("gpu", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("graphics", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("arc", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("radeon", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("geforce", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("intel", StringComparison.OrdinalIgnoreCase) && s.Contains("gpu", StringComparison.OrdinalIgnoreCase);

    private static bool MentionsMemory(string s) =>
        s.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("dram", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("ram", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("spd", StringComparison.OrdinalIgnoreCase);

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

    private static bool HasTempWord(string label) =>
        label.Contains("temp", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("tctl", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("tdie", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("junction", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("hot spot", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("hotspot", StringComparison.OrdinalIgnoreCase);

    private static bool IsPercentCandidate(Entry e) =>
        e.PercentUnit || e.Type == TypeUsage;

    private static bool IsTempCandidate(Entry e) =>
        e.Type == TypeTemp && (e.TempUnit || HasTempWord(e.Label));

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
        if (e.Type != TypePower) return -1;
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
        if (e.Type != TypeClock) return -1;
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
        if (e.Type != TypePower) return -1;
        var l = Norm(e.Label);
        if (!(MentionsGpu(e.Sensor) || MentionsGpu(l))) return -1;
        if (l.Contains("gpu power") || l.Contains("gpu package")) return 100;
        if (l.Contains("power") || l.Contains("package")) return 80;
        if (MentionsGpu(e.Sensor) && e.PowerUnit) return 50;
        return -1;
    }

    private static int ScoreVramUsed(Entry e)
    {
        if (!e.DataUnit || e.Type != TypeOther) return -1;
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
        if (!e.DataUnit || e.Type != TypeOther) return -1;
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
        if (e is null || double.IsNaN(e.Value) || double.IsInfinity(e.Value)) return double.NaN;
        double v = e.Value;
        if (e.Fahrenheit) v = (v - 32.0) * 5.0 / 9.0;
        else if (e.Kelvin) v -= 273.15;
        if (v <= 0 || v > 150) return double.NaN;
        return v;
    }

    private static double RawOf(Entry? e) =>
        e is null || double.IsNaN(e.Value) || double.IsInfinity(e.Value) ? double.NaN : e.Value;

    private static double DataGbOf(Entry? e)
    {
        if (e is null || double.IsNaN(e.Value) || double.IsInfinity(e.Value)) return double.NaN;
        double v = e.Value;
        var u = e.Unit;
        if (u.Contains("TB", StringComparison.OrdinalIgnoreCase)) v *= 1024.0;
        else if (u.Contains("GB", StringComparison.OrdinalIgnoreCase) || u.Contains("GiB", StringComparison.OrdinalIgnoreCase)) { }
        else if (u.Contains("MB", StringComparison.OrdinalIgnoreCase) || u.Contains("MiB", StringComparison.OrdinalIgnoreCase)) v /= 1024.0;
        else if (u.Contains("KB", StringComparison.OrdinalIgnoreCase) || u.Contains("KiB", StringComparison.OrdinalIgnoreCase)) v /= 1024.0 * 1024.0;
        else v /= 1024.0; // HWiNFO memory readings are usually MB.
        return v > 0 ? v : double.NaN;
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        if (double.IsNaN(CpuTemp) && double.IsNaN(GpuTemp) && double.IsNaN(RamTemp) &&
            double.IsNaN(GpuLoad) && double.IsNaN(RamLoad))
        {
            sb.AppendLine("    HWiNFO shared    : not available");
            return sb.ToString();
        }

        sb.AppendLine("    HWiNFO shared    : available");
        Append(sb, "CPU temp", CpuTemp, CpuTempLabel);
        Append(sb, "GPU temp", GpuTemp, GpuTempLabel);
        Append(sb, "GPU hot spot", GpuHotspot, GpuHotspotLabel);
        Append(sb, "RAM temp", RamTemp, RamTempLabel);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string name, double value, string? label)
    {
        if (double.IsNaN(value)) return;
        sb.AppendLine($"        {name,-12} : {value:0.0} C   ({label})");
    }

    public string Dump()
    {
        var sb = new StringBuilder();
        try
        {
            if (!_section.Attach()) { sb.AppendLine("HWiNFO shared memory not available."); return sb.ToString(); }
            var acc = _section.Accessor!;
            if (acc.ReadUInt32(HdrSignature) != Signature) { _section.Detach(); sb.AppendLine("Bad signature."); return sb.ToString(); }

            uint sOff = acc.ReadUInt32(HdrOffsetOfSensorSection);
            uint sSize = acc.ReadUInt32(HdrSizeOfSensorElement);
            uint sCount = acc.ReadUInt32(HdrNumSensorElements);
            uint rOff = acc.ReadUInt32(HdrOffsetOfReadingSection);
            uint rSize = acc.ReadUInt32(HdrSizeOfReadingElement);
            uint rCount = acc.ReadUInt32(HdrNumReadingElements);

            if (sSize < SeNameUser + LabelLen || rSize < RdValue + 8 ||
                !_section.Fits(sOff, sCount, sSize) || !_section.Fits(rOff, rCount, rSize))
            {
                _section.Detach();
                sb.AppendLine("Section bounds check failed.");
                return sb.ToString();
            }

            var sensors = new string[sCount];
            for (uint i = 0; i < sCount; i++)
            {
                long b = sOff + (long)i * sSize;
                uint id = acc.ReadUInt32(b + SeId);
                uint inst = acc.ReadUInt32(b + SeInst);
                var orig = _section.ReadAnsi(b + SeNameOrig, LabelLen, _scratch);
                var user = _section.ReadAnsi(b + SeNameUser, LabelLen, _scratch);
                sensors[i] = $"{id:X8}_{inst} {(user.Length > 0 ? user : orig)}";
            }

            Update();
            sb.AppendLine($"HWiNFO shared dump ({sCount} sensors, {rCount} readings)");
            sb.AppendLine($"CPU temp      : {(double.IsNaN(CpuTemp) ? "n/a" : CpuTemp.ToString("0.0"))}  {CpuTempLabel}");
            sb.AppendLine($"GPU temp      : {(double.IsNaN(GpuTemp) ? "n/a" : GpuTemp.ToString("0.0"))}  {GpuTempLabel}");
            sb.AppendLine($"GPU hot spot  : {(double.IsNaN(GpuHotspot) ? "n/a" : GpuHotspot.ToString("0.0"))}  {GpuHotspotLabel}");
            sb.AppendLine($"RAM temp      : {(double.IsNaN(RamTemp) ? "n/a" : RamTemp.ToString("0.0"))}  {RamTempLabel}");
            sb.AppendLine();

            for (uint i = 0; i < rCount; i++)
            {
                long b = rOff + (long)i * rSize;
                uint type = acc.ReadUInt32(b + RdType);
                uint sIdx = acc.ReadUInt32(b + RdSensorIndex);
                uint rId = acc.ReadUInt32(b + RdReadingId);
                var label = _section.ReadAnsi(b + RdLabelOrig, LabelLen, _scratch);
                var unit = _section.ReadAnsi(b + RdUnit, UnitLen, _scratch);
                double v = acc.ReadDouble(b + RdValue);
                var sname = sIdx < sensors.Length ? sensors[sIdx] : $"#{sIdx}";
                sb.AppendLine($"{type,2} {sname,-45} {label,-40} {unit,-8} {v:0.###}  id={rId}");
            }
        }
        catch (Exception ex)
        {
            _section.Detach();
            sb.AppendLine("Dump failed: " + ex.Message);
        }
        return sb.ToString();
    }

    public void Dispose() => _section.Dispose();
}
