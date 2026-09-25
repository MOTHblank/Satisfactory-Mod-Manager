using System.Drawing.Drawing2D;

namespace SatisfactoryModManager;

internal readonly record struct UiPalette(
    Color BackgroundDeep,
    Color BackgroundBase,
    Color BackgroundElevated,
    Color Surface,
    Color SurfaceRaised,
    Color SurfaceHover,
    Color Foreground,
    Color Muted,
    Color Subtle,
    Color Accent,
    Color AccentBright,
    Color AccentGlow,
    Color Border,
    Color BorderHover,
    Color Danger,
    Color Success,
    Color Warning,
    Color Selection);

internal static class UiTheme
{
    internal static UiPalette Create(bool dark) => dark
        ? new UiPalette(
            Color.FromArgb(2, 2, 3),
            Color.FromArgb(5, 5, 6),
            Color.FromArgb(10, 10, 12),
            Color.FromArgb(15, 15, 18),
            Color.FromArgb(20, 20, 25),
            Color.FromArgb(27, 27, 34),
            Color.FromArgb(237, 237, 239),
            Color.FromArgb(138, 143, 152),
            Color.FromArgb(175, 178, 186),
            Color.FromArgb(94, 106, 210),
            Color.FromArgb(104, 114, 217),
            Color.FromArgb(74, 82, 171),
            Color.FromArgb(31, 31, 37),
            Color.FromArgb(48, 48, 58),
            Color.FromArgb(214, 99, 112),
            Color.FromArgb(111, 207, 151),
            Color.FromArgb(215, 168, 90),
            Color.FromArgb(25, 28, 52))
        : new UiPalette(
            Color.FromArgb(232, 234, 240),
            Color.FromArgb(244, 245, 248),
            Color.FromArgb(250, 250, 252),
            Color.FromArgb(255, 255, 255),
            Color.FromArgb(247, 248, 251),
            Color.FromArgb(239, 241, 247),
            Color.FromArgb(31, 33, 38),
            Color.FromArgb(96, 101, 112),
            Color.FromArgb(118, 123, 133),
            Color.FromArgb(82, 94, 196),
            Color.FromArgb(69, 82, 188),
            Color.FromArgb(196, 201, 239),
            Color.FromArgb(219, 222, 231),
            Color.FromArgb(197, 202, 216),
            Color.FromArgb(181, 63, 78),
            Color.FromArgb(34, 137, 83),
            Color.FromArgb(157, 105, 24),
            Color.FromArgb(226, 229, 247));

    internal static Color Blend(Color a, Color b, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(a.A + (b.A - a.A) * amount),
            (int)Math.Round(a.R + (b.R - a.R) * amount),
            (int)Math.Round(a.G + (b.G - a.G) * amount),
            (int)Math.Round(a.B + (b.B - a.B) * amount));
    }

    internal static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var diameter = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class ModernBackgroundPanel : Panel
{
    private UiPalette _palette = UiTheme.Create(true);
    private readonly System.Windows.Forms.Timer _motionTimer;
    private float _phase;

    internal ModernBackgroundPanel()
    {
        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint, true);

        _motionTimer = new System.Windows.Forms.Timer { Interval = 70 };
        _motionTimer.Tick += (_, _) =>
        {
            _phase += 0.018f;
            if (_phase > MathF.PI * 2f)
                _phase -= MathF.PI * 2f;
            Invalidate();
        };

        // Use the Windows UI-animation preference as a conservative reduced-motion signal.
        if (SystemInformation.IsMenuAnimationEnabled)
            _motionTimer.Start();
    }

    internal void SetPalette(UiPalette palette)
    {
        _palette = palette;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _motionTimer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0)
        {
            base.OnPaintBackground(e);
            return;
        }

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var background = new LinearGradientBrush(
                   ClientRectangle,
                   _palette.BackgroundElevated,
                   _palette.BackgroundDeep,
                   LinearGradientMode.Vertical))
        {
            g.FillRectangle(background, ClientRectangle);
        }

        var driftX = (int)(MathF.Sin(_phase) * 34f);
        var driftY = (int)(MathF.Cos(_phase * 0.72f) * 22f);
        DrawGlow(g,
            new Rectangle(Width / 2 - 520 + driftX, -330 + driftY, 1040, 720),
            Color.FromArgb(54, _palette.Accent));
        DrawGlow(g,
            new Rectangle(-330 - driftX / 2, Height / 4 - 170, 720, 620),
            Color.FromArgb(24, 113, 87, 195));
        DrawGlow(g,
            new Rectangle(Width - 430 + driftX / 3, Height / 3, 620, 560),
            Color.FromArgb(20, 73, 118, 210));

        using var gridPen = new Pen(Color.FromArgb(10, _palette.Foreground), 1f);
        const int grid = 64;
        for (var x = 0; x < Width; x += grid)
            g.DrawLine(gridPen, x, 0, x, Height);
        for (var y = 0; y < Height; y += grid)
            g.DrawLine(gridPen, 0, y, Width, y);
    }

    private static void DrawGlow(Graphics g, Rectangle bounds, Color centerColor)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        using var path = new GraphicsPath();
        path.AddEllipse(bounds);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = centerColor,
            SurroundColors = new[] { Color.FromArgb(0, centerColor) }
        };
        g.FillEllipse(brush, bounds);
    }
}

internal sealed class ModernCardPanel : Panel
{
    private UiPalette _palette = UiTheme.Create(true);
    private bool _hot;
    private Point _pointer;

    internal ModernCardPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        MouseEnter += (_, _) => UpdatePointer(PointToClient(Cursor.Position));
        MouseLeave += (_, _) => UpdatePointer(PointToClient(Cursor.Position));
        MouseMove += (_, e) => UpdatePointer(e.Location);
        ControlAdded += (_, e) =>
        {
            if (e.Control is { } child)
                TrackPointerFrom(child);
        };
    }

    private void TrackPointerFrom(Control control)
    {
        control.MouseEnter += ChildPointerChanged;
        control.MouseLeave += ChildPointerChanged;
        control.MouseMove += ChildPointerMoved;
        control.ControlAdded += (_, e) =>
        {
            if (e.Control is { } child)
                TrackPointerFrom(child);
        };

        foreach (Control child in control.Controls)
            TrackPointerFrom(child);
    }

    private void ChildPointerChanged(object? sender, EventArgs e)
    {
        UpdatePointer(PointToClient(Cursor.Position));
    }

    private void ChildPointerMoved(object? sender, MouseEventArgs e)
    {
        if (sender is not Control child)
            return;

        UpdatePointer(PointToClient(child.PointToScreen(e.Location)));
    }

    private void UpdatePointer(Point location)
    {
        _pointer = location;
        _hot = ClientRectangle.Contains(location);
        Invalidate();
    }

    internal void SetPalette(UiPalette palette)
    {
        _palette = palette;
        Invalidate();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        if (Width <= 0 || Height <= 0)
            return;
        using var path = UiTheme.RoundedRect(new Rectangle(0, 0, Width, Height), 16);
        Region?.Dispose();
        Region = new Region(path);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = UiTheme.RoundedRect(rect, 16);

        using (var fill = new LinearGradientBrush(
                   rect,
                   UiTheme.Blend(_palette.SurfaceRaised, _palette.Foreground, 0.035f),
                   _palette.Surface,
                   LinearGradientMode.Vertical))
        {
            g.FillPath(fill, path);
        }

        if (_hot)
        {
            var glowBounds = new Rectangle(_pointer.X - 170, _pointer.Y - 170, 340, 340);
            using var glowPath = new GraphicsPath();
            glowPath.AddEllipse(glowBounds);
            using var glow = new PathGradientBrush(glowPath)
            {
                CenterColor = Color.FromArgb(32, _palette.Accent),
                SurroundColors = new[] { Color.FromArgb(0, _palette.Accent) }
            };
            g.FillEllipse(glow, glowBounds);
        }

        using var border = new Pen(_hot ? _palette.BorderHover : _palette.Border);
        g.DrawPath(border, path);

        using var topHighlight = new Pen(Color.FromArgb(20, _palette.Foreground));
        g.DrawLine(topHighlight, 16, 1, Math.Max(16, Width - 16), 1);
    }
}

internal enum ModernButtonTone
{
    Secondary,
    Accent,
    Danger
}

internal sealed class ModernButton : Button
{
    private UiPalette _palette = UiTheme.Create(true);
    private bool _hot;
    private bool _pressed;

    internal ModernButtonTone Tone { get; set; } = ModernButtonTone.Secondary;

    internal ModernButton()
    {
        DoubleBuffered = true;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Color.Transparent;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        MouseEnter += (_, _) => { _hot = true; Invalidate(); };
        MouseLeave += (_, _) => { _hot = false; _pressed = false; Invalidate(); };
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } };
        MouseUp += (_, _) => { _pressed = false; Invalidate(); };
    }

    internal void SetPalette(UiPalette palette)
    {
        _palette = palette;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        if (_pressed)
            rect.Inflate(-1, -1);

        using var path = UiTheme.RoundedRect(rect, 8);

        var baseColor = Tone switch
        {
            ModernButtonTone.Accent => _palette.Accent,
            ModernButtonTone.Danger => _palette.SurfaceRaised,
            _ => _palette.SurfaceRaised
        };

        if (!Enabled)
            baseColor = UiTheme.Blend(baseColor, _palette.BackgroundBase, 0.45f);
        else if (_hot)
            baseColor = Tone == ModernButtonTone.Accent
                ? _palette.AccentBright
                : _palette.SurfaceHover;

        var bottomColor = Tone == ModernButtonTone.Accent
            ? UiTheme.Blend(baseColor, _palette.BackgroundDeep, 0.13f)
            : UiTheme.Blend(baseColor, _palette.BackgroundDeep, 0.08f);

        using (var fill = new LinearGradientBrush(rect, baseColor, bottomColor, LinearGradientMode.Vertical))
            g.FillPath(fill, path);

        var borderColor = Tone switch
        {
            ModernButtonTone.Accent => UiTheme.Blend(_palette.AccentBright, _palette.Foreground, 0.16f),
            ModernButtonTone.Danger => UiTheme.Blend(_palette.Danger, _palette.Border, 0.35f),
            _ => _hot ? _palette.BorderHover : _palette.Border
        };
        using (var border = new Pen(borderColor))
            g.DrawPath(border, path);

        using (var top = new Pen(Color.FromArgb(Tone == ModernButtonTone.Accent ? 46 : 24, _palette.Foreground)))
            g.DrawLine(top, 9, rect.Top + 1, Math.Max(9, Width - 10), rect.Top + 1);

        var textColor = !Enabled
            ? UiTheme.Blend(_palette.Muted, _palette.BackgroundBase, 0.3f)
            : Tone switch
            {
                ModernButtonTone.Accent => Color.White,
                ModernButtonTone.Danger => _palette.Danger,
                _ => _palette.Foreground
            };

        var textRect = Rectangle.Inflate(rect, -8, 0);
        TextRenderer.DrawText(
            g,
            Text,
            Font,
            textRect,
            textColor,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);

        if (Focused && ShowFocusCues)
        {
            var focusRect = Rectangle.Inflate(rect, -3, -3);
            using var focusPath = UiTheme.RoundedRect(focusRect, 6);
            using var focusPen = new Pen(Color.FromArgb(150, _palette.Accent), 1.5f);
            g.DrawPath(focusPen, focusPath);
        }
    }
}
