namespace OnbodyMic.Desktop;

using OnbodyMic.Application;
using OnbodyMic.Cli;
using OnbodyMic.Domain;
using OnbodyMic.Persistence;
using OnbodyMic.Platform.Windows;

/// <summary>
/// Composition root for the desktop shell (docs/02 §5 Desktop role): builds
/// the session, soundboard and preset manager, validates preset devices
/// against live endpoints, and owns the teardown order on exit.
/// </summary>
internal sealed class AppShell : IDisposable
{
    private bool _disposed;

    public AppShell(string? rootOverride, Action<string> log)
    {
        Log = log;
        // enumerate through NAudio before any pump activates the enumerator
        // coclass via raw interop: once the cached RCW belongs to another
        // type, NAudio's coclass cast fails (P1 constraint, devlog 2026-10-26)
        _ = WindowsAudioDevices.List(activeOnly: true);
        Soundboard = Soundboard.Create(rootOverride, log);
        Session = new AudioSession(new AudioSessionOptions
        {
            ClipDecoder = new NAudioClipDecoder(),
            SampleStore = Soundboard.Cache,
            ClipResolver = Soundboard.Resolve,
            MicAdapterFactory = (micDeviceId, channel) => new WindowsMicSource(micDeviceId, channel),
            AppAdapterFactory = (channel, binding) => new WindowsAppSource(binding, channel),
            OutputFactory = renderDeviceId => new WasapiExclusiveRenderPump(renderDeviceId),
            MonitorOutputFactory = monitorDeviceId => new WindowsMonitorOutput(monitorDeviceId),
            MonitorCaptureFactory = (cableDeviceId, _) => new WindowsMonitorCapture(cableDeviceId),
            DeviceValidator = ValidateDevices,
        });
        Session.Noticed += notice => log($"[notice] {notice.Kind}: {notice.Message}");

        PresetStore = new JsonPresetStore(Path.Combine(Soundboard.Root, "presets"));
        Presets = new PresetManager(Session, PresetStore, Soundboard.CreateLastPresetSink());
        Presets.Noticed += message => log($"[preset] {message}");
        Soundboard.SetPresetClipReferenceLookup(Presets.PresetsReferencingClip);
    }

    public Action<string> Log { get; }

    public Soundboard Soundboard { get; }

    public AudioSession Session { get; }

    public JsonPresetStore PresetStore { get; }

    public PresetManager Presets { get; }

    /// <summary>
    /// Startup wiring (T01/T24): restore the last preset config-only, or fall
    /// back to first-run guidance when nothing is stored.
    /// </summary>
    /// <returns>True when a preset was restored; false when first-run guidance is needed.</returns>
    public bool TryRestoreStartup()
    {
        Soundboard.Attach(Session, Presets.Exists, Presets.Apply);
        var result = Presets.RestoreStartup();
        if (result.Accepted)
        {
            return true;
        }

        Log(result.Rejection == "no-last-preset"
            ? "[startup] 无已保存预设，进入首次引导"
            : $"[startup] 启动恢复失败（保持安全关闭）: {result.Rejection}");
        return false;
    }

    /// <summary>Live-endpoint validation (B05): missing devices become visible route issues, never substitutions.</summary>
    private IReadOnlyList<RouteIssue> ValidateDevices(PresetDeviceConfig config)
    {
        var issues = new List<RouteIssue>();
        var activeIds = WindowsAudioDevices.List(activeOnly: true).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

        if (!activeIds.Contains(config.MicDeviceId))
        {
            issues.Add(new RouteIssue(PresetRoute.Microphone, "endpoint-missing"));
        }

        if (!activeIds.Contains(config.VirtualRenderDeviceId))
        {
            issues.Add(new RouteIssue(PresetRoute.VirtualOutput, "endpoint-missing"));
        }

        if (config.VirtualCaptureDeviceId is { } cable && !activeIds.Contains(cable))
        {
            issues.Add(new RouteIssue(PresetRoute.VirtualCapture, "endpoint-missing"));
        }

        if (!activeIds.Contains(config.MonitorDeviceId))
        {
            issues.Add(new RouteIssue(PresetRoute.Monitor, "endpoint-missing"));
        }

        if (config.Application is { IsBound: true } app
            && app.ExecutablePath is { } path
            && !File.Exists(path))
        {
            issues.Add(new RouteIssue(PresetRoute.Application, "executable-missing"));
        }

        return issues;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Soundboard.Dispose();
        Session.Dispose();
    }
}
