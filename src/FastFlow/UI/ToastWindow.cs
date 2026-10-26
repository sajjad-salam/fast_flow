using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Microsoft.Win32;
using static FastFlow.Native.NativeMethods;

namespace FastFlow.UI;

/// <summary>
/// A small rounded notification that slides up from the bottom of the screen, stays for a few
/// seconds and slides away again. It never takes focus; clicking it dismisses it early.
///
/// Drawn as a per-pixel-alpha layered window, so the rounded corners and the soft shadow are
/// real transparency rather than a rectangular window behind them.
/// </summary>
internal sealed class ToastWindow : Form
{
    private const int CardWidth = 360;
    private const int CardHeight = 72;
    private const int CornerRadius = 20;
    private const int ShadowSize = 10;
    private const int ScreenMargin = 20;
    private const int SlideDistance = 28;

    private static readonly TimeSpan InDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(3200);
    private static readonly TimeSpan OutDuration = TimeSpan.FromMilliseconds(240);

    private static ToastWindow? _current;

    private readonly string _title;
    private readonly string _message;
    private readonly Rectangle _workArea;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly Stopwatch _clock = new();
    private readonly TaskCompletionSource _closed = new();

    private IntPtr _memDc;
    private IntPtr _bitmap;
    private IntPtr _oldBitmap;
    private Size _size;
    private Point _restingPos;
    private TimeSpan? _leavingAt;

    private ToastWindow(string title, string message)
    {
        _title = title;
        _message = message;
        _workArea = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // Created on the target screen so DeviceDpi is that monitor's DPI.
        Bounds = new Rectangle(_workArea.Left + _workArea.Width / 2, _workArea.Bottom - 1, 1, 1);

        _timer.Tick += (_, _) => Animate();
        MouseClick += (_, _) => Dismiss();
    }

    /// <summary>Shows a toast (replacing one that is still visible); the task completes once it has gone.</summary>
    public static Task ShowAsync(string title, string message)
    {
        _current?.Close();
        var toast = new ToastWindow(title, message);
        _current = toast;
        toast.Show();
        return toast._closed.Task;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOPMOST | (int)WS_EX_TOOLWINDOW | (int)WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var scale = DeviceDpi / 96f;
        using (var image = Render(scale))
        {
            _size = image.Size;
            var screenDc = GetDC(IntPtr.Zero);
            _memDc = CreateCompatibleDC(screenDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            _bitmap = image.GetHbitmap(Color.FromArgb(0));
            _oldBitmap = SelectObject(_memDc, _bitmap);
        }

        // The card sits ScreenMargin above the bottom of the work area; the shadow hangs around it.
        var shadow = (int)Math.Round(ShadowSize * scale);
        _restingPos = new Point(
            _workArea.Left + (_workArea.Width - _size.Width) / 2,
            _workArea.Bottom - _size.Height + shadow - (int)Math.Round(ScreenMargin * scale));
        Present(SlideDistance * scale, 0);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PlayNotificationSound();
        _clock.Start();
        _timer.Start();
    }

    /// <summary>
    /// Plays the Windows notification sound (the one chosen in Sound settings), falling back to the
    /// stock one if the user's scheme has none. Asynchronous, so the slide-in isn't held up.
    /// </summary>
    private static void PlayNotificationSound()
    {
        if (PlaySound("Notification.Default", IntPtr.Zero, SND_ALIAS | SND_ASYNC | SND_NODEFAULT | SND_SYSTEM))
            return;
        var wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify System Generic.wav");
        PlaySound(wav, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT | SND_SYSTEM);
    }

    private void Dismiss()
    {
        if (_leavingAt != null)
            return;
        // Leave from wherever the slide-in got to, so an early click doesn't jump.
        var progress = Math.Min(1, _clock.Elapsed / InDuration);
        _leavingAt = _clock.Elapsed - OutDuration * (1 - progress);
    }

    private void Animate()
    {
        var elapsed = _clock.Elapsed;
        var scale = DeviceDpi / 96f;

        if (_leavingAt == null && elapsed >= InDuration + HoldDuration)
            _leavingAt = InDuration + HoldDuration;

        if (_leavingAt is { } leavingAt)
        {
            var t = Math.Min(1, (elapsed - leavingAt) / OutDuration);
            var eased = t * t * t; // ease-in
            Present(SlideDistance * scale * (float)eased, (float)(1 - eased));
            if (t >= 1)
                Close();
            return;
        }

        var p = Math.Min(1, elapsed / InDuration);
        var easedIn = 1 - Math.Pow(1 - p, 3); // ease-out
        Present(SlideDistance * scale * (float)(1 - easedIn), (float)easedIn);
    }

    private void Present(float offsetY, float opacity)
    {
        var dst = new POINT { X = _restingPos.X, Y = _restingPos.Y + (int)Math.Round(offsetY) };
        var size = new SIZE { Width = _size.Width, Height = _size.Height };
        var src = new POINT();
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            SourceConstantAlpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255),
            AlphaFormat = AC_SRC_ALPHA,
        };
        UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, _memDc, ref src, 0, ref blend, ULW_ALPHA);
    }

    private Bitmap Render(float scale)
    {
        var dark = IsDarkTheme();
        float Px(float v) => v * scale;

        var shadow = (int)Math.Round(Px(ShadowSize));
        var width = (int)Math.Round(Px(CardWidth)) + shadow * 2;
        var height = (int)Math.Round(Px(CardHeight)) + shadow * 2;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var card = new RectangleF(shadow, shadow, width - shadow * 2, height - shadow * 2);
        var radius = Px(CornerRadius);

        // Soft shadow: faint rounded rings that build up towards the card, nudged slightly down.
        using (var ring = new SolidBrush(Color.FromArgb(dark ? 9 : 5, 0, 0, 0)))
        {
            for (var i = shadow; i >= 1; i--)
            {
                var r = card;
                r.Inflate(i, i);
                r.Offset(0, Px(2));
                using var path = Theme.RoundedRect(r, radius + i);
                g.FillPath(ring, path);
            }
        }

        using (var path = Theme.RoundedRect(card, radius))
        {
            using var fill = new SolidBrush(dark ? Color.FromArgb(32, 30, 30) : Color.White);
            using var border = new Pen(dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0), Math.Max(1, Px(1)));
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        // Check mark badge, in the near-black of FastFlow's buttons.
        var badgeSize = Px(32);
        var badge = new RectangleF(card.Left + Px(18), card.Top + (card.Height - badgeSize) / 2, badgeSize, badgeSize);
        using (var fill = new SolidBrush(dark ? Color.White : Theme.Primary))
            g.FillEllipse(fill, badge);
        using (var tick = new Pen(dark ? Theme.Primary : Color.White, Px(2.4f))
               { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            g.DrawLines(tick, new[]
            {
                new PointF(badge.Left + badge.Width * 0.29f, badge.Top + badge.Height * 0.52f),
                new PointF(badge.Left + badge.Width * 0.44f, badge.Top + badge.Height * 0.66f),
                new PointF(badge.Left + badge.Width * 0.72f, badge.Top + badge.Height * 0.37f),
            });
        }

        using var titleFont = new Font(Theme.BodyMedium.FontFamily, Px(14), GraphicsUnit.Pixel);
        using var messageFont = new Font(Theme.Body.FontFamily, Px(12.5f), GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };

        var textLeft = badge.Right + Px(14);
        var textWidth = card.Right - Px(18) - textLeft;
        var titleHeight = titleFont.GetHeight(g);
        var messageHeight = messageFont.GetHeight(g);
        var gap = Px(1);
        var top = card.Top + (card.Height - titleHeight - gap - messageHeight) / 2;

        using (var brush = new SolidBrush(dark ? Color.White : Theme.Ink))
            g.DrawString(_title, titleFont, brush, new RectangleF(textLeft, top, textWidth, titleHeight), format);
        using (var brush = new SolidBrush(dark ? Color.FromArgb(170, 170, 170) : Theme.Muted))
            g.DrawString(_message, messageFont, brush,
                new RectangleF(textLeft, top + titleHeight + gap, textWidth, messageHeight), format);

        return bitmap;
    }

    /// <summary>Follows the Windows (taskbar / notification) colour mode.</summary>
    private static bool IsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        if (_current == this)
            _current = null;
        base.OnFormClosed(e);
        _closed.TrySetResult();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        if (_memDc != IntPtr.Zero)
        {
            SelectObject(_memDc, _oldBitmap);
            DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
        if (_bitmap != IntPtr.Zero)
        {
            DeleteObject(_bitmap);
            _bitmap = IntPtr.Zero;
        }
        base.Dispose(disposing);
    }
}
