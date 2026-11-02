namespace OnbodyMic.Desktop;

using System.Drawing;
using OnbodyMic.Media;

/// <summary>
/// One soundboard row (docs/09 §D1): ★ favorite, name, duration, group, live
/// playback state, hotkey cell, then ▶ preview / 发送 (air amber) / ⋯ menu.
/// 52 DIP tall, top hairline separator, whole-row click selects, double-click
/// sends — the row is a real control tree (not an owner-drawn list) so every
/// cell keeps native focus, tooltips and accessibility.
/// </summary>
internal sealed class ClipRow : TableLayoutPanel
{
    private readonly Label _star;
    private readonly Label _name;
    private readonly Label _state;
    private readonly Button _hotkeyButton;
    private bool _selected;

    public ClipRow(
        ClipItem item,
        string group,
        string? hotkey,
        bool available,
        Action<ClipRow> select,
        Action<ClipRow> toggleFavorite,
        Action<ClipRow> preview,
        Action<ClipRow> send,
        Action<ClipRow> editHotkey,
        Action<ClipRow, Point> menu)
    {
        Item = item;
        Dock = DockStyle.Top;
        Height = Theme.D(Theme.ClipRowHeight);
        Margin = new Padding(0);
        BackColor = Theme.CardBack;
        ColumnCount = 9;
        RowCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.D(34)));   // star
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));           // name
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.D(62)));   // duration
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.D(96)));   // group
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.D(96)));   // state
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));                // hotkey
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.D(38)));   // preview
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));                // send
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.D(38)));   // more
        RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Paint += DrawSeparator;

        _star = new Label
        {
            Text = item.Favorite ? "★" : "☆",
            Font = Theme.CardTitle,
            ForeColor = item.Favorite ? Theme.Warning : Theme.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            Margin = new Padding(0),
        };
        _star.Click += (_, _) => { select(this); toggleFavorite(this); };

        _name = new Label
        {
            Text = item.DisplayName,
            Font = Theme.BodyBold,
            ForeColor = available ? Theme.TextMain : Theme.StatusFault,
            AutoEllipsis = true,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        };

        Label Cell(string text, ContentAlignment align) => new()
        {
            Text = text,
            Font = Theme.NoteFont,
            ForeColor = Theme.TextSecondary,
            AutoEllipsis = true,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = align,
            Margin = new Padding(0),
        };

        _state = Cell("", ContentAlignment.MiddleLeft);
        _state.Font = Theme.NoteFont;

        _hotkeyButton = SmallButton(string.IsNullOrEmpty(hotkey) ? "设置热键" : hotkey!);
        _hotkeyButton.Click += (_, _) => { select(this); editHotkey(this); };

        var previewButton = SmallButton("▶");
        previewButton.Enabled = available;
        previewButton.AccessibleName = $"本地试听 {item.DisplayName}";
        previewButton.Width = Theme.D(34);
        previewButton.Click += (_, _) => { select(this); preview(this); };

        var sendButton = Theme.AirButton("发送");
        sendButton.Enabled = available;
        sendButton.Font = Theme.Body;
        sendButton.MinimumSize = Theme.DS(64, 28);
        sendButton.Padding = Theme.DP(10, 2, 10, 2);
        sendButton.Margin = Theme.DP(0, 0, Theme.GapSmall, 0);
        sendButton.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        sendButton.Click += (_, _) => { select(this); send(this); };

        var moreButton = SmallButton("⋯");
        moreButton.Width = Theme.D(34);
        moreButton.Click += (_, _) => { select(this); menu(this, moreButton.PointToScreen(new Point(0, moreButton.Height))); };

        Controls.Add(_star, 0, 0);
        Controls.Add(_name, 1, 0);
        Controls.Add(Cell(available ? FormatDuration(item.DurationMs) : "—", ContentAlignment.MiddleRight), 2, 0);
        Controls.Add(Cell(group.Length == 0 ? "—" : group, ContentAlignment.MiddleLeft), 3, 0);
        Controls.Add(_state, 4, 0);
        Controls.Add(_hotkeyButton, 5, 0);
        Controls.Add(previewButton, 6, 0);
        Controls.Add(sendButton, 7, 0);
        Controls.Add(moreButton, 8, 0);

        Click += (_, _) => select(this);
        foreach (var cell in Controls.OfType<Label>().Where(cell => cell != _star))
        {
            cell.Click += (_, _) => select(this);
            cell.DoubleClick += (_, _) => { select(this); if (available) send(this); };
        }
        _star.MouseEnter += (_, _) => _star.ForeColor = item.Favorite ? Theme.Warning : Theme.Accent;
        _star.MouseLeave += (_, _) => _star.ForeColor = item.Favorite ? Theme.Warning : Theme.TextSecondary;
        DoubleClick += (_, _) => { select(this); if (available) send(this); };

        if (!available)
        {
            _state.Text = "素材缺失";
            _state.ForeColor = Theme.StatusFault;
        }
    }

    private static readonly Pen SeparatorPen = new(Theme.Border);

    public ClipItem Item { get; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
            {
                return;
            }

            _selected = value;
            BackColor = _selected ? Theme.IconBack : Theme.CardBack;
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Hotkey
    {
        set => _hotkeyButton.Text = string.IsNullOrEmpty(value) ? "设置热键" : value;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Favorite
    {
        set
        {
            _star.Text = value ? "★" : "☆";
            _star.ForeColor = value ? Theme.Warning : Theme.TextSecondary;
        }
    }

    /// <summary>Playback state cell: empty / ▶ 发送中 (green) / ▶ 试听中 (amber) / 素材缺失 (red).</summary>
    public void SetPlaying(bool sending, bool previewing, bool missing)
    {
        if (missing)
        {
            _state.Text = "素材缺失";
            _state.ForeColor = Theme.StatusFault;
        }
        else if (sending)
        {
            _state.Text = "▶ 发送中";
            _state.ForeColor = Theme.StatusSending;
        }
        else if (previewing)
        {
            _state.Text = "▶ 试听中";
            _state.ForeColor = Theme.StatusWarn;
        }
        else
        {
            _state.Text = string.Empty;
        }
    }

    private void DrawSeparator(object? sender, PaintEventArgs e) =>
        e.Graphics.DrawLine(SeparatorPen, 0, 0, Width, 0);

    /// <summary>Compact 28 DIP button for in-row actions; hand cursor, no min width.</summary>
    private static Button SmallButton(string text)
    {
        var button = Theme.SecondaryButton(text);
        button.Font = Theme.NoteFont;
        button.MinimumSize = Theme.DS(0, 28);
        button.Padding = Theme.DP(8, 2, 8, 2);
        button.Margin = Theme.DP(0, 0, Theme.GapSmall, 0);
        button.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        return button;
    }

    private static string FormatDuration(int durationMs) => $"{durationMs / 1000.0:0.0} 秒";
}
