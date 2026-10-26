namespace OnbodyMic.Desktop;

using System.ComponentModel;
using System.Diagnostics;
using OnbodyMic.Application;
using OnbodyMic.Audio;
using OnbodyMic.Domain;
using OnbodyMic.Media;

/// <summary>
/// Main window (docs/随声麦_UI重设计方案.md §3): app bar with preset
/// management and the emergency mute, a live status summary, one unified card
/// per audio route (§8), a compact sound library (§9), a hotkey digest with
/// its editor entry (§10) and a low-weight status strip (§12).
///
/// Layout rules that keep this honest at every size:
/// - the emergency button owns its own auto-sized column, so a narrow window
///   wraps the config cluster instead of clipping the emergency action;
/// - the content stack is Dock=Top inside an AutoScroll viewport, so its width
///   always equals the viewport (no horizontal overflow) and height scrolls;
/// - every pixel metric and font goes through <see cref="Theme"/>, which scales
///   both by the same DPI factor (point fonts used to scale while paddings did
///   not, which is what clipped controls at 125%);
/// - route cards collapse to one column below <see cref="TwoColumnMinDip"/>.
///
/// State rules (§7.4): labels and checkboxes are written only from the polled
/// <see cref="SessionStatus"/>; a command is shown as failed when the engine
/// rejects it *or* when the read-back disagrees with the request.
/// </summary>
internal sealed class MainForm : Form
{
    private const float GainStep = 0.05f;
    private const int TwoColumnMinDip = 720;

    private readonly AppShell _shell;
    private readonly Action _onDirty;
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    // §6 app bar
    private Label _titleLabel = null!;
    private ComboBox _presetCombo = null!;
    private Button _applyButton = null!;
    private Button _presetMenuButton = null!;
    private Button _saveButton = null!;
    private Button _settingsButton = null!;
    private Button _emergencyButton = null!;
    private Label _dirtyLabel = null!;
    private ContextMenuStrip _presetMenu = null!;

    // §7 status summary
    private Label _phaseChip = null!;
    private Label _micChip = null!;
    private Label _extrasChip = null!;
    private Label _appChip = null!;
    private Label _monitorChip = null!;
    private Label _clipChip = null!;
    private Label _issueLabel = null!;

    // §8 route cards
    private TableLayoutPanel _routeGrid = null!;
    private RouteCard _micCard = null!;
    private RouteCard _extrasCard = null!;
    private RouteCard _monitorCard = null!;
    private RouteCard _clipCard = null!;
    private CheckBox _micCheck = null!;
    private NumericUpDown _micGain = null!;
    private CheckBox _extrasCheck = null!;
    private CheckBox _appCheck = null!;
    private NumericUpDown _appGain = null!;
    private Button _bindAppButton = null!;
    private ComboBox _monitorCombo = null!;
    private TrackBar _monitorVolume = null!;
    private NumericUpDown _monitorGain = null!;
    private Button _stopButton = null!;
    private NumericUpDown _clipGain = null!;
    private bool _twoColumnRoutes = true;

    // §9 sound library
    private TextBox _searchBox = null!;
    private CheckBox _favoriteFilter = null!;
    private Button _importButton = null!;
    private ListView _clipsList = null!;
    private Label _clipsEmptyLabel = null!;
    private Button _playButton = null!;
    private Button _previewButton = null!;
    private Button _favoriteButton = null!;
    private Button _moreButton = null!;
    private ContextMenuStrip _clipMenu = null!;

    // §10 hotkeys
    private Label _hotkeySummary = null!;
    private Button _hotkeyPanelButton = null!;

    // §12 status strip
    private StatusStrip _status = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private ToolStripStatusLabel _metricsLabel = null!;

    private Panel _content = null!;
    private TableLayoutPanel _stack = null!;
    private TableLayoutPanel _topBar = null!;
    private FlowLayoutPanel _topCluster = null!;

    private bool _refreshing;
    private bool _importing;
    private bool _fitQueued;
    private string? _lastClipKey;

    public MainForm(AppShell shell, Action onDirty)
    {
        _shell = shell;
        _onDirty = onDirty;

        // DPI must be known before any control metric is computed: runtime
        // controls never pass through WinForms' auto-scaling, so Theme owns it.
        Theme.InitializeFor(this);
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.Body;
        BackColor = Theme.WindowBack;
        Text = "随声麦 Onbody Mic";

        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, Theme.D(1280), Theme.D(800));
        var maxWidth = Math.Max(Theme.D(560), work.Width * 9 / 10);
        var maxHeight = Math.Max(Theme.D(420), work.Height * 9 / 10);
        // 760×640 DIP is the design minimum (§4.1); the audit below proves the
        // layout still holds at 760×560, so this is not hiding a layout defect.
        MinimumSize = new Size(Math.Min(Theme.D(760), maxWidth), Math.Min(Theme.D(640), maxHeight));
        Size = new Size(Math.Min(Theme.D(1000), maxWidth), Math.Min(Theme.D(820), maxHeight));

        CreateControls();
        BuildLayout();
        WireEvents();

        RefreshPresets();
        RefreshClips();
        RefreshHotkeys();
        RefreshStatus();

        _shell.Devices.Refreshed += OnDevicesRefreshed;
        _shell.Devices.RefreshAsync();

        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
    }

    public bool Dirty { get; private set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal TrayContext? Tray { get; set; }

    // ---------------------------------------------------------------- layout

    private void CreateControls()
    {
        _titleLabel = new Label
        {
            Text = "随声麦",
            AutoSize = true,
            Font = Theme.AppTitle,
            ForeColor = Theme.TextMain,
            Margin = Theme.DP(0, 0, Theme.GapMedium, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _presetCombo = Theme.DropDown();
        _presetCombo.Width = Theme.D(190);

        _applyButton = Theme.PrimaryButton("应用预设");
        _presetMenuButton = Theme.SecondaryButton("配置管理 ▾");
        _saveButton = Theme.SecondaryButton("保存");
        _settingsButton = Theme.SecondaryButton("设备设置…");
        _emergencyButton = Theme.DangerButton("全部静音");
        _presetMenu = new ContextMenuStrip { Font = Theme.Body };

        _dirtyLabel = new Label
        {
            Text = "",
            AutoSize = true,
            ForeColor = Theme.Warning,
            Font = Theme.Body,
            Margin = Theme.DP(0, Theme.GapSmall, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _phaseChip = Theme.StatusChip("就绪", Theme.TextSecondary);
        _micChip = Theme.StatusChip("麦克风 关", Theme.TextSecondary);
        _extrasChip = Theme.StatusChip("附加音频 关", Theme.TextSecondary);
        _appChip = Theme.StatusChip("应用音频 关", Theme.TextSecondary);
        _monitorChip = Theme.StatusChip("监听 关", Theme.TextSecondary);
        _clipChip = Theme.StatusChip("音效 空闲", Theme.TextSecondary);
        _issueLabel = new Label
        {
            Text = "",
            AutoSize = true,
            MaximumSize = new Size(Theme.D(620), 0),
            ForeColor = Theme.Warning,
            Font = Theme.Body,
            Margin = Theme.DP(0, 2, 0, 2),
        };

        _micCheck = new CheckBox { Text = "发送到队友", AutoSize = true, Font = Theme.Body, Margin = new Padding(0) };
        _micGain = Theme.Numeric(0m, 4m, (decimal)GainStep, 2);

        _extrasCheck = new CheckBox { Text = "发送到队友", AutoSize = true, Font = Theme.Body, Margin = new Padding(0) };
        _appCheck = new CheckBox { Text = "捕获指定应用", AutoSize = true, Font = Theme.Body, Margin = new Padding(0) };
        _appGain = Theme.Numeric(0m, 4m, (decimal)GainStep, 2);
        _bindAppButton = Theme.SecondaryButton("绑定应用…");

        _monitorCombo = Theme.DropDown();
        _monitorCombo.Width = Theme.D(190);
        _monitorCombo.Items.AddRange(["监听关闭", "仅音效（试听用）", "实际虚拟麦（队友听到的）"]);
        _monitorCombo.SelectedIndex = 0;

        _monitorVolume = new TrackBar
        {
            Minimum = 0,
            Maximum = 400,
            TickFrequency = 50,
            SmallChange = 5,
            LargeChange = 25,
            TickStyle = TickStyle.None,
            Width = Theme.D(180),
            Margin = new Padding(0),
        };
        _monitorGain = Theme.Numeric(0m, 4m, (decimal)GainStep, 2);

        _stopButton = Theme.SecondaryButton("停止音效");
        _clipGain = Theme.Numeric(0m, 4m, (decimal)GainStep, 2);

        _searchBox = new TextBox
        {
            Width = Theme.D(200),
            Font = Theme.Body,
            Margin = new Padding(0),
            PlaceholderText = "搜索音效名称…",
        };
        _favoriteFilter = new CheckBox { Text = "只看收藏", AutoSize = true, Font = Theme.Body, Margin = new Padding(0) };
        _importButton = Theme.SecondaryButton("导入音效…");

        _clipsList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Font = Theme.Body,
        };
        _clipsEmptyLabel = new Label
        {
            Text = "尚无音效，导入音频文件以开始使用",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.TextSecondary,
            Font = Theme.Body,
        };

        _playButton = Theme.PrimaryButton("向队友播放");
        _previewButton = Theme.SecondaryButton("本地试听");
        _favoriteButton = Theme.SecondaryButton("收藏/取消");
        _moreButton = Theme.SecondaryButton("更多 ▾");
        _clipMenu = new ContextMenuStrip { Font = Theme.Body };

        _hotkeySummary = Theme.Ellipsis();
        _hotkeySummary.ForeColor = Theme.TextSecondary;
        _hotkeyPanelButton = Theme.SecondaryButton("设置热键…");

        _status = new StatusStrip { SizingGrip = false, Font = Theme.Body };
        _statusLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _metricsLabel = new ToolStripStatusLabel { AutoSize = true, TextAlign = ContentAlignment.MiddleRight };
    }

    private void BuildLayout()
    {
        BuildPresetMenu();
        BuildClipMenu();

        _content = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.WindowBack,
            Padding = Theme.DP(Theme.WindowMargin),
        };

        // Dock=Top makes the stack exactly as wide as the viewport: nothing can
        // overflow horizontally, and tall content scrolls vertically instead.
        _stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        _stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        AddStackRow(BuildTopBar());
        AddStackRow(BuildStatusSummary());
        AddStackRow(BuildRouteSection());
        AddStackRow(BuildSoundLibrary());
        AddStackRow(BuildHotkeyDigest());

        _content.Controls.Add(_stack);

        _status.Items.Add(_statusLabel);
        _status.Items.Add(_metricsLabel);
        Controls.Add(_status);
        Controls.Add(_content);

        _tips.SetToolTip(_emergencyButton, "立即关闭全部发送（真麦/附加音频/应用/音效）并停止本地监听；锁存后只能手动解除，解除后保持全关。");
        _tips.SetToolTip(_presetCombo, "当前配置预设（切换会先安全关闭全部发送与监听）");
        _tips.SetToolTip(_searchBox, "按名称搜索音效");
        _tips.SetToolTip(_favoriteFilter, "只显示已收藏的音效");
        _tips.SetToolTip(_importButton, "导入 wav/mp3 到受管音效库（原文件不改动，上限 60 秒）");
        _tips.SetToolTip(_monitorCombo, "本地监听模式：关闭 / 仅试听音效 / 实际虚拟麦（队友听到的内容）");
        _tips.SetToolTip(_bindAppButton, "绑定一个正在运行的应用；捕获需先开启「发送到队友」");
    }

    private void AddStackRow(Control control)
    {
        var row = _stack.RowCount;
        _stack.RowCount = row + 1;
        _stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Dock=Top (not Fill): inside an AutoSize row, Fill makes the row height
        // and the control's preferred height depend on each other, which
        // WinForms resolves by stretching the row — that is the 294 px empty
        // app bar. Top fills the cell width, keeps the preferred height.
        control.Dock = DockStyle.Top;
        _stack.Controls.Add(control, 0, row);
    }

    private Control BuildTopBar()
    {
        // Three fixed columns: title | config cluster | emergency. The cluster
        // never wraps — a wrapping flow inside an AutoSize table gets measured
        // at a near-zero width and inflates the row (7 rows tall for one line).
        // Narrow windows hide the low-frequency buttons instead, and those
        // actions stay reachable from the 配置管理 menu (§6.1).
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, Theme.D(Theme.GapMedium)),
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _titleLabel.Margin = Theme.DP(0, 0, Theme.GapMedium, 0);
        _titleLabel.Anchor = AnchorStyles.Left;

        var cluster = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        _topCluster = cluster;

        _presetCombo.Margin = Theme.DP(0, 0, Theme.GapSmall, 0);
        _applyButton.Margin = Theme.DP(0, 0, Theme.GapSmall, 0);
        _presetMenuButton.Margin = Theme.DP(0, 0, Theme.GapSmall, 0);
        _saveButton.Margin = Theme.DP(0, 0, Theme.GapSmall, 0);
        _settingsButton.Margin = Theme.DP(0, 0, Theme.GapSmall, 0);
        _dirtyLabel.Margin = Theme.DP(0, 0, Theme.GapSmall, 0);
        _dirtyLabel.Anchor = AnchorStyles.Left;
        cluster.Controls.AddRange([_presetCombo, _applyButton, _presetMenuButton, _saveButton, _settingsButton, _dirtyLabel]);

        // The emergency action owns its own column: narrowing the window can
        // never push it out of the window (§6.1).
        _emergencyButton.Margin = Theme.DP(Theme.GapMedium, 0, 0, 0);
        _emergencyButton.Anchor = AnchorStyles.Right;

        _topBar = bar;
        bar.Controls.Add(_titleLabel, 0, 0);
        bar.Controls.Add(cluster, 1, 0);
        bar.Controls.Add(_emergencyButton, 2, 0);
        return bar;
    }

    private Control BuildStatusSummary()
    {
        var chips = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, Theme.D(Theme.GapMedium)),
        };
        chips.Controls.AddRange([_phaseChip, _micChip, _extrasChip, _appChip, _monitorChip, _clipChip, _issueLabel]);
        return chips;
    }

    private Control BuildRouteSection()
    {
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = new Padding(0, 0, 0, Theme.D(Theme.SectionSpacing)),
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = Theme.SectionTitle("音频路由");
        title.Dock = DockStyle.Fill;
        title.Margin = Theme.DP(0, 0, 0, Theme.GapSmall);
        host.Controls.Add(title, 0, 0);

        _routeGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = new Padding(0),
        };
        _routeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        _routeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

        _micCard = new RouteCard("真实麦克风", "输入：未配置", _tips);
        _micCard.AddRow("发送", [_micCheck]);
        _micCard.AddRow("输入增益", [_micGain]);

        _extrasCard = new RouteCard("附加音频", "未绑定应用（附加音频=音效）", _tips);
        _extrasCard.AddRow("发送", [_extrasCheck]);
        _extrasCard.AddRow("目标应用", [_appCheck, _bindAppButton]);
        _extrasCard.AddRow("应用增益", [_appGain]);

        _monitorCard = new RouteCard("本地监听", "监听设备：未配置", _tips);
        _monitorCard.AddRow("模式", [_monitorCombo]);
        _monitorCard.AddRow("监听音量", [_monitorVolume, _monitorGain]);

        _clipCard = new RouteCard("音效输出", "空闲", _tips);
        _clipCard.AddRow("播放", [_stopButton]);
        _clipCard.AddRow("音效增益", [_clipGain]);

        PlaceRouteCards(twoColumn: true);
        host.RowCount = 2;
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.Controls.Add(_routeGrid, 0, 1);
        return host;
    }

    /// <summary>Two columns when the viewport allows it, one column when narrow (§15).</summary>
    private void PlaceRouteCards(bool twoColumn)
    {
        _twoColumnRoutes = twoColumn;
        _routeGrid.SuspendLayout();
        _routeGrid.Controls.Clear();
        _routeGrid.ColumnCount = twoColumn ? 2 : 1;
        _routeGrid.ColumnStyles.Clear();
        _routeGrid.RowStyles.Clear();
        if (twoColumn)
        {
            _routeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            _routeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            _routeGrid.RowCount = 2;
            _routeGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _routeGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        else
        {
            _routeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _routeGrid.RowCount = 4;
            for (var i = 0; i < 4; i++)
            {
                _routeGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
        }

        var cards = new[] { _micCard, _extrasCard, _monitorCard, _clipCard };
        for (var i = 0; i < cards.Length; i++)
        {
            var card = cards[i];
            card.Margin = Theme.DP(0, 0, twoColumn ? Theme.GapMedium : 0, Theme.GapMedium);
            var column = twoColumn ? i % 2 : 0;
            var row = twoColumn ? i / 2 : i;
            _routeGrid.Controls.Add(card, column, row);
        }

        _routeGrid.ResumeLayout(performLayout: true);
    }

    private Control BuildSoundLibrary()
    {
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = new Padding(0, 0, 0, Theme.D(Theme.SectionSpacing)),
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.D(150)));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Theme.DP(0, 0, 0, Theme.GapSmall),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = Theme.SectionTitle("音效库");
        title.Margin = new Padding(0);
        title.Anchor = AnchorStyles.Left;

        var tools = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            WrapContents = false,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0),
        };
        _importButton.Margin = Theme.DP(0, 0, Theme.GapSmall, 0);
        _favoriteFilter.Margin = Theme.DP(0, Theme.D(4), Theme.GapSmall, 0);
        _favoriteFilter.Anchor = AnchorStyles.Left;
        _searchBox.Margin = Theme.DP(0, Theme.D(3), 0, 0);
        _searchBox.Anchor = AnchorStyles.Left;
        tools.Controls.AddRange([_importButton, _favoriteFilter, _searchBox]);

        header.Controls.Add(title, 0, 0);
        header.Controls.Add(tools, 1, 0);
        host.Controls.Add(header, 0, 0);

        var listCard = Theme.Card();
        listCard.Padding = Theme.DP(1);
        listCard.Dock = DockStyle.Fill;
        listCard.Paint += Theme.CardBorder;
        _clipsList.Columns.Add("名称", Theme.D(320));
        _clipsList.Columns.Add("时长", Theme.D(90), HorizontalAlignment.Right);
        _clipsList.Columns.Add("分组", Theme.D(110));
        _clipsList.Columns.Add("收藏", Theme.D(60), HorizontalAlignment.Center);
        _clipsList.Columns.Add("状态", Theme.D(120));
        _clipsList.Dock = DockStyle.Fill;
        _clipsEmptyLabel.Dock = DockStyle.Fill;
        listCard.Controls.Add(_clipsList);
        listCard.Controls.Add(_clipsEmptyLabel);
        host.Controls.Add(listCard, 0, 1);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            WrapContents = true,
            Margin = Theme.DP(0, Theme.GapSmall, 0, 0),
        };
        _playButton.Margin = Theme.DP(0, 0, Theme.GapSmall, Theme.GapSmall);
        _previewButton.Margin = Theme.DP(0, 0, Theme.GapSmall, Theme.GapSmall);
        _favoriteButton.Margin = Theme.DP(0, 0, Theme.GapSmall, Theme.GapSmall);
        _moreButton.Margin = Theme.DP(0, 0, 0, Theme.GapSmall);
        actions.Controls.AddRange([_playButton, _previewButton, _favoriteButton, _moreButton]);
        host.Controls.Add(actions, 0, 2);
        return host;
    }

    private Control BuildHotkeyDigest()
    {
        var card = Theme.Card(
            new ColumnStyle(SizeType.Percent, 100f),
            new ColumnStyle(SizeType.AutoSize));
        card.Dock = DockStyle.Fill;
        card.Paint += Theme.CardBorder;

        var title = new Label
        {
            Text = "全局热键",
            Font = Theme.BodyBold,
            ForeColor = Theme.TextMain,
            AutoSize = true,
            Margin = Theme.DP(0, 0, 0, 2),
        };
        _hotkeySummary.Margin = new Padding(0);
        _hotkeyPanelButton.Margin = new Padding(0);
        _hotkeyPanelButton.Anchor = AnchorStyles.Right | AnchorStyles.Top;

        var textColumn = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = new Padding(0),
        };
        textColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        textColumn.Controls.Add(title, 0, 0);
        textColumn.Controls.Add(_hotkeySummary, 0, 1);

        card.Controls.Add(textColumn, 0, 0);
        card.Controls.Add(_hotkeyPanelButton, 1, 0);
        return card;
    }

    private void BuildPresetMenu()
    {
        var create = new ToolStripMenuItem("新建预设…");
        create.Click += (_, _) => CreatePreset();
        var duplicate = new ToolStripMenuItem("复制预设…");
        duplicate.Click += (_, _) => DuplicatePreset();
        var rename = new ToolStripMenuItem("重命名预设…");
        rename.Click += (_, _) => RenamePreset();
        var delete = new ToolStripMenuItem("删除预设…") { ForeColor = Theme.Error };
        delete.Click += (_, _) => DeletePreset();
        var save = new ToolStripMenuItem("保存当前预设");
        save.Click += (_, _) => SavePreset();
        var settings = new ToolStripMenuItem("设备设置…");
        settings.Click += (_, _) => OpenDeviceSettings();
        _presetMenu.Items.AddRange(
        [
            create, duplicate, rename, new ToolStripSeparator(),
            save, settings, new ToolStripSeparator(),
            delete,
        ]);
    }

    private void BuildClipMenu()
    {
        var rename = new ToolStripMenuItem("重命名…");
        rename.Click += (_, _) => RenameClip();
        var hotkey = new ToolStripMenuItem("绑定播放热键…");
        hotkey.Click += (_, _) => OpenHotkeyEditor(focusClipId: SelectedClip()?.ClipId);
        var open = new ToolStripMenuItem("打开文件位置");
        open.Click += (_, _) => OpenClipLocation();
        var delete = new ToolStripMenuItem("删除音效…") { ForeColor = Theme.Error };
        delete.Click += (_, _) => DeleteClip();
        _clipMenu.Items.AddRange([rename, hotkey, open, new ToolStripSeparator(), delete]);
        _clipMenu.Opening += (_, _) =>
        {
            var has = SelectedClip() is not null;
            foreach (ToolStripItem item in _clipMenu.Items)
            {
                item.Enabled = has;
            }
        };
    }

    // ----------------------------------------------------------------- events

    private void WireEvents()
    {
        _presetCombo.SelectedIndexChanged += (_, _) => QueueTopClusterFit();
        _applyButton.Click += (_, _) => ApplySelectedPreset();
        _presetMenuButton.Click += (_, _) => _presetMenu.Show(_presetMenuButton, new Point(0, _presetMenuButton.Height));
        _saveButton.Click += (_, _) => SavePreset();
        _settingsButton.Click += (_, _) => OpenDeviceSettings();
        _emergencyButton.Click += (_, _) => ToggleEmergency();

        _micCheck.CheckedChanged += (_, _) => Toggle(ToggleRoute.Mic, _micCheck.Checked);
        _extrasCheck.CheckedChanged += (_, _) => Toggle(ToggleRoute.Extras, _extrasCheck.Checked);
        _appCheck.CheckedChanged += (_, _) => Toggle(ToggleRoute.App, _appCheck.Checked);
        _bindAppButton.Click += (_, _) => BindApplication();
        _monitorCombo.SelectedIndexChanged += (_, _) => ApplyMonitorSelection();
        _monitorVolume.ValueChanged += (_, _) => OnMonitorVolumeChanged();
        _monitorGain.ValueChanged += (_, _) => OnMonitorGainChanged();
        WireGain(_micGain, g => _shell.Session.SetGains(CurrentGains() with { Mic = g }), s => s.Gains.Mic);
        WireGain(_appGain, g => _shell.Session.SetGains(CurrentGains() with { App = g }), s => s.Gains.App);
        WireGain(_clipGain, g => _shell.Session.SetGains(CurrentGains() with { Clip = g }), s => s.Gains.Clip);
        _stopButton.Click += (_, _) => Run(_shell.Session.StopClip(), "停止音效");

        _playButton.Click += async (_, _) => await PlaySelectedAsync(send: true);
        _previewButton.Click += async (_, _) => await PlaySelectedAsync(send: false);
        _importButton.Click += async (_, _) => await ImportClipAsync();
        _favoriteButton.Click += (_, _) => ToggleFavorite();
        _moreButton.Click += (_, _) => _clipMenu.Show(_moreButton, new Point(0, _moreButton.Height));
        _searchBox.TextChanged += (_, _) => RefreshClips();
        _favoriteFilter.CheckedChanged += (_, _) => RefreshClips();
        _clipsList.SelectedIndexChanged += (_, _) => UpdateClipButtons();
        _clipsList.DoubleClick += async (_, _) => await PlaySelectedAsync(send: true);

        _hotkeyPanelButton.Click += (_, _) => OpenHotkeyEditor(focusClipId: null);

        Resize += (_, _) =>
        {
            ApplyResponsiveLayout();
            QueueTopClusterFit();
        };
        Shown += (_, _) => ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        var twoColumn = ClientSize.Width - Theme.D(2 * Theme.WindowMargin) >= Theme.D(TwoColumnMinDip);
        if (twoColumn != _twoColumnRoutes)
        {
            PlaceRouteCards(twoColumn);
        }

        ConstrainTopCluster();
    }

    /// <summary>
    /// Re-fits the app bar after the layout pass that Resize has not finished
    /// yet. Coalesced: a drag produces many Resize events and only the last
    /// measurement matters.
    /// </summary>
    private void QueueTopClusterFit()
    {
        if (_fitQueued)
        {
            return;
        }

        // Before the handle exists (constructor-time preset refresh) there is
        // nothing to lay out yet and BeginInvoke would throw.
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        _fitQueued = true;
        BeginInvoke(() =>
        {
            _fitQueued = false;
            if (!IsDisposed)
            {
                ConstrainTopCluster();
            }
        });
    }

    /// <summary>
    /// Fits the app bar by hiding low-frequency buttons, widest-first, until the
    /// cluster and the emergency action both fit. Nothing is ever clipped, and
    /// every hidden action stays in the 配置管理 menu.
    /// </summary>
    private void ConstrainTopCluster()
    {
        if (_topCluster is null || _topBar is null || _topBar.ClientSize.Width <= 0)
        {
            return;
        }

        var available = _topBar.ClientSize.Width
            - _titleLabel.PreferredSize.Width - _titleLabel.Margin.Horizontal
            - _emergencyButton.PreferredSize.Width - _emergencyButton.Margin.Horizontal;
        available = Math.Max(Theme.D(120), available);

        // priority order: drop the least essential first
        // 配置管理 holds the only entry to create/duplicate/rename/delete, so it
        // is the last thing hidden; 保存/设备设置 are duplicated inside that menu.
        var optional = new Control[] { _dirtyLabel, _settingsButton, _saveButton, _applyButton, _presetMenuButton };
        foreach (var control in optional)
        {
            control.Visible = true;
        }

        foreach (var control in optional)
        {
            if (MeasureClusterWidth() <= available)
            {
                break;
            }

            control.Visible = false;
        }

        // the preset selector itself may shrink before it is dropped
        if (MeasureClusterWidth() > available)
        {
            _presetCombo.Width = Math.Max(Theme.D(120), _presetCombo.Width - (MeasureClusterWidth() - available));
        }
    }

    private int MeasureClusterWidth()
    {
        var total = _topCluster.Padding.Horizontal;
        foreach (Control child in _topCluster.Controls)
        {
            if (!child.Visible)
            {
                continue;
            }

            total += child.PreferredSize.Width + child.Margin.Horizontal;
        }

        return total;
    }

    // ------------------------------------------------------------- status poll

    public void RefreshStatus()
    {
        if (IsDisposed)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var status = _shell.Session.Status();

            UpdatePhaseChip(status);
            _emergencyButton.Text = status.EmergencyLatched ? "解除静音（保持全关）" : "全部静音";

            UpdateRouteChip(_micChip, "麦克风", status.Switches.MicSendEnabled, status.Devices is not null && !MicAvailable(status));
            UpdateRouteChip(_extrasChip, "附加音频", status.Switches.ExtrasSendEnabled, unavailable: false);
            UpdateRouteChip(_appChip, "应用音频", status.Switches.AppSendEnabled, unavailable: status.AppBinding is null);
            UpdateRouteChip(_monitorChip, status.MonitorMode == MonitorMode.FullEndpoint ? "监听·实际" : "监听·仅音效",
                status.Switches.MonitorEnabled, unavailable: status.Devices is not null && string.IsNullOrEmpty(status.Devices.MonitorDeviceId));
            UpdateClipChip(status);
            _issueLabel.Text = DescribeIssues(status);

            // route cards: controls mirror the snapshot; user edits are ignored while refreshing
            _micCheck.Checked = status.Switches.MicSendEnabled;
            _micCheck.Enabled = !status.EmergencyLatched;
            _micGain.Value = (decimal)status.Gains.Mic;

            _extrasCheck.Checked = status.Switches.ExtrasSendEnabled;
            _extrasCheck.Enabled = !status.EmergencyLatched;
            _appCheck.Checked = status.Switches.AppSendEnabled;
            _appCheck.Enabled = status.Switches.ExtrasSendEnabled && !status.EmergencyLatched;
            _bindAppButton.Enabled = !status.Switches.AppSendEnabled && !status.EmergencyLatched;
            _appGain.Value = (decimal)status.Gains.App;

            _monitorCombo.SelectedIndex = MonitorIndex(status);
            _monitorCombo.Enabled = !status.EmergencyLatched;
            _monitorVolume.Value = ClampTrack((int)Math.Round(status.Gains.Monitor * 100));
            _monitorGain.Value = (decimal)Math.Round(status.Gains.Monitor, 2);
            _monitorVolume.Enabled = !status.EmergencyLatched;
            _monitorGain.Enabled = !status.EmergencyLatched;

            _clipGain.Value = (decimal)status.Gains.Clip;
            _stopButton.Enabled = status.Clip is not null;

            UpdateCards(status);
            UpdateClipPlayState(status);
            UpdateStatusStrip(status);
            Text = $"随声麦 — {ActivePresetName()}{(Dirty ? " *" : "")}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void UpdateCards(SessionStatus status)
    {
        var mic = status.Devices;
        _micCard.SetDescription(mic is null || string.IsNullOrEmpty(mic.MicDeviceId)
            ? "输入：未配置（在「设备设置…」中选择）"
            : EndpointDescription("输入", mic.MicDeviceId));
        _micCard.SetStatus(
            status.EmergencyLatched ? "锁存" :
            !MicAvailable(status) ? "设备不可用" :
            status.Switches.MicSendEnabled ? "发送中" : "已关闭",
            status.EmergencyLatched || !MicAvailable(status) ? Theme.Error :
            status.Switches.MicSendEnabled ? Theme.Success : Theme.TextSecondary);

        _extrasCard.SetDescription(status.AppBinding is { } binding
            ? $"目标应用：{binding.Description}（pid {binding.ProcessId}）"
            : "目标应用：未绑定（附加音频=音效）");
        _extrasCard.SetStatus(
            status.EmergencyLatched ? "锁存" :
            status.Switches.AppSendEnabled ? "应用捕获中" :
            status.Switches.ExtrasSendEnabled ? "已开启" : "已关闭",
            status.EmergencyLatched ? Theme.Error :
            status.Switches.ExtrasSendEnabled ? Theme.Success : Theme.TextSecondary);

        var monitorDevice = status.Devices?.MonitorDeviceId;
        _monitorCard.SetDescription(string.IsNullOrEmpty(monitorDevice)
            ? "监听设备：未配置（在「设备设置…」中选择）"
            : EndpointDescription("监听设备", monitorDevice));
        _monitorCard.SetStatus(
            status.EmergencyLatched ? "锁存" :
            status.MonitorSuspendedForPreview ? "试听中暂停" :
            status.Switches.MonitorEnabled ? "监听中" : "已关闭",
            status.EmergencyLatched ? Theme.Error :
            status.Switches.MonitorEnabled ? Theme.Success : Theme.TextSecondary);

        _clipCard.SetDescription(status.Clip is { } clip
            ? $"{(clip.Mode == ClipPlaybackMode.Send ? "正在发送" : "本地试听")}：{ClipName(clip.ClipId)}"
            : "空闲（在音效库中选择并播放）");
        _clipCard.SetStatus(
            status.Clip is null ? "空闲" : status.Clip.Mode == ClipPlaybackMode.Send ? "发送中" : "试听中",
            status.Clip is { Mode: ClipPlaybackMode.Send } ? Theme.Success : Theme.TextSecondary);
    }

    private void UpdateStatusStrip(SessionStatus status)
    {
        var deviceText = status.Devices is null
            ? "设备：未配置"
            : $"设备：已配置{(status.DevicesValidated ? "" : "（校验未通过）")}";
        var hotkeys = _shell.Soundboard.ActiveHotkeys.Count;
        var hotkeyText = _shell.Soundboard.HotkeyBackendAvailable ? $"热键：{hotkeys} 项" : "热键：不可用";
        _statusLabel.Text = $"{DescribePhase(status)}　|　{deviceText}　|　{hotkeyText}";

        // live level forensics: shows WHERE clipping happens (docs/01 B08)
        var metrics = _shell.Session.Metrics();
        _metricsLabel.Text =
            $"峰值 {metrics.MixerPeak:0.00} | 削波 {metrics.MixerClippedSamples} | 硬跳变 {metrics.MixerHardSteps} | 欠载 {metrics.MixerUnderrunFrames}";
        _metricsLabel.ForeColor = metrics.MixerClippedSamples > 0 || metrics.MixerPeak >= 0.99f
            ? Theme.Error
            : metrics.MixerPeak >= 0.8f ? Theme.Warning : Theme.TextMain;
    }

    // ---------------------------------------------------------------- commands

    private enum ToggleRoute
    {
        Mic,
        Extras,
        App,
    }

    private void Toggle(ToggleRoute route, bool on)
    {
        if (_refreshing)
        {
            return;
        }

        var (result, what) = route switch
        {
            ToggleRoute.Mic => (_shell.Session.EnableMic(on), "真麦发送"),
            ToggleRoute.Extras => (_shell.Session.EnableExtras(on), "附加音频"),
            _ => (_shell.Session.EnableApp(on), "应用捕获"),
        };

        Run(result, what);
        if (!result.Accepted)
        {
            return;
        }

        // read back: the switch is only "on" when the engine says so (§7.4)
        var status = _shell.Session.Status();
        var actual = route switch
        {
            ToggleRoute.Mic => status.Switches.MicSendEnabled,
            ToggleRoute.Extras => status.Switches.ExtrasSendEnabled,
            _ => status.Switches.AppSendEnabled,
        };
        if (actual != on)
        {
            ShowMismatch(what, on ? "开启" : "关闭");
        }
    }

    private void ToggleEmergency()
    {
        var status = _shell.Session.Status();
        var latched = status.EmergencyLatched;
        var result = latched ? _shell.Session.UnlockEmergency() : _shell.Session.EmergencyMute();
        Run(result, latched ? "解除静音" : "全部静音");
        if (!result.Accepted)
        {
            return;
        }

        var after = _shell.Session.Status();
        if (latched && after.EmergencyLatched)
        {
            ShowMismatch("解除静音", "解除锁存");
        }
        else if (!latched && (!after.EmergencyLatched || after.Switches.AnySendEnabled || after.Switches.MonitorEnabled))
        {
            ShowMismatch("全部静音", "关闭全部发送与监听");
        }
    }

    private void ApplyMonitorSelection()
    {
        if (_refreshing)
        {
            return;
        }

        var current = _shell.Session.Status();
        var (result, what) = _monitorCombo.SelectedIndex switch
        {
            1 => (_shell.Session.SetMonitor(true, MonitorMode.ClipOnly), "监听（仅音效）"),
            2 => (_shell.Session.SetMonitor(true, MonitorMode.FullEndpoint), "监听（实际虚拟麦）"),
            _ => (_shell.Session.SetMonitor(false, current.MonitorMode), "关闭监听"),
        };
        Run(result, what);
    }

    private void OnMonitorVolumeChanged()
    {
        if (_refreshing)
        {
            return;
        }

        var gain = _monitorVolume.Value / 100f;
        _refreshing = true;
        try
        {
            _monitorGain.Value = (decimal)Math.Round(gain, 2);
        }
        finally
        {
            _refreshing = false;
        }

        ApplyMonitorGain(gain);
    }

    private void OnMonitorGainChanged()
    {
        if (_refreshing)
        {
            return;
        }

        var gain = (float)_monitorGain.Value;
        _refreshing = true;
        try
        {
            _monitorVolume.Value = ClampTrack((int)Math.Round(gain * 100));
        }
        finally
        {
            _refreshing = false;
        }

        ApplyMonitorGain(gain);
    }

    private void ApplyMonitorGain(float gain)
    {
        _ = _shell.Session.SetGains(CurrentGains() with { Monitor = gain });
        MarkDirty();
    }

    private void WireGain(NumericUpDown control, Func<float, CommandResult> apply, Func<SessionStatus, float> read)
    {
        control.ValueChanged += (_, _) =>
        {
            if (_refreshing)
            {
                return;
            }

            var requested = (float)control.Value;
            var result = apply(requested);
            if (!result.Accepted)
            {
                Run(result, "调整增益");
                return;
            }

            var actual = read(_shell.Session.Status());
            if (Math.Abs(actual - requested) > 0.001f)
            {
                ShowMismatch("增益", $"引擎实际值 {actual:0.00}");
            }

            MarkDirty();
        };
    }

    private void BindApplication()
    {
        using var picker = new AppPickerForm(_shell);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is not { } instance)
        {
            return;
        }

        var result = _shell.Session.BindApplication(new AppBinding(instance.ProcessId, Path.GetFileName(instance.ExecutablePath)));
        Run(result, "绑定应用");
    }

    private async Task PlaySelectedAsync(bool send)
    {
        if (SelectedClip() is not { } item)
        {
            return;
        }

        var what = send ? "向队友播放" : "本地试听";
        SetBusy(true);
        try
        {
            // decoding and the serialized session command run off the UI thread
            var result = await Task.Run(() => send
                ? _shell.Session.PlayClip(item.ClipId)
                : _shell.Session.PreviewClip(item.ClipId));
            Run(result, what);
            if (!result.Accepted)
            {
                return;
            }

            var status = _shell.Session.Status();
            if (status.Clip is not { } clip || clip.ClipId != item.ClipId)
            {
                ShowMismatch(what, "播放未开始");
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ImportClipAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "导入音效",
            Filter = "音频文件 (*.wav;*.mp3)|*.wav;*.mp3",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var library = _shell.Soundboard.Library;
        if (library is null)
        {
            ShowError("音效库不可用", "索引文件损坏，本次运行已禁用音效库（文件未被改动）。\n处理：查看 logs/onbody.log 定位损坏的 index.json，修复或移除后重启。");
            return;
        }

        _importing = true;
        SetBusy(true);
        _importButton.Text = "导入中…";
        try
        {
            var path = dialog.FileName;
            var first = await Task.Run(() => TryImport(library, path, allowDuplicate: false));
            if (first.Error is not null)
            {
                ShowError("导入失败", $"{first.Error}\n处理：确认文件为 wav/mp3、未损坏、时长不超过 60 秒。");
                return;
            }

            if (first.Result is { Outcome: ImportOutcome.Duplicate } duplicate)
            {
                var answer = MessageBox.Show(
                    this,
                    $"库中已存在相同内容的音效「{duplicate.ExistingDuplicate!.DisplayName}」。\n仍要再导入一份副本吗？",
                    "重复音效", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    return;
                }

                var second = await Task.Run(() => TryImport(library, path, allowDuplicate: true));
                if (second.Error is not null || second.Result is not { } forced)
                {
                    ShowError("导入失败", second.Error ?? "未知原因");
                    return;
                }

                _shell.Log($"[clip] 已导入副本 {forced.Item.ClipId}（{forced.Item.DisplayName}）");
                RefreshClips();
                return;
            }

            if (first.Result is { } imported)
            {
                _shell.Log($"[clip] 已导入 {imported.Item.ClipId}（{imported.Item.DurationMs / 1000.0:F1}s，{imported.Item.Format}）");
            }

            RefreshClips();
        }
        finally
        {
            _importing = false;
            _importButton.Text = "导入音效…";
            SetBusy(false);
        }
    }

    private static (ImportResult? Result, string? Error) TryImport(ClipLibrary library, string path, bool allowDuplicate)
    {
        try
        {
            return (library.Import(path, displayName: null, allowDuplicate: allowDuplicate), null);
        }
        catch (ClipImportException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ToggleFavorite()
    {
        if (SelectedClip() is not { } item)
        {
            return;
        }

        var library = _shell.Soundboard.Library;
        if (library is null || !library.SetFavorite(item.ClipId, !item.Favorite))
        {
            ShowError("收藏失败", "音效库不可用或条目已被删除。");
            return;
        }

        RefreshClips(selectClipId: item.ClipId);
    }

    private void RenameClip()
    {
        if (SelectedClip() is not { } item)
        {
            return;
        }

        var name = Prompt("新的音效名称", "重命名音效", item.DisplayName);
        if (name is null)
        {
            return;
        }

        if (_shell.Soundboard.Library?.Rename(item.ClipId, name) != true)
        {
            ShowError("重命名失败", "音效库不可用、名称为空或条目已被删除。");
            return;
        }

        RefreshClips(selectClipId: item.ClipId);
    }

    private void OpenClipLocation()
    {
        if (SelectedClip() is not { } item || _shell.Soundboard.Library is not { } library)
        {
            return;
        }

        var path = library.AssetAbsolutePath(item);
        if (!File.Exists(path))
        {
            ShowError("文件缺失", "该音效的受管副本已不存在。\n处理：重新导入原文件，或在「更多」中删除此条目。");
            return;
        }

        _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private void DeleteClip()
    {
        if (SelectedClip() is not { } item)
        {
            return;
        }

        var referencing = _shell.Presets.PresetsReferencingClip(item.ClipId);
        var referenceNote = referencing.Count > 0
            ? $"\n\n以下预设的布局引用了它：{string.Join("、", referencing)}"
            : "";
        if (MessageBox.Show(
                this,
                $"删除音效「{item.DisplayName}」？\n仅删除库内受管副本（原文件不受影响），同时解除它的播放热键。{referenceNote}",
                "删除音效", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        if (!_shell.Soundboard.DeleteClip(item.ClipId))
        {
            ShowError("删除失败", "音效库不可用或条目已被删除。");
            return;
        }

        RefreshClips();
        RefreshHotkeys();
    }

    // --------------------------------------------------------------- presets

    private void ApplySelectedPreset()
    {
        if (SelectedPreset() is not { Corrupt: false } preset)
        {
            ShowError("无法应用预设", "请选择一个有效预设（标有「损坏」的预设不能应用）。");
            return;
        }

        if (!ConfirmDiscardOrSave("切换预设"))
        {
            return;
        }

        var result = _shell.Presets.Apply(preset.PresetId);
        if (!result.Accepted)
        {
            ShowError("应用预设失败", $"{Rejections.Describe(result.Rejection)}\n{Rejections.NextStep(result.Rejection)}");
        }
        else if (result.Issues.Count > 0 || result.OutputAttachRejection is not null)
        {
            var detail = string.Join("\n", result.Issues.Select(i => $"· {RouteLabel(i.Route)}：{IssueLabel(i.Reason)}"));
            if (result.OutputAttachRejection is not null)
            {
                detail += $"\n· 虚拟输出接入失败：{result.OutputAttachRejection}";
            }

            ShowError("预设已应用，但存在设备问题", $"{detail}\n\n处理：打开「设备设置…」重新选择端点后保存。");
        }

        ClearDirty();
        RefreshPresets();
        RefreshStatus();
        _shell.Devices.RefreshAsync();
    }

    private void CreatePreset()
    {
        var name = Prompt("新预设名称", "新建预设");
        if (name is null)
        {
            return;
        }

        var layout = _shell.Soundboard.Library?.ItemsSnapshot().Select(i => i.ClipId).ToList() ?? [];
        var created = _shell.Presets.Create(name, layout, favoritesOnly: false, groupId: null);
        if (!created.Result.Accepted)
        {
            ShowError("创建预设失败", $"{Rejections.Describe(created.Result.Rejection)}\n{Rejections.NextStep(created.Result.Rejection)}");
            return;
        }

        ClearDirty();
        RefreshPresets();
        RefreshStatus();
    }

    private void DuplicatePreset()
    {
        if (SelectedPreset() is not { Corrupt: false } preset)
        {
            return;
        }

        var name = Prompt($"复制 {preset.Name} 为新预设名称", "复制预设", $"{preset.Name} 副本");
        if (name is null)
        {
            return;
        }

        Run(_shell.Presets.Duplicate(preset.PresetId, name), "复制预设");
        RefreshPresets();
    }

    private void RenamePreset()
    {
        if (SelectedPreset() is not { Corrupt: false } preset)
        {
            return;
        }

        var name = Prompt($"将 {preset.Name} 重命名为", "重命名预设", preset.Name);
        if (name is null)
        {
            return;
        }

        Run(_shell.Presets.Rename(preset.PresetId, name), "重命名预设");
        RefreshPresets();
    }

    private void DeletePreset()
    {
        if (SelectedPreset() is not { Corrupt: false } preset)
        {
            return;
        }

        var active = _shell.Presets.ActivePresetId == preset.PresetId;
        var message = active
            ? $"预设「{preset.Name}」当前正在使用：删除将安全关闭全部发送/监听，且不会自动启用其他预设。\n确认删除？"
            : $"删除预设「{preset.Name}」？";
        if (MessageBox.Show(this, message, "删除预设", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        var deleted = _shell.Presets.Delete(preset.PresetId);
        if (deleted.Accepted)
        {
            _shell.Soundboard.RemovePresetHotkey(preset.PresetId);
        }

        Run(deleted, "删除预设");
        RefreshPresets();
        RefreshHotkeys();
        RefreshStatus();
    }

    private void SavePreset()
    {
        var active = _shell.Presets.ActivePresetId;
        if (active is null)
        {
            ShowError("无法保存", "当前没有活动预设。\n处理：在「配置管理」中新建预设。");
            return;
        }

        var layout = _shell.Soundboard.Library?.ItemsSnapshot().Select(i => i.ClipId).ToList() ?? [];
        var result = _shell.Presets.Save(active, layout, favoritesOnly: false, groupId: null);
        if (!result.Accepted)
        {
            ShowError("保存失败（旧文件保留）", $"{Rejections.Describe(result.Rejection)}\n{Rejections.NextStep(result.Rejection)}");
            return;
        }

        ClearDirty();
        RefreshStatus();
    }

    /// <summary>Offers 保存/放弃/取消 when preset edits exist; false means cancel.</summary>
    public bool ConfirmDiscardOrSave(string action)
    {
        if (!Dirty)
        {
            return true;
        }

        switch (MessageBox.Show(this, $"当前预设有未保存的修改，{action}前如何处理？", "未保存的修改",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question))
        {
            case DialogResult.Yes:
                SavePreset();
                return !Dirty;
            case DialogResult.No:
                return true;
            default:
                return false;
        }
    }

    private void OpenDeviceSettings()
    {
        using var settings = new DeviceSettingsForm(_shell);
        if (settings.ShowDialog(this) == DialogResult.OK)
        {
            ClearDirty();
            RefreshPresets();
            RefreshStatus();
        }
    }

    private void OpenHotkeyEditor(string? focusClipId)
    {
        using var editor = new HotkeyEditorForm(_shell, focusClipId);
        editor.ShowDialog(this);
        RefreshHotkeys();
        RefreshStatus();
    }

    // ------------------------------------------------------------------ lists

    private void RefreshPresets()
    {
        _presetCombo.BeginUpdate();
        try
        {
            _presetCombo.Items.Clear();
            var active = _shell.Presets.ActivePresetId;
            var select = 0;
            foreach (var preset in _shell.Presets.List())
            {
                _presetCombo.Items.Add(preset.Corrupt ? $"{preset.Name}（损坏）" : preset.Name);
                if (preset.PresetId == active)
                {
                    select = _presetCombo.Items.Count - 1;
                }
            }

            _presetCombo.SelectedIndex = _presetCombo.Items.Count > 0 ? select : -1;
        }
        finally
        {
            _presetCombo.EndUpdate();
        }
    }

    private void RefreshClips(string? selectClipId = null)
    {
        var selectedId = selectClipId ?? SelectedClip()?.ClipId;
        var library = _shell.Soundboard.Library;
        var query = _searchBox.Text.Trim();
        var items = library?.Search(query.Length == 0 ? null : query, favoritesOnly: _favoriteFilter.Checked) ?? [];

        _clipsList.BeginUpdate();
        try
        {
            _clipsList.Items.Clear();
            foreach (var item in items)
            {
                var available = library?.IsAvailable(item.ClipId) ?? false;
                var row = new ListViewItem(item.DisplayName) { Tag = item };
                row.SubItems.Add(FormatDuration(item.DurationMs));
                row.SubItems.Add(GroupName(item.GroupId));
                row.SubItems.Add(item.Favorite ? "★" : "");
                row.SubItems.Add(available ? "" : "素材缺失");
                if (!available)
                {
                    row.ForeColor = Theme.Error;
                }

                _clipsList.Items.Add(row);
                if (item.ClipId == selectedId)
                {
                    row.Selected = true;
                    _clipsList.FocusedItem = row;
                }
            }
        }
        finally
        {
            _clipsList.EndUpdate();
        }

        var empty = _clipsList.Items.Count == 0;
        _clipsList.Visible = !empty;
        _clipsEmptyLabel.Visible = empty;
        _clipsEmptyLabel.Text = library is null
            ? "音效库不可用（索引损坏）；本次运行无法导入或播放。"
            : empty
                ? query.Length > 0 || _favoriteFilter.Checked
                    ? "没有匹配的音效（清空搜索或取消「只看收藏」）"
                    : "尚无音效，导入音频文件以开始使用"
                : string.Empty;
        UpdateClipButtons();
        _lastClipKey = null;
        UpdateClipPlayState(_shell.Session.Status());
    }

    private void UpdateClipButtons()
    {
        var has = SelectedClip() is not null;
        _playButton.Enabled = has && !_importing;
        _previewButton.Enabled = has && !_importing;
        _favoriteButton.Enabled = has;
        _moreButton.Enabled = has;
    }

    /// <summary>Updates only the "状态" cell of the playing row (no full rebuild each poll).</summary>
    private void UpdateClipPlayState(SessionStatus status)
    {
        var key = status.Clip is { } clip ? $"{clip.ClipId}|{clip.Mode}|{clip.PlaybackEpoch}" : string.Empty;
        if (key == _lastClipKey)
        {
            return;
        }

        _lastClipKey = key;
        foreach (ListViewItem row in _clipsList.Items)
        {
            if (row.Tag is not ClipItem item)
            {
                continue;
            }

            var text = status.Clip is { } active && active.ClipId == item.ClipId
                ? active.Mode == ClipPlaybackMode.Send ? "▶ 发送中" : "▶ 试听中"
                : row.SubItems[4].Text == "素材缺失" ? "素材缺失" : string.Empty;
            if (row.SubItems[4].Text != text)
            {
                row.SubItems[4].Text = text;
            }
        }
    }

    private void RefreshHotkeys()
    {
        var bindings = _shell.Soundboard.ActiveHotkeys;
        if (!_shell.Soundboard.HotkeyBackendAvailable)
        {
            _hotkeySummary.Text = "热键后端不可用（查看 logs/onbody.log）";
            _hotkeySummary.ForeColor = Theme.Error;
            return;
        }

        if (bindings.Count == 0)
        {
            _hotkeySummary.Text = "尚未绑定快捷键（热键初始为空，需在设置中主动绑定）";
            _hotkeySummary.ForeColor = Theme.TextSecondary;
            _tips.SetToolTip(_hotkeySummary, "点击「设置热键…」为真麦/附加音频/监听/音效/预设绑定全局快捷键。");
            return;
        }

        _hotkeySummary.ForeColor = Theme.TextMain;
        var digest = string.Join("　", bindings.Take(4).Select(b => $"{b.Gesture} = {BindingLabel(b)}"));
        _hotkeySummary.Text = bindings.Count > 4 ? $"{digest}　+{bindings.Count - 4} 项…" : digest;
        _tips.SetToolTip(_hotkeySummary, string.Join("\n", bindings.Select(b => $"{b.Gesture} = {BindingLabel(b)}")));
    }

    private string BindingLabel(HotkeyBinding binding) =>
        Rejections.BindingLabel(binding, ClipName, PresetName);

    private string PresetName(string presetId) =>
        _shell.Presets.List().FirstOrDefault(p => p.PresetId == presetId).Name ?? presetId;

    // ---------------------------------------------------------------- helpers

    private void OnDevicesRefreshed()
    {
        if (IsDisposed)
        {
            return;
        }

        RefreshStatus();
    }

    private void SetBusy(bool busy)
    {
        _playButton.Enabled = !busy && SelectedClip() is not null;
        _previewButton.Enabled = !busy && SelectedClip() is not null;
        _importButton.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private ClipItem? SelectedClip() => _clipsList.SelectedItems.Count > 0
        ? _clipsList.SelectedItems[0].Tag as ClipItem
        : null;

    private PresetSummary? SelectedPreset()
    {
        var index = _presetCombo.SelectedIndex;
        if (index < 0)
        {
            return null;
        }

        var list = _shell.Presets.List();
        return index < list.Count ? list[index] : null;
    }

    private string ActivePresetName()
    {
        var active = _shell.Presets.ActivePresetId;
        var match = _shell.Presets.List().FirstOrDefault(p => p.PresetId == active);
        return match.PresetId == active ? match.Name : "（未选择预设）";
    }

    private RouteGains CurrentGains() => _shell.Session.Status().Gains;

    private void MarkDirty()
    {
        Dirty = true;
        _dirtyLabel.Text = "未保存";
    }

    private void ClearDirty()
    {
        Dirty = false;
        _dirtyLabel.Text = "";
    }

    private void Run(CommandResult result, string what)
    {
        if (!result.Accepted)
        {
            ShowError($"{what}失败", $"{Rejections.Describe(result.Rejection)}\n{Rejections.NextStep(result.Rejection)}");
        }

        RefreshStatus();
    }

    private void ShowError(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void ShowMismatch(string what, string expectation) =>
        MessageBox.Show(
            this,
            $"{what}：命令已提交，但引擎状态与预期不符（预期{expectation}）。\n"
            + "可能原因：设备被占用/已移除，或虚拟输出未接入。\n"
            + "处理：查看底部状态栏与 logs/onbody.log；必要时在「设备设置…」重新应用。",
            $"{what}未生效", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private string? Prompt(string text, string title, string initial = "")
    {
        using var form = new Form
        {
            Text = title,
            Font = Theme.Body,
            AutoScaleMode = AutoScaleMode.None,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false,
            BackColor = Theme.WindowBack,
            ClientSize = Theme.DS(360, 132),
            Padding = Theme.DP(Theme.GapMedium),
        };
        var label = new Label { Text = text, Dock = DockStyle.Top, Height = Theme.D(24), ForeColor = Theme.TextSecondary };
        var input = new TextBox { Text = initial, Dock = DockStyle.Top, Font = Theme.Body };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = Theme.DP(0, Theme.GapSmall, 0, 0),
        };
        var ok = Theme.PrimaryButton("确定");
        ok.DialogResult = DialogResult.OK;
        var cancel = Theme.SecondaryButton("取消");
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Margin = Theme.DP(0, 0, Theme.GapSmall, 0);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        form.Controls.Add(buttons);
        form.Controls.Add(input);
        form.Controls.Add(label);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
    }

    private static int MonitorIndex(SessionStatus status) => status.Switches.MonitorEnabled
        ? status.MonitorMode == MonitorMode.FullEndpoint ? 2 : 1
        : 0;

    private static int ClampTrack(int value) => Math.Clamp(value, 0, 400);

    private static string FormatDuration(int durationMs) => $"{durationMs / 1000.0:0.0} 秒";

    private string GroupName(string? groupId) => groupId is null
        ? ""
        : _shell.Soundboard.Library?.GroupsSnapshot().FirstOrDefault(g => g.GroupId == groupId)?.Name ?? groupId;

    private string ClipName(string clipId) => _shell.Soundboard.Library?.Find(clipId)?.DisplayName ?? $"（已删除 {clipId}）";

    private static string RouteLabel(PresetRoute route) => route switch
    {
        PresetRoute.Microphone => "真实麦克风",
        PresetRoute.VirtualOutput => "虚拟输出",
        PresetRoute.VirtualCapture => "虚拟麦 Capture",
        PresetRoute.Monitor => "监听输出",
        PresetRoute.Application => "目标应用",
        _ => route.ToString(),
    };

    private static string IssueLabel(string reason) => reason switch
    {
        "endpoint-missing" => "端点不存在（驱动可能未安装）",
        "endpoint-disabled" => "端点已被禁用（可在「设备设置…」启用）",
        "executable-missing" => "应用路径不存在",
        "attach-failed" => "输出接入失败（可能被独占）",
        _ => reason,
    };

    /// <summary>
    /// Endpoint text that distinguishes the three states a user must be able to
    /// tell apart: present and active, present but disabled (fixable in 设备设置),
    /// and gone entirely (re-select it).
    /// </summary>
    private string EndpointDescription(string prefix, string endpointId)
    {
        if (_shell.Devices.Find(endpointId) is not { } endpoint)
        {
            return $"{prefix}：设备不存在（未检测到，请在「设备设置…」重新选择）";
        }

        return DeviceCatalog.IsActive(endpoint)
            ? $"{prefix}：{endpoint.FriendlyName}"
            : $"{prefix}：{endpoint.FriendlyName}（已禁用 — 在「设备设置…」中启用）";
    }

    private bool MicAvailable(SessionStatus status) =>
        status.Devices is { } devices
        && !string.IsNullOrEmpty(devices.MicDeviceId)
        && _shell.Devices.IsAvailable(devices.MicDeviceId);

    private void UpdatePhaseChip(SessionStatus status)
    {
        var (text, color) = status.EmergencyLatched
            ? ("紧急静音已锁存", Theme.Error)
            : status.OutputLost
                ? ("虚拟输出已失效", Theme.Error)
                : status.Switches.AnySendEnabled
                    ? ("正在发送", Theme.Success)
                    : status.DevicesValidated
                        ? ("就绪", Theme.TextSecondary)
                        : ("待配置", Theme.Warning);
        _phaseChip.Text = text;
        _phaseChip.ForeColor = color;
    }

    private static void UpdateRouteChip(Label chip, string name, bool enabled, bool unavailable)
    {
        chip.Text = unavailable && !enabled ? $"{name} 不可用" : $"{name} {(enabled ? "开" : "关")}";
        chip.ForeColor = unavailable && !enabled ? Theme.Error : enabled ? Theme.Success : Theme.TextSecondary;
    }

    private void UpdateClipChip(SessionStatus status)
    {
        if (status.Clip is { } clip)
        {
            _clipChip.Text = $"音效 {(clip.Mode == ClipPlaybackMode.Send ? "发送中" : "试听中")}";
            _clipChip.ForeColor = clip.Mode == ClipPlaybackMode.Send ? Theme.Success : Theme.TextSecondary;
        }
        else
        {
            _clipChip.Text = "音效 空闲";
            _clipChip.ForeColor = Theme.TextSecondary;
        }
    }

    private string DescribeIssues(SessionStatus status)
    {
        if (status.EmergencyLatched)
        {
            return "紧急静音已锁存：发送与监听全部关闭，解除后仍保持全关，需逐路手动开启。";
        }

        if (status.OutputLost)
        {
            return "虚拟输出已失效：发送全部关闭。处理：打开「设备设置…」确认端点后保存重新应用。";
        }

        if (status.Devices is null)
        {
            return "设备未配置：点击「设备设置…」选择麦克风、虚拟输出与监听设备。";
        }

        // A disabled or missing virtual capture endpoint is the reason the voice
        // app receives nothing: the virtual microphone exists but never carries
        // audio. Report it explicitly instead of a generic "校验未通过".
        if (string.IsNullOrEmpty(status.Devices.VirtualCaptureDeviceId))
        {
            return "虚拟麦 Capture 未配置：队友端（KOOK 等）将收不到任何声音。"
                + "处理：打开「设备设置…」选择虚拟麦 Capture 端点；若显示「已禁用」，选中后点「启用选中端点」。";
        }

        if (_shell.Devices.Find(status.Devices.VirtualCaptureDeviceId) is not { } captureEndpoint)
        {
            return "虚拟麦 Capture 端点不存在：驱动可能未安装或被移除。"
                + "处理：确认 Steam Streaming Speakers 已安装，然后在「设备设置…」重新选择。";
        }

        if (!DeviceCatalog.IsActive(captureEndpoint))
        {
            return $"虚拟麦 Capture 已禁用（{captureEndpoint.FriendlyName}）：队友端收不到声音。"
                + "处理：打开「设备设置…」，选中该端点后点「启用选中端点」。";
        }

        if (!status.DevicesValidated)
        {
            return "设备校验未通过：部分端点缺失，点击「设备设置…」重新选择。";
        }

        return "";
    }

    private static string DescribePhase(SessionStatus status) => status.EmergencyLatched
        ? "紧急静音已锁存"
        : status.OutputLost
            ? "虚拟输出已失效（发送全关）"
            : status.Switches.AnySendEnabled
                ? "正在发送"
                : status.DevicesValidated
                    ? "就绪（全部关闭）"
                    : "待配置";

    // ------------------------------------------------------------ lifecycle

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.CloseReason is CloseReason.ApplicationExitCall or CloseReason.WindowsShutDown)
        {
            return;
        }

        // close-to-tray baseline (docs/01 §6): closing the window never stops audio
        e.Cancel = true;
        Hide();
        Tray?.NotifyHiddenToTray(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _shell.Devices.Refreshed -= OnDevicesRefreshed;
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }

    internal void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

}
