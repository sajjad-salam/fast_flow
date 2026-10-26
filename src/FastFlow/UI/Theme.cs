using System.Drawing.Drawing2D;
using System.Drawing.Text;
using static FastFlow.Native.NativeMethods;

namespace FastFlow.UI;

/// <summary>
/// FastFlow's look, after typeless.com: a white canvas, soft grey rounded cards, near-black pill
/// buttons, Inter type and black / grey two-tone text.
/// </summary>
internal static class Theme
{
    public static readonly Color Background = Color.White;
    public static readonly Color Card = Color.FromArgb(245, 245, 245);
    public static readonly Color Ink = Color.FromArgb(17, 17, 17);
    public static readonly Color Muted = Color.FromArgb(136, 136, 136);
    public static readonly Color Line = Color.FromArgb(236, 236, 236);
    public static readonly Color Primary = Color.FromArgb(29, 26, 26);
    public static readonly Color PrimaryHover = Color.FromArgb(58, 54, 54);
    public static readonly Color PrimaryDisabled = Color.FromArgb(190, 188, 188);
    public static readonly Color Track = Color.FromArgb(214, 214, 214);
    public static readonly Color Warning = Color.FromArgb(196, 106, 16);

    /// <summary>Translucent grey of the site's chips (rgba(119,119,119,.1)), so it reads on white and on cards.</summary>
    public static readonly Color Chip = Color.FromArgb(26, 119, 119, 119);
    public static readonly Color ChipHover = Color.FromArgb(46, 119, 119, 119);
    public static readonly Color ChipPressed = Color.FromArgb(64, 119, 119, 119);

    private static readonly string Family = Installed("Inter") ? "Inter" : "Segoe UI";
    private static readonly string MediumFamily = Installed("Inter Medium") ? "Inter Medium" : "Segoe UI Semibold";
    private static readonly string SemiboldFamily = Installed("Inter SemiBold") ? "Inter SemiBold" : "Segoe UI Semibold";

    public static readonly Font Body = new(Family, 9.75f);
    public static readonly Font BodyMedium = new(MediumFamily, 9.75f);
    public static readonly Font Small = new(Family, 8.75f);
    public static readonly Font SmallMedium = new(MediumFamily, 8.75f);
    public static readonly Font Heading = new(SemiboldFamily, 11.5f);
    public static readonly Font Title = new(SemiboldFamily, 19f);
    public static readonly Font Hero = new(SemiboldFamily, 22f);
    public static readonly Font Brand = new(Family, 15f, FontStyle.Bold);
    public static readonly Font Mono = new(Installed("Cascadia Mono") ? "Cascadia Mono" : "Consolas", 8.75f);

    private static Icon? _appIcon;

    /// <summary>The FastFlow mark as a window / tray icon.</summary>
    public static Icon AppIcon => _appIcon ??= CreateAppIcon();

    private static bool Installed(string family)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase));
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Color color, RectangleF r, float radius)
    {
        using var path = RoundedRect(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>The FastFlow mark: two forward chevrons on a dark rounded square.</summary>
    public static void DrawLogo(Graphics g, RectangleF r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        FillRounded(g, Primary, r, r.Width * 0.3f);
        using var pen = new Pen(Color.White, r.Width * 0.12f)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
        };
        foreach (var x in new[] { 0.28f, 0.5f })
        {
            g.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * x, r.Top + r.Height * 0.3f),
                new PointF(r.Left + r.Width * (x + 0.2f), r.Top + r.Height * 0.5f),
                new PointF(r.Left + r.Width * x, r.Top + r.Height * 0.7f),
            });
        }
    }

    private static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bitmap))
            DrawLogo(g, new RectangleF(2, 2, 60, 60));
        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>White window with Inter text; on Windows 11 also a white title bar so it blends into the page.</summary>
    public static void Apply(Form form)
    {
        form.AutoScaleDimensions = new SizeF(96, 96);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.BackColor = Background;
        form.ForeColor = Ink;
        form.Font = Body;
        form.Icon = AppIcon;
        form.HandleCreated += (_, _) => StyleTitleBar(form.Handle);
    }

    private static void StyleTitleBar(IntPtr hwnd)
    {
        // Ignored before Windows 11, which keeps its default title bar.
        var caption = ColorTranslator.ToWin32(Background);
        var text = ColorTranslator.ToWin32(Ink);
        var border = ColorTranslator.ToWin32(Color.FromArgb(222, 222, 222));
        var corners = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
    }

    /// <summary>Right-aligned row of dialog buttons, shown left to right in the order given.</summary>
    public static FlowLayoutPanel ButtonRow(params Button[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill,
            WrapContents = false, Margin = new Padding(0, 20, 0, 0),
        };
        for (var i = buttons.Length - 1; i >= 0; i--)
        {
            buttons[i].Margin = new Padding(i == 0 ? 0 : 8, 0, 0, 0);
            row.Controls.Add(buttons[i]);
        }
        return row;
    }

    /// <summary>Tray menu in the same style: white, hairline border, rounded grey highlight.</summary>
    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new MenuRenderer();
        menu.Font = Body;
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = true;
        menu.Padding = new Padding(4);
    }

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) => RoundedEdges = true;

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
                return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = e.Item.Owner?.DeviceDpi / 96f ?? 1;
            FillRounded(e.Graphics, Card, new RectangleF(2, 1, e.Item.Width - 4, e.Item.Height - 2), 6 * scale);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Ink, Math.Max(1.5f, r.Width / 8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            e.Graphics.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.32f),
            });
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Ink : Muted;
            base.OnRenderItemText(e);
        }
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Color.FromArgb(222, 222, 222);
        public override Color MenuItemBorder => Card;
        public override Color MenuItemSelected => Card;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Background;
    }
}
