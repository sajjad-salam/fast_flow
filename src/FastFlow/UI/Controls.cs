using System.Drawing.Drawing2D;

namespace FastFlow.UI;

/// <summary>A rounded panel; its children sit on <see cref="Fill"/>.</summary>
internal class Card : Panel
{
    private Color _fill = Theme.Card;

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = _fill;
        Padding = new Padding(20);
        Margin = Padding.Empty;
    }

    public Color Fill
    {
        get => _fill;
        set { _fill = value; BackColor = value; Invalidate(); }
    }

    /// <summary>Corner radius in 96-dpi pixels.</summary>
    public int Radius { get; set; } = 24;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRounded(g, _fill, new RectangleF(0, 0, Width - 0.5f, Height - 0.5f), Radius * DeviceDpi / 96f);
    }
}

/// <summary>A borderless text box on a rounded grey field.</summary>
internal sealed class FieldBox : Card
{
    private readonly TextBox _box;

    public FieldBox(TextBox box, int width, int height = 0)
    {
        _box = box;
        Radius = 12;
        Padding = new Padding(12, 9, 12, 9);
        Margin = new Padding(0, 0, 0, 4);
        box.BorderStyle = BorderStyle.None;
        box.BackColor = Fill;
        box.ForeColor = Theme.Ink;
        box.Dock = box.Multiline ? DockStyle.Fill : DockStyle.Top;
        Controls.Add(box);
        Size = new Size(width, height > 0 ? height : 38);
        Cursor = Cursors.IBeam;
        MouseDown += (_, _) => box.Focus();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        // A single-line box has a fixed height; the field wraps it. (Card's constructor lays out before _box is set.)
        if (_box is { Multiline: false })
            Height = _box.PreferredHeight + Padding.Vertical;
        base.OnLayout(e);
    }
}

/// <summary>Rounded button: a near-black pill (primary) or a translucent grey chip (secondary).</summary>
internal sealed class PillButton : Button
{
    private bool _hover;
    private bool _pressed;

    public PillButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.BodyMedium;
        Cursor = Cursors.Hand;
        AutoSize = true;
        Margin = new Padding(0, 0, 8, 8);
    }

    public bool Primary { get; init; }

    /// <summary>Corner radius in 96-dpi pixels; 0 makes a full pill.</summary>
    public int Radius { get; init; }

    private float Px(float value) => value * DeviceDpi / 96f;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        var padX = Primary ? 20 : 14;
        return new Size(text.Width + (int)Math.Ceiling(Px(padX * 2)),
            Math.Max((int)Math.Ceiling(Px(Primary ? 40 : 34)), text.Height + (int)Px(16)));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color fill, text;
        if (Primary)
        {
            fill = !Enabled ? Theme.PrimaryDisabled : _hover && !_pressed ? Theme.PrimaryHover : Theme.Primary;
            text = Color.White;
        }
        else
        {
            fill = !Enabled ? Theme.Chip : _pressed ? Theme.ChipPressed : _hover ? Theme.ChipHover : Theme.Chip;
            text = Enabled ? Theme.Ink : Color.FromArgb(170, 170, 170);
        }

        var bounds = new RectangleF(0, 0, Width - 0.5f, Height - 0.5f);
        var radius = Radius > 0 ? Px(Radius) : Height / 2f;
        Theme.FillRounded(g, fill, bounds, radius);
        if (Focused && ShowFocusCues)
        {
            using var path = Theme.RoundedRect(RectangleF.Inflate(bounds, -Px(1.5f), -Px(1.5f)), radius - Px(1.5f));
            using var pen = new Pen(Primary ? Color.FromArgb(150, 255, 255, 255) : Theme.Muted, Px(1.5f));
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

/// <summary>A check box drawn as an on/off switch.</summary>
internal sealed class ToggleSwitch : CheckBox
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private float _position; // 0 = off, 1 = on (animated)

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        AutoSize = true;
        Margin = new Padding(0, 4, 0, 4);
        _timer.Tick += (_, _) =>
        {
            var target = Checked ? 1f : 0f;
            _position += Math.Sign(target - _position) * 0.2f;
            if (Math.Abs(target - _position) < 0.2f)
            {
                _position = target;
                _timer.Stop();
            }
            Invalidate();
        };
    }

    private float Px(float value) => value * DeviceDpi / 96f;

    private SizeF TrackSize => new(Px(36), Px(20));

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        return new Size((int)Math.Ceiling(TrackSize.Width + Px(12)) + text.Width,
            (int)Math.Ceiling(Math.Max(TrackSize.Height, text.Height) + Px(6)));
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        if (IsHandleCreated && Visible)
            _timer.Start();
        else
            _position = Checked ? 1 : 0;
        base.OnCheckedChanged(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        _position = Checked ? 1 : 0;
        base.OnHandleCreated(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var size = TrackSize;
        var track = new RectangleF(Px(1), (Height - size.Height) / 2, size.Width, size.Height);
        var on = Enabled ? Theme.Primary : Theme.PrimaryDisabled;
        var off = Enabled ? Theme.Track : Color.FromArgb(230, 230, 230);
        Theme.FillRounded(g, Blend(off, on, _position), track, size.Height / 2);

        var inset = Px(2.5f);
        var knob = size.Height - inset * 2;
        var x = track.Left + inset + (size.Width - inset * 2 - knob) * _position;
        using (var white = new SolidBrush(Color.White))
            g.FillEllipse(white, x, track.Top + inset, knob, knob);

        var textLeft = (int)Math.Ceiling(track.Right + Px(11));
        TextRenderer.DrawText(g, Text, Font, new Rectangle(textLeft, 0, Width - textLeft, Height),
            Enabled ? Theme.Ink : Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues)
        {
            using var path = Theme.RoundedRect(RectangleF.Inflate(track, Px(2), Px(2)), size.Height / 2 + Px(2));
            using var pen = new Pen(Theme.Muted, Px(1));
            g.DrawPath(pen, path);
        }
    }

    private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>One line of text in two tones, like the site's "Speak, <i>don't type</i>" headings.</summary>
internal sealed class TwoToneLabel : Control
{
    private string _strong = "";
    private string _soft = "";

    public TwoToneLabel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        AutoSize = true;
        Margin = Padding.Empty;
    }

    public string Strong { get => _strong; set { _strong = value; Changed(); } }
    public string Soft { get => _soft; set { _soft = value; Changed(); } }
    public Color SoftColor { get; set; } = Theme.Muted;

    private void Changed()
    {
        if (AutoSize)
            Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Changed(); }

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var strong = TextRenderer.MeasureText(_strong, Font, Size.Empty, Flags);
        var soft = TextRenderer.MeasureText(_soft, Font, Size.Empty, Flags);
        return new Size(strong.Width + soft.Width + 2, Math.Max(strong.Height, soft.Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background);
        var strong = TextRenderer.MeasureText(e.Graphics, _strong, Font, Size.Empty, Flags);
        TextRenderer.DrawText(e.Graphics, _strong, Font, Point.Empty, ForeColor, Flags);
        TextRenderer.DrawText(e.Graphics, _soft, Font, new Point(strong.Width, 0), SoftColor, Flags);
    }
}

/// <summary>The FastFlow logo mark.</summary>
internal sealed class LogoMark : Control
{
    public LogoMark()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(30, 30);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background);
        Theme.DrawLogo(e.Graphics, new RectangleF(0, 0, Width - 1, Height - 1));
    }
}
