namespace OnbodyMic.Desktop;

using System.Drawing;

/// <summary>
/// One audio route card (§8.1): identical skeleton for every path — title with
/// a state chip, a one-line description (device / app / playing clip), then
/// parameter rows of "label → control". Only the rows differ per path, so the
/// four cards read the same way and no path's parameters leak into another.
///
/// Every metric is DPI-scaled through <see cref="Theme"/>; the description
/// ellipsizes instead of clipping and carries the full text as a tooltip
/// (§14: long device names must not break the layout).
/// </summary>
internal sealed class RouteCard : TableLayoutPanel
{
    private readonly Label _status;
    private readonly Label _description;
    private readonly ToolTip _tips;

    public RouteCard(string title, string description, ToolTip tips)
    {
        _tips = tips;
        ColumnCount = 2;
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.D(Theme.LabelColumnWidth)));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        BackColor = Theme.CardBack;
        Padding = Theme.DP(Theme.CardPadding);
        Margin = new Padding(0);
        Dock = DockStyle.Fill;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Paint += Theme.CardBorder;

        var header = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Margin = Theme.DP(0, 0, 0, Theme.GapSmall),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var titleLabel = new Label
        {
            Text = title,
            Font = Theme.CardTitle,
            ForeColor = Theme.TextMain,
            AutoSize = true,
            Margin = new Padding(0),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _status = new Label
        {
            Text = "关",
            Font = Theme.Body,
            ForeColor = Theme.TextSecondary,
            BackColor = Theme.SecondaryBack,
            AutoSize = true,
            Padding = Theme.DP(8, 2, 8, 2),
            Margin = new Padding(0),
        };
        header.Controls.Add(titleLabel, 0, 0);
        header.Controls.Add(_status, 1, 0);

        _description = Theme.Ellipsis(description);
        _description.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        _description.Margin = Theme.DP(0, 0, 0, Theme.GapSmall);
        _tips.SetToolTip(_description, description);

        AddRow(header, span: true);
        AddRow(_description, span: true);
    }

    /// <summary>State chip driven only by a polled session snapshot (§7.4).</summary>
    public void SetStatus(string text, Color color)
    {
        if (_status.Text != text)
        {
            _status.Text = text;
        }

        if (_status.ForeColor != color)
        {
            _status.ForeColor = color;
        }
    }

    public void SetDescription(string text)
    {
        if (_description.Text == text)
        {
            return;
        }

        _description.Text = text;
        _tips.SetToolTip(_description, text);
    }

    /// <summary>Adds a "label → control" row; the control fills the value column.</summary>
    public void AddRow(string label, Control control) => AddRow(label, [control]);

    public void AddRow(string label, IReadOnlyList<Control> controls)
    {
        // Fill + centred text: with a fixed row height an AutoSize label anchors
        // to the row top and drifts away from the control beside it.
        var labelControl = new Label
        {
            Text = label,
            Font = Theme.Body,
            ForeColor = Theme.TextSecondary,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        };

        // No wrapping: an AutoSize wrapping flow reports a two-line preferred
        // height while being measured, which doubles every row's height. Rows
        // are short by construction; the layout audit catches any row that
        // would exceed the card width.
        var host = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            WrapContents = false,
            Margin = new Padding(0),
            FlowDirection = FlowDirection.LeftToRight,
        };
        foreach (var control in controls)
        {
            // FlowLayoutPanel top-aligns children; centre short controls
            // (checkbox, numeric box) against the label's line instead.
            var slack = Math.Max(0, Theme.D(Theme.ControlHeight) - control.PreferredSize.Height) / 2;
            control.Margin = Theme.DP(0, slack, Theme.GapSmall, 0);
            host.Controls.Add(control);
        }

        AddRow(labelControl, host);
    }

    /// <summary>Adds an arbitrary control pair (label may be a whole row control when span is true).</summary>
    private void AddRow(Control left, Control right)
    {
        var row = RowCount;
        RowCount = row + 1;

        // Fixed row height, not AutoSize: a checkbox row measured 23 px while a
        // numeric row measured 27 px, so rows visibly drifted. Every parameter
        // row now has the same height and centres its content.
        RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.D(Theme.ControlHeight + Theme.GapSmall)));
        left.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        right.Anchor = AnchorStyles.Left;
        Controls.Add(left, 0, row);
        Controls.Add(right, 1, row);
        AppendSlackRow();
    }

    /// <summary>
    /// Keeps a 100%-height row as the last row: a card stretched to match its
    /// taller neighbour collects the extra height there instead of spreading it
    /// between parameter rows. Re-appended after every AddRow because rows are
    /// appended in order and the slack must stay last.
    /// </summary>
    private void AppendSlackRow()
    {
        if (RowStyles.Count > 0 && RowStyles[^1].SizeType == SizeType.Percent)
        {
            RowStyles.RemoveAt(RowStyles.Count - 1);
            RowCount--;
        }

        RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        RowCount++;
    }

    private void AddRow(Control full, bool span)
    {
        var row = RowCount;
        RowCount = row + 1;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(full, 0, row);
        if (span)
        {
            SetColumnSpan(full, 2);
        }
    }
}
