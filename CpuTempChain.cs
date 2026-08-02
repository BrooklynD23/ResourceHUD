namespace DeskMon;

/// <summary>
/// Tries each external monitoring app in turn for a CPU temperature.
///
/// The widget ships no kernel driver of its own. Reading Ryzen Tctl/Tdie needs ring 0,
/// so the only driver-free option is to borrow the number from a tool that already has
/// one loaded. All three sources are read-only attachments to a shared-memory section
/// the other process publishes; none of them is started, installed or written to, and
/// when none is present every value stays NaN and the widget honestly shows "n/a".
/// </summary>
public sealed class CpuTempChain : IDisposable
{
    private readonly ICpuTempSource[] _sources;

    public CpuTempChain()
        : this(new ICpuTempSource[]
        {
            // Ordered by how likely each is to be running on a gaming machine.
            new AfterburnerSensors(),
            new HwInfoSensors(),
            new CoreTempSensors(),
        })
    {
    }

    /// <summary>Test seam: inject sources (and section names) directly.</summary>
    public CpuTempChain(ICpuTempSource[] sources) => _sources = sources;

    public double CpuTemp { get; private set; } = double.NaN;
    public double CpuPower { get; private set; } = double.NaN;
    public double CpuClock { get; private set; } = double.NaN;

    /// <summary>Which source supplied the temperature, or null.</summary>
    public string? SourceName { get; private set; }

    public void Update()
    {
        CpuTemp = CpuPower = CpuClock = double.NaN;
        SourceName = null;

        foreach (var src in _sources)
        {
            src.Update();

            if (double.IsNaN(CpuTemp) && !double.IsNaN(src.CpuTemp))
            {
                CpuTemp = src.CpuTemp;
                SourceName = src.Name;
            }

            // Power and clock are taken independently: a source may publish one and
            // not the others, and each is toggled separately in these tools' settings.
            if (double.IsNaN(CpuPower)) CpuPower = src.CpuPower;
            if (double.IsNaN(CpuClock)) CpuClock = src.CpuClock;

            // Stop once everything is satisfied; otherwise keep filling gaps.
            if (!double.IsNaN(CpuTemp) && !double.IsNaN(CpuPower) && !double.IsNaN(CpuClock)) break;
        }
    }

    /// <summary>One line per source, for the diagnostics dump.</summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in _sources)
        {
            sb.AppendLine(double.IsNaN(s.CpuTemp)
                ? $"    {s.Name,-16} : not available"
                : $"    {s.Name,-16} : CPU {s.CpuTemp:0.0} C");
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        foreach (var s in _sources)
        {
            try { s.Dispose(); } catch { }
        }
    }
}
