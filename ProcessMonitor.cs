using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskMon;

public sealed class ProcInfo
{
    public string Name = "";
    public double Cpu;      // percent of total CPU capacity
    public double MemMb;
    public double Score;
}

/// <summary>
/// Samples processes every ~5s and ranks them by a simple CPU+memory score.
/// CPU% needs two samples, so the first call only primes the baseline.
/// </summary>
public sealed class ProcessMonitor
{
    private const int IntervalMs = 5000;

    private readonly Dictionary<int, TimeSpan> _prevCpu = new();
    private DateTime _prevTime = DateTime.MinValue;
    private ProcInfo[] _top = Array.Empty<ProcInfo>();
    private static readonly long TotalPhys = ReadTotalPhys();

    public ProcInfo[] Top => _top;

    public void Update()
    {
        var now = DateTime.UtcNow;
        if (_prevTime != DateTime.MinValue && (now - _prevTime).TotalMilliseconds < IntervalMs) return;
        Sample(now);
    }

    private void Sample(DateTime now)
    {
        var dt = _prevTime == DateTime.MinValue ? 0.0 : (now - _prevTime).TotalSeconds;
        var cur = new Dictionary<int, TimeSpan>();
        var list = new List<ProcInfo>(256);
        var procs = Process.GetProcesses();
        try
        {
            foreach (var p in procs)
            {
                TimeSpan cpu;
                long mem;
                string name;
                try
                {
                    cpu = p.TotalProcessorTime;
                    mem = p.WorkingSet64;
                    name = p.ProcessName;
                }
                catch { continue; }

                if (name.Equals("Idle", StringComparison.OrdinalIgnoreCase)) continue;

                double cpuPct = 0;
                if (dt > 0 && _prevCpu.TryGetValue(p.Id, out var old))
                {
                    cpuPct = (cpu - old).TotalSeconds / (dt * Environment.ProcessorCount) * 100.0;
                    if (cpuPct < 0) cpuPct = 0;
                    if (cpuPct > 100) cpuPct = 100;
                }

                double memMb = mem / 1048576.0;
                double memPct = TotalPhys > 0 ? mem / (double)TotalPhys * 100.0 : 0;
                list.Add(new ProcInfo
                {
                    Name = name,
                    Cpu = cpuPct,
                    MemMb = memMb,
                    Score = cpuPct + memPct,
                });
                cur[p.Id] = cpu;
            }
        }
        finally
        {
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }

        _prevCpu.Clear();
        foreach (var kv in cur) _prevCpu[kv.Key] = kv.Value;
        _prevTime = now;
        _top = list.OrderByDescending(x => x.Score).Take(3).ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static long ReadTotalPhys()
    {
        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref m) ? (long)m.ullTotalPhys : 0;
        }
        catch { return 0; }
    }
}
