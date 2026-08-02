namespace DeskMon;

/// <summary>
/// Reads Core Temp's shared memory (CoreTempMappingObject).
///
/// Core Temp is a small free utility, which makes it the lightest way to get CPU
/// temperature on a machine that has neither Afterburner nor HWiNFO. It loads its own
/// kernel driver, so DeskMon still adds none of its own.
///
/// Layout (Core Temp shared memory SDK, CORE_TEMP_SHARED_DATA):
///   uint  uiLoad[256]        @    0
///   uint  uiTjMax[128]       @ 1024
///   uint  uiCoreCnt          @ 1536
///   uint  uiCPUCnt           @ 1540
///   float fTemp[256]         @ 1544
///   float fVID               @ 2568
///   float fCPUSpeed          @ 2572
///   float fFSBSpeed          @ 2576
///   float fMultiplier        @ 2580
///   char  sCPUName[100]      @ 2584
///   byte  ucFahrenheit       @ 2684
///   byte  ucDeltaToTjMax     @ 2685
///   byte  ucTdpSupported     @ 2686
///   byte  ucPowerSupported   @ 2687
///   uint  uiStructVersion    @ 2688
///   uint  uiTdp[128]         @ 2692
///   float fPower[128]        @ 3204
/// </summary>
public sealed class CoreTempSensors : ICpuTempSource
{
    private const int OffTjMax = 1024;
    private const int OffCoreCnt = 1536;
    private const int OffCpuCnt = 1540;
    private const int OffTemp = 1544;
    private const int OffCpuSpeed = 2572;
    private const int OffFahrenheit = 2684;
    private const int OffDeltaToTjMax = 2685;
    private const int OffPowerSupported = 2687;
    private const int OffPower = 3204;

    private const int MinSize = 2686;      // through ucDeltaToTjMax
    private const int SizeWithPower = 3204 + 4;

    private readonly NamedSection _section;

    public string Name => "Core Temp";
    public double CpuTemp { get; private set; } = double.NaN;
    public double CpuPower { get; private set; } = double.NaN;
    public double CpuClock { get; private set; } = double.NaN;

    public CoreTempSensors(string? sectionNameOverride = null)
        => _section = new NamedSection(sectionNameOverride is null
            ? new[] { "CoreTempMappingObject", @"Global\CoreTempMappingObject" }
            : new[] { sectionNameOverride });

    public void Update()
    {
        CpuTemp = CpuPower = CpuClock = double.NaN;

        try
        {
            if (!_section.Attach()) return;
            var acc = _section.Accessor!;

            if (acc.Capacity < MinSize) { _section.Detach(); return; }

            uint coreCnt = acc.ReadUInt32(OffCoreCnt);
            uint cpuCnt = acc.ReadUInt32(OffCpuCnt);

            // Core Temp has no timestamp field, so structural sanity is the only
            // validation available. A blank or absurd section is rejected here.
            if (coreCnt == 0 || coreCnt > 256 || cpuCnt == 0 || cpuCnt > 128)
            {
                _section.Detach();
                return;
            }

            bool fahrenheit = acc.ReadByte(OffFahrenheit) != 0;
            bool deltaToTjMax = acc.ReadByte(OffDeltaToTjMax) != 0;

            // Per-core only; the package figure is the hottest core, which is what
            // every other tool shows as "CPU temperature".
            double hottest = double.NaN;
            int total = (int)Math.Min(coreCnt * cpuCnt, 256);

            for (int i = 0; i < total; i++)
            {
                float raw = acc.ReadSingle(OffTemp + i * 4);
                if (float.IsNaN(raw) || float.IsInfinity(raw)) continue;

                double c;
                if (deltaToTjMax)
                {
                    // Value is distance below TjMax, in the display unit.
                    int cpuIndex = coreCnt == 0 ? 0 : Math.Min(i / (int)coreCnt, 127);
                    double tjMax = acc.ReadUInt32(OffTjMax + cpuIndex * 4);
                    if (fahrenheit) tjMax = (tjMax - 32.0) / 1.8;
                    double delta = fahrenheit ? raw / 1.8 : raw;
                    c = tjMax - delta;
                }
                else
                {
                    c = fahrenheit ? (raw - 32.0) / 1.8 : raw;
                }

                if (c <= 0 || c > 150) continue;
                if (double.IsNaN(hottest) || c > hottest) hottest = c;
            }

            CpuTemp = hottest;

            float speed = acc.ReadSingle(OffCpuSpeed);
            if (speed > 0 && !float.IsInfinity(speed)) CpuClock = speed;   // MHz

            if (acc.Capacity >= SizeWithPower && acc.ReadByte(OffPowerSupported) != 0)
            {
                float p = acc.ReadSingle(OffPower);
                if (p > 0 && !float.IsInfinity(p)) CpuPower = p;
            }
        }
        catch
        {
            _section.Detach();
        }
    }

    public void Dispose() => _section.Dispose();
}
