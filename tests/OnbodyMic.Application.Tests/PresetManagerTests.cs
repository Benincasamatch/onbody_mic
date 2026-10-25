using OnbodyMic.Audio;
using OnbodyMic.Domain;
using Xunit;

namespace OnbodyMic.Application.Tests;

/// <summary>
/// P3 preset transaction semantics (docs/06 §5.3.5, T23/T24/T38, B06):
/// applies tear the session down before committing, failures keep the old
/// selection muted, and only successful applies update the last-preset id.
/// </summary>
public sealed class PresetManagerTests : IDisposable
{
    private readonly Env _env = new();

    public void Dispose() => _env.Dispose();

    private static PresetConfig Config(
        string id,
        float micGain = 1f,
        string micId = "test-mic",
        MonitorMode monitorMode = MonitorMode.ClipOnly,
        float monitorGain = 1f) => new(
        id,
        $"预设 {id}",
        new PresetDeviceConfig(micId, "test-render", "test-cable", "test-monitor", null),
        micGain,
        1f,
        1f,
        new MonitorPreferences(monitorMode, monitorGain),
        [],
        false,
        null);

    [Fact]
    public void Apply_stops_every_running_path_and_commits_the_new_config()
    {
        var manager = _env.Manager;
        _env.Store.Save(Config("preset-a"));
        _env.Store.Save(Config("preset-b", micGain: 1.5f, micId: "mic-b", monitorMode: MonitorMode.FullEndpoint, monitorGain: 0.5f));
        Accept(manager.Apply("preset-a"));
        Accept(_env.Session.EnableMic(true));
        Accept(_env.Session.EnableExtras(true));
        Accept(_env.Session.BindApplication(new AppBinding(42, "app")));
        Accept(_env.Session.EnableApp(true));
        Accept(_env.Session.PlayClip("clip-a"));
        Accept(_env.Session.SetMonitor(true, MonitorMode.ClipOnly));

        var result = manager.Apply("preset-b");

        Assert.True(result.Accepted, result.Rejection);
        var status = _env.Session.Status();
        Assert.False(status.Switches.MicSendEnabled);
        Assert.False(status.Switches.ExtrasSendEnabled);
        Assert.False(status.Switches.AppSendEnabled);
        Assert.False(status.Switches.MonitorEnabled);
        Assert.Null(status.Clip);
        Assert.Equal("preset-b", status.LastPresetId);
        Assert.Equal(1.5f, status.Gains.Mic, 3);
        Assert.Equal(0.5f, status.Gains.Monitor, 3);
        Assert.Equal(MonitorMode.FullEndpoint, status.MonitorMode); // preference restored, enable state not
        Assert.Equal("preset-b", _env.Sink.LastPresetId);
        Assert.True(status.DevicesValidated); // fresh output attached for the new render endpoint
        Assert.Equal(2, _env.OutputCreates.Count); // one attach per applied preset
    }

    [Fact]
    public void Subsequent_enables_use_the_new_device_config()
    {
        _env.Store.Save(Config("preset-b", micId: "mic-b"));
        Accept(_env.Manager.Apply("preset-b"));

        Accept(_env.Session.EnableMic(true));

        Assert.Equal("mic-b", _env.MicIds.Single());
    }

    [Fact]
    public void Apply_while_emergency_latched_keeps_the_latch()
    {
        _env.Store.Save(Config("preset-a"));
        _env.Store.Save(Config("preset-b"));
        Accept(_env.Manager.Apply("preset-a"));
        Accept(_env.Session.EmergencyMute());

        var result = _env.Manager.Apply("preset-b");

        Assert.True(result.Accepted, result.Rejection);
        Assert.True(_env.Session.Status().EmergencyLatched);
        Assert.False(_env.Session.Status().Switches.AnySendEnabled);
        Assert.Equal("preset-b", _env.Session.Status().LastPresetId);
    }

    [Fact]
    public void Applying_a_corrupt_preset_tears_down_but_keeps_the_old_selection()
    {
        _env.Store.Save(Config("preset-a"));
        _env.Store.Save(Config("preset-b"));
        Accept(_env.Manager.Apply("preset-a"));
        Accept(_env.Session.EnableMic(true));
        _env.Store.Corrupt("preset-b");

        var result = _env.Manager.Apply("preset-b");

        Assert.False(result.Accepted);
        Assert.StartsWith("preset-corrupt", result.Rejection);
        var status = _env.Session.Status();
        Assert.False(status.Switches.MicSendEnabled); // the attempt stopped everything
        Assert.Equal("preset-a", status.LastPresetId); // old selection kept (B06)
        Assert.Equal("preset-a", _env.Sink.LastPresetId);
    }

    [Fact]
    public void Applying_an_unknown_preset_is_rejected_without_touching_the_session()
    {
        _env.Store.Save(Config("preset-a"));
        Accept(_env.Manager.Apply("preset-a"));
        Accept(_env.Session.EnableMic(true));

        var result = _env.Manager.Apply("preset-nope");

        Assert.False(result.Accepted);
        Assert.Equal("preset-not-found", result.Rejection);
        Assert.True(_env.Session.Status().Switches.MicSendEnabled); // invalid command, no switch attempt
    }

    [Fact]
    public void Device_validator_issues_surface_without_blocking_the_commit()
    {
        _env.Store.Save(Config("preset-b", micId: "mic-gone"));
        _env.MissingRoutes.Add(PresetRoute.Microphone);

        var result = _env.Manager.Apply("preset-b");

        Assert.True(result.Accepted, result.Rejection);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(PresetRoute.Microphone, issue.Route);
        Assert.Equal("endpoint-missing", issue.Reason);
        Assert.Equal("mic-gone", _env.Session.Status().Devices!.MicDeviceId); // kept, not substituted (B05)
    }

    [Fact]
    public void Preview_suspension_does_not_survive_a_preset_apply()
    {
        _env.Store.Save(Config("preset-a"));
        _env.Store.Save(Config("preset-b"));
        Accept(_env.Manager.Apply("preset-a"));
        Accept(_env.Session.SetMonitor(true, MonitorMode.ClipOnly));
        Accept(_env.Session.PreviewClip("clip-a")); // suspends monitor, resume-eligible

        Accept(_env.Manager.Apply("preset-b"));

        var status = _env.Session.Status();
        Assert.False(status.Switches.MonitorEnabled);
        Assert.False(status.MonitorSuspendedForPreview);
    }

    [Fact]
    public void Delete_active_preset_tears_down_and_clears_last_selection()
    {
        _env.Store.Save(Config("preset-a"));
        Accept(_env.Manager.Apply("preset-a"));
        Accept(_env.Session.EnableMic(true));

        Accept(_env.Manager.Delete("preset-a"));

        var status = _env.Session.Status();
        Assert.False(status.Switches.MicSendEnabled);
        Assert.Null(_env.Sink.LastPresetId); // startup restore forgets the deleted preset
        Assert.False(_env.Store.Exists("preset-a"));
        Assert.Equal("preset-a", status.LastPresetId); // engine keeps the torn-down selection id
    }

    [Fact]
    public void Delete_inactive_preset_leaves_the_session_untouched()
    {
        _env.Store.Save(Config("preset-a"));
        _env.Store.Save(Config("preset-b"));
        Accept(_env.Manager.Apply("preset-a"));
        Accept(_env.Session.EnableMic(true));

        Accept(_env.Manager.Delete("preset-b"));

        Assert.True(_env.Session.Status().Switches.MicSendEnabled);
        Assert.Equal("preset-a", _env.Sink.LastPresetId);
    }

    [Fact]
    public void Restore_startup_applies_the_last_preset_with_everything_off()
    {
        _env.Store.Save(Config("preset-a"));
        Accept(_env.Manager.Apply("preset-a"));

        _env.RebuildSession(); // simulates a restart on the same store/sink

        Accept(_env.Manager.RestoreStartup());

        var status = _env.Session.Status();
        Assert.Equal("preset-a", status.LastPresetId);
        Assert.Equal(SessionSwitches.AllOff, status.Switches);
        Assert.Null(status.Clip);
    }

    [Fact]
    public void Restore_startup_without_last_preset_is_rejected()
    {
        Assert.Equal("no-last-preset", _env.Manager.RestoreStartup().Rejection);
    }

    [Fact]
    public void Save_snapshots_current_session_config_and_view()
    {
        _env.Store.Save(Config("preset-a"));
        Accept(_env.Manager.Apply("preset-a"));
        Accept(_env.Session.SetGains(new RouteGains(2f, 1f, 1f, 0.75f)));

        Accept(_env.Manager.Save("preset-a", ["clip-x", "clip-y"], favoritesOnly: true, groupId: "g1"));

        var loaded = _env.Store.Load("preset-a").Config!;
        Assert.Equal(2f, loaded.MicGain, 3);
        Assert.Equal(0.75f, loaded.Monitor.Gain, 3);
        Assert.Equal(["clip-x", "clip-y"], loaded.ClipLayout);
        Assert.True(loaded.LibraryFavoritesOnly);
        Assert.Equal("g1", loaded.LibraryGroupId);
    }

    [Fact]
    public void Duplicate_creates_an_independent_copy_with_a_new_id()
    {
        _env.Store.Save(Config("preset-a"));
        _env.Store.Save(Config("preset-a") with { ClipLayout = ["clip-9"] });

        Accept(_env.Manager.Duplicate("preset-a", "副本"));

        var copy = _env.Store.List().Single(p => p.Name == "副本");
        Assert.NotEqual("preset-a", copy.PresetId);
        Assert.Equal(["clip-9"], _env.Store.Load(copy.PresetId).Config!.ClipLayout);
    }

    [Fact]
    public void Presets_referencing_a_clip_are_listed_for_delete_prompts()
    {
        _env.Store.Save(Config("preset-a"));
        _env.Store.Save(Config("preset-a") with { ClipLayout = ["clip-shared"] });
        _env.Store.Save(Config("preset-b"));

        var referencing = _env.Manager.PresetsReferencingClip("clip-shared");

        Assert.StartsWith("预设", Assert.Single(referencing), StringComparison.Ordinal);
    }

    private static void Accept(CommandResult result) => Assert.True(result.Accepted, result.Rejection);
    private static void Accept(PresetApplyResult result) => Assert.True(result.Accepted, result.Rejection);

    // ---- environment ----

    private sealed class Env : IDisposable
    {
        public readonly FakePresetStore Store = new();
        public readonly FakeLastPresetSink Sink = new();
        public readonly List<string> MicIds = [];
        public readonly List<int> OutputCreates = [];
        public readonly HashSet<PresetRoute> MissingRoutes = [];

        private readonly FakeSourceAdapter _mic = new();
        private readonly FakeSourceAdapter _app = new();
        private readonly AudioSessionOptions _options;
        private PresetManager? _manager;

        public Env()
        {
            _options = new AudioSessionOptions
            {
                ClipDecoder = new FakeDecoder(),
                ClipBoundaryFadeFrames = 0,
                ClipResolver = _ => new ClipResolution(true, true, "assets/x.wav", 1f),
                MicAdapterFactory = (micId, channel) =>
                {
                    MicIds.Add(micId);
                    _mic.Channel = channel;
                    return _mic;
                },
                AppAdapterFactory = (channel, _) =>
                {
                    _app.Channel = channel;
                    return _app;
                },
                OutputFactory = _ =>
                {
                    OutputCreates.Add(OutputCreates.Count);
                    return new FakeVirtualOutput();
                },
                MonitorOutputFactory = _ => new FakeMonitorOutput(),
                MonitorCaptureFactory = (_, _) => new FakeMonitorCapture(),
                SourcePrerollTimeoutMs = 20,
                AdaptiveRate = false,
                DeviceValidator = Validate,
            };
            Session = new AudioSession(_options);
        }

        public AudioSession Session { get; private set; }

        public PresetManager Manager => _manager ??= new PresetManager(Session, Store, Sink);

        public void RebuildSession()
        {
            Session.Dispose();
            Session = new AudioSession(_options);
            _manager = null;
        }

        private IReadOnlyList<RouteIssue> Validate(PresetDeviceConfig config)
        {
            var issues = new List<RouteIssue>();
            if (MissingRoutes.Contains(PresetRoute.Microphone))
            {
                issues.Add(new RouteIssue(PresetRoute.Microphone, "endpoint-missing"));
            }

            return issues;
        }

        public void Dispose() => Session.Dispose();
    }

    private sealed class FakeLastPresetSink : ILastPresetSink
    {
        public string? LastPresetId { get; set; }

        public void SetLastPreset(string? presetId) => LastPresetId = presetId;
    }

    private sealed class FakePresetStore : IPresetStore
    {
        private readonly Dictionary<string, PresetConfig> _configs = [];
        private readonly HashSet<string> _corrupt = [];

        public IReadOnlyList<PresetSummary> List() =>
            _configs.Keys
                .Select(id => new PresetSummary(id, _configs[id].Name, _corrupt.Contains(id)))
                .OrderBy(s => s.PresetId)
                .ToList();

        public PresetLoadResult Load(string presetId) => !_configs.ContainsKey(presetId)
            ? PresetLoadResult.NotFound()
            : _corrupt.Contains(presetId)
                ? PresetLoadResult.Corrupt("injected corruption")
                : PresetLoadResult.Loaded(_configs[presetId]);

        public CommandResult Save(PresetConfig config)
        {
            if (!PresetConfigRules.IsValidId(config.PresetId)
                || string.IsNullOrWhiteSpace(config.Name)
                || string.IsNullOrWhiteSpace(config.Devices.MicDeviceId))
            {
                return CommandResult.Reject("invalid-config");
            }

            _configs[config.PresetId] = config;
            _corrupt.Remove(config.PresetId);
            return CommandResult.Ok;
        }

        public bool Exists(string presetId) => _configs.ContainsKey(presetId);

        public CommandResult Delete(string presetId) => _configs.Remove(presetId)
            ? CommandResult.Ok
            : CommandResult.Reject("preset-not-found");

        public CommandResult Rename(string presetId, string newName)
        {
            if (!_configs.TryGetValue(presetId, out var config))
            {
                return CommandResult.Reject("preset-not-found");
            }

            _configs[presetId] = config with { Name = newName };
            return CommandResult.Ok;
        }

        public CommandResult RestoreBackup(string presetId) => CommandResult.Reject("no-backup");

        public string NewId() => "preset-fake-" + (_configs.Count + 1);

        public void Corrupt(string presetId) => _corrupt.Add(presetId);
    }
}
