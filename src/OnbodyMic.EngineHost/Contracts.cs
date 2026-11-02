namespace OnbodyMic.EngineHost;

using OnbodyMic.Application;
using OnbodyMic.Audio;
using OnbodyMic.Domain;
using OnbodyMic.Media;

internal sealed class SettingsData
{
    public required int SchemaVersion { get; init; }
    public string? LastPresetId { get; init; }
    public string? LibraryRoot { get; init; }
    public required Dictionary<string, string?> GlobalHotkeys { get; init; }
    public required Dictionary<string, string?> ClipHotkeys { get; init; }
    public required Dictionary<string, string?> PresetHotkeys { get; init; }
    public required UiPreferencesData Ui { get; init; }

    public void Validate()
    {
        if (SchemaVersion != 1)
            throw HostCommandException.Invalid($"unsupported settings schemaVersion: {SchemaVersion}");
        if (GlobalHotkeys is null || ClipHotkeys is null || PresetHotkeys is null || Ui is null)
            throw HostCommandException.Invalid("settings maps and ui must be objects");
    }
}

internal sealed class UiPreferencesData
{
    public bool StartMinimized { get; init; }
    public bool CloseToTrayAcknowledged { get; init; }
    public string ClipSort { get; init; } = "name";
    public bool ReduceTransparency { get; init; }
}

internal sealed record AppSelectionData(string? ExecutablePath, string? DisplayName);

internal sealed record DeviceConfigData(
    string MicDeviceId,
    string VirtualRenderDeviceId,
    string? VirtualCaptureDeviceId,
    string MonitorDeviceId,
    AppSelectionData? Application)
{
    public static DeviceConfigData FromDomain(PresetDeviceConfig devices) => new(
        devices.MicDeviceId, devices.VirtualRenderDeviceId, devices.VirtualCaptureDeviceId,
        devices.MonitorDeviceId, devices.Application is { } app
            ? new AppSelectionData(app.ExecutablePath, app.DisplayName) : null);
}

internal sealed class PresetConfigData
{
    public required string PresetId { get; init; }
    public required string Name { get; init; }
    public required DeviceConfigData Devices { get; init; }
    public required float MicGain { get; init; }
    public required float AppGain { get; init; }
    public required float ClipGain { get; init; }
    public required MonitorPreferencesData Monitor { get; init; }
    public required string[] ClipLayout { get; init; }
    public required bool LibraryFavoritesOnly { get; init; }
    public string? LibraryGroupId { get; init; }

    public PresetConfig ToDomain()
    {
        if (string.IsNullOrWhiteSpace(PresetId) || !PresetConfigRules.IsValidId(PresetId))
            throw HostCommandException.Invalid("invalid-preset-id");
        if (string.IsNullOrWhiteSpace(Name))
            throw HostCommandException.Invalid("preset-name-required");
        if (Devices is null || Monitor is null || ClipLayout is null || ClipLayout.Any(id => id is null))
            throw HostCommandException.Invalid("preset devices, monitor and clipLayout are required");
        if (string.IsNullOrWhiteSpace(Devices.MicDeviceId)
            || string.IsNullOrWhiteSpace(Devices.VirtualRenderDeviceId)
            || string.IsNullOrWhiteSpace(Devices.MonitorDeviceId))
            throw HostCommandException.Invalid("microphone, virtual render and monitor endpoint ids are required");
        var mode = CommandArguments.MonitorMode(Monitor.Mode);
        CommandArguments.ValidateGain(MicGain, "micGain");
        CommandArguments.ValidateGain(AppGain, "appGain");
        CommandArguments.ValidateGain(ClipGain, "clipGain");
        CommandArguments.ValidateGain(Monitor.Gain, "monitor.gain");
        var application = Devices.Application is { } app
            ? new AppSelection(app.ExecutablePath, app.DisplayName) : null;
        return new PresetConfig(PresetId, Name, new PresetDeviceConfig(
            Devices.MicDeviceId, Devices.VirtualRenderDeviceId, Devices.VirtualCaptureDeviceId,
            Devices.MonitorDeviceId, application), MicGain, AppGain, ClipGain,
            new MonitorPreferences(mode, Monitor.Gain), ClipLayout,
            LibraryFavoritesOnly, LibraryGroupId);
    }
}

internal sealed class MonitorPreferencesData
{
    public required string Mode { get; init; }
    public required float Gain { get; init; }
}

internal sealed record ClipItemData(
    string ClipId, string DisplayName, string ManagedAssetPath, string? OriginalImportPath,
    int DurationMs, string Format, bool Favorite, string? GroupId, float Gain,
    long ImportUnixMs, bool Available)
{
    public static ClipItemData FromDomain(ClipItem item, ClipLibrary library) => new(
        item.ClipId, item.DisplayName, item.ManagedAssetPath, item.OriginalImportPath,
        item.DurationMs, item.Format, item.Favorite, item.GroupId, item.Gain,
        item.ImportUnixMs, File.Exists(library.AssetAbsolutePath(item)));
}

internal sealed record ImportResultData(string Outcome, ClipItemData Item, ClipItemData? ExistingDuplicate);
internal sealed record AudioDeviceData(string Id, string FriendlyName, string DataFlow, string State, bool IsDefault);
internal sealed record AppInstanceData(uint ProcessId, string ExecutablePath, string DisplayName);
internal sealed record HotkeyBindingData(string Action, string? TargetId, string Gesture);
internal sealed record SettingsIssueData(string Section, string Key, string Reason);
internal sealed record HotkeyData(HotkeyBindingData[] Hotkeys, SettingsIssueData[] HotkeyIssues, string[] HotkeyActions);
internal sealed record LibraryData(ClipItemData[] Clips, ClipGroup[] Groups, string? LibraryError, string LibraryRoot);

internal sealed record SessionStatusData(
    SessionPhase Phase, SessionSwitches Switches, bool EmergencyLatched, bool OutputLost,
    bool DevicesValidated, string? LastPresetId, AppBinding? AppBinding, ActiveClip? Clip,
    bool MonitorSuspendedForPreview, MonitorMode MonitorMode, RouteGains Gains,
    DeviceConfigData? Devices, bool ClipLoading)
{
    public static SessionStatusData FromDomain(SessionStatus status) => new(
        status.Phase, status.Switches, status.EmergencyLatched, status.OutputLost,
        status.DevicesValidated, status.LastPresetId, status.AppBinding, status.Clip,
        status.MonitorSuspendedForPreview, status.MonitorMode, status.Gains,
        status.Devices is { } devices ? DeviceConfigData.FromDomain(devices) : null,
        status.ClipLoading);
}

internal sealed record RuntimeMetricsData(SessionMetrics Audio, ClipCacheStats Cache);
internal sealed record RuntimeIssueData(string Route, string Reason)
{
    public static RuntimeIssueData FromDomain(RouteIssue issue) => new(issue.Route.ToString(), issue.Reason);
}

internal record RuntimeStateData(
    long Revision, SessionStatusData Status, SessionLevels Levels,
    RuntimeMetricsData Metrics, RuntimeIssueData[] Issues);
internal sealed record EngineSnapshotData(
    long Revision, SessionStatusData Status, SessionLevels Levels,
    RuntimeMetricsData Metrics, RuntimeIssueData[] Issues,
    ClipItemData[] Clips, ClipGroup[] Groups,
    HotkeyBindingData[] Hotkeys, SettingsIssueData[] HotkeyIssues, string[] HotkeyActions,
    string? LibraryError, string LibraryRoot)
    : RuntimeStateData(Revision, Status, Levels, Metrics, Issues);
