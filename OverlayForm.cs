using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DeskMon;

public sealed class OverlayForm : Form
{
    // ---- logical layout units (multiplied by _s at paint time) ----
    private const int PadX = 10;
    private const int PadY = 7;
    private const int RowH = 20;
    private const int LabelW = 30;
    private const int Col1W = 40;   // load %
    private const int Col2W = 46;   // temp
    private const int BaseW = 238;
    private const int Radius = 10;
    private const int HistLen = 64;
    private const int GripSize = 13;

    public const double MinScale = 0.6;
    public const double MaxScale = 3.0;

    private sealed class Row
    {
        public required string Label;
        public required Color Color;
        public readonly float[] Hist = new float[HistLen];
        public int Count;
        public int Head;
        public string Right1 = "--";
        public string Right2 = "";
        public Color Right2Color = Palette.TextDim;
        public bool Right2Wide;   // draw Right2 across both right columns

        public void Push(double pct)
        {
            Hist[Head] = float.IsNaN((float)pct) ? 0f : Math.Clamp((float)pct, 0f, 100f);
            Head = (Head + 1) % HistLen;
            if (Count < HistLen) Count++;
        }

        public float At(int i)
        {
            // i = 0 is the oldest retained sample.
            int start = (Head - Count + HistLen) % HistLen;
            return Hist[(start + i) % HistLen];
        }
    }

    private static class Palette
    {
        public static readonly Color Back = Color.FromArgb(16, 17, 21);
        public static readonly Color Border = Color.FromArgb(62, 64, 74);
        public static readonly Color Track = Color.FromArgb(30, 32, 38);
        public static readonly Color Text = Color.FromArgb(228, 230, 235);
        public static readonly Color TextDim = Color.FromArgb(138, 143, 152);
        public static readonly Color Cpu = Color.FromArgb(96, 180, 255);
        public static readonly Color Gpu = Color.FromArgb(255, 146, 80);
        public static readonly Color Vram = Color.FromArgb(186, 132, 255);
        public static readonly Color Ram = Color.FromArgb(88, 214, 141);
        public static readonly Color Ok = Color.FromArgb(86, 214, 130);
        public static readonly Color Warm = Color.FromArgb(250, 198, 66);
        public static readonly Color Hot = Color.FromArgb(251, 146, 60);
        public static readonly Color Crit = Color.FromArgb(248, 113, 113);
    }

    private readonly Config _cfg;
    private readonly HardwareMonitor _mon;
    private readonly NotifyIcon _tray;
    private readonly List<Row> _rows = new();
    private readonly Row _cpu, _gpu, _vram, _ram;

    private float _s = 1f;
    private Font _fLabel = null!, _fValue = null!;
    private IntPtr _rgn = IntPtr.Zero;

    private bool _dragging;
    private Point _dragOffset;

    private bool _resizing;
    private Point _resizeAnchor;
    private double _resizeStartScale;
    private Size _resizeStartSize;
    private Snapshot _last = new();
    private string? _error;
    private ToolStripMenuItem? _cpuTempItem;

    public OverlayForm(Config cfg, HardwareMonitor mon)
    {
        _cfg = cfg;
        _mon = mon;

        _cpu = new Row { Label = "CPU", Color = Palette.Cpu };
        _gpu = new Row { Label = "GPU", Color = Palette.Gpu };
        _vram = new Row { Label = "VRM", Color = Palette.Vram };
        _ram = new Row { Label = "RAM", Color = Palette.Ram };

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Palette.Back;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = "DeskMon";
        Opacity = Math.Clamp(_cfg.Opacity, 0.25, 1.0);

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "DeskMon",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => CycleZMode();

        BuildMenu();
        ApplyLayout();

        _mon.Updated += OnSensorUpdate;
        _mon.Failed += OnSensorFailure;

        MouseDown += OnMouseDownDrag;
        MouseMove += OnMouseMoveDrag;
        MouseUp += OnMouseUpDrag;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Tool window: no alt-tab entry, no taskbar button, exempt from Show Desktop.
            // NOACTIVATE: clicking or dragging the widget must never take foreground --
            // doing so would knock a borderless-fullscreen game out of fullscreen.
            // ShowWithoutActivation alone only covers the initial show, not later clicks.
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    // ------------------------------------------------------------------ layout

    private void ApplyLayout(bool reposition = true)
    {
        double scale = Math.Clamp(_cfg.Scale, MinScale, MaxScale);
        _s = (DeviceDpi / 96f) * (float)scale;

        _fLabel?.Dispose();
        _fValue?.Dispose();
        _fLabel = new Font("Segoe UI", 7.5f * (float)scale, FontStyle.Bold, GraphicsUnit.Point);
        _fValue = new Font("Segoe UI", 8f * (float)scale, FontStyle.Regular, GraphicsUnit.Point);

        _rows.Clear();
        _rows.Add(_cpu);
        _rows.Add(_gpu);
        if (_cfg.ShowVram) _rows.Add(_vram);
        _rows.Add(_ram);

        int w = R(BaseW);
        int h = R(PadY * 2) + _rows.Count * R(RowH);
        ClientSize = new Size(w, h);

        if (reposition) PlaceOnScreen();
        ApplyRegion();
        ApplyZMode();
        Invalidate();
    }

    private int R(float logical) => (int)Math.Round(logical * _s);

    private void PlaceOnScreen()
    {
        if (_cfg.X != int.MinValue && _cfg.Y != int.MinValue)
        {
            var rect = new Rectangle(new Point(_cfg.X, _cfg.Y), Size);

            // Keep the saved spot only if a decent chunk of it is really on a
            // monitor -- display layouts change when a screen is unplugged.
            foreach (var s in Screen.AllScreens)
            {
                var vis = Rectangle.Intersect(s.WorkingArea, rect);
                if (vis.Width >= Width / 2 && vis.Height >= Height / 2)
                {
                    Location = rect.Location;
                    return;
                }
            }
        }

        HomeToMonitor(_cfg.Monitor, save: false);
    }

    /// <summary>
    /// Windows display number, as shown in Settings > Display. DeviceName looks
    /// like "\\.\DISPLAY3".
    /// </summary>
    private static int DisplayNumber(Screen s)
    {
        var name = s.DeviceName;
        int i = name.Length;
        while (i > 0 && char.IsDigit(name[i - 1])) i--;
        return i < name.Length && int.TryParse(name.AsSpan(i), out var n) ? n : 0;
    }

    private static List<Screen> OrderedScreens()
    {
        var list = Screen.AllScreens.ToList();
        list.Sort((a, b) => DisplayNumber(a).CompareTo(DisplayNumber(b)));
        return list;
    }

    private void HomeToMonitor(int number, bool save = true)
    {
        Screen? target = null;
        foreach (var s in Screen.AllScreens)
        {
            if (DisplayNumber(s) == number) { target = s; break; }
        }
        target ??= Screen.PrimaryScreen ?? Screen.AllScreens[0];

        var wa = target.WorkingArea;
        int m = R(16);
        int x = _cfg.Corner is Corner.TopLeft or Corner.BottomLeft ? wa.Left + m : wa.Right - Width - m;
        int y = _cfg.Corner is Corner.TopLeft or Corner.TopRight ? wa.Top + m : wa.Bottom - Height - m;

        Location = new Point(x, y);

        if (save)
        {
            _cfg.Monitor = number;
            _cfg.X = x;
            _cfg.Y = y;
            _cfg.Save();
        }
    }

    private void ApplyRegion()
    {
        var old = _rgn;
        _rgn = Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, R(Radius), R(Radius));
        Native.SetWindowRgn(Handle, _rgn, true);
        if (old != IntPtr.Zero) Native.DeleteObject(old);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // Re-scale in place. Repositioning here would fight the move that caused
        // the DPI change in the first place.
        ApplyLayout(reposition: false);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // DeviceDpi is only trustworthy once the handle is on a real monitor.
        ApplyLayout();
    }

    // ------------------------------------------------------------------ z-order

    private void ApplyZMode()
    {
        TopMost = _cfg.ZMode == ZMode.Topmost;
        if (!IsHandleCreated) return;

        IntPtr after = _cfg.ZMode switch
        {
            ZMode.Topmost => Native.HWND_TOPMOST,
            ZMode.Desktop => Native.HWND_BOTTOM,
            _ => Native.HWND_NOTOPMOST,
        };
        // NOACTIVATE matters: without it this call takes foreground from whatever is
        // running whenever the placement is re-applied (startup, DPI change, hotplug).
        Native.SetWindowPos(Handle, after, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    protected override void WndProc(ref Message m)
    {
        // Keep the widget glued to the bottom of the z-order in Desktop mode, so
        // clicking it (or any other window activating) never raises it over apps.
        if (m.Msg == Native.WM_WINDOWPOSCHANGING && _cfg.ZMode == ZMode.Desktop)
        {
            var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.WINDOWPOS>(m.LParam);
            wp.hwndInsertAfter = Native.HWND_BOTTOM;
            wp.flags &= ~Native.SWP_NOZORDER;
            System.Runtime.InteropServices.Marshal.StructureToPtr(wp, m.LParam, false);
        }

        base.WndProc(ref m);

        // A monitor was added, removed or rearranged -- re-home if we fell off.
        if (m.Msg == Native.WM_DISPLAYCHANGE && !_dragging && IsHandleCreated)
        {
            BeginInvoke(() =>
            {
                PlaceOnScreen();
                ApplyZMode();
            });
        }
    }

    // ------------------------------------------------------------------ dragging

    /// <summary>Bottom-right corner box that starts a resize instead of a move.</summary>
    private Rectangle GripRect() =>
        new(ClientSize.Width - R(GripSize), ClientSize.Height - R(GripSize), R(GripSize), R(GripSize));

    private void OnMouseDownDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _cfg.Locked) return;

        if (GripRect().Contains(e.Location))
        {
            _resizing = true;
            _resizeAnchor = Cursor.Position;
            _resizeStartScale = _cfg.Scale;
            _resizeStartSize = ClientSize;
            Capture = true;
            return;
        }

        _dragging = true;
        _dragOffset = new Point(e.X, e.Y);
        Capture = true;
    }

    private void OnMouseMoveDrag(object? sender, MouseEventArgs e)
    {
        if (!_dragging && !_resizing)
        {
            // Hovering the grip advertises that the corner is draggable.
            Cursor = _cfg.Locked ? Cursors.Default
                : GripRect().Contains(e.Location) ? Cursors.SizeNWSE
                : Cursors.Default;
            return;
        }

        // Belt and braces: if the button is already up, a MouseUp went missing.
        // Without this the widget would snap to the cursor on an ordinary hover.
        if ((MouseButtons & MouseButtons.Left) == 0)
        {
            OnMouseUpDrag(sender, e);
            return;
        }

        if (_resizing)
        {
            var c = Cursor.Position;
            double fx = (_resizeStartSize.Width + (c.X - _resizeAnchor.X)) / (double)_resizeStartSize.Width;
            double fy = (_resizeStartSize.Height + (c.Y - _resizeAnchor.Y)) / (double)_resizeStartSize.Height;

            // Whichever axis the pointer moved further along, proportionally, drives
            // the scale -- so a mostly-horizontal or mostly-vertical drag both work.
            double factor = Math.Abs(fx - 1.0) >= Math.Abs(fy - 1.0) ? fx : fy;

            double scale = Math.Clamp(_resizeStartScale * factor, MinScale, MaxScale);
            if (Math.Abs(scale - _cfg.Scale) > 0.004)
            {
                _cfg.Scale = scale;
                ApplyLayout(reposition: false);   // keep the top-left corner pinned
            }
            return;
        }

        var p = Cursor.Position;
        var target = new Point(p.X - _dragOffset.X, p.Y - _dragOffset.Y);

        // Snap to the edges of whichever monitor we are over.
        var wa = Screen.FromPoint(p).WorkingArea;
        int snap = R(14);
        if (Math.Abs(target.X - wa.Left) < snap) target.X = wa.Left;
        if (Math.Abs(target.X + Width - wa.Right) < snap) target.X = wa.Right - Width;
        if (Math.Abs(target.Y - wa.Top) < snap) target.Y = wa.Top;
        if (Math.Abs(target.Y + Height - wa.Bottom) < snap) target.Y = wa.Bottom - Height;

        Location = target;
    }

    private void OnMouseUpDrag(object? sender, MouseEventArgs e)
    {
        if (!_dragging && !_resizing) return;
        // The flags must be cleared before releasing capture: the release re-enters
        // OnMouseCaptureChanged, which would otherwise save a second time.
        bool wasResizing = _resizing;
        _dragging = false;
        _resizing = false;
        Capture = false;
        Cursor = Cursors.Default;

        _cfg.X = Location.X;
        _cfg.Y = Location.Y;
        _cfg.Save();

        // The window grew away from its anchor; make sure it did not walk off-screen.
        if (wasResizing) ClampIntoScreen();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);

        // Windows revokes capture without ever sending WM_LBUTTONUP -- secure desktop,
        // a UAC prompt, Win+D, alt-tab, another process calling SetCapture. Ending the
        // gesture here stops the flags from latching on forever.
        if ((_dragging || _resizing) && !Capture)
        {
            _dragging = false;
            _resizing = false;
            Cursor = Cursors.Default;
            _cfg.X = Location.X;
            _cfg.Y = Location.Y;
            _cfg.Save();
        }
    }

    /// <summary>Nudge the widget back inside its monitor after it changed size.</summary>
    private void ClampIntoScreen()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int x = Location.X, y = Location.Y;

        if (x + Width > wa.Right) x = wa.Right - Width;
        if (y + Height > wa.Bottom) y = wa.Bottom - Height;
        if (x < wa.Left) x = wa.Left;
        if (y < wa.Top) y = wa.Top;

        if (x == Location.X && y == Location.Y) return;

        Location = new Point(x, y);
        _cfg.X = x;
        _cfg.Y = y;
        _cfg.Save();
    }

    // ------------------------------------------------------------------ data

    private void OnSensorUpdate(Snapshot s)
    {
        if (!IsHandleCreated || IsDisposed) return;
        // The handle can be destroyed between the check above and the marshal below.
        // BeginInvoke surfaces that as InvalidOperationException, not
        // ObjectDisposedException; ODE derives from IOE, so this covers both.
        try { BeginInvoke(() => Consume(s)); } catch (InvalidOperationException) { }
    }

    private void OnSensorFailure(string msg)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(() => { _error = msg; Invalidate(); }); } catch (InvalidOperationException) { }
    }

    private void Consume(Snapshot s)
    {
        _last = s;
        // The snapshot is the single source of truth for failure. Clearing this
        // unconditionally would erase the message the failed poll just reported,
        // since both callbacks drain before the next WM_PAINT.
        _error = s.Error;

        _cpu.Push(s.CpuLoad);
        _cpu.Right1 = Pct(s.CpuLoad);
        if (double.IsNaN(s.CpuTemp) && s.CpuTempNeedsDriver)
        {
            // Sensor is present but unreadable without PawnIO. Say so rather than
            // showing "--", which reads as "this machine has no such sensor".
            _cpu.Right2 = "n/a";
            _cpu.Right2Color = Palette.TextDim;
        }
        else
        {
            _cpu.Right2 = Temp(s.CpuTemp);
            _cpu.Right2Color = TempColor(s.CpuTemp, 60, 75, 85);
        }

        _gpu.Push(s.GpuLoad);
        _gpu.Right1 = Pct(s.GpuLoad);
        if (_cfg.ShowHotspot && !double.IsNaN(s.GpuHotspot))
        {
            _gpu.Right2 = Temp(s.GpuHotspot);
            _gpu.Right2Color = TempColor(s.GpuHotspot, 85, 100, 108);
        }
        else
        {
            _gpu.Right2 = Temp(s.GpuTemp);
            _gpu.Right2Color = TempColor(s.GpuTemp, 60, 75, 85);
        }

        double vramPct = (!double.IsNaN(s.VramUsedGb) && s.VramTotalGb > 0)
            ? s.VramUsedGb / s.VramTotalGb * 100.0 : double.NaN;
        _vram.Push(vramPct);
        _vram.Right1 = "";
        _vram.Right2 = Gb(s.VramUsedGb, s.VramTotalGb);
        _vram.Right2Color = Palette.Text;
        _vram.Right2Wide = true;

        _ram.Push(s.RamLoad);
        _ram.Right1 = "";
        _ram.Right2 = Gb(s.RamUsedGb, s.RamTotalGb);
        _ram.Right2Color = Palette.Text;
        _ram.Right2Wide = true;

        UpdateTooltip(s);
        Invalidate();
    }

    private void UpdateTooltip(Snapshot s)
    {
        var parts = new List<string>(6);
        // Most-informative first. The shell's cap is small enough that whatever is
        // appended last never survives -- the hardware names alone nearly fill it.
        if (s.CpuTempFrom == CpuTempSource.External)
            parts.Add($"temp: {s.CpuTempSourceName ?? "external"}");
        if (s.CpuName is not null) parts.Add(Trim(s.CpuName, 40));
        if (s.GpuName is not null) parts.Add(Trim(s.GpuName, 40));
        if (!double.IsNaN(s.CpuClock)) parts.Add($"CPU {s.CpuClock / 1000.0:0.00} GHz");
        if (_cfg.ShowPower)
        {
            if (!double.IsNaN(s.CpuPower)) parts.Add($"CPU {s.CpuPower:0} W");
            if (!double.IsNaN(s.GpuPower)) parts.Add($"GPU {s.GpuPower:0} W");
        }

        // NotifyIcon.Text is capped at 63 chars by the shell. Drop whole parts that
        // do not fit rather than slicing one in half and leaving a dangling separator.
        const string sep = "  ·  ";
        var sb = new System.Text.StringBuilder(62);
        foreach (var p in parts)
        {
            int cost = sb.Length == 0 ? p.Length : p.Length + sep.Length;
            if (sb.Length + cost > 62) continue;   // skip, not break: shorter tails still fit
            if (sb.Length > 0) sb.Append(sep);
            sb.Append(p);
        }
        _tray.Text = sb.ToString();
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
    private static string Pct(double v) => double.IsNaN(v) ? "--" : $"{v:0}%";
    private static string Temp(double v) => double.IsNaN(v) ? "--" : $"{v:0}°";

    private static string Gb(double used, double total)
    {
        if (double.IsNaN(used)) return "--";
        return double.IsNaN(total) || total <= 0 ? $"{used:0.0}G" : $"{used:0.0}/{total:0.0}G";
    }

    private static Color TempColor(double v, double ok, double warm, double hot)
    {
        if (double.IsNaN(v)) return Palette.TextDim;
        if (v < ok) return Palette.Ok;
        if (v < warm) return Palette.Warm;
        if (v < hot) return Palette.Hot;
        return Palette.Crit;
    }

    // ------------------------------------------------------------------ paint

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Palette.Back);

        using (var pen = new Pen(Palette.Border, 1f))
        using (var path = RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), R(Radius)))
            g.DrawPath(pen, path);

        if (_error is not null)
        {
            TextRenderer.DrawText(g, "sensor error - see tray tooltip",
                _fValue, new Rectangle(R(PadX), 0, Width - R(PadX * 2), Height),
                Palette.Crit, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            _tray.Text = Trim("DeskMon: " + _error, 62);
            return;
        }

        int y = R(PadY);
        int rowH = R(RowH);

        foreach (var row in _rows)
        {
            DrawRow(g, row, new Rectangle(R(PadX), y, Width - R(PadX * 2), rowH));
            y += rowH;
        }

        if (!_cfg.Locked) DrawGrip(g);
    }

    /// <summary>Three short diagonals in the bottom-right corner: the resize handle.</summary>
    private void DrawGrip(Graphics g)
    {
        var r = GripRect();
        float t = Math.Max(1f, 1.1f * _s);
        int inset = R(3);

        using var pen = new Pen(Color.FromArgb(_resizing ? 190 : 105, Palette.TextDim), t);
        for (int i = 0; i < 3; i++)
        {
            float off = inset + i * (R(GripSize) - inset * 2) / 3f;
            g.DrawLine(pen,
                r.Right - inset, r.Bottom - inset - off,
                r.Right - inset - off, r.Bottom - inset);
        }
    }

    private void DrawRow(Graphics g, Row row, Rectangle bounds)
    {
        int labelW = R(LabelW);
        int col1W = R(Col1W);
        int col2W = R(Col2W);
        int gap = R(6);

        var labelRect = new Rectangle(bounds.X, bounds.Y, labelW, bounds.Height);
        TextRenderer.DrawText(g, row.Label, _fLabel, labelRect, Palette.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        int rightW = row.Right2Wide ? col1W + col2W : col2W;
        var col2Rect = new Rectangle(bounds.Right - rightW, bounds.Y, rightW, bounds.Height);
        TextRenderer.DrawText(g, row.Right2, _fValue, col2Rect, row.Right2Color,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        if (!row.Right2Wide)
        {
            var col1Rect = new Rectangle(bounds.Right - col2W - col1W, bounds.Y, col1W, bounds.Height);
            TextRenderer.DrawText(g, row.Right1, _fValue, col1Rect, Palette.Text,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        var graph = new Rectangle(
            bounds.X + labelW + gap,
            bounds.Y + R(4),
            bounds.Width - labelW - gap * 2 - col1W - col2W,
            bounds.Height - R(8));

        if (graph.Width > R(8)) DrawSparkline(g, row, graph);
    }

    private void DrawSparkline(Graphics g, Row row, Rectangle r)
    {
        using (var track = new SolidBrush(Palette.Track))
        using (var path = RoundedRect(r, R(3)))
            g.FillPath(track, path);

        if (row.Count < 2) return;

        // Save/Restore rather than reading g.Clip -- that property hands back a new
        // Region every call, which would leak a GDI handle on every repaint.
        var state = g.Save();
        g.SetClip(r, CombineMode.Intersect);

        // One point per horizontal pixel-step across the retained history.
        int n = row.Count;
        var pts = new PointF[n + 2];
        for (int i = 0; i < n; i++)
        {
            float fx = n == 1 ? 1f : i / (float)(n - 1);
            float v = row.At(i) / 100f;
            pts[i] = new PointF(r.Left + fx * r.Width, r.Bottom - v * r.Height);
        }
        pts[n] = new PointF(r.Right, r.Bottom);
        pts[n + 1] = new PointF(r.Left, r.Bottom);

        using (var fill = new SolidBrush(Color.FromArgb(70, row.Color)))
            g.FillPolygon(fill, pts);

        using (var pen = new Pen(row.Color, Math.Max(1f, 1.2f * _s)))
        {
            pen.LineJoin = LineJoin.Round;
            if (n >= 2) g.DrawLines(pen, pts.AsSpan(0, n).ToArray());
        }

        g.Restore(state);
    }

    private static GraphicsPath RoundedRect(RectangleF r, int radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ------------------------------------------------------------------ menu

    private void BuildMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        menu.Opening += (_, _) =>
        {
            // With WS_EX_NOACTIVATE the right-click no longer activates the form, and
            // a drop-down whose owner is not foreground will not dismiss on an outside
            // click. Asking for foreground here is the documented workaround, and it is
            // allowed because this process just received the click.
            Native.SetForegroundWindow(Handle);
            SyncMenuChecks(menu);
        };

        // Built fresh each time it opens so hot-plugged displays show up.
        var monitors = new ToolStripMenuItem("Move to monitor");
        monitors.DropDownOpening += (_, _) =>
        {
            monitors.DropDownItems.Clear();
            foreach (var s in OrderedScreens())
            {
                int n = DisplayNumber(s);
                var b = s.Bounds;
                string label = $"{n}  —  {b.Width}×{b.Height}{(s.Primary ? "  (primary)" : "")}";
                monitors.DropDownItems.Add(Radio(label,
                    () => HomeToMonitor(n),
                    () => Bounds.IntersectsWith(s.WorkingArea) && DisplayNumber(Screen.FromControl(this)) == n));
            }
        };
        menu.Items.Add(monitors);

        var corner = new ToolStripMenuItem("Corner");
        foreach (var (label, c) in new[]
                 {
                     ("Top left", Corner.TopLeft), ("Top right", Corner.TopRight),
                     ("Bottom left", Corner.BottomLeft), ("Bottom right", Corner.BottomRight),
                 })
        {
            corner.DropDownItems.Add(Radio(label, () =>
            {
                _cfg.Corner = c;
                HomeToMonitor(DisplayNumber(Screen.FromControl(this)));
            }, () => _cfg.Corner == c));
        }
        menu.Items.Add(corner);

        var place = new ToolStripMenuItem("Placement");
        place.DropDownItems.Add(Radio("On desktop (behind windows)", () => SetZ(ZMode.Desktop), () => _cfg.ZMode == ZMode.Desktop));
        place.DropDownItems.Add(Radio("Normal", () => SetZ(ZMode.Normal), () => _cfg.ZMode == ZMode.Normal));
        place.DropDownItems.Add(Radio("Always on top", () => SetZ(ZMode.Topmost), () => _cfg.ZMode == ZMode.Topmost));
        menu.Items.Add(place);

        var rate = new ToolStripMenuItem("Refresh rate");
        foreach (var (label, ms) in new[] { ("0.5 s", 500), ("1 s", 1000), ("1.5 s", 1500), ("3 s", 3000), ("5 s", 5000) })
            rate.DropDownItems.Add(Radio(label, () => SetInterval(ms), () => _cfg.IntervalMs == ms));
        menu.Items.Add(rate);

        var size = new ToolStripMenuItem("Size");
        foreach (var (label, sc) in new[]
                 { ("Small  (85%)", 0.85), ("Normal  (100%)", 1.0), ("Large  (125%)", 1.25), ("Huge  (160%)", 1.6) })
            size.DropDownItems.Add(Radio(label, () => SetScale(sc), () => Math.Abs(_cfg.Scale - sc) < 0.005));
        size.DropDownItems.Add(new ToolStripSeparator());
        var hint = new ToolStripMenuItem("or drag the bottom-right corner") { Enabled = false };
        size.DropDownItems.Add(hint);
        size.DropDownOpening += (_, _) =>
            hint.Text = $"or drag the bottom-right corner  —  now {_cfg.Scale * 100:0}%";
        menu.Items.Add(size);

        var opacity = new ToolStripMenuItem("Opacity");
        foreach (var (label, op) in new[] { ("40%", 0.40), ("60%", 0.60), ("75%", 0.75), ("88%", 0.88), ("100%", 1.0) })
            opacity.DropDownItems.Add(Radio(label, () => SetOpacity(op), () => Math.Abs(_cfg.Opacity - op) < 0.001));
        menu.Items.Add(opacity);

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Check("Show VRAM row", () =>
        {
            _cfg.ShowVram = !_cfg.ShowVram;
            _cfg.Save();
            ApplyLayout();
        }, () => _cfg.ShowVram));

        menu.Items.Add(Check("GPU hot spot instead of core", () =>
        {
            _cfg.ShowHotspot = !_cfg.ShowHotspot;
            _cfg.Save();
            Consume(_last);
        }, () => _cfg.ShowHotspot));

        menu.Items.Add(Check("Lock position", () =>
        {
            _cfg.Locked = !_cfg.Locked;
            _cfg.Save();
        }, () => _cfg.Locked));

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Reset position", null, (_, _) =>
        {
            _cfg.X = _cfg.Y = int.MinValue;
            _cfg.Save();
            PlaceOnScreen();
        }));

        // Only worth offering when CPU temperature is actually missing. Afterburner
        // can start or stop while DeskMon runs, so this is decided each time the
        // menu opens rather than once at construction.
        _cpuTempItem = new ToolStripMenuItem("Enable CPU temperature…", null, (_, _) => ExplainCpuTemp());
        menu.Items.Add(_cpuTempItem);
        menu.Opening += (_, _) => _cpuTempItem.Visible = _last.CpuTempFrom == CpuTempSource.None;

        menu.Items.Add(new ToolStripMenuItem("Copy sensor list…", null, (_, _) => DumpSensors()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Close()));

        ContextMenuStrip = menu;
        _tray.ContextMenuStrip = menu;
    }

    private static void SyncMenuChecks(ToolStripDropDown menu)
    {
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripMenuItem mi)
            {
                if (mi.Tag is Func<bool> isChecked) mi.Checked = isChecked();
                if (mi.HasDropDownItems) SyncMenuChecks(mi.DropDown);
            }
        }
    }

    private static ToolStripMenuItem Radio(string text, Action onClick, Func<bool> isChecked)
    {
        var mi = new ToolStripMenuItem(text, null, (_, _) => onClick()) { Tag = isChecked, Checked = isChecked() };
        return mi;
    }

    private static ToolStripMenuItem Check(string text, Action onClick, Func<bool> isChecked) =>
        Radio(text, onClick, isChecked);

    private void SetZ(ZMode m)
    {
        _cfg.ZMode = m;
        _cfg.Save();
        ApplyZMode();
    }

    private void CycleZMode() => SetZ(_cfg.ZMode switch
    {
        ZMode.Desktop => ZMode.Topmost,
        ZMode.Topmost => ZMode.Normal,
        _ => ZMode.Desktop,
    });

    private void SetInterval(int ms)
    {
        _cfg.IntervalMs = ms;
        _cfg.Save();
        _mon.IntervalMs = ms;
    }

    private void SetScale(double sc)
    {
        _cfg.Scale = sc;
        _cfg.Save();
        ApplyLayout();
    }

    private void SetOpacity(double op)
    {
        _cfg.Opacity = op;
        _cfg.Save();
        Opacity = op;
    }

    private void ExplainCpuTemp()
    {
        const string text =
            "Ryzen CPU temperature (Tctl/Tdie) can only be read through a kernel driver. " +
            "Everything else in this widget — GPU core and hot-spot temperature, GPU load, " +
            "VRAM, and RAM — already works without one.\n\n" +
            "DeskMon does not ship a driver. It gets CPU temperature two other ways:\n\n" +
            "1. MSI Afterburner. If it is running, DeskMon reads the value straight out " +
            "of its shared memory. Afterburner already loads its own kernel driver, so " +
            "this costs nothing and adds no new exposure. Just start Afterburner.\n\n" +
            "2. PawnIO — a signed, Microsoft-attested driver that replaced the old " +
            "WinRing0. WinRing0 is what most monitoring tools still ship, and Defender " +
            "quarantines it (CVE-2020-14979) while Vanguard and Easy Anti-Cheat block " +
            "it. DeskMon deliberately does not use WinRing0.\n\n" +
            "Option 1 needs no install. Option 2 is still a kernel driver install and is " +
            "your call. Open the PawnIO download page?";

        var answer = MessageBox.Show(text, "DeskMon — CPU temperature",
            MessageBoxButtons.YesNo, MessageBoxIcon.Information);

        if (answer != DialogResult.Yes) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://github.com/namazso/PawnIO.Setup/releases") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "DeskMon", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DumpSensors()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "DeskMon-sensors.txt");
            File.WriteAllText(path, _mon.DumpSensors());
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "DeskMon", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Detach while the handle is still alive, so the poll thread stops marshalling
        // into a form that is about to go away.
        _mon.Updated -= OnSensorUpdate;
        _mon.Failed -= OnSensorFailure;

        _cfg.X = Location.X;
        _cfg.Y = Location.Y;
        _cfg.Save();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _fLabel?.Dispose();
            _fValue?.Dispose();
            if (_rgn != IntPtr.Zero) { Native.DeleteObject(_rgn); _rgn = IntPtr.Zero; }
        }
        base.Dispose(disposing);
    }
}
