namespace FastFlow.UI;

/// <summary>
/// A keyboard-first popup, opened from anywhere with the quick switcher shortcut: type a few letters of a
/// workflow (or an action), press Enter. The workflow you came from is listed first, so the shortcut followed
/// by Enter switches back to it. Closes on Esc, on a second press of the shortcut, or when it loses focus.
/// </summary>
internal sealed class QuickSwitcher : Form
{
    /// <summary>One row: <paramref name="Badge"/> is shown as a key cap on the right (e.g. the workflow's shortcut).</summary>
    public sealed record Item(string Title, string Detail, string Badge, Action Run);

    private const int PanelWidth = 560;
    private const int RowHeight = 56;
    private const int MaxRows = 8;
    private const int Gutter = 14;

    private static QuickSwitcher? _open;

    private readonly IReadOnlyList<Item> _items;
    private readonly TextBox _search = new() { Font = Theme.Heading, PlaceholderText = "Type a workflow name..." };
    private readonly FieldBox _field;
    private readonly ListBox _list = new()
    {
        DrawMode = DrawMode.OwnerDrawFixed, BorderStyle = BorderStyle.None, IntegralHeight = false,
        BackColor = Theme.Background, TabStop = false,
    };
    private readonly Label _empty = new()
    {
        Text = "Nothing matches – press Esc to close.", ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(16, 0, 0, 0), Visible = false,
    };
    private readonly Label _footer = new()
    {
        Text = "↑ ↓  choose   ·   Enter  run   ·   Esc  close", ForeColor = Theme.Muted, Font = Theme.Small,
        TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0),
    };
    private readonly Rectangle _workArea;
    private bool _done;

    /// <summary>Opens the switcher, or closes it if it is already open (pressing the shortcut again).</summary>
    public static void Toggle(IReadOnlyList<Item> items)
    {
        if (_open is { IsDisposed: false })
        {
            _open.Close();
            return;
        }
        _open = new QuickSwitcher(items);
        _open.Show();
        _open.Activate();
        _open._search.Focus();
    }

    private QuickSwitcher(IReadOnlyList<Item> items)
    {
        _items = items;
        Theme.Apply(this);
        Text = "FastFlow";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;

        // Open on the monitor with the mouse, so the window is created at that monitor's DPI.
        _workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(_workArea.Left + _workArea.Width / 4, _workArea.Top + _workArea.Height / 5);
        Size = new Size(PanelWidth, 200);

        _field = new FieldBox(_search, PanelWidth - 2 * Gutter);
        _search.TextChanged += (_, _) => Filter();
        _list.DrawItem += DrawItem;
        _list.MouseMove += (_, e) =>
        {
            var index = _list.IndexFromPoint(e.Location);
            if (index >= 0 && index != _list.SelectedIndex)
                _list.SelectedIndex = index;
        };
        _list.MouseClick += (_, _) => RunSelected();
        _list.SelectedIndexChanged += (_, _) => _list.Invalidate();
        Controls.AddRange(new Control[] { _field, _list, _empty, _footer });

        Load += (_, _) => Filter();
        DpiChanged += (_, _) => BeginInvoke(LayoutPanel);
        Deactivate += (_, _) => { if (!_done) Close(); };
        FormClosed += (_, _) => { if (_open == this) _open = null; };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x20000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                Close();
                return true;
            case Keys.Enter:
                RunSelected();
                return true;
            case Keys.Down:
            case Keys.Tab:
                MoveSelection(+1);
                return true;
            case Keys.Up:
            case Keys.Shift | Keys.Tab:
                MoveSelection(-1);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(222, 222, 222));
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private void MoveSelection(int step)
    {
        if (_list.Items.Count == 0)
            return;
        _list.SelectedIndex = (_list.SelectedIndex + step + _list.Items.Count) % _list.Items.Count;
    }

    private void RunSelected()
    {
        if (_list.SelectedItem is not Item item)
            return;
        _done = true;
        Close();
        item.Run();
    }

    private void Filter()
    {
        var query = _search.Text.Trim();
        var matches = query.Length == 0
            ? _items
            : _items.Select(item => (Item: item, Score: Score(item.Title, query)))
                .Where(m => m.Score > 0)
                .OrderByDescending(m => m.Score) // stable: equal matches keep their order (most recent first)
                .Select(m => m.Item)
                .ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Items.AddRange(matches.Cast<object>().ToArray());
        if (_list.Items.Count > 0)
            _list.SelectedIndex = 0;
        _list.EndUpdate();
        LayoutPanel();
    }

    /// <summary>How well <paramref name="query"/> matches a title: prefix, word start, anywhere, or letters in order.</summary>
    private static int Score(string title, string query)
    {
        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 4;
        var words = title.Split(new[] { ' ', '-', '_', '.', '(', '“' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
            return 3;
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 2;

        var position = 0;
        foreach (var c in query)
        {
            if (char.IsWhiteSpace(c))
                continue;
            position = title.IndexOf(c.ToString(), position, StringComparison.OrdinalIgnoreCase);
            if (position < 0)
                return 0;
            position++;
        }
        return 1;
    }

    /// <summary>Lays the panel out in device pixels and keeps it centred, growing downwards as rows are added.</summary>
    private void LayoutPanel()
    {
        int Px(int value) => LogicalToDeviceUnits(value);
        var width = Px(PanelWidth);
        var gutter = Px(Gutter);
        _list.ItemHeight = Math.Min(255, Px(RowHeight));

        _field.SetBounds(gutter, gutter, width - 2 * gutter, _field.Height);
        var rows = Math.Clamp(_list.Items.Count, 1, MaxRows);
        var listBounds = new Rectangle(gutter - Px(4), _field.Bottom + Px(10), width - 2 * gutter + Px(8), rows * _list.ItemHeight);
        _list.Bounds = listBounds;
        _empty.Bounds = listBounds;
        _list.Visible = _list.Items.Count > 0;
        _empty.Visible = !_list.Visible;
        _footer.SetBounds(gutter, listBounds.Bottom + Px(6), width - 2 * gutter, Px(26));
        ClientSize = new Size(width, _footer.Bottom + Px(10));

        Location = new Point(_workArea.Left + (_workArea.Width - Width) / 2, _workArea.Top + _workArea.Height / 5);
        Invalidate();
    }

    private void DrawItem(object? sender, DrawItemEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(_list.BackColor))
            g.FillRectangle(back, e.Bounds);
        if (e.Index < 0 || e.Index >= _list.Items.Count || _list.Items[e.Index] is not Item item)
            return;

        int Px(int value) => LogicalToDeviceUnits(value);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var row = Rectangle.Inflate(e.Bounds, -Px(2), -Px(2));
        if (e.Index == _list.SelectedIndex)
            Theme.FillRounded(g, Theme.Card, row, Px(14));

        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
                                      | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        var left = row.Left + Px(14);
        var right = row.Right - Px(12);

        if (item.Badge.Length > 0)
        {
            var size = TextRenderer.MeasureText(g, item.Badge, Theme.SmallMedium, Size.Empty, TextFormatFlags.NoPadding);
            var cap = new Rectangle(right - size.Width - Px(16), row.Top + (row.Height - Px(24)) / 2, size.Width + Px(16), Px(24));
            Theme.FillRounded(g, Theme.Chip, cap, Px(7));
            TextRenderer.DrawText(g, item.Badge, Theme.SmallMedium, cap, Theme.Ink,
                flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            right = cap.Left - Px(10);
        }

        var titleHeight = TextRenderer.MeasureText(g, "Ag", Theme.BodyMedium, Size.Empty, flags).Height;
        var detailHeight = TextRenderer.MeasureText(g, "Ag", Theme.Small, Size.Empty, flags).Height;
        var top = row.Top + (row.Height - titleHeight - Px(3) - detailHeight) / 2;
        TextRenderer.DrawText(g, item.Title, Theme.BodyMedium, new Rectangle(left, top, right - left, titleHeight), Theme.Ink, flags);
        TextRenderer.DrawText(g, item.Detail, Theme.Small,
            new Rectangle(left, top + titleHeight + Px(3), right - left, detailHeight), Theme.Muted, flags);
    }
}
