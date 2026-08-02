using System.IO.MemoryMappedFiles;
using System.Text;

namespace DeskMon;

/// <summary>
/// A read-only attachment to another process's named shared-memory section, with
/// throttled reattach when the publisher is not running.
///
/// Releasing the handle promptly matters: a Windows named section object lives as long
/// as any handle to it, so holding a dead one would pin the name and a restarting
/// publisher asking for a larger section would be handed our stale, smaller one.
/// </summary>
public sealed class NamedSection : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(20);

    private readonly string[] _names;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _acc;
    private DateTime _nextTry = DateTime.MinValue;

    public NamedSection(params string[] names) => _names = names;

    public MemoryMappedViewAccessor? Accessor => _acc;

    /// <summary>Attach if not already attached. False when the publisher is absent.</summary>
    public bool Attach()
    {
        if (_acc is not null) return true;
        if (DateTime.UtcNow < _nextTry) return false;

        // Set before attempting, so a failing open costs one syscall per interval.
        _nextTry = DateTime.UtcNow + RetryInterval;

        foreach (var name in _names)
        {
            try
            {
                var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                _acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                _mmf = mmf;
                return true;
            }
            catch
            {
                // Not published under this name; try the next.
            }
        }

        return false;
    }

    public void Detach()
    {
        try { _acc?.Dispose(); } catch { }
        try { _mmf?.Dispose(); } catch { }
        _acc = null;
        _mmf = null;
    }

    /// <summary>Fixed-length ANSI string, truncated at the first NUL.</summary>
    public string ReadAnsi(long offset, int length, byte[] scratch)
    {
        int n = Math.Min(length, scratch.Length);
        _acc!.ReadArray(offset, scratch, 0, n);
        int z = Array.IndexOf(scratch, (byte)0, 0, n);
        if (z < 0) z = n;
        return Encoding.ASCII.GetString(scratch, 0, z);
    }

    /// <summary>True when the whole table lies inside the mapping.</summary>
    public bool Fits(long offset, long count, long elementSize) =>
        _acc is not null &&
        offset >= 0 && count >= 0 && elementSize > 0 &&
        offset + count * elementSize <= _acc.Capacity;

    public void Dispose() => Detach();
}

/// <summary>One external monitoring app that can supply CPU readings.</summary>
public interface ICpuTempSource : IDisposable
{
    /// <summary>Shown in diagnostics and in the tray tooltip.</summary>
    string Name { get; }

    /// <summary>Refresh. Leaves values NaN when unavailable. Never throws.</summary>
    void Update();

    double CpuTemp { get; }
    double CpuPower { get; }
    double CpuClock { get; }
}
