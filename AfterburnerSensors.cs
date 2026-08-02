using System.IO.MemoryMappedFiles;
using System.Text;

namespace DeskMon;

/// <summary>
/// Reads MSI Afterburner's hardware-monitor shared memory (MAHMSharedMemory).
///
/// Why this exists: Ryzen Tctl/Tdie needs ring 0, and DeskMon deliberately ships no
/// kernel driver. But on a machine where Afterburner is already running, a ring-0
/// driver is already loaded and is already reading that value. Reading the number it
/// publishes costs nothing, needs no elevation, and adds no attack surface of our own.
/// If Afterburner is not running, this silently reports nothing.
///
/// Layout (MSIAfterburnerSharedMemory.h), all verified against the live section:
///   header  : dwSignature, dwVersion, dwHeaderSize, dwNumEntries, dwEntrySize,
///             time (32-bit time_t), dwNumGpuEntries, dwGpuEntrySize  = 32 bytes
///   entry   : 5 x char[260] strings, then float data, minLimit, maxLimit,
///             then dwFlags, dwGpu, dwSrcId                           = 1324 bytes
/// </summary>
public sealed class AfterburnerSensors : ICpuTempSource
{
    public string Name => "MSI Afterburner";

    private const uint SignatureMahm = 0x4D41484D;   // bytes 'M','H','A','M' as read
    private const int StrLen = 260;
    private const int OffName = 0;
    private const int OffData = 1300;

    private const int HdrSignature = 0;
    private const int HdrHeaderSize = 8;
    private const int HdrNumEntries = 12;
    private const int HdrEntrySize = 16;
    private const int HdrTime = 20;

    /// <summary>Afterburner publishes roughly once a second; allow generous slack.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(15);

    /// <summary>Do not hammer OpenExisting when Afterburner simply is not running.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(20);

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _acc;
    private DateTime _nextTry = DateTime.MinValue;

    private readonly byte[] _strBuf = new byte[StrLen];

    // Resolved entry indexes. Entry order depends on the user's Afterburner
    // monitoring configuration, so they are found by name and then re-validated.
    private int _iTemp = -1, _iPower = -1, _iClock = -1;
    private uint _knownEntryCount;

    public bool Available { get; private set; }

    public double CpuTemp { get; private set; } = double.NaN;
    public double CpuPower { get; private set; } = double.NaN;
    public double CpuClock { get; private set; } = double.NaN;

    /// <summary>Refresh the cached values. Never throws.</summary>
    public void Update()
    {
        CpuTemp = CpuPower = CpuClock = double.NaN;
        Available = false;

        try
        {
            if (!EnsureOpen()) return;

            var acc = _acc!;

            if (acc.ReadUInt32(HdrSignature) != SignatureMahm)
            {
                // 0xDEAD means Afterburner is shutting the section down.
                Close();
                return;
            }

            uint headerSize = acc.ReadUInt32(HdrHeaderSize);
            uint numEntries = acc.ReadUInt32(HdrNumEntries);
            uint entrySize = acc.ReadUInt32(HdrEntrySize);

            if (headerSize < 24 || entrySize < OffData + 4 || numEntries == 0 || numEntries > 4096)
            {
                Close();
                return;
            }

            // Every offset below is derived from values read out of the section, so
            // check the whole table fits the mapping rather than relying on the
            // accessor to throw once a read runs off the end.
            if ((long)headerSize + (long)numEntries * entrySize > acc.Capacity)
            {
                Close();
                return;
            }

            // Stale section: Afterburner was killed, or monitoring is paused. The
            // mapping outlives the updates, so the timestamp is the real liveness test.
            uint stamp = acc.ReadUInt32(HdrTime);
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(stamp);
            if (age > MaxAge || age < -MaxAge)
            {
                // Release, do not just bail. A named section lives as long as any
                // handle to it, so holding this one would pin the name after
                // Afterburner died -- and a restarting Afterburner asking for a
                // LARGER section would then be handed our stale, smaller one.
                // Dropping it also lets EnsureOpen reattach to the new section.
                Close();
                return;
            }

            if (numEntries != _knownEntryCount)
            {
                _knownEntryCount = numEntries;
                _iTemp = _iPower = _iClock = -1;
            }

            // "CPU temperature" (the aggregate entry) rather than "CPU1 temperature":
            // on Ryzen every per-core entry is just Tdie repeated.
            _iTemp = Resolve(_iTemp, "CPU temperature", headerSize, entrySize, numEntries);
            _iPower = Resolve(_iPower, "CPU power", headerSize, entrySize, numEntries);
            _iClock = Resolve(_iClock, "CPU clock", headerSize, entrySize, numEntries);

            CpuTemp = ReadValue(_iTemp, headerSize, entrySize);
            CpuPower = ReadValue(_iPower, headerSize, entrySize);
            CpuClock = ReadValue(_iClock, headerSize, entrySize);

            Available = !double.IsNaN(CpuTemp);
        }
        catch
        {
            // A torn read while Afterburner restarts, or the section vanishing
            // mid-read. Drop it and re-open on the next cycle.
            Close();
        }
    }

    private double ReadValue(int index, uint headerSize, uint entrySize)
    {
        if (index < 0) return double.NaN;
        float v = _acc!.ReadSingle(headerSize + (long)index * entrySize + OffData);

        // FLT_MAX is only how the RTSS framerate entries mark "no data". A hardware
        // source Afterburner lists but cannot read publishes exactly 0 instead --
        // "FB usage" and "Fan tachometer" do so permanently on this card. Temperature,
        // power and clock are all things a running CPU cannot report as zero, so this
        // applies the same rule the direct path uses in HardwareMonitor.Positive().
        // IsNaN must be tested first: every comparison against NaN is false.
        if (float.IsNaN(v) || v <= 0f || v >= 1e30f) return double.NaN;
        return v;
    }

    /// <summary>Confirm the cached index still holds <paramref name="name"/>, else rescan.</summary>
    private int Resolve(int cached, string name, uint headerSize, uint entrySize, uint numEntries)
    {
        if (cached >= 0 && cached < numEntries &&
            NameAt(cached, headerSize, entrySize) == name)
            return cached;

        for (int i = 0; i < numEntries; i++)
        {
            if (NameAt(i, headerSize, entrySize) == name) return i;
        }
        return -1;
    }

    private string NameAt(int index, uint headerSize, uint entrySize)
    {
        long off = headerSize + (long)index * entrySize + OffName;
        _acc!.ReadArray(off, _strBuf, 0, StrLen);
        int z = Array.IndexOf(_strBuf, (byte)0);
        if (z < 0) z = StrLen;
        return Encoding.ASCII.GetString(_strBuf, 0, z);
    }

    private bool EnsureOpen()
    {
        if (_acc is not null) return true;
        if (DateTime.UtcNow < _nextTry) return false;

        _nextTry = DateTime.UtcNow + RetryInterval;

        // Afterburner runs per-session, so the unqualified name is the usual one;
        // the Global\ form appears when it runs in another session.
        foreach (var name in new[] { "MAHMSharedMemory", @"Global\MAHMSharedMemory" })
        {
            try
            {
                var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                _mmf = mmf;
                _acc = acc;
                _knownEntryCount = 0;
                _iTemp = _iPower = _iClock = -1;
                return true;
            }
            catch
            {
                // Not present under this name; try the next.
            }
        }

        return false;
    }

    private void Close()
    {
        try { _acc?.Dispose(); } catch { }
        try { _mmf?.Dispose(); } catch { }
        _acc = null;
        _mmf = null;
        _iTemp = _iPower = _iClock = -1;
        _knownEntryCount = 0;
    }

    public void Dispose() => Close();
}
