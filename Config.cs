using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskMon;

public enum ZMode
{
    /// <summary>Pinned to the desktop: always at the bottom of the z-order, above the wallpaper.</summary>
    Desktop = 0,
    Normal = 1,
    Topmost = 2,
}

public enum Corner
{
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
    BottomRight = 3,
}

public sealed class Config
{
    // int.MinValue == "never positioned". Plain -1 is a real coordinate on a
    // monitor placed to the left of the primary, so it cannot be the sentinel.
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;

    /// <summary>
    /// Windows display number (the one shown in Display Settings), or -1 to just
    /// use the saved X/Y. Used on first run and whenever X/Y lands off-screen.
    /// </summary>
    public int Monitor { get; set; } = 2;

    /// <summary>Which corner of <see cref="Monitor"/> to home to.</summary>
    public Corner Corner { get; set; } = Corner.TopRight;
    public int IntervalMs { get; set; } = 1500;
    public double Opacity { get; set; } = 0.88;
    public double Scale { get; set; } = 1.0;
    public ZMode ZMode { get; set; } = ZMode.Topmost;
    public bool Locked { get; set; }

    /// <summary>Global show/hide hotkey. Default Ctrl+Alt+D.</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>Modifier bits: 1=Alt, 2=Ctrl, 4=Shift, 8=Win.</summary>
    public uint HotkeyMods { get; set; } = 0x0002 | 0x0001;   // Ctrl+Alt

    /// <summary>Virtual-key code. 0x44 = 'D'.</summary>
    public uint HotkeyKey { get; set; } = 0x44;
    public bool ShowPower { get; set; } = true;
    public bool ShowVram { get; set; } = true;
    public bool ShowHotspot { get; set; }

    [JsonIgnore]
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeskMon", "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Config Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path), Options);
                if (cfg is not null)
                {
                    // This file is hand-editable and is rewritten on every drag, so a
                    // truncated or edited value must not reach the poll loop or the UI.
                    cfg.IntervalMs = Math.Clamp(cfg.IntervalMs, 250, 60_000);
                    cfg.Opacity = Math.Clamp(cfg.Opacity, 0.25, 1.0);
                    cfg.Scale = Math.Clamp(cfg.Scale, OverlayForm.MinScale, OverlayForm.MaxScale);
                    return cfg;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable config is not worth failing startup over.
        }
        return new Config();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Best effort; losing window position is not fatal.
        }
    }
}
