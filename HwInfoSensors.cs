namespace DeskMon;

/// <summary>
/// Reads HWiNFO's sensor shared memory (HWiNFO_SENS_SM2).
///
/// Requires "Shared Memory Support" to be enabled in HWiNFO's settings. Note that in
/// current HWiNFO builds that switch is a Pro feature and the free version grants it
/// only for a limited period per session -- when it lapses the section simply stops
/// updating, which the staleness check below treats as unavailable.
///
/// Layout (HWiNFO SDK, HWiNFO_SHARED_MEM2):
///   header : dwSignature 'HWiS', dwVersion, dwRevision, __time64_t poll_time,
///            dwOffsetOfSensorSection, dwSizeOfSensorElement, dwNumSensorElements,
///            dwOffsetOfReadingSection, dwSizeOfReadingElement, dwNumReadingElements
///   reading: DWORD tReading, DWORD dwSensorIndex, DWORD dwReadingID,
///            char szLabelOrig[128], char szLabelUser[128], char szUnit[16],
///            double Value, ValueMin, ValueMax, ValueAvg
/// </summary>
public sealed class HwInfoSensors : ICpuTempSource
{
    // Bytes 'H','W','i','S' little-endian.
    private const uint Signature = 0x53695748;

    private const int HdrSignature = 0;
    private const int HdrPollTime = 12;                 // __time64_t, 8 bytes
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
    private const int RdValue = 284;                    // after szUnit[16] at 268
    private const int LabelLen = 128;

    private const uint ReadingTypeTemp = 1;
    private const uint ReadingTypePower = 5;
    private const uint ReadingTypeClock = 6;

    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly NamedSection _section;
    private readonly byte[] _scratch = new byte[LabelLen];

    public string Name => "HWiNFO";
    public double CpuTemp { get; private set; } = double.NaN;
    public double CpuPower { get; private set; } = double.NaN;
    public double CpuClock { get; private set; } = double.NaN;

    public HwInfoSensors(string? sectionNameOverride = null)
        : this(sectionNameOverride is null
            ? new[] { @"Global\HWiNFO_SENS_SM2", "HWiNFO_SENS_SM2" }
            : new[] { sectionNameOverride })
    {
    }

    private HwInfoSensors(string[] names) => _section = new NamedSection(names);

    public void Update()
    {
        CpuTemp = CpuPower = CpuClock = double.NaN;

        try
        {
            if (!_section.Attach()) return;
            var acc = _section.Accessor!;

            if (acc.ReadUInt32(HdrSignature) != Signature) { _section.Detach(); return; }

            long pollTime = acc.ReadInt64(HdrPollTime);
            uint offset = acc.ReadUInt32(HdrOffsetOfReadingSection);
            uint size = acc.ReadUInt32(HdrSizeOfReadingElement);
            uint count = acc.ReadUInt32(HdrNumReadingElements);

            if (size < RdValue + 8 || count == 0 || count > 100_000) { _section.Detach(); return; }
            if (!_section.Fits(offset, count, size)) { _section.Detach(); return; }

            // HWiNFO stamps poll_time as a 64-bit unix time. A section left behind by a
            // closed HWiNFO, or one whose Shared Memory grant lapsed, stops advancing.
            if (pollTime > 0)
            {
                var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(pollTime);
                if (age > MaxAge || age < -MaxAge) { _section.Detach(); return; }
            }

            for (uint i = 0; i < count; i++)
            {
                long baseOff = offset + (long)i * size;
                uint type = acc.ReadUInt32(baseOff + RdType);
                if (type is not (ReadingTypeTemp or ReadingTypePower or ReadingTypeClock)) continue;

                // Prefer the original label: the user label is freely renameable.
                var label = _section.ReadAnsi(baseOff + RdLabelOrig, LabelLen, _scratch);
                if (label.Length == 0)
                    label = _section.ReadAnsi(baseOff + RdLabelUser, LabelLen, _scratch);

                double v = acc.ReadDouble(baseOff + RdValue);
                if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) continue;

                switch (type)
                {
                    case ReadingTypeTemp when double.IsNaN(CpuTemp) && IsCpuPackageTemp(label):
                        CpuTemp = v;
                        break;
                    case ReadingTypePower when double.IsNaN(CpuPower) && IsCpuPackagePower(label):
                        CpuPower = v;
                        break;
                    case ReadingTypeClock when double.IsNaN(CpuClock) && IsCpuClock(label):
                        CpuClock = v;
                        break;
                }
            }
        }
        catch
        {
            _section.Detach();
        }
    }

    // HWiNFO labels Ryzen package temperature "CPU (Tctl/Tdie)"; Intel uses
    // "CPU Package". Avoid per-core ("Core 0") and per-CCD ("CCD1 (Tdie)") entries.
    private static bool IsCpuPackageTemp(string l) =>
        l.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
        l.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) ||
        l.Equals("CPU (Tdie)", StringComparison.OrdinalIgnoreCase) ||
        l.Equals("Core Temperature", StringComparison.OrdinalIgnoreCase);

    private static bool IsCpuPackagePower(string l) =>
        l.Equals("CPU Package Power", StringComparison.OrdinalIgnoreCase) ||
        l.Equals("CPU PPT", StringComparison.OrdinalIgnoreCase) ||
        l.Contains("Package Power", StringComparison.OrdinalIgnoreCase);

    private static bool IsCpuClock(string l) =>
        l.Equals("Core 0 Clock", StringComparison.OrdinalIgnoreCase) ||
        l.Equals("Average Effective Clock", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Dumps every published reading with its sensor id/instance, for mapping
    /// labels onto the VSB registry keys. Diagnostic only.
    /// </summary>
    public string Dump()
    {
        var sb = new System.Text.StringBuilder();
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

            for (uint i = 0; i < rCount; i++)
            {
                long b = rOff + (long)i * rSize;
                uint type = acc.ReadUInt32(b + RdType);
                uint sIdx = acc.ReadUInt32(b + RdSensorIndex);
                uint rId = acc.ReadUInt32(b + RdReadingId);
                var label = _section.ReadAnsi(b + RdLabelOrig, LabelLen, _scratch);
                var unit = _section.ReadAnsi(b + RdValue - 16, 16, _scratch);
                double v = acc.ReadDouble(b + RdValue);
                var sname = sIdx < sensors.Length ? sensors[sIdx] : $"#{sIdx}";
                sb.AppendLine($"{type,2} {sname,-45} {label,-40} {unit,-8} {v:0.###}");
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
