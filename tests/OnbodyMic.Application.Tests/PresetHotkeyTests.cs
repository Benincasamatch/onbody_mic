using OnbodyMic.Domain;
using Xunit;

namespace OnbodyMic.Application.Tests;

/// <summary>
/// Preset-selection hotkeys (P3): target validation at bind time, refusal
/// when the preset disappears, and apply-through-the-manager on trigger.
/// </summary>
public sealed class PresetHotkeyTests : IDisposable
{
    private readonly FakeHotkeyService _hotkeys = new();
    private readonly FakeDecoder _decoder = new();
    private readonly FakeSourceAdapter _mic = new();
    private readonly OutputSlot _output = new();
    private readonly MonitorOutputSlot _monitorOutput = new();
    private readonly AudioSession _session;
    private readonly HotkeyDispatcher _dispatcher;
    private readonly PresetManager _manager;
    private readonly TinyStore _store = new();
    private readonly List<SessionNotice> _notices = [];
    private readonly FakeLastSink _sink = new();

    public PresetHotkeyTests()
    {
        _session = new AudioSession(new AudioSessionOptions
        {
            ClipDecoder = _decoder,
            ClipBoundaryFadeFrames = 0,
            MicAdapterFactory = (_, channel) =>
            {
                _mic.Channel = channel;
                return _mic;
            },
            AppAdapterFactory = (_, _) => throw new InvalidOperationException("unused"),
            OutputFactory = _ => _output.Create(),
            MonitorOutputFactory = _ => _monitorOutput.Create(),
            InitialDevices = new PresetDeviceConfig("test-mic", "test-render", "test-cable", "test-monitor", null),
            SourcePrerollTimeoutMs = 20,
            AdaptiveRate = false,
        });
        _manager = new PresetManager(_session, _store, _sink);
        _manager.Noticed += message => _notices.Add(new SessionNotice(SessionNoticeKind.Info, message));
        _dispatcher = new HotkeyDispatcher(
            _session,
            _hotkeys,
            presetExists: _store.Exists,
            presetApplier: id => _manager.Apply(id));
        _dispatcher.Noticed += notice => _notices.Add(notice);
        Assert.True(_session.AttachOutput().Accepted);
    }

    public void Dispose()
    {
        _dispatcher.Dispose();
        _hotkeys.Dispose();
        _session.Dispose();
    }

    private static PresetConfig Config(string id) => new(
        id,
        $"预设 {id}",
        new PresetDeviceConfig("test-mic", "test-render", "test-cable", "test-monitor", null),
        1f, 1f, 1f,
        new MonitorPreferences(MonitorMode.ClipOnly, 1f),
        [], false, null);

    [Fact]
    public void Binding_an_unknown_preset_is_rejected()
    {
        var result = _dispatcher.Bind(new HotkeyBinding(HotkeyAction.SelectPreset, "preset-nope", Gesture()));

        Assert.False(result.Accepted);
        Assert.Equal("unknown-preset:preset-nope", result.Rejection);
        Assert.Empty(_hotkeys.Bound);
    }

    [Fact]
    public void Trigger_applies_the_preset_through_the_manager()
    {
        Assert.True(_store.Save(Config("preset-a")).Accepted);
        Assert.True(_store.Save(Config("preset-b")).Accepted);
        Assert.True(_manager.Apply("preset-a").Accepted);
        Assert.True(_session.EnableMic(true).Accepted);
        Assert.True(_dispatcher.Bind(new HotkeyBinding(HotkeyAction.SelectPreset, "preset-b", Gesture())).Accepted);

        _hotkeys.Fire(_hotkeys.Bound.Single());

        var status = _session.Status();
        Assert.Equal("preset-b", status.LastPresetId);
        Assert.False(status.Switches.MicSendEnabled); // hotkey-driven switch is the same safe flow
        Assert.Equal("preset-b", _sink.LastPresetId);
    }

    [Fact]
    public void Trigger_after_the_preset_was_deleted_is_refused()
    {
        Assert.True(_store.Save(Config("preset-a")).Accepted);
        Assert.True(_manager.Apply("preset-a").Accepted);
        Assert.True(_session.EnableMic(true).Accepted);
        var binding = new HotkeyBinding(HotkeyAction.SelectPreset, "preset-a", Gesture());
        Assert.True(_dispatcher.Bind(binding).Accepted);
        Assert.True(_store.Delete("preset-a").Accepted); // dangling target

        _hotkeys.Fire(binding);

        var status = _session.Status();
        Assert.True(status.Switches.MicSendEnabled); // refusal, no teardown
        Assert.Contains(_notices, n => n.Message.Contains("preset missing"));
    }

    [Fact]
    public void Trigger_with_no_manager_wired_reports_unavailable()
    {
        using var bare = new HotkeyDispatcher(_session, new FakeHotkeyService());
        var service = new FakeHotkeyService();
        using var dispatcher = new HotkeyDispatcher(_session, service);
        dispatcher.Noticed += notice => _notices.Add(notice);
        var binding = new HotkeyBinding(HotkeyAction.SelectPreset, "preset-a", Gesture());
        Assert.True(dispatcher.Bind(binding).Accepted); // no presetExists hook: bind passes

        service.Fire(binding);

        Assert.Contains(_notices, n => n.Message.Contains("no preset manager"));
    }

    private static HotkeyGesture Gesture()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+F9", out var gesture));
        return gesture;
    }

    private sealed class FakeLastSink : ILastPresetSink
    {
        public string? LastPresetId { get; set; }

        public void SetLastPreset(string? presetId) => LastPresetId = presetId;
    }

    private sealed class TinyStore : IPresetStore
    {
        private readonly Dictionary<string, PresetConfig> _configs = [];

        public IReadOnlyList<PresetSummary> List() =>
            _configs.Values.Select(c => new PresetSummary(c.PresetId, c.Name, false)).ToList();

        public PresetLoadResult Load(string presetId) =>
            _configs.TryGetValue(presetId, out var config) ? PresetLoadResult.Loaded(config) : PresetLoadResult.NotFound();

        public CommandResult Save(PresetConfig config)
        {
            _configs[config.PresetId] = config;
            return CommandResult.Ok;
        }

        public bool Exists(string presetId) => _configs.ContainsKey(presetId);

        public CommandResult Delete(string presetId) => _configs.Remove(presetId) ? CommandResult.Ok : CommandResult.Reject("preset-not-found");

        public CommandResult Rename(string presetId, string newName) => CommandResult.Ok;

        public CommandResult RestoreBackup(string presetId) => CommandResult.Reject("no-backup");

        public string NewId() => "preset-tiny";
    }
}
